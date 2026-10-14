using Loom.Core;
using Loom.Sqlite;
using Loom.Demo.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

static class AuthoringReportTests
{
    public static int Run(Catalog baseline)
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS authoring/report: " + message); }
        var catalog = AttributeCatalog.From(typeof(AssetModel), typeof(WorkOrderModel));
        Check(SchemaIdentity.Hash(catalog) == SchemaIdentity.Hash(baseline), "C# attributes and JSON produce identical catalogs");
        var type = catalog.Get("Asset"); var field = type.Fields.Single(f => f.Name == "tag");
        var report = new SavedReport("Test", type.Id!, [new(field.Id!)], field.Id, Filter: new(field.Id!, "TEST-1"));
        var serialized = JsonSerializer.Serialize(report, Catalog.Json);
        var path = Path.Combine(Path.GetTempPath(), "loom-report-"+Guid.NewGuid().ToString("N")+".db");
        var actor = new Actor("test", "Admin");
        try
        {
            using (var store = new SqliteStore("Pooling=False;Data Source=" + path))
            {
                store.Migrate(catalog); var service = new DocumentService(catalog, store, []);
                service.Create("Asset", new JsonObject { ["name"]="Scope", ["tag"]="TEST-1", ["category"]="Electronics", ["location"]="Lab", ["active"]=true }, actor);
                var before = SavedReports.Run(report, catalog, service, actor);
                Check(before.Total == 1 && before.Rows[0]![0]!.ToString() == "TEST-1", "saved report resolves field identity");
                var target = new Catalog(catalog.Types.Values.Select(t => t.Id != type.Id ? t : t with {Version=2,Fields=t.Fields.Select(f => f.Id != field.Id ? f : f with {Name="serialNumber",Label="Serial number"}).ToArray()}));
                store.Apply(store.Plan(target).Id, actor); catalog = target;
                service = new DocumentService(catalog, store, []);
                var after = SavedReports.Run(JsonSerializer.Deserialize<SavedReport>(serialized, Catalog.Json)!, catalog, service, actor);
                Check(after.Rows.ToJsonString() == before.Rows.ToJsonString(), "unchanged serialized report retains values/filter/sort through rename");
                Check(after.Columns[0].Name == "serialNumber" && after.Columns[0].Label == "Serial number", "readable current names and captions");
                var explicitHeading = report with {Columns=[new(field.Id!, "Asset identifier")]};
                Check(SavedReports.Run(explicitHeading,catalog,service,actor).Columns[0].Label == "Asset identifier", "explicit report headings preserved");
                try { SavedReports.Run(report,catalog,service,new Actor("x","Unknown")); throw new Exception("Expected denial"); }
                catch (LoomException e) { Check(e.Status == 403, "report cannot bypass document permissions"); }
                try { SavedReports.Run(report with {Columns=[new(Guid.NewGuid().ToString("N"))]},catalog,service,actor); throw new Exception("Expected invalid reference"); }
                catch (LoomException e) { Check(e.Status == 422, "missing report identity fails explicitly"); }
            }
            using (var reopened = new SqliteStore("Pooling=False;Data Source="+path))
            {
                var accepted = reopened.ReadCatalog()!;
                Check(SavedReports.Run(JsonSerializer.Deserialize<SavedReport>(serialized,Catalog.Json)!,accepted,new DocumentService(accepted,reopened,[]),actor).Total==1, "saved report survives database reopen");
            }
            try { AttributeCatalog.From(typeof(BadModel)); throw new Exception("Expected unsupported CLR type"); }
            catch (InvalidOperationException) { Check(true,"unsupported CLR type rejected"); }
            var a=AttributeCatalog.From(typeof(BeforeModel)).Get("Example");
            var b=AttributeCatalog.From(typeof(AfterModel)).Get("Example");
            Check(a.Fields[0].Id==b.Fields[0].Id && a.Fields[0].Name!=b.Fields[0].Name, "C# property rename retains explicitly declared identity");
        }
        finally { foreach(var file in new[]{path,path+"-wal",path+"-shm"}) if(File.Exists(file)) File.Delete(file); }
        return count;
    }
    [Document("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", nameof(Value))]
    private class BadModel { [Field("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")] public double Value {get;set;} }
    [Document("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",nameof(SerialNumber),Name="Example")]
    private class BeforeModel { [Field("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")] public string? SerialNumber {get;set;} }
    [Document("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",nameof(AssetTag),Name="Example",Version=2)]
    private class AfterModel { [Field("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")] public string? AssetTag {get;set;} }
}
