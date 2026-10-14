using Loom.Core;
using Loom.Sqlite;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

static class DataTypeTests
{
 public static int Run(string temp)
 {
  int count=0;var actor=new Actor("types","Admin");
  void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;Console.WriteLine("PASS data types: "+message);}
  void Reject(Action action,string message){try{action();throw new Exception("Expected refusal: "+message);}catch(LoomException e){Check(e.Status==422,message);}}
  Check(FieldValues.SumExact([0.1m,0.2m])==0.3m,"exact decimal sum");
  Reject(()=>FieldValues.SumExact([7.9999999999999999999999999999m,0.0000000000000000000000000001m,0.0000000000000000000000000001m]),"sum cannot silently discard low digits");
  var money=new Field("cost","Cost","Money",Currency:"USD");
  Check(FieldValues.Decimal("12345678901234567.89",money)==12345678901234567.89m,"money preserves all configured digits");
  Reject(()=>FieldValues.Decimal("1.005",money),"money rejects implicit rounding");
  Check(FieldValues.Decimal("1.005",money with{Rounding="even"})==1.00m&&FieldValues.Decimal("-1.005",money with{Rounding="away"})==-1.01m,"explicit tie rounding in both signs");
  Reject(()=>FieldValues.Decimal("999.995",money with{Precision=5,Scale=2,Rounding="away"}),"rounding carry cannot overflow precision");
  Reject(()=>FieldValues.Decimal("0.00000000000000000000000000001",new("d","D","Decimal")),"tiny nonrepresentable decimal rejected");
  foreach(var bad in new[]{"1e2","1,234.50","NaN"})Reject(()=>FieldValues.Decimal(bad,money),"reject ambiguous numeric input "+bad);
  var bytes=Enumerable.Repeat((byte)42,8192).ToArray();
  foreach(var mode in new[]{"auto","none","brotli"}){var encoded=BlobCodec.Encode(bytes,mode);Check(BlobCodec.Decode(encoded,8192).SequenceEqual(bytes),mode+" byte-exact codec roundtrip");if(mode!="none")Check(encoded.Length<bytes.Length/4,"compressible data actually shrinks "+mode);}
  var random=new byte[4096];new Random(7).NextBytes(random);Check(BlobCodec.Encode(random)[4]==0,"auto retains incompressible data raw");
  var corrupt=BlobCodec.Encode(bytes);corrupt[9]^=1;Reject(()=>BlobCodec.Decode(corrupt,8192),"checksum corruption rejected");
  Reject(()=>BlobCodec.Decode(BlobCodec.Encode(bytes),100),"decoded length bounded before allocation");
  Reject(()=>BlobCodec.Decode(BlobCodec.Encode(bytes)[..^2],8192),"truncated compressed stream rejected");
  Reject(()=>BlobCodec.Decode([..BlobCodec.Encode(bytes),0],8192),"trailing compressed bytes rejected");
  Check(BlobCodec.Decode(BlobCodec.Encode([],"brotli"),0).Length==0,"empty compressed blob is valid");
  Check(BlobCodec.Decode(BlobCodec.Encode([]),0).Length==0,"empty blob is valid");
  var cat=AttributeCatalog.From(typeof(TypedResult));
  foreach(var provider in new[]{"Memory","Sqlite"})
  {
   using IDocumentStore store=provider=="Memory"?new MemoryStore():new SqliteStore("Pooling=False;Data Source="+Path.Combine(temp,"datatypes.db"));store.Migrate(cat);
   var service=new DocumentService(cat,store,[]);var repo=service.Repository<TypedResult>(actor);
   var doc=repo.Create(new(){Name="Run",Cost=12.34m,EuroCost=9.87m,Payload=bytes,Series=new Uri("tsdb://example.test/run/1"),Counter=9007199254740993});
   Check(repo.Get(doc.Id).Value.Payload!.SequenceEqual(bytes)&&repo.Get(doc.Id).Value.Cost==12.34m,provider+" typed binary/money roundtrip");
   Check(repo.Get(doc.Id).Value.Series!.Scheme=="tsdb",provider+" external reference retained");
   var wire=WireJson.Encode(service.Get("TypedResult",doc.Id,actor))!;
   Check(wire["values"]!["Counter"]!.GetValue<string>()=="9007199254740993"&&wire["values"]!["Cost"]!.GetValue<string>()=="12.34",provider+" exact numeric string transport");
   Check(wire["version"]!.GetValue<int>()==1,provider+" versions retain numeric transport");
   Check(Convert.FromBase64String(repo.History(doc.Id)[0].After!.Values["Payload"]!.ToString()).SequenceEqual(bytes),provider+" audit byte preservation");
   var schema=cat.Get("TypedResult");var cost=schema.Fields.Single(f=>f.Name=="Cost");var euro=schema.Fields.Single(f=>f.Name=="EuroCost");
   var report=new SavedReport("Costs",schema.Id!,schema.Fields.Select(f=>new ReportColumn(f.Id!)).ToArray());
   var html=ReportWriter.Write(report,new("Costs",SumFieldIds:[cost.Id!,euro.Id!]),cat,service,actor);
   Check(html.Contains("USD 12.34")&&html.Contains("EUR 9.87")&&!html.Contains(Convert.ToBase64String(bytes)),provider+" currency totals remain separate and binary reports show size");
   Reject(()=>repo.Create(new(){Name="Oversized",Payload=new byte[16385]}),provider+" field byte limit");
   Reject(()=>repo.Create(new(){Name="BadURI",Series=new Uri("file:///tmp/test")}),provider+" URI scheme validation");
   var empty=repo.Create(new(){Name="Empty",Payload=[]});Check(repo.Get(empty.Id).Value.Payload!.Length==0,provider+" empty differs from null");
  }
  using(var db=new SqliteConnection("Pooling=False;Data Source="+Path.Combine(temp,"datatypes.db"))){db.Open();using var c=db.CreateCommand();c.CommandText="SELECT typeof(Payload), length(Payload) FROM TypedResult WHERE Name='Run'";using var reader=c.ExecuteReader();reader.Read();Check(reader.GetString(0)=="blob"&&reader.GetInt64(1)<1024,"SQLite physical blob is compressed");}
  using(var db=new SqliteConnection("Pooling=False;Data Source="+Path.Combine(temp,"datatypes.db"))){db.Open();using var c=db.CreateCommand();c.CommandText="SELECT typeof(json) FROM _audit LIMIT 1";Check((string)c.ExecuteScalar()! == "blob","audit snapshots use compressed envelopes");}
  // Each migration executes the real SQLite rebuild, not just scalar helpers.
  int scenario=0;
  void ConvertCase(string from,string value,Field target,string expected,bool reject=false)
  {
   var source=new Catalog([new DocType("Sample","Samples",1,"name",false,[new("name","Name","Text",Required:true),new("v","Value",from,Options:from=="Select"?["A","B"]:null)],new(){{"Admin",["read","create","write","audit"]}})]);
   using var store=new SqliteStore("Pooling=False;Data Source="+Path.Combine(temp,"cast"+(scenario++)+".db"));store.Migrate(source);var service=new DocumentService(source,store,[]);var document=service.Create("Sample",new JsonObject{["name"]="one",["v"]=value},actor);var old=source.Get("Sample");
   target=target with{Id=old.Fields[1].Id};var next=new Catalog([old with{Version=2,Fields=[old.Fields[0],target]}]);
   if(reject){Reject(()=>store.Plan(next),from+" to "+target.Type+" refuses "+value);Check(service.Get("Sample",document.Id,actor).Values["v"]!.ToString()==value,"failed cast preserves source");return;}
   var plan=store.Plan(next);var accepted=store.Apply(plan.Id,actor);Check(new DocumentService(accepted,store,[]).Get("Sample",document.Id,actor).Values["v"]!.ToString()==expected,from+" to "+target.Type+" migration "+value);
  }
  ConvertCase("Text","9007199254740993",new("v","V","Integer"),"9007199254740993");
  ConvertCase("Text","12.340000000000000001",new("v","V","Decimal"),"12.340000000000000001");
  ConvertCase("Text","14/10/2026",new("v","V","Date",DateFormat:"dd/MM/yyyy"),"2026-10-14");
  ConvertCase("Text","14/10/2026",new("v","V","Date"),"",true);
  ConvertCase("Text","A",new("v","V","Select",Options:["A","B"]),"A");
  ConvertCase("Text","C",new("v","V","Select",Options:["A","B"]),"",true);
  ConvertCase("Select","B",new("v","V","Text"),"B");
  ConvertCase("Text","tsdb://example.test/run",new("v","V","Uri"),"tsdb://example.test/run");
  ConvertCase("Text","12.345",money with{Name="v",Rounding="even"},"12.34");
  ConvertCase("Decimal","12.345",new("v","V","Decimal",Precision:10,Scale:2,Rounding:"away"),"12.35");
  ConvertCase("Decimal","12.345",new("v","V","Decimal",Precision:10,Scale:2),"",true);
  using(var store=new SqliteStore("Pooling=False;Data Source="+Path.Combine(temp,"datatypes.db")))
  {
   var old=store.ReadCatalog()!;var schema=old.Get("TypedResult");
   Catalog Change(Func<Field,Field> change)=>new([schema with{Version=2,Fields=schema.Fields.Select(change).ToArray()}]);
   Reject(()=>store.Plan(Change(f=>f.Name=="Cost"?f with{Currency="EUR"}:f)),"currency changes require explicit FX");
   Reject(()=>store.Plan(Change(f=>f.Name=="Payload"?f with{MaxBytes=64}:f)),"blob limit migration preflights decoded size");
   var target=Change(f=>f.Name=="Payload"?f with{Compression="none"}:f.Name=="Name"?f with{Required=false}:f);
   var accepted=store.Apply(store.Plan(target).Id,actor);var service=new DocumentService(accepted,store,[]);
   Check(!accepted.Get("TypedResult").Fields.Single(f=>f.Name=="Name").Required,"required to optional migration");
   Check(Convert.FromBase64String(service.List("TypedResult",actor).Items.Single(d=>d.Values["Name"]!.ToString()=="Run").Values["Payload"]!.ToString()).SequenceEqual(bytes),"blob compression policy migration preserves bytes");
   service.Create("TypedResult",new JsonObject(),actor);Check(true,"relaxed field accepts null");
  }
  return count;
 }
}
[Document("dd000000000000000000000000000001",nameof(Name))]
[Grant("Admin","read","create","write","audit")]
public sealed class TypedResult
{
 [Field("dd000000000000000000000000000002",Required=true)]public string Name{get;set;}="";
 [Field("dd000000000000000000000000000003",Currency="USD")]public decimal? Cost{get;set;}
 [Field("dd000000000000000000000000000004",Currency="EUR")]public decimal? EuroCost{get;set;}
 [Field("dd000000000000000000000000000005",MaxBytes=16384)]public byte[]? Payload{get;set;}
 [Field("dd000000000000000000000000000006")]public Uri? Series{get;set;}
 [Field("dd000000000000000000000000000007")]public long? Counter{get;set;}
}
