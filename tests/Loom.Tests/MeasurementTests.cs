using Loom.Core;
using Loom.Core.Science;
using Loom.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

static class MeasurementTests
{
    public static int Run(string temp)
    {
        int count=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;Console.WriteLine("PASS measurement: "+message);}
        var catalog=AttributeCatalog.From(typeof(TemperatureRun));var actor=new Actor("scientist","Admin");
        foreach(var name in new[]{"Memory","SQLite"})
        {
            using IDocumentStore store=name=="Memory"?new MemoryStore():new SqliteStore("Pooling=False;Data Source="+Path.Combine(temp,"measurements.db"));
            store.Migrate(catalog);var service=new DocumentService(catalog,store,[]);var repo=service.Repository<TemperatureRun>(actor);
            var m=new Measurement(new Quantity(25,Units.Celsius),.1);
            var doc=repo.Create(new(){Name="Thermal run",Temperature=m});
            Check(repo.Get(doc.Id).Value.Temperature==m,name+" typed roundtrip preserves original units and uncertainty");
            Check(MeasurementJson.Read(repo.History(doc.Id)[0].After!.Values["Temperature"]!)==m,name+" audit preserves original measurement");
            var wire=WireJson.Encode(service.Get("TemperatureRun",doc.Id,actor));
            Check(wire!["values"]!["Temperature"]!["value"]!.ToString()=="25",name+" API compound value");
            var schema=catalog.Get("TemperatureRun");var report=new SavedReport("Temperatures",schema.Id!,schema.Fields.Select(f=>new ReportColumn(f.Id!)).ToArray());
            Check(System.Net.WebUtility.HtmlDecode(ReportWriter.Write(report,new("Temperatures"),catalog,service,actor)).Contains("25 ± 0.1 degC"),name+" report labels quantity and uncertainty");
            var narrowed=new Catalog([schema with{Version=2,Fields=schema.Fields.Select(f=>f.Type=="Measurement"?f with{AllowedUnits=["K"]}:f).ToArray()}]);
            try{store.Migrate(narrowed);throw new Exception("Stored unit removed");}catch(LoomException e){Check(e.Status==422,name+" migration preserves unit interpretation");}
            foreach(var bad in new[]{"{\"value\":1,\"unit\":\"m\"}","{\"value\":\"NaN\",\"unit\":\"K\"}","{\"value\":1,\"unit\":\"K\",\"standardUncertainty\":-1}","{\"value\":1,\"unit\":\"K\",\"typo\":1}"})
            {
                try{service.Create(schema.Name,new(){["Name"]="invalid",["Temperature"]=JsonNode.Parse(bad)},actor);throw new Exception("Invalid measurement accepted");}
                catch(LoomException e){Check(e.Status==422,name+" malformed measurement rejected");}
            }
        }
        using(var reopened=new SqliteStore("Pooling=False;Data Source="+Path.Combine(temp,"measurements.db")))
        {
            reopened.Migrate(catalog);var service=new DocumentService(catalog,reopened,[]);
            Check(service.Repository<TemperatureRun>(actor).List().Items[0].Value.Temperature!.Value.Unit==Units.Celsius,"SQLite reopen retains units");
            var schema=catalog.Get("TemperatureRun");
            var next=new Catalog([schema with{Version=2,Fields=schema.Fields.Select(f=>f.Type=="Measurement"?f with{AllowedUnits=["K"]}:f).ToArray()}]);
            try{reopened.Migrate(next);throw new Exception("Removal of stored unit accepted");}catch(LoomException e){Check(e.Status==422,"migration refuses removal of populated unit");}
        }
        Check(JsonSerializer.Deserialize<Measurement>(JsonSerializer.Serialize(new Measurement(new Quantity(1,Units.Volt))))!.StandardUncertainty==null,"unknown uncertainty serialization");
        return count;
    }
}
[Document("7eab82cb6e2e4c108d39f02161dbd001",nameof(Name))]
[Grant("Admin","read","create","write","delete","audit")]
public sealed class TemperatureRun
{
    [Field("7eab82cb6e2e4c108d39f02161dbd002",Required=true)] public string? Name {get;set;}
    [Field("7eab82cb6e2e4c108d39f02161dbd003",QuantityKind="Temperature",AllowedUnits=new[]{"degC","K"})] public Measurement? Temperature {get;set;}
}
