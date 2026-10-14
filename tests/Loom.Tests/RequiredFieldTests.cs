using Loom.Core;
using Loom.Sqlite;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

static class RequiredFieldTests
{
 public static int Run(string temp)
 {
  int checks=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS required: "+message);}
  void Reject(Action action,string message){try{action();throw new Exception("Expected refusal");}catch(LoomException e){Check(e.Status==422,message);}}
  var actor=new Actor("admin","Admin");
  var source=new Catalog([new DocType("Item","Items",1,"name",false,[new Field("name","Name","Text",Required:true),new Field("site","Site","Text"),new Field("enabled","Enabled","Boolean"),new Field("amount","Amount","Integer")],new(){{"Admin",["read","create","write","audit"]}})]);
  var item=source.Get("Item");var site=item.Fields.Single(f=>f.Name=="site");
  Catalog Target(bool rename=false)=>new([item with{Version=2,Fields=item.Fields.Select(f=>f.Id==site.Id?f with{Required=true,Name=rename?"campus":"site"}:f).ToArray()}]);
  var path=Path.Combine(temp,"required.db");var cs="Data Source="+path;
  void Sql(string sql){using var db=new SqliteConnection(cs);db.Open();using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery();}
  using(var store=new SqliteStore(cs))
  {
   store.Migrate(source);var service=new DocumentService(source,store,[]);
   var known=service.Create("Item",new JsonObject{["name"]="Known",["site"]="Annex",["enabled"]=false,["amount"]=0},actor);
   var missing=service.Create("Item",new JsonObject{["name"]="Missing"},actor);
   Reject(()=>store.Plan(Target()),"missing values require explicit backfill");
   Reject(()=>store.Plan(Target(),[new(site.Id!,JsonValue.Create(" "))]),"empty backfill rejected");
   var plan=store.Plan(Target(true),[new(site.Id!,JsonValue.Create("Main"))]);
   Check(plan.Changes.Any(c=>c.Kind=="RequireField"&&c.Description.Contains("1 missing")),"preview reports missing count");
   Check(service.Get("Item",missing.Id,actor).Values["site"]==null,"preview does not fill values");
   Sql("UPDATE Item SET site=' ' WHERE name='Known'");
   Reject(()=>store.Apply(plan.Id,actor),"invalid non-null data introduced after preview blocks apply");
   Check(store.ReadCatalog()!.Get("Item").Version==1&&store.Migrations().Count==0,"failed preflight leaves schema and ledger unchanged");
   Sql("UPDATE Item SET site='Annex' WHERE name='Known'");
   var next=store.Apply(plan.Id,actor);service=new DocumentService(next,store,[]);
   Check(service.Get("Item",known.Id,actor).Values["campus"]!.ToString()=="Annex","non-null value survives simultaneous rename and required change");
   Check(service.Get("Item",missing.Id,actor).Values["campus"]!.ToString()=="Main","only null receives backfill");
   Check(service.Get("Item",known.Id,actor).Version==known.Version,"record version preserved");
   Check(service.History("Item",missing.Id,actor)[0].After!.Values["site"]==null,"audit snapshot retains original null and field name");
   Reject(()=>service.Create("Item",new JsonObject{["name"]="Incomplete"},actor),"new record must supply required value");
   // Two optional scalar types become required together; false and zero are populated values.
   var current=next.Get("Item");var numeric=new Catalog([current with{Version=3,Fields=current.Fields.Select(f=>f.Name is "enabled" or "amount"?f with{Required=true}:f).ToArray()}]);
   var fills=current.Fields.Where(f=>f.Name is "enabled" or "amount").Select(f=>new MigrationBackfill(f.Id!,f.Name=="enabled"?JsonValue.Create(true):JsonValue.Create(99))).ToArray();
   next=store.Apply(store.Plan(numeric,fills).Id,actor);service=new DocumentService(next,store,[]);
   Check(!service.Get("Item",known.Id,actor).Values["enabled"]!.GetValue<bool>()&&service.Get("Item",known.Id,actor).Values["amount"]!.GetValue<long>()==0,"false and zero are preserved, not treated as missing");
   Check(service.Get("Item",missing.Id,actor).Values["enabled"]!.GetValue<bool>()&&service.Get("Item",missing.Id,actor).Values["amount"]!.GetValue<long>()==99,"Boolean and Integer nulls are backfilled");
  }
  using(var reopened=new SqliteStore(cs)){var next=reopened.ReadCatalog()!;Check(next.Get("Item").Fields.Single(f=>f.Name=="campus").Required,"required constraint survives reopen");}
  using(var store=new SqliteStore("Data Source="+Path.Combine(temp,"complete.db")))
  {
   store.Migrate(source);var service=new DocumentService(source,store,[]);
   service.Create("Item",new JsonObject{["name"]="Complete",["site"]="West"},actor);
   var plan=store.Plan(Target());Check(plan.Backfills!.Length==0,"fully populated field needs no backfill");
   var late=service.Create("Item",new JsonObject{["name"]="Late"},actor);
   Reject(()=>store.Apply(plan.Id,actor),"null inserted after no-backfill preview blocks apply");
   Check(store.Migrations().Count==0,"failed apply records no receipt");
   service.Update("Item",late.Id,late.Version,new JsonObject{["name"]="Late",["site"]="East"},actor);
   var next=store.Apply(plan.Id,actor);Check(next.Get("Item").Fields.Single(f=>f.Id==site.Id).Required,"same reviewed no-backfill plan succeeds after correction");
   var values=new DocumentService(next,store,[]).List("Item",actor).Items;
   Check(values.Any(d=>d.Values["site"]!.ToString()=="West")&&values.Any(d=>d.Values["site"]!.ToString()=="East"),"no-backfill transition preserves all populated values");
  }
  return checks;
 }
}
