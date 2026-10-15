using Loom.Core;
using Loom.Sqlite;
using Microsoft.Data.Sqlite;

namespace Loom.Demo;

/// <summary>Offline demonstration of persisted scheduling, retry and receiver-side deduplication.</summary>
internal static class OutboxDemo
{
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    public static async Task Run()
    {
        var path=Path.Combine(Path.GetTempPath(),"loom-outbox-"+Guid.NewGuid().ToString("N")+".db");
        var clock=new Clock();var outbox=new DeliveryOutbox(new(InitialBackoff:TimeSpan.FromSeconds(5)),clock);
        var catalog=new Catalog([DeliveryOutbox.Definition("Delivery",new(){["Worker"]=["read","create","write","audit"]})]);
        SqliteStore Open()=>new(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
        ApplicationSession Session(SqliteStore store)=>new(new(catalog,store,[]),new("demo-worker","Worker"));
        var accepted=new HashSet<string>();var calls=0;
        // This sink simulates a remote receiver. No network messages are sent by the demo.
        var handlers=new Dictionary<string,Func<OutboxMessage,CancellationToken,Task>>
        {
            ["certificate.ready"]=(message,_)=>
            {
                calls++;
                if(accepted.Add(message.Key))Console.WriteLine("Receiver accepted certificate: "+message.Payload["certificate"]);
                else Console.WriteLine("Receiver suppressed duplicate key: "+message.Key);
                if(calls==1)throw new IOException("Simulated lost acknowledgement after receiver acceptance.");
                return Task.CompletedTask;
            }
        };
        try
        {
            using(var store=Open())
            {
                store.Migrate(catalog);var session=Session(store);
                session.Atomic(s=>outbox.Enqueue(s,"Delivery","certificate.ready",new(){["certificate"]="CAL-2026-001"},clock.Now.AddMinutes(1),"demo-certificate-1"));
                if(outbox.Candidates(session,"Delivery").Count!=0)throw new InvalidOperationException("Delivered before schedule.");
                Console.WriteLine("Persisted delivery scheduled for one minute from now; restarting storage.");
            }
            clock.Now=clock.Now.AddMinutes(1);
            using(var store=Open())
            {
                store.Migrate(catalog);var session=Session(store);
                var lease=outbox.Claim(session,outbox.Candidates(session,"Delivery").Single())!;
                var first=await outbox.DeliverAsync(session,lease,handlers);
                if(first.Exception==null||outbox.Candidates(session,"Delivery").Count!=0)throw new InvalidOperationException("Expected scheduled retry.");
                Console.WriteLine("Lost acknowledgement: persisted five-second backoff.");
            }
            clock.Now=clock.Now.AddSeconds(5);
            using(var store=Open())
            {
                store.Migrate(catalog);var session=Session(store);
                var lease=outbox.Claim(session,outbox.Candidates(session,"Delivery").Single())!;
                if(!(await outbox.DeliverAsync(session,lease,handlers)).Acknowledged||accepted.Count!=1||calls!=2)
                    throw new InvalidOperationException("Retry/deduplication failed.");
                Console.WriteLine("PASS: restart recovery, scheduled retry, two deliveries, one receiver effect.");
            }
        }
        finally{File.Delete(path);}
    }
}
