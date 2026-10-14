using Loom.Core;
using Loom.Sqlite;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

static class ConversionTests
{
 public static int Run(string temp)
 {
  int checks=0;var actor=new Actor("migration","Admin");
  void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS conversion: "+message);}
  void Reject(Action action,string message){try{action();throw new Exception("Expected refusal");}catch(LoomException e){Check(e.Status==422,message);}}
  var initial=new Catalog([new DocType("Reading","Readings",1,"name",false,[new Field("name","Name","Text",Required:true),new Field("value","Value","Integer")],new(){{"Admin",["read","create","write","audit"]}})]);
  var field=initial.Get("Reading").Fields.Single(f=>f.Name=="value");
  Catalog Change(Catalog source,string type,bool required=false,string name="value")=>new(source.Types.Values.Select(t=>t with{Version=t.Version+1,Fields=t.Fields.Select(f=>f.Id==field.Id?f with{Type=type,Required=required,Name=name}:f).ToArray()}));
  var cs="Data Source="+Path.Combine(temp,"conversions.db");
  void Sql(string sql){using var db=new SqliteConnection(cs);db.Open();using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery();}
  using(var store=new SqliteStore(cs))
  {
   store.Migrate(initial);var service=new DocumentService(initial,store,[]);
   var ids=new List<string>();
   foreach(var value in new long?[]{long.MinValue,long.MaxValue,9007199254740993,0,null})
    ids.Add(service.Create("Reading",new JsonObject{["name"]="R"+ids.Count,["value"]=value},actor).Id);
   var target=Change(initial,"Decimal");var plan=store.Plan(target);
   Check(plan.Changes.Any(c=>c.Kind=="ConvertField"&&c.Description.Contains("4 populated")),"preview reports conversion and populated count");
   Check(service.Get("Reading",ids[1],actor).Values["value"]!.GetValue<long>()==long.MaxValue,"preview leaves integer values unchanged");
   var accepted=store.Apply(plan.Id,actor);service=new DocumentService(accepted,store,[]);
   Check(service.Get("Reading",ids[0],actor).Values["value"]!.GetValue<decimal>()==long.MinValue&&service.Get("Reading",ids[1],actor).Values["value"]!.GetValue<decimal>()==long.MaxValue,"Integer to Decimal preserves signed 64-bit endpoints");
   Check(service.Get("Reading",ids[2],actor).Values["value"]!.GetValue<decimal>()==9007199254740993m,"Integer to Decimal preserves values above double exact range");
   Check(service.Get("Reading",ids[4],actor).Values["value"]==null,"optional null preserved");
   Check(store.Migrations().Single().Changes.Any(c=>c.Kind=="ConvertField"),"conversion recorded in migration ledger");
   var back=Change(accepted,"Integer");var reverse=store.Plan(back);
   foreach(var (value,label) in new[]{("1.5","fractional value"),("9223372036854775808","positive overflow"),("-9223372036854775809","negative overflow"),("1.0000000000000000000000000000001","tiny fraction beyond Decimal parser precision"),("1e3","noncanonical exponent"),("invalid","malformed text")})
   {
    Sql("UPDATE Reading SET value='"+value+"' WHERE name='R3'");
    Reject(()=>store.Plan(back),label+" rejected during preview");
   }
   Reject(()=>store.Apply(reverse.Id,actor),"apply rechecks changed data under previously valid plan");
   Check(store.ReadCatalog()!.Get("Reading").Fields.Single(f=>f.Id==field.Id).Type=="Decimal"&&store.Migrations().Count==1,"failed conversion changes neither schema nor ledger");
   Sql("UPDATE Reading SET value='0.000' WHERE name='R3'");
   Sql("CREATE TRIGGER fail_conversion BEFORE INSERT ON _migrations BEGIN SELECT RAISE(ABORT,'test rollback'); END");
   try{store.Apply(reverse.Id,actor);throw new Exception("Expected ledger failure");}catch(SqliteException){Check(true,"failure after conversion triggers rollback");}
   Check(store.ReadCatalog()!.Get("Reading").Fields.Single(f=>f.Id==field.Id).Type=="Decimal"&&store.Migrations().Count==1,"rollback restores original schema and ledger");
   Check(new DocumentService(accepted,store,[]).Get("Reading",ids[1],actor).Values["value"]!.GetValue<decimal>()==long.MaxValue,"rollback preserves original exact data");
   Sql("DROP TRIGGER fail_conversion");
   accepted=store.Apply(reverse.Id,actor);service=new DocumentService(accepted,store,[]);
   Check(service.Get("Reading",ids[0],actor).Values["value"]!.GetValue<long>()==long.MinValue&&service.Get("Reading",ids[1],actor).Values["value"]!.GetValue<long>()==long.MaxValue,"Decimal to Integer preserves both endpoints");
   Check(service.Get("Reading",ids[3],actor).Values["value"]!.GetValue<long>()==0,"whole Decimal with zero fractional digits accepted");
   Check(service.Get("Reading",ids[1],actor).Version==1&&service.History("Reading",ids[1],actor).Count==1,"conversion preserves record version and audit history");
   var combined=Change(accepted,"Decimal",true,"measurement");
   var combinedPlan=store.Plan(combined,[new(field.Id!,JsonValue.Create(2.5m))]);
   accepted=store.Apply(combinedPlan.Id,actor);service=new DocumentService(accepted,store,[]);
   Check(service.Get("Reading",ids[4],actor).Values["measurement"]!.GetValue<decimal>()==2.5m,"conversion rename and required backfill combine atomically");
   Check(service.Get("Reading",ids[2],actor).Values["measurement"]!.GetValue<decimal>()==9007199254740993m,"combined conversion preserves populated values");
   Check(SchemaIdentity.Hash(store.Apply(combinedPlan.Id,actor))==SchemaIdentity.Hash(accepted),"conversion apply is idempotent");
   Reject(()=>store.Plan(Change(accepted,"Text",true,"measurement")),"unsupported conversion rejected");
  }
  using(var reopened=new SqliteStore(cs)){var accepted=reopened.ReadCatalog()!;Check(accepted.Get("Reading").Fields.Single(f=>f.Id==field.Id).Type=="Decimal","converted model survives reopen");}
  return checks;
 }
}
