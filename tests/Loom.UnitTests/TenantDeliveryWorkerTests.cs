using System.Collections.Concurrent;
using Loom.Core;
using Loom.Sqlite;
using Xunit;

namespace Loom.Tests;

/// <summary>Real tenant databases exercise trusted routing, revocation and restart delivery.</summary>
public sealed class TenantDeliveryWorkerTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"loom-tenant-worker-"+Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string,string> grants=new(StringComparer.Ordinal);
    private readonly Catalog model=new([DeliveryOutbox.Definition("Delivery",new(){["Worker"]=["read","create","write"]})]);
    private readonly Clock clock=new();
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    private TenantWorkspace Workspace()=>new(root,["north","south"],(user,tenant)=>user=="service"?grants.GetValueOrDefault(tenant):null);
    private DeliveryOutbox Outbox()=>new(clock:clock);
    private static DeliveryHandlerRegistry Registry(Func<TenantDeliveryContext,CancellationToken,Task> send)=>
        new(new Dictionary<string,Func<TenantDeliveryContext,CancellationToken,Task>>{["send"]=send});
    private TenantDeliveryWorker Worker(DeliveryHandlerRegistry handlers,int batch=25)=>
        new(new[]{Workspace().DeliveryRoute("service","north"),Workspace().DeliveryRoute("service","south")},Outbox(),"Delivery",handlers,new(2,batch));
    public TenantDeliveryWorkerTests()
    {
        foreach(var tenant in new[]{"north","south"})
        {
            grants[tenant]="Worker";var workspace=Workspace();workspace.Provision(tenant,model);
            using var session=workspace.Open("service",tenant);
            Outbox().Enqueue(session,"Delivery","send",new(){["tenant"]="attacker-supplied"},key:"same-local-key");
        }
    }
    [Fact] public async Task TrustedContextAndReceiverKeysStayIsolatedAcrossTenants()
    {
        var calls=new ConcurrentDictionary<string,string>();
        var results=await Worker(Registry((context,_)=>
        {
            Assert.Equal("attacker-supplied",context.Message.Payload["tenant"]!.ToString());
            Assert.Equal("same-local-key",context.Message.Key);
            calls[context.TenantId]=context.IdempotencyKey;return Task.CompletedTask;
        })).RunOnceAsync();
        Assert.All(results,result=>Assert.Equal(1,result.Batch.Completed));
        Assert.Equal("north:same-local-key",calls["north"]);Assert.Equal("south:same-local-key",calls["south"]);
        using var north=Workspace().Open("service","north");using var south=Workspace().Open("service","south");
        var northId=north.List("Delivery").Items.Single().Id;
        Assert.Equal(404,Assert.Throws<LoomException>(()=>south.Get("Delivery",northId)).Status);
    }
    [Fact] public async Task RevokedTenantDoesNotStopOthersAndLaterHostRecoversItsQueue()
    {
        var accepted=new ConcurrentBag<string>();var handlers=Registry((context,_)=>{accepted.Add(context.IdempotencyKey);return Task.CompletedTask;});
        var worker=Worker(handlers);grants.TryRemove("north",out _);
        var results=await worker.RunOnceAsync();
        Assert.Single(results[0].Batch.Results,r=>r.Outcome=="ScanFailed"&&r.Error is LoomException {Status:403});
        Assert.Equal(1,results[1].Batch.Completed);Assert.Single(accepted);
        grants["north"]="Worker";
        var restarted=await Worker(handlers).RunOnceAsync();
        Assert.Equal(1,restarted[0].Batch.Completed);Assert.Equal(0,restarted[1].Batch.Completed);
        Assert.Equal(2,accepted.Count);
    }
    [Fact] public async Task RevocationDuringHandlerFencesAcknowledgementAndExpiryReplaysStableKey()
    {
        var keys=new List<string>();
        var first=await Worker(Registry((context,_)=>
        {
            keys.Add(context.IdempotencyKey);
            if(context.TenantId=="north")grants.TryRemove("north",out var removed);
            return Task.CompletedTask;
        })).RunOnceAsync();
        Assert.Single(first[0].Batch.Results,r=>r.Outcome=="Interrupted");Assert.Equal(1,first[1].Batch.Completed);
        grants["north"]="Worker";clock.Now=clock.Now.AddMinutes(3);
        var recovered=await Worker(Registry((context,_)=>{keys.Add(context.IdempotencyKey);return Task.CompletedTask;})).RunOnceAsync();
        Assert.Equal(1,recovered[0].Batch.Completed);
        Assert.Equal(2,keys.Count(key=>key=="north:same-local-key"));
        Assert.Single(keys,key=>key=="south:same-local-key");
    }
    [Fact] public async Task BoundedTenantBatchLetsNextTenantRunAndCancellationRecoversOnRestart()
    {
        using(var north=Workspace().Open("service","north"))
            for(var i=0;i<4;i++)Outbox().Enqueue(north,"Delivery","send",new());
        var batches=await Worker(Registry((_,_)=>Task.CompletedTask),batch:1).RunOnceAsync();
        Assert.All(batches,b=>Assert.Equal(1,b.Batch.Completed));
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop=new CancellationTokenSource();
        var run=Worker(Registry(async(_,token)=>{started.TrySetResult();await Task.Delay(Timeout.Infinite,token);})).RunOnceAsync(stop.Token);
        try{await started.Task.WaitAsync(TimeSpan.FromSeconds(10));}finally{stop.Cancel();}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run);
        clock.Now=clock.Now.AddMinutes(3);
        var recovered=await Worker(Registry((_,_)=>Task.CompletedTask)).RunOnceAsync();
        Assert.Equal(4,recovered[0].Batch.Completed);Assert.Equal(0,recovered[1].Batch.Completed);
    }
    [Fact] public void InvalidAndDuplicateRegistrationsFailBeforeOpeningStorage()
    {
        var entry=new KeyValuePair<string,Func<TenantDeliveryContext,CancellationToken,Task>>("send",(_,_)=>Task.CompletedTask);
        Assert.Throws<ArgumentException>(()=>new DeliveryHandlerRegistry([entry,entry]));
        var opened=0;var route=new DeliveryTenant("north",()=>{opened++;throw new InvalidOperationException();});
        Assert.Throws<ArgumentException>(()=>new TenantDeliveryWorker([route,route],Outbox(),"Delivery",Registry(entry.Value)));
        Assert.Throws<ArgumentException>(()=>new TenantDeliveryWorker([route with{Id="../south"}],Outbox(),"Delivery",Registry(entry.Value)));
        Assert.Equal(403,Assert.Throws<LoomException>(()=>Workspace().DeliveryRoute("service","unknown")).Status);
        Assert.Equal(0,opened);
    }
    public void Dispose()=>Directory.Delete(root,true);
}
