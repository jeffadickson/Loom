using Loom.Core;
using System.Text.Json.Nodes;
using Xunit;
namespace Loom.Tests;

public sealed class AccessPolicyTests
{
    private sealed class Policy : IDocumentAccessPolicy
    {
        public FieldFilter[] Scope(DocType schema,Actor actor)=>actor.Role=="Admin"?[]:[new(schema.Fields.Single(f=>f.Name=="Owner").Id!,FilterOperator.Equal,JsonValue.Create(actor.Id))];
        public bool CanRead(Field field,Actor actor)=>field.Name!="Secret"||actor.Role=="Admin";
        public bool CanWrite(Field field,Actor actor)=>CanRead(field,actor);
    }
    [Fact] public void Scope_and_field_permissions_cover_queries_writes_audit_and_reports()
    {
        var model=new Catalog([new("Private","Private",1,"Name",false,[new("Name","Name","Text"),new("Owner","Owner","Text"),new("Secret","Secret","Text")],new(){["Admin"]=["read","create","write","delete","audit"],["Editor"]=["read","create","write","delete","audit"]})]);
        using var store=new MemoryStore();store.Migrate(model);var service=new DocumentService(model,store,[],new Policy());
        var admin=new Actor("root","Admin");var alice=new Actor("alice","Editor");var bob=new Actor("bob","Editor");
        var doc=service.Create("Private",new(){["Name"]="Private run",["Owner"]="alice",["Secret"]="sensitive"},admin);
        Assert.Empty(service.List("Private",bob).Items);Assert.Equal(0,service.List("Private",bob).Total);
        Assert.Equal(404,Assert.Throws<LoomException>(()=>service.Get("Private",doc.Id,bob)).Status);
        Assert.False(service.Get("Private",doc.Id,alice).Values.ContainsKey("Secret"));
        Assert.Equal(403,Assert.Throws<LoomException>(()=>service.List("Private",alice,search:"sensitive")).Status);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>service.Update("Private",doc.Id,1,new(){["Secret"]="change"},alice)).Status);
        Assert.Equal(404,Assert.Throws<LoomException>(()=>service.Delete("Private",doc.Id,1,bob)).Status);
        var update=service.Update("Private",doc.Id,1,new(){["Name"]="Revised",["Owner"]="alice"},alice);
        Assert.False(update.Values.ContainsKey("Secret"));Assert.Equal("sensitive",service.Get("Private",doc.Id,admin).Values["Secret"]!.ToString());
        Assert.All(service.History("Private",doc.Id,alice),e=>Assert.False(e.After!.Values.ContainsKey("Secret")));
        Assert.Equal(404,Assert.Throws<LoomException>(()=>service.History("Private",doc.Id,bob)).Status);
        Assert.Equal(404,Assert.Throws<LoomException>(()=>service.Update("Private",doc.Id,2,new(){["Name"]="move",["Owner"]="bob"},alice)).Status);
        var report=new SavedReport("Secret",model.Get("Private").Id!,[new(model.Get("Private").Fields[2].Id!)]);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>SavedReports.Run(report,model,service,alice)).Status);
    }
}
