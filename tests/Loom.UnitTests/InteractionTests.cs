using Loom.Core;
using System.Text.Json.Nodes;
using NSubstitute;
using Xunit;
namespace Loom.Tests;
public sealed class InteractionTests
{
    private static Catalog Model()=>new([new("Reading","Readings",1,"Name",false,[new("Name","Name","Text",Required:true)],new(){["Admin"]=["read","create","audit"]})]);
    [Fact] public void Authorization_fails_before_transaction_or_hook()
    {
        var store=Substitute.For<IDocumentStore>();var hook=Substitute.For<IDocumentHook>();hook.Type.Returns("Reading");
        var service=new DocumentService(Model(),store,[hook]);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>service.Create("Reading",new(){["Name"]="denied"},new("outsider","Viewer"))).Status);
        store.DidNotReceive().Transaction(Arg.Any<Func<IStoreSession,Document>>());
        hook.DidNotReceive().BeforeSave(Arg.Any<JsonObject>());
    }
    [Fact] public void Hook_runs_before_insert_and_audit_follows_insert()
    {
        var session=Substitute.For<IStoreSession>();var store=Substitute.For<IDocumentStore>();
        store.Transaction(Arg.Any<Func<IStoreSession,Document>>()).Returns(call=>call.Arg<Func<IStoreSession,Document>>()(session));
        var hook=Substitute.For<IDocumentHook>();hook.Type.Returns("Reading");
        hook.When(h=>h.BeforeSave(Arg.Any<JsonObject>())).Do(call=>call.Arg<JsonObject>()["Name"]="normalized");
        var service=new DocumentService(Model(),store,[hook]);var doc=service.Create("Reading",new(){["Name"]="input"},new("writer","Admin"));
        Received.InOrder(()=>
        {
            hook.BeforeSave(Arg.Any<JsonObject>());
            session.Insert(Arg.Is<Document>(d=>d.Values["Name"]!.ToString()=="normalized"));
            session.AppendAudit(Arg.Is<AuditEntry>(a=>a.DocumentId==doc.Id && a.Actor=="writer" && a.Action=="create"));
        });
        session.Received(1).Insert(Arg.Any<Document>());session.Received(1).AppendAudit(Arg.Any<AuditEntry>());
    }
    [Fact] public void Audit_failure_rolls_back_the_real_insert()
    {
        using var memory=new MemoryStore();memory.Migrate(Model());
        var store=Substitute.For<IDocumentStore>();string? insertedId=null;
        store.Transaction(Arg.Any<Func<IStoreSession,Document>>()).Returns(call=>memory.Transaction(actual=>
        {
            var session=Substitute.For<IStoreSession>();
            session.When(s=>s.Insert(Arg.Any<Document>())).Do(c=>{var document=c.Arg<Document>();insertedId=document.Id;actual.Insert(document);});
            session.When(s=>s.AppendAudit(Arg.Any<AuditEntry>())).Do(_=>throw new IOException("Injected audit outage"));
            return call.Arg<Func<IStoreSession,Document>>()(session);
        }));
        var service=new DocumentService(Model(),store,[]);
        Assert.Throws<IOException>(()=>service.Create("Reading",new(){["Name"]="input"},new("writer","Admin")));
        Assert.NotNull(insertedId);
        memory.Transaction(session=>{Assert.Empty(session.List("Reading"));Assert.Empty(session.Audit("Reading",insertedId!));return 0;});
    }
    [Fact] public void Hook_cannot_inject_undeclared_fields()
    {
        using var store=new MemoryStore();var model=Model();store.Migrate(model);
        var hook=Substitute.For<IDocumentHook>();hook.Type.Returns("Reading");
        hook.When(h=>h.BeforeSave(Arg.Any<JsonObject>())).Do(call=>call.Arg<JsonObject>()["Typo"]=123);
        var service=new DocumentService(model,store,[hook]);
        Assert.Equal(422,Assert.Throws<LoomException>(()=>service.Create("Reading",new(){["Name"]="input"},new("writer","Admin"))).Status);
        Assert.Empty(service.List("Reading",new("writer","Admin")).Items);
    }
}
