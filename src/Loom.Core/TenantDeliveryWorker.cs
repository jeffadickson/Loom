using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Loom.Core;

/// <summary>Trusted routing context, created by the host rather than deserialized from a payload.</summary>
/// <param name="TenantId">Operator-configured tenant identifier.</param>
/// <param name="Message">Persisted delivery, including its original tenant-local key.</param>
public sealed record TenantDeliveryContext(string TenantId,OutboxMessage Message)
{
    /// <summary>Stable, tenant-qualified key for receivers shared by multiple tenants.</summary>
    public string IdempotencyKey=>$"{TenantId}:{Message.Key}";
}

/// <summary>Immutable allowlist of application handlers. Delegates must support concurrent invocation.</summary>
public sealed class DeliveryHandlerRegistry
{
    private readonly IReadOnlyDictionary<string,Func<TenantDeliveryContext,CancellationToken,Task>> handlers;
    /// <summary>Snapshots registrations and rejects empty names, null callbacks and duplicate names.</summary>
    public DeliveryHandlerRegistry(IEnumerable<KeyValuePair<string,Func<TenantDeliveryContext,CancellationToken,Task>>> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var copy=new Dictionary<string,Func<TenantDeliveryContext,CancellationToken,Task>>(StringComparer.Ordinal);
        foreach(var entry in registrations)
            if(string.IsNullOrWhiteSpace(entry.Key)||entry.Value==null||!copy.TryAdd(entry.Key,entry.Value))
                throw new ArgumentException("Handlers need unique nonempty names and callbacks.",nameof(registrations));
        handlers=new ReadOnlyDictionary<string,Func<TenantDeliveryContext,CancellationToken,Task>>(copy);
    }
    /// <summary>Binds callbacks to trusted routing metadata, independently of message contents.</summary>
    internal IReadOnlyDictionary<string,Func<OutboxMessage,CancellationToken,Task>> Bind(string tenant)=>
        handlers.ToDictionary(entry=>entry.Key,entry=>(Func<OutboxMessage,CancellationToken,Task>)
            ((message,token)=>entry.Value(new(tenant,message),token)),StringComparer.Ordinal);
}

/// <summary>An operator-owned route. The factory must open fresh storage bound to this tenant.</summary>
/// <param name="Id">Lowercase path-safe tenant slug; never selected by a message payload.</param>
/// <param name="Open">Creates an independently authorized session and its disposable lifetime.</param>
/// <param name="Prepare">Optional synchronous tenant-local preparation, such as recurring schedules.</param>
public sealed record DeliveryTenant(string Id,Func<ApplicationSessionScope> Open,Action<IApplicationSession>? Prepare=null);

/// <summary>One tenant's diagnostic result. Exceptions are for trusted host logs, not public APIs.</summary>
/// <param name="TenantId">Configured route identifier.</param>
/// <param name="Batch">Bounded scan and delivery results.</param>
public sealed record TenantDeliveryBatch(string TenantId,DeliveryBatch Batch);

/// <summary>
/// Polls a fixed allowlist of tenant routes. One bounded batch per tenant per round limits global
/// concurrency while isolating tenant storage/authorization failures. This is not a distributed scheduler.
/// </summary>
public sealed class TenantDeliveryWorker
{
    private readonly (string Id,DeliveryWorker Worker)[] workers;
    private readonly TimeSpan interval;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate=new(1,1);
    /// <summary>Snapshots routes and handlers; validates configuration before opening any database.</summary>
    /// <param name="tenants">Trusted routes. Membership must be resolved by each session factory.</param>
    /// <param name="outbox">Shared stateless delivery service and lease policy.</param>
    /// <param name="type">Outbox document type used by all configured tenant catalogs.</param>
    /// <param name="handlers">Explicit application handler allowlist.</param>
    /// <param name="options">Concurrency and batch limits per active tenant, and delay between rounds.</param>
    /// <param name="clock">Optional clock for polling delays.</param>
    public TenantDeliveryWorker(IEnumerable<DeliveryTenant> tenants,DeliveryOutbox outbox,string type,
        DeliveryHandlerRegistry handlers,DeliveryWorkerOptions? options=null,TimeProvider? clock=null)
    {
        ArgumentNullException.ThrowIfNull(tenants);ArgumentNullException.ThrowIfNull(handlers);
        var routes=tenants.ToArray();var ids=new HashSet<string>(StringComparer.Ordinal);
        if(routes.Length==0)throw new ArgumentException("Configure at least one tenant.",nameof(tenants));
        foreach(var route in routes)
            if(route==null||route.Id==null||!Regex.IsMatch(route.Id,@"\A[a-z][a-z0-9-]{0,47}\z")||!ids.Add(route.Id)||route.Open==null)
                throw new ArgumentException("Tenant routes need unique normalized identifiers and scope factories.",nameof(tenants));
        this.clock=clock??TimeProvider.System;interval=options?.PollInterval??TimeSpan.FromSeconds(1);
        workers=routes.Select(route=>(route.Id,new DeliveryWorker(route.Open,outbox,type,handlers.Bind(route.Id),options,route.Prepare,this.clock))).ToArray();
    }
    /// <summary>Visits every tenant once. Overlapping rounds serialize; cancellation stops the round.</summary>
    public async Task<IReadOnlyList<TenantDeliveryBatch>> RunOnceAsync(CancellationToken stop=default)
    {
        await gate.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            var results=new List<TenantDeliveryBatch>(workers.Length);
            foreach(var (id,worker) in workers)
            {
                stop.ThrowIfCancellationRequested();
                results.Add(new(id,await worker.RunOnceAsync(stop).ConfigureAwait(false)));
            }
            return results.AsReadOnly();
        }
        finally{gate.Release();}
    }
    /// <summary>Polls until cancelled. Diagnostic callbacks must be fast, synchronous and non-throwing.</summary>
    public async Task RunAsync(Action<TenantDeliveryBatch>? report=null,CancellationToken stop=default)
    {
        while(true)
        {
            foreach(var result in await RunOnceAsync(stop).ConfigureAwait(false))report?.Invoke(result);
            await Task.Delay(interval,clock,stop).ConfigureAwait(false);
        }
    }
}
