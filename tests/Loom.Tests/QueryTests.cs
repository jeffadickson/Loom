using Loom.Core;
using Loom.Sqlite;
using System.Text.Json.Nodes;
static class QueryTests
{
    public static int Run(string temp)
    {
        int count=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS queries: "+name);}
        var catalog=new Catalog([new("QueryItem","Query item",1,"Name",false,[new("Name","Name","Text"),new("Amount","Amount","Decimal"),new("Flag","Flag","Boolean")],new(){["Admin"]=["read","create"]})]);
        var schema=catalog.Get("QueryItem");var amount=schema.Fields[1];var actor=new Actor("query","Admin");
        foreach(var provider in new[]{"memory","sqlite"})
        {
            using IDocumentStore store=provider=="memory"?new MemoryStore():new SqliteStore("Data Source="+Path.Combine(temp,"queries.db"));store.Migrate(catalog);var service=new DocumentService(catalog,store,[]);
            foreach(var value in new[]{"10","2","9007199254740993","9007199254740992"})service.Create(schema.Name,new(){["Name"]="x'--",["Amount"]=value,["Flag"]=true},actor);
            service.Create(schema.Name,new(){["Name"]="empty"},actor);
            var page=service.Query(schema.Name,actor,new(amount.Id!,Skip:1,Take:2));Check(page.Total==5&&page.Items.Select(d=>d.Values["Amount"]!.ToString()).SequenceEqual(new[]{"2","10"}),provider+" exact numeric sort and null-first paging");
            page=service.Query(schema.Name,actor,new(amount.Id!,Filters:[new(amount.Id!,FilterOperator.GreaterThan,JsonValue.Create("9007199254740992"))]));Check(page.Total==1&&page.Items[0].Values["Amount"]!.ToString()=="9007199254740993",provider+" comparison beyond JavaScript integer precision");
            page=service.Query(schema.Name,actor,new(amount.Id!,Filters:[new(schema.Fields[0].Id!,FilterOperator.Equal,JsonValue.Create("x'--"))]));Check(page.Total==4,provider+" parameterized string filter");
            page=service.Query(schema.Name,actor,new(amount.Id!,Filters:[new(amount.Id!,FilterOperator.IsNull)]));Check(page.Total==1,provider+" null filter");
            try{service.Query(schema.Name,actor,new("Name"));throw new Exception("Unstable name accepted");}catch(LoomException e){Check(e.Status==400,provider+" query requires stable field identity");}
            try{service.Query(schema.Name,new("outsider","Viewer"),new(amount.Id!));throw new Exception("Authorization bypass");}catch(LoomException e){Check(e.Status==403,provider+" query authorization");}
        }
        Check(Loom.Demo.Designer.Export(catalog).SequenceEqual(Loom.Demo.Designer.Export(catalog)),"deterministic model export");
        var generated=Path.Combine(temp,"incremental");TypeGenerator.Generate(catalog,generated);var path=Path.Combine(generated,"QueryItem.g.cs");var stamp=File.GetLastWriteTimeUtc(path);TypeGenerator.Generate(catalog,generated);Check(File.GetLastWriteTimeUtc(path)==stamp,"unchanged generated binding keeps timestamp");
        File.WriteAllText(path,"// handwritten");try{TypeGenerator.Generate(catalog,generated);throw new Exception("Handwritten overwrite");}catch(InvalidOperationException){Check(File.ReadAllText(path)=="// handwritten","handwritten files protected");}
        return count;
    }
}
