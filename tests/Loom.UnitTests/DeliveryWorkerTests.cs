using System.Collections.Concurrent;
using Loom.Core;
using Loom.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Loom.Tests;

public sealed class DeliveryWorkerTests
{
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    private sealed class DisposeAction(Action action):IDisposable {public void Dispose()=>action();}
    private sealed class Fixture:IDisposable
    {
        private readonly string path=Path.Combine(Path.GetTempPath(),"loom-pump-"+Guid.NewGuid().ToString("N")+".db");
        private readonly Catalog model=new([DeliveryOutbox.Definition("Delivery",new(){["Admin"]=["read","create","write"]})]);
        public readonly Clock Clock=new();
        public readonly DeliveryOutbox Outbox;
        public int Active;
        public Fixture(int count)
        {
            Outbox=new(clock:Clock);
            using var store=Store();store.Migrate(model);var session=Session(store);
            for(var i=0;i<count;i++)Outbox.Enqueue(session,"Delivery","send",new(){["index"]=i});
        }
        private SqliteStore Store()=>new(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
        private ApplicationSession Session(SqliteStore store)=>new(new(model,store,[]),new("worker","Admin"));
        public ApplicationSessionScope Open()
        {
            var store=Store();try{store.ReadCatalog();Interlocked.Increment(ref Active);
                return new(Session(store),new DisposeAction(()=>{store.Dispose();Interlocked.Decrement(ref Active);}));}
            catch{store.Dispose();throw;}
        }
        public Document[] Rows(){using var scope=Open();return scope.Session.Query("Delivery",new(model.Get("Delivery").Fields[0].Id!)).Items.ToArray();}
        public DeliveryWorker Worker(Func<OutboxMessage,CancellationToken,Task> handler,int concurrency=2,int batch=25,
            Action<IApplicationSession>? prepare=null)=>new(Open,Outbox,"Delivery",new Dictionary<string,Func<OutboxMessage,CancellationToken,Task>>{{"send",handler}},new(concurrency,batch),prepare);
        public void Dispose(){Assert.Equal(0,Active);File.Delete(path);}
    }
    [Fact] public async Task ConcurrencyAndBatchLimitsDoNotClaimWaitingItems()
    {
        using var f=new Fixture(7);var entered=0;var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker=f.Worker(async(_,token)=>{if(Interlocked.Increment(ref entered)==2)ready.TrySetResult();await release.Task.WaitAsync(token);},batch:5);
        var run=worker.RunOnceAsync();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(2,Volatile.Read(ref entered));
            var rows=f.Rows();Assert.Equal(2,rows.Count(r=>r.Values["Status"]!.ToString()=="Leased"));
            Assert.Equal(5,rows.Count(r=>r.Values["Attempts"]!.ToString()=="0"));
        }
        finally{release.TrySetResult();}
        Assert.Equal(5,(await run.WaitAsync(TimeSpan.FromSeconds(10))).Completed);
        Assert.Equal(5,entered);Assert.Equal(0,f.Active);
        Assert.Equal(2,f.Rows().Count(r=>r.Values["Status"]!.ToString()=="Pending"));
    }
    [Fact] public async Task CompetingPumpsCompleteEachLiveLeaseOnce()
    {
        using var f=new Fixture(12);var calls=new ConcurrentDictionary<string,int>();
        async Task Send(OutboxMessage message,CancellationToken token)
        {calls.AddOrUpdate(message.Key,1,(_,n)=>n+1);await Task.Yield();token.ThrowIfCancellationRequested();}
        var first=f.Worker(Send);var second=f.Worker(Send);
        await Task.WhenAll(first.RunOnceAsync(),second.RunOnceAsync()).WaitAsync(TimeSpan.FromSeconds(20));
        // Any provider contention remains durable; another poll may pick up unclaimed candidates.
        await first.RunOnceAsync();
        Assert.Equal(12,calls.Count);Assert.All(calls.Values,n=>Assert.Equal(1,n));
        Assert.All(f.Rows(),r=>Assert.Equal("Complete",r.Values["Status"]!.ToString()));Assert.Equal(0,f.Active);
    }
    [Fact] public async Task CancellationDisposesScopesAndExpiredClaimsRecoverWithStableKeys()
    {
        using var f=new Fixture(5);using var stop=new CancellationTokenSource();var entered=0;
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var keys=new ConcurrentBag<string>();
        var run=f.Worker(async(message,token)=>{keys.Add(message.Key);if(Interlocked.Increment(ref entered)==2)ready.TrySetResult();await Task.Delay(Timeout.Infinite,token);}).RunOnceAsync(stop.Token);
        try{await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));}finally{stop.Cancel();}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run);Assert.Equal(0,f.Active);
        Assert.Equal(2,f.Rows().Count(r=>r.Values["Status"]!.ToString()=="Leased"));
        f.Clock.Now=f.Clock.Now.AddMinutes(3);var recovered=new ConcurrentBag<string>();
        var result=await f.Worker((message,_)=>{recovered.Add(message.Key);return Task.CompletedTask;}).RunOnceAsync();
        Assert.Equal(5,result.Completed);Assert.All(keys,key=>Assert.Contains(key,recovered));Assert.Equal(0,f.Active);
    }
    [Fact] public async Task PreparationFailureAndIndependentCancellationDoNotStopOtherItems()
    {
        using var f=new Fixture(3);
        var worker=f.Worker((message,_)=>message.Payload["index"]!.GetValue<int>()==0?Task.FromCanceled(new CancellationToken(true)):Task.CompletedTask,
            prepare:_=>throw new InvalidOperationException("Preparation unavailable"));
        var result=await worker.RunOnceAsync();Assert.Equal(2,result.Completed);
        Assert.Single(result.Results,r=>r.Outcome=="PreparationFailed");Assert.Single(result.Results,r=>r.Outcome=="Interrupted");
        Assert.Equal(0,f.Active);
    }
}
