using Loom.Core;
using Loom.Sqlite;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

static class BackfillTests
{
 public static int Run(Catalog original,string temp)
 {
  int count=0;var admin=new Actor("backfill-admin","Admin");
  void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;Console.WriteLine("PASS backfill: "+message);}
  void Reject(Action action,string message){try{action();throw new Exception("Expected refusal");}catch(Exception e)when(e is LoomException or InvalidOperationException or SqliteException){Check(true,message);}}
  var path=Path.Combine(temp,"backfills.db");var cs="Pooling=False;Data Source="+path;
  void Sql(string sql){using var db=new SqliteConnection(cs);db.Open();using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery();}
  string assetId;
  using(var store=new SqliteStore(cs))
  {
   store.Migrate(original);var service=new DocumentService(original,store,[]);
   var asset=service.Create("Asset",new JsonObject{["name"]="Meter",["tag"]="B-1",["location"]="Lab",["category"]="Electronics",["active"]=true},admin);assetId=asset.Id;
   var old=original.Get("Asset");
   Field Added(string name,string type)=>new(name,name,type,Required:true,Id:Guid.NewGuid().ToString("N"));
   var site=Added("site","Text");var cycles=Added("cycles","Integer");var cost=Added("cost","Decimal");var verified=Added("verified","Boolean");var since=Added("since","Date");
   var target=new Catalog(original.Types.Values.Select(t=>t.Id!=old.Id?t:t with{Version=2,Fields=[..t.Fields.Select(f=>f.Name=="tag"?f with{Name="serial"}:f),site,cycles,cost,verified,since,new Field("notes","Notes","Text",Id:Guid.NewGuid().ToString("N"))]}));
   var text="O'Brien'); DROP TABLE Asset; --";
   MigrationBackfill[] fills=[new(site.Id!,JsonValue.Create(text)),new(cycles.Id!,JsonValue.Create(long.MaxValue)),new(cost.Id!,JsonValue.Create(12.34m)),new(verified.Id!,JsonValue.Create(false)),new(since.Id!,JsonValue.Create("2026-10-14"))];
   Reject(()=>store.Plan(target),"required fields need explicit backfills");
   Reject(()=>store.Plan(target,[..fills,new(site.Id!,JsonValue.Create("duplicate"))]),"duplicate backfill refused");
   Reject(()=>store.Plan(target,[..fills,new(old.Fields[0].Id!,JsonValue.Create("overwrite"))]),"existing field overwrite refused");
   foreach(var (index,value) in new (int,JsonNode?)[]{(0,null),(0,JsonValue.Create(" ")),(1,JsonValue.Create("1.5")),(2,JsonValue.Create("not a number")),(3,JsonValue.Create("maybe")),(4,JsonValue.Create("2026-02-30")),(0,new JsonObject())})
   {var bad=fills.ToArray();bad[index]=bad[index] with{Value=value};Reject(()=>store.Plan(target,bad),"invalid backfill value refused "+index+" "+value);}
   var plan=store.Plan(target,fills);
   Check(plan.Rows["Asset"]==1&&plan.Changes.Count(c=>c.Kind=="BackfillField")==5,"preview reports affected rows and each backfill");
   Check(service.Get("Asset",assetId,admin).Values["tag"]!.ToString()=="B-1","preview leaves records unchanged");
   var other=fills.ToArray();other[0]=other[0] with{Value=JsonValue.Create("Elsewhere")};
   Check(store.Plan(target,other).Id!=plan.Id,"backfill value is part of plan identity");
   Check(store.Plan(target,fills.Reverse().ToArray()).Id==plan.Id,"backfill ordering does not change identity");
   plan.Backfills![0]=new(site.Id!,JsonValue.Create("mutated"));
   Reject(()=>store.Apply(plan.Id,new Actor("e","Editor")),"non-admin apply refused");
   Sql("CREATE TRIGGER fail_ledger BEFORE INSERT ON _migrations BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
   Reject(()=>store.Apply(plan.Id,admin),"ledger failure rolls back rebuild");
   Check(store.ReadCatalog()!.Get("Asset").Version==1&&service.Get("Asset",assetId,admin).Values["tag"]!.ToString()=="B-1"&&store.Migrations().Count==0,"rollback preserves schema data and ledger");
   Sql("DROP TRIGGER fail_ledger");
   // A record inserted after preview must also receive the reviewed backfills.
   service.Create("Asset",new JsonObject{["name"]="Later",["tag"]="B-2",["location"]="Lab",["category"]="Electronics",["active"]=true},admin);
   var accepted=store.Apply(plan.Id,admin);service=new DocumentService(accepted,store,[]);
   var after=service.Get("Asset",assetId,admin);
   Check(after.Values["site"]!.ToString()==text,"SQL-looking text is literal; caller mutation cannot alter pending plan");
   Check(after.Values["cycles"]!.GetValue<long>()==long.MaxValue&&after.Values["cost"]!.GetValue<decimal>()==12.34m&&!after.Values["verified"]!.GetValue<bool>()&&after.Values["since"]!.ToString()=="2026-10-14","scalar types retain exact values");
   Check(after.Values["serial"]!.ToString()=="B-1"&&after.Values["notes"]==null&&after.Id==assetId&&after.Version==asset.Version,"combined rename and optional addition preserve identity and version");
   Check(service.List("Asset",admin).Items.All(d=>d.Values["site"]!.ToString()==text),"apply covers records added after preview");
   Check(service.History("Asset",assetId,admin)[0].After!.Values["tag"]!.ToString()=="B-1","historical snapshots remain unchanged");
   Check(store.Migrations().Single().Backfills!.Length==5,"ledger records explicit backfills");
   Check(SchemaIdentity.Hash(store.Apply(plan.Id,admin))==SchemaIdentity.Hash(accepted)&&store.Migrations().Count==1,"apply remains idempotent");
   Reject(()=>service.Create("Asset",new JsonObject{["name"]="Missing",["serial"]="B-3",["location"]="Lab",["category"]="Electronics",["active"]=true},admin),"future records must supply required values");
   Sql("CREATE INDEX custom_site ON Asset(site)");
   var extra=Added("department","Text");var next=new Catalog(accepted.Types.Values.Select(t=>t.Id!=old.Id?t:t with{Version=3,Fields=[..t.Fields,extra]}));
   Reject(()=>store.Plan(next,[new(extra.Id!,JsonValue.Create("Lab"))]),"custom indexes block rebuild instead of being lost");Sql("DROP INDEX custom_site");
   Sql("CREATE TABLE external_child(id TEXT REFERENCES Asset(id) ON DELETE CASCADE)");
   Reject(()=>store.Plan(next,[new(extra.Id!,JsonValue.Create("Lab"))]),"external foreign keys block rebuild");Sql("DROP TABLE external_child");
   Sql("CREATE TRIGGER custom_change AFTER UPDATE ON Asset BEGIN SELECT 1; END");
   Reject(()=>store.Plan(next,[new(extra.Id!,JsonValue.Create("Lab"))]),"custom triggers block rebuild");Sql("DROP TRIGGER custom_change");
   store.Apply(store.Plan(next,[new(extra.Id!,JsonValue.Create("Lab"))]).Id,admin);
   Check(store.ReadCatalog()!.Get("Asset").Version==3,"successive rebuild preserves framework indexes");
   using var db=new SqliteConnection(cs);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM pragma_table_info('Asset') WHERE name='site' AND \"notnull\"=1 AND dflt_value IS NULL";
   Check((long)cmd.ExecuteScalar()! ==1,"physical required constraint has no permanent default");
   Reject(()=>Sql("UPDATE Asset SET serial='B-1' WHERE serial='B-2'"),"unique indexes survive rebuild");
  }
  using(var reopened=new SqliteStore(cs))
  {var catalog=reopened.ReadCatalog()!;Check(new DocumentService(catalog,reopened,[]).Get("Asset",assetId,admin).Values["department"]!.ToString()=="Lab","required values survive database reopen");}
  return count;
 }
}
