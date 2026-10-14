using Loom.Core;
using Loom.Sqlite;
using Loom.Demo;
using System.Text.Json.Nodes;
using System.Text.Json;

var root = args.Length > 0 ? args[0] : ".";
var catalog = Catalog.Load(Path.Combine(root, "src/Loom.Demo/Metadata"));
var admin = new Actor("alice", "Admin");
var viewer = new Actor("bob", "Viewer");
var editor = new Actor("charlie", "Editor");
int count = AuthoringReportTests.Run(catalog) + ReportWriterTests.Run(catalog) + ScienceTests.Run();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); count++; }
void Reject(int status, Action action, string message)
{
    try { action(); throw new Exception("Expected rejection: " + message); }
    catch (LoomException e) { Check(e.Status == status, message); }
}
JsonObject Asset(string tag) => new() { ["name"]="Test equipment", ["tag"]=tag, ["category"]="Electronics", ["location"]="Lab", ["active"]=true };
JsonObject Work(string id, decimal hours=2) => new() { ["title"]="Service", ["asset"]=id, ["priority"]="High", ["due"]="2026-11-02", ["hours"]=hours, ["rate"]=125 };
var temp = Path.Combine(Path.GetTempPath(), "loom-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
try
{
    foreach(var provider in new[]{"Memory","Sqlite"})
    {
        using IDocumentStore store = provider == "Memory" ? new MemoryStore() : new SqliteStore("Data Source="+Path.Combine(temp,"test.db"));
        store.Migrate(catalog);
        var service = new DocumentService(catalog,store,[new WorkOrderHook()]);
        var asset = service.Create("Asset",Asset("A-001"),admin);
        Check(service.Get("Asset",asset.Id,viewer).Id==asset.Id,provider+": read permission");
        Reject(403,()=>service.Create("Asset",Asset("X"),viewer),provider+": read-only role rejects write");
        Reject(403,()=>service.History("Asset",asset.Id,viewer),provider+": audit permission");
        Reject(403,()=>service.Delete("Asset",asset.Id,1,editor),provider+": editor cannot delete");
        Reject(409,()=>service.Create("Asset",Asset("A-001"),admin),provider+": uniqueness");
        var bad=Asset("A-002");bad.Remove("name");Reject(422,()=>service.Create("Asset",bad,admin),provider+": required field");
        bad=Asset("A-002");bad["category"]="Invalid";Reject(422,()=>service.Create("Asset",bad,admin),provider+": select options");
        bad=Asset("A-002");bad["surprise"]="x";Reject(400,()=>service.Create("Asset",bad,admin),provider+": unknown field");
        Reject(422,()=>service.Create("WorkOrder",Work("missing"),admin),provider+": missing link");
        Reject(422,()=>service.Create("WorkOrder",Work(asset.Id,-1),admin),provider+": C# validation hook");
        var work=service.Create("WorkOrder",Work(asset.Id),editor);
        Check(work.Values["estimate"]!.GetValue<decimal>()==250,provider+": C# calculation hook");
        var tamper=Work(asset.Id);tamper["estimate"]=1;Reject(400,()=>service.Update("WorkOrder",work.Id,1,tamper,admin),provider+": read-only field tampering");
        Reject(409,()=>service.Delete("Asset",asset.Id,1,admin),provider+": referenced delete blocked");
        var revised=service.Update("WorkOrder",work.Id,1,Work(asset.Id,3),admin);
        Check(revised.Version==2&&revised.Values["estimate"]!.GetValue<decimal>()==375,provider+": update version and calculation");
        Reject(409,()=>service.Update("WorkOrder",work.Id,1,Work(asset.Id,8),admin),provider+": stale save rejected");
        Check(service.Get("WorkOrder",work.Id,admin).Values["estimate"]!.GetValue<decimal>()==375,provider+": stale save leaves data unchanged");
        Reject(409,()=>service.Transition("WorkOrder",work.Id,2,"cancel",admin),provider+": invalid transition");
        var submitted=service.Transition("WorkOrder",work.Id,2,"submit",editor);
        Reject(409,()=>service.Update("WorkOrder",work.Id,3,Work(asset.Id),admin),provider+": submitted document locked");
        Reject(403,()=>service.Transition("WorkOrder",work.Id,3,"cancel",editor),provider+": cancellation permission");
        var cancelled=service.Transition("WorkOrder",work.Id,3,"cancel",admin);
        Check(cancelled.State=="Cancelled"&&cancelled.Version==4,provider+": lifecycle complete");
        Check(service.History("WorkOrder",work.Id,admin).Count==4,provider+": audit only successful operations");
        service.History("WorkOrder",work.Id,admin)[0].After!.Values["title"]="tampered";
        Check(service.History("WorkOrder",work.Id,admin)[0].After!.Values["title"]!.ToString()=="Service",provider+": returned audit snapshots cannot mutate stored history");
        Check(service.List("Asset",viewer,search:"Test",field:"category",equals:"Electronics").Total==1,provider+": search and filter");
        Reject(400,()=>service.List("Asset",viewer,sort:"name; DROP TABLE Asset"),provider+": query field allowlist");
        Reject(400,()=>service.List("Asset",viewer,take:999),provider+": bounded pagination");
        var countBefore=service.List("Asset",admin).Total;
        try { store.Transaction<int>(s=>{s.Insert(asset with {Id="rollback",Values=Asset("ROLLBACK")});s.AppendAudit(new("rollback-audit","Asset","rollback","test","create","now",null,asset));throw new InvalidOperationException("fail after write and audit");}); }
        catch(InvalidOperationException) { }
        Check(service.List("Asset",admin).Total==countBefore&&service.History("Asset","rollback",admin).Count==0,provider+": transaction rolls back record and audit");
        var quoted=Asset("SQL'--");quoted["name"]="<script>alert('x')</script>";var safe=service.Create("Asset",quoted,admin);
        Check(service.Get("Asset",safe.Id,admin).Values["tag"]!.ToString()=="SQL'--",provider+": SQL values parameterized");
        service.Delete("Asset",safe.Id,1,admin);
        Check(service.History("Asset",safe.Id,admin).Last().Action=="delete",provider+": audit survives deletion");
        var values=Asset("A-001");values["location"]="Updated lab";
        int wins=0,conflicts=0;
        Parallel.For(0,8,_=>{try{service.Update("Asset",asset.Id,1,values,admin);Interlocked.Increment(ref wins);}catch(LoomException e)when(e.Status==409){Interlocked.Increment(ref conflicts);}});
        Check(wins==1&&conflicts==7,provider+": concurrent writers cannot overwrite each other");
    }
    using(var store=new SqliteStore("Data Source="+Path.Combine(temp,"test.db")))
    {
        store.Migrate(catalog);var service=new DocumentService(catalog,store,[new WorkOrderHook()]);
        Check(service.List("Asset",admin).Total==1&&service.List("WorkOrder",admin).Total==1,"SQLite: persistence after reopening");
        var asset=catalog.Get("Asset");var expanded=asset with {Version=2,Fields=[..asset.Fields,new Field("notes","Notes","Text")]};
        var next=new Catalog([expanded,catalog.Get("WorkOrder")]);store.Migrate(next);
        Check(new DocumentService(next,store,[]).List("Asset",admin).Items[0].Values.ContainsKey("notes"),"SQLite: additive migration preserves records");
        store.Migrate(next);Check(true,"SQLite: migration is idempotent");
        try{store.Migrate(catalog);throw new Exception("Expected downgrade refusal");}catch(InvalidOperationException){Check(true,"SQLite: schema downgrade refused");}
        var destructive=expanded with {Version=3,Fields=expanded.Fields.Skip(1).ToArray(),TitleField="tag"};
        try{store.Migrate(new Catalog([destructive,catalog.Get("WorkOrder")]));throw new Exception("Expected destructive refusal");}catch(InvalidOperationException){Check(true,"SQLite: destructive migration refused");}
    }
    TypeGenerator.Generate(catalog,Path.Combine(temp,"Generated"));
    Check(File.ReadAllText(Path.Combine(temp,"Generated","WorkOrder.g.cs")).Contains("decimal? @estimate"),"Generator: decimal CLR type");
    Check(File.ReadAllText(Path.Combine(temp,"Generated","WorkOrder.g.cs")).Contains("DateOnly? @due"),"Generator: date CLR type");
    count += ReportDesignTests.Run(catalog, temp);
    count += TypedRepositoryTests.Run(catalog, temp);
    count += MigrationTests.Run(catalog, temp);
    count += BackfillTests.Run(catalog, temp);
    count += RequiredFieldTests.Run(temp);
    count += ConversionTests.Run(temp);
    count += DataTypeTests.Run(temp);
    count += MeasurementTests.Run(temp);
    count += ScienceFoundationTests.Run(temp);
    count += QueryTests.Run(temp);
    Console.WriteLine($"\n{count} checks passed.");
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(temp,true); }
