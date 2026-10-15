using Loom.Core;
using Loom.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Loom.Tests;

public sealed class ProjectionTests
{
    [Fact] public void SqliteSummaryAndScalarReportNeverDecodeOmittedBlob()
    {
        var root=Path.Combine(Path.GetTempPath(),"loom-projection-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"records.db");
            var model=new Catalog([new("Reading","Reading",1,"Name",false,
                [new("Name","Name","Text",true),new("Payload","Raw evidence","Binary")],
                new(){["Admin"]=["read","create"]})]);
            using var store=new SqliteStore(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
            store.Migrate(model);var service=new DocumentService(model,store,[]);var actor=new Actor("reader","Admin");
            var doc=service.Create("Reading",new(){["Name"]="Visible summary",["Payload"]=Convert.ToBase64String(new byte[1048576])},actor);
            // An invalid compressed envelope proves projection avoids decoding, rather than discarding after reading.
            using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString()))
            {
                connection.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE Reading SET Payload=X'00'";command.ExecuteNonQuery();
            }
            var schema=model.Get("Reading");var name=schema.Fields[0].Id!;
            var row=Assert.Single(service.Query("Reading",actor,new(name,SelectFieldIds:[name])).Items);
            Assert.Equal(doc.Id,row.Id);Assert.Single(row.Values);Assert.Equal("Visible summary",row.Values["Name"]!.ToString());
            Assert.Equal(1,SavedReports.Run(new("Names",schema.Id!,[new(name)]),model,service,actor).Total);
            Assert.Equal(422,Assert.Throws<LoomException>(()=>service.Get("Reading",doc.Id,actor)).Status);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void ProjectionAndFormMetadataCannotExposeRestrictedFields()
    {
        var model=new Catalog([new("Private","Private",1,"Name",false,
            [new("Name","Display name","Text",true,MaxLength:80),new("Secret","Secret","Binary")],
            new(){["Admin"]=["read","create","write"],["Viewer"]=["read"]},Immutable:true)]);
        using var store=new MemoryStore();store.Migrate(model);var service=new DocumentService(model,store,[],new SecretPolicy());
        var admin=new Actor("admin","Admin");var viewer=new Actor("viewer","Viewer");
        service.Create("Private",new(){["Name"]="Allowed",["Secret"]=Convert.ToBase64String([1,2,3])},admin);
        var schema=model.Get("Private");
        Assert.Equal(403,Assert.Throws<LoomException>(()=>service.Query("Private",viewer,new(schema.Fields[0].Id!,SelectFieldIds:[schema.Fields[1].Id!]))).Status);
        Assert.False(Assert.Single(service.List("Private",viewer).Items).Values.ContainsKey("Secret"));
        var form=service.Describe("Private",viewer);Assert.False(form.CanCreate);Assert.False(form.CanEdit);
        var field=Assert.Single(form.Fields);Assert.Equal(schema.Fields[0].Id,field.Id);Assert.Equal("Display name",field.Label);Assert.Equal(80,field.MaxLength);
        Assert.False(service.Describe("Private",admin).CanEdit); // Immutable records never advertise editing.
    }
    private sealed class SecretPolicy:IDocumentAccessPolicy
    {
        public bool CanRead(Field field,Actor actor)=>field.Name!="Secret"||actor.Role=="Admin";
    }
}
