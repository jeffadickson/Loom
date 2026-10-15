using System.Net;
using System.Text.Json.Nodes;
using Loom.Core;
using Loom.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Loom.Tests;

public sealed class OutboxTests
{
    [Fact] public async Task IndependentSqliteConnectionsCannotClaimTheSameVersionTwice()
    {
        var path=Path.Combine(Path.GetTempPath(),"loom-claim-"+Guid.NewGuid().ToString("N")+".db");
        var catalog=new Catalog([DeliveryOutbox.Definition("Delivery",new(){["Admin"]=["read","create","write"]})]);
        SqliteStore Open()=>new(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
        ApplicationSession Session(SqliteStore store){store.ReadCatalog();return new(new(catalog,store,[]),new("worker","Admin"));}
        try
        {
            Document candidate;
            using(var store=Open()){store.Migrate(catalog);candidate=new DeliveryOutbox().Enqueue(Session(store),"Delivery","test",new());}
            var claims=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Task.Run(()=>
            {
                using var store=Open();return new DeliveryOutbox().Claim(Session(store),candidate);
            })));
            Assert.Single(claims,c=>c!=null);
            using var check=Open();Assert.Equal("1",Session(check).Get("Delivery",candidate.Id).Values["Attempts"]!.ToString());
        }
        finally{File.Delete(path);}
    }
    private sealed class Receiver:HttpMessageHandler
    {
        public List<string> Keys {get;}=[];
        public List<string> Bodies {get;}=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Assert.Equal("https://receiver.example/events",request.RequestUri!.AbsoluteUri);
            Keys.Add(Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(Keys.Count==1?HttpStatusCode.ServiceUnavailable:HttpStatusCode.Accepted);
        }
    }
    [Fact] public async Task HttpTransportPreservesKeyAndPayloadAndSurfacesFailures()
    {
        using var receiver=new Receiver();using var client=new HttpClient(receiver);
        var transport=new HttpOutboxTransport(client,new("https://receiver.example/events"));
        var message=new OutboxMessage("id","stable-key","event",new(){["value"]="12345678901234567.89"},1);
        await Assert.ThrowsAsync<HttpRequestException>(()=>transport.SendAsync(message,CancellationToken.None));
        await transport.SendAsync(message with{Attempt=2},CancellationToken.None);
        Assert.Equal(new[]{"stable-key","stable-key"},receiver.Keys);
        Assert.Equal(receiver.Bodies[0],receiver.Bodies[1]);
        Assert.Equal("12345678901234567.89",JsonNode.Parse(receiver.Bodies[0])!["value"]!.ToString());
    }
    [Fact] public async Task HostCancellationLeavesClaimForRecoveryWithoutImmediateRedelivery()
    {
        var catalog=new Catalog([DeliveryOutbox.Definition("Delivery",new(){["Admin"]=["read","create","write"]})]);
        using var store=new MemoryStore();store.Migrate(catalog);
        var session=new ApplicationSession(new DocumentService(catalog,store,[]),new("worker","Admin"));
        var outbox=new DeliveryOutbox();var job=outbox.Enqueue(session,"Delivery","test",new());var lease=outbox.Claim(session,job)!;
        var handlers=new Dictionary<string,Func<OutboxMessage,CancellationToken,Task>>{["test"]=(_,_)=>throw new OperationCanceledException()};
        await Assert.ThrowsAsync<OperationCanceledException>(()=>outbox.DeliverAsync(session,lease,handlers));
        Assert.Equal("Leased",session.Get("Delivery",job.Id).Values["Status"]!.ToString());
        Assert.Empty(outbox.Candidates(session,"Delivery"));
    }
}
