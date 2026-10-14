using Loom.Core;
using Loom.Sqlite;
using Loom.Demo;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class MigrationTests
{
    public static int Run(Catalog original, string temp)
    {
        var admin = new Actor("schema-admin", "Admin"); int count = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS migration: " + text); count++; }
        void Block(Action action, string text)
        { try { action(); throw new Exception("Expected refusal: " + text); } catch (Exception e) when (e is LoomException or InvalidOperationException or SqliteException) { Check(true, text); } }
        var path = Path.Combine(temp, "migration.db"); var cs = "Data Source=" + path;
        string assetId, workId, planId; Catalog accepted;
        using (var store = new SqliteStore(cs))
        {
            store.Migrate(original); var service = new DocumentService(original, store, [new WorkOrderHook()]);
            var asset = service.Create("Asset", new JsonObject { ["name"]="Meter", ["tag"]="KEEP-42", ["category"]="Electronics", ["location"]="Lab A", ["active"]=true }, admin); assetId=asset.Id;
            var work = service.Create("WorkOrder", new JsonObject { ["title"]="Linked job", ["asset"]=asset.Id, ["priority"]="Routine", ["due"]="2026-11-01", ["hours"]=2, ["rate"]=100 }, admin); workId=work.Id;
            var old=original.Get("Asset");
            var revised=old with { Version=2, Fields=[..old.Fields.Select(f=>f.Name=="tag"?f with {Name="serialNumber",Label="Serial number"}:f.Name=="location"?f with {Label="Room"}:f),new Field("warrantyExpires","Warranty expires","Date",Id:Guid.NewGuid().ToString("N"))] };
            var target=new Catalog([revised,original.Get("WorkOrder")]);
            var plan=store.Plan(target); planId=plan.Id;
            Check(plan.Sql.Length==3&&plan.Changes.Any(c=>c.Kind=="ChangeLabel"),"preview separates rename/addition from caption edit");
            Check(service.Get("Asset",asset.Id,admin).Values["tag"]!.ToString()=="KEEP-42","preview is read-only");
            Check(plan.Rows["Asset"]==1,"preview includes affected record count");
            Check(store.Plan(target).Id==plan.Id,"same source and target produce same plan ID");
            Block(()=>store.Apply(plan.Id,new Actor("editor","Editor")),"provider denies non-admin migration");
            // Deliberately fail after DDL and metadata writes: transaction must undo everything.
            using(var db=new SqliteConnection(cs)) { db.Open();using var c=db.CreateCommand();c.CommandText="CREATE TRIGGER fail_ledger BEFORE INSERT ON _migrations BEGIN SELECT RAISE(ABORT,'injected failure'); END;";c.ExecuteNonQuery(); }
            Block(()=>store.Apply(plan.Id,admin),"injected ledger failure aborts migration");
            Check(store.ReadCatalog()!.Get("Asset").Version==1,"failed apply restores metadata");
            Check(service.Get("Asset",asset.Id,admin).Values["tag"]!.ToString()=="KEEP-42"&&store.Migrations().Count==0,"failed apply restores columns, data and ledger");
            using(var db=new SqliteConnection(cs)) { db.Open();using var c=db.CreateCommand();c.CommandText="DROP TRIGGER fail_ledger";c.ExecuteNonQuery(); }
            accepted=store.Apply(plan.Id,admin); service=new DocumentService(accepted,store,[new WorkOrderHook()]);
            var after=service.Get("Asset",asset.Id,admin);
            Check(after.Values["serialNumber"]!.ToString()=="KEEP-42"&&!after.Values.ContainsKey("tag"),"rename preserves values under new field name");
            Check(after.Values["warrantyExpires"]==null,"new optional field is null for existing rows");
            Check(after.Id==asset.Id&&after.Version==asset.Version,"schema edit preserves record identity/version");
            Check(service.Get("WorkOrder",work.Id,admin).Values["asset"]!.ToString()==asset.Id,"linked records survive migration");
            Check(service.History("Asset",asset.Id,admin)[0].After!.Values["tag"]!.ToString()=="KEEP-42","historical snapshots are not rewritten");
            Check(store.Migrations().Single().Before.Single(d=>d.Name=="Asset").Fields.Any(f=>f.Name=="tag"),"ledger retains previous schema for history interpretation");
            Check(store.Migrations().Single().Actor==admin.Id,"ledger records migration actor");
            Check(SchemaIdentity.Hash(store.Apply(plan.Id,admin))==SchemaIdentity.Hash(accepted)&&store.Migrations().Count==1,"reapplying completed plan is idempotent");
            var a=accepted.Get("Asset");
            var future1=new Catalog([a with {Version=3,Label="Instruments"},accepted.Get("WorkOrder")]);
            var future2=new Catalog([a with {Version=3,Label="Equipment records"},accepted.Get("WorkOrder")]);
            var p1=store.Plan(future1);var p2=store.Plan(future2);accepted=store.Apply(p1.Id,admin);
            Block(()=>store.Apply(p2.Id,admin),"stale preview cannot overwrite a newer schema");
            a=accepted.Get("Asset");
            Block(()=>store.Plan(new Catalog([a with {Version=4,Fields=a.Fields.Where(f=>f.Name!="serialNumber").ToArray()},accepted.Get("WorkOrder")])),"field removal blocked");
            Block(()=>store.Plan(new Catalog([a with {Version=4,Fields=a.Fields.Select(f=>f.Name=="serialNumber"?f with {Type="Integer"}:f).ToArray()},accepted.Get("WorkOrder")])),"unsupported conversion blocked before mutation");
            Block(()=>store.Plan(new Catalog([a with {Version=4,Fields=[..a.Fields,new Field("site","Site","Text",Required:true)]},accepted.Get("WorkOrder")])),"required addition without backfill blocked");
            Block(()=>new Catalog([a with {Fields=a.Fields.Select(f=>f.Name=="serialNumber"?f with {Id=a.Fields[0].Id}:f).ToArray()},accepted.Get("WorkOrder")]),"duplicate permanent IDs rejected");
            // Protected hook fields cannot be renamed by the visual designer.
            var w=accepted.Get("WorkOrder");
            var edits=w.Fields.Select(f=>new FieldEdit(f.Id!,f.Name=="hours"?"duration":f.Name,f.Label)).ToArray();
            Block(()=>Designer.Edit(accepted,new DesignRequest(SchemaIdentity.Hash(accepted),"WorkOrder",edits,[]),[new WorkOrderHook()]),"C# hook dependency rename blocked");
            var typeEdits=w.Fields.Select(f=>new FieldEdit(f.Id!,f.Name,f.Label,Type:f.Name=="hours"?"Integer":f.Type)).ToArray();
            Block(()=>Designer.Edit(accepted,new DesignRequest(SchemaIdentity.Hash(accepted),"WorkOrder",typeEdits,[]),[new WorkOrderHook()]),"C# hook dependency type change blocked");
        }
        using (var reopened=new SqliteStore(cs))
        {
            var c=reopened.ReadCatalog()!;var service=new DocumentService(c,reopened,[new WorkOrderHook()]);
            Check(c.Get("Asset").Fields.Any(f=>f.Name=="serialNumber"),"accepted model survives database reopening");
            Check(service.Get("Asset",assetId,admin).Values["serialNumber"]!.ToString()=="KEEP-42"&&service.Get("WorkOrder",workId,admin).Values["asset"]!.ToString()==assetId,"reopening preserves renamed values and links");
            Check(reopened.Migrations().Count==2,"migration ledger survives reopening");
            using(var db=new SqliteConnection(cs)) { db.Open();using var q=db.CreateCommand();q.CommandText="ALTER TABLE Asset ADD COLUMN unexpected TEXT";q.ExecuteNonQuery(); }
            Block(()=>reopened.Plan(c),"out-of-band physical column drift detected");
        }
        var legacy=Path.Combine(temp,"legacy.db");
        using(var s=new SqliteStore("Data Source="+legacy))s.Migrate(original);
        using(var db=new SqliteConnection("Data Source="+legacy))
        {
            db.Open();foreach(var d in original.Types.Values)
            {
                var v=JsonSerializer.SerializeToNode(d,Catalog.Json)!.AsObject();v.Remove("id");foreach(var f in v["fields"]!.AsArray())f!.AsObject().Remove("id");
                using var c=db.CreateCommand();c.CommandText="UPDATE _schemas SET json=$json WHERE name=$name";c.Parameters.AddWithValue("$json",v.ToJsonString());c.Parameters.AddWithValue("$name",d.Name);c.ExecuteNonQuery();
            }
        }
        using(var s=new SqliteStore("Data Source="+legacy))
        {
            s.Migrate(original);Check(s.ReadCatalog()!.Get("Asset").Fields.All(f=>f.Id!=null),"legacy exact-name adoption provides stable IDs");
            using var db=new SqliteConnection("Data Source="+legacy);db.Open();using var c=db.CreateCommand();c.CommandText="SELECT json FROM _schemas WHERE name='Asset'";
            Check(JsonSerializer.Deserialize<DocType>((string)c.ExecuteScalar()!,Catalog.Json)!.Fields.All(f=>f.Id!=null),"legacy adoption IDs are persisted");
        }
        return count;
    }
}
