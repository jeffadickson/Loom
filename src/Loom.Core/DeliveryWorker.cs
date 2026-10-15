using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Loom.Core;

/// <summary>A worker-owned authorized session and its lifetime (for example a store or DI scope).</summary>
public sealed class ApplicationSessionScope(IApplicationSession session,IDisposable lifetime):IDisposable
{
    private IDisposable? owner=lifetime;
    public IApplicationSession Session=>owner!=null?session:throw new ObjectDisposedException(nameof(ApplicationSessionScope));
    public void Dispose()=>Interlocked.Exchange(ref owner,null)?.Dispose();
}
public sealed record DeliveryWorkerOptions(int Concurrency=4,int BatchSize=25,TimeSpan? PollInterval=null);
public sealed record DeliveryWorkResult(string? Id,string Outcome,Exception? Error=null);
public sealed record DeliveryBatch(IReadOnlyList<DeliveryWorkResult> Results)
{
    public int Completed=>Results.Count(r=>r.Outcome=="Complete");
}

/// <summary>
/// Bounded asynchronous delivery pump. Each active item owns a separate session; claims are acquired
/// only when a concurrency slot is available. Durable state remains entirely in DeliveryOutbox.
/// </summary>
public sealed class DeliveryWorker
{
    private readonly Func<ApplicationSessionScope> open;
    private readonly DeliveryOutbox outbox;
    private readonly string type;
    private readonly IReadOnlyDictionary<string,Func<OutboxMessage,CancellationToken,Task>> handlers;
    private readonly Action<IApplicationSession>? prepare;
    private readonly DeliveryWorkerOptions options;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim batchGate=new(1,1);
    public DeliveryWorker(Func<ApplicationSessionScope> open,DeliveryOutbox outbox,string type,
        IReadOnlyDictionary<string,Func<OutboxMessage,CancellationToken,Task>> handlers,
        DeliveryWorkerOptions? options=null,Action<IApplicationSession>? prepare=null,TimeProvider? clock=null)
    {
        this.open=open??throw new ArgumentNullException(nameof(open));this.outbox=outbox??throw new ArgumentNullException(nameof(outbox));Catalog.Identifier(type);this.type=type;
        this.options=options??new();this.clock=clock??TimeProvider.System;
        if(this.options.Concurrency is <1 or >32||this.options.BatchSize is <1 or >100||
            (this.options.PollInterval??TimeSpan.FromSeconds(1))<TimeSpan.FromMilliseconds(1)||
            (this.options.PollInterval??TimeSpan.FromSeconds(1))>TimeSpan.FromHours(1))throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentNullException.ThrowIfNull(handlers);
        if(handlers.Any(h=>string.IsNullOrWhiteSpace(h.Key)||h.Value==null))throw new ArgumentException("Register named handlers.",nameof(handlers));
        this.handlers=new ReadOnlyDictionary<string,Func<OutboxMessage,CancellationToken,Task>>(handlers.ToDictionary(h=>h.Key,h=>h.Value,StringComparer.Ordinal));
        ApplicationWorkflow.Synchronous(prepare);this.prepare=prepare;
    }
    /// <summary>Process one bounded snapshot. Overlapping calls on this instance serialize; other workers use lease fencing.</summary>
    public async Task<DeliveryBatch> RunOnceAsync(CancellationToken stop=default)
    {
        await batchGate.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            var results=new ConcurrentQueue<DeliveryWorkResult>();IReadOnlyList<Document> candidates;
            try
            {
                using var scan=open();stop.ThrowIfCancellationRequested();
                try{prepare?.Invoke(scan.Session);}
                catch(Exception ex) when(ex is not OperationCanceledException && ex is not OutOfMemoryException)
                {results.Enqueue(new(null,"PreparationFailed",ex));}
                // Preparation failure must not strand already queued deliveries.
                stop.ThrowIfCancellationRequested();candidates=outbox.Candidates(scan.Session,type,options.BatchSize);
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested){throw;}
            catch(Exception ex) when(ex is not OutOfMemoryException)
            {results.Enqueue(new(null,"ScanFailed",ex));return new(results.ToArray());}
            await Parallel.ForEachAsync(candidates,new ParallelOptions{MaxDegreeOfParallelism=options.Concurrency,CancellationToken=stop},async(candidate,token)=>
            {
                try
                {
                    // No database session/transaction is shared by concurrent handlers.
                    using var work=open();token.ThrowIfCancellationRequested();
                    var lease=outbox.Claim(work.Session,candidate);
                    if(lease==null){results.Enqueue(new(candidate.Id,"Skipped"));return;}
                    var outcome=await outbox.DeliverAsync(work.Session,lease,handlers,token).ConfigureAwait(false);
                    results.Enqueue(new(candidate.Id,outcome.Acknowledged?"Complete":outcome.Exception==null?"Unacknowledged":"DeliveryFailed",outcome.Exception));
                }
                catch(OperationCanceledException) when(stop.IsCancellationRequested){throw;}
                catch(Exception ex) when(ex is not OutOfMemoryException)
                {
                    // A lease deadline or provider conflict affects this item, not other workers.
                    // Any unacknowledged claim remains persisted for lease-expiry recovery.
                    results.Enqueue(new(candidate.Id,"Interrupted",ex));
                }
            }).ConfigureAwait(false);
            return new(results.ToArray());
        }
        finally{batchGate.Release();}
    }
    /// <summary>Poll after each completed batch. Cancellation awaits cooperative active handlers and disposes their scopes.</summary>
    public async Task RunAsync(Action<DeliveryBatch>? report=null,CancellationToken stop=default)
    {
        while(true)
        {
            stop.ThrowIfCancellationRequested();
            var batch=await RunOnceAsync(stop).ConfigureAwait(false);
            report?.Invoke(batch); // Host diagnostics must be fast and non-throwing; exceptions intentionally stop this loop.
            await Task.Delay(options.PollInterval??TimeSpan.FromSeconds(1),clock,stop).ConfigureAwait(false);
        }
    }
}
