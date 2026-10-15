using Loom.Core;
using Loom.Sqlite;

namespace Loom.Demo;

/// <summary>Offline executable example of host-owned handler registration and tenant worker routing.</summary>
internal static class TenantDeliveryDemo
{
    /// <summary>Queues equal local keys in two databases, reopens them and verifies distinct receiver keys.</summary>
    public static async Task Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"loom-tenant-delivery-"+Guid.NewGuid().ToString("N"));
        var tenants=new[]{"north","south"};
        TenantWorkspace Workspace()=>new(root,tenants,(user,_)=>user=="delivery-service"?"Worker":null);
        var model=new Catalog([DeliveryOutbox.Definition("Delivery",new(){["Worker"]=["read","create","write"]})]);
        var outbox=new DeliveryOutbox();
        try
        {
            foreach(var tenant in tenants)
            {
                var workspace=Workspace();workspace.Provision(tenant,model);
                using var session=workspace.Open("delivery-service",tenant);
                outbox.Enqueue(session,"Delivery","certificate.ready",new(){["certificate"]="CAL-001"},key:"certificate-1");
            }
            // Real applications persist receiver keys and effects atomically. This local sink sends no network traffic.
            var received=new System.Collections.Concurrent.ConcurrentDictionary<string,bool>();
            var handlers=new DeliveryHandlerRegistry(new Dictionary<string,Func<TenantDeliveryContext,CancellationToken,Task>>
            {
                ["certificate.ready"]=(context,token)=>
                {
                    token.ThrowIfCancellationRequested();received.TryAdd(context.IdempotencyKey,true);
                    Console.WriteLine($"{context.TenantId}: accepted {context.Message.Payload["certificate"]}, key {context.IdempotencyKey}");
                    return Task.CompletedTask;
                }
            });
            // Routes reopen storage after enqueue; each scan and active handler gets its own authorized session.
            var worker=new TenantDeliveryWorker(tenants.Select(t=>Workspace().DeliveryRoute("delivery-service",t)),outbox,"Delivery",handlers);
            var results=await worker.RunOnceAsync();
            if(results.Sum(r=>r.Batch.Completed)!=2||received.Count!=2||(await worker.RunOnceAsync()).Any(r=>r.Batch.Completed!=0))
                throw new InvalidOperationException("Tenant delivery isolation/recovery failed.");
            Console.WriteLine("PASS: two tenant databases, trusted routing, distinct receiver keys, persisted completion.");
        }
        finally{Directory.Delete(root,true);}
    }
}
