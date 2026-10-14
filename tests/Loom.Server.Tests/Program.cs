using Loom.Core;
using Loom.Server;
using System.Data.Common;
using System.Text.Json.Nodes;

if(args.Length!=1 || args[0] is not ("postgres" or "sqlserver"))throw new ArgumentException("Specify postgres or sqlserver and LOOM_TEST_CONNECTION. Use a disposable test database.");
var connection=Environment.GetEnvironmentVariable("LOOM_TEST_CONNECTION")??throw new InvalidOperationException("LOOM_TEST_CONNECTION is required; tests never silently skip.");
DbConnection Connect()=>args[0]=="postgres"?new Npgsql.NpgsqlConnection(connection):new Microsoft.Data.SqlClient.SqlConnection(connection);
ServerDocumentStore Open()=>new(Connect(),args[0]=="postgres"?ServerEngine.PostgreSql:ServerEngine.SqlServer);
var catalog=new Catalog([new("Conformance","Conformance",1,"Name",false,[new("Name","Name","Text",Required:true,Unique:true),new("Amount","Amount","Money",Currency:"USD")],new(){["Admin"]=["read","create","write","delete","audit"]})]);
var actor=new Actor("integration","Admin");int count=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);count++;Console.WriteLine("PASS "+message);}
string id;string name="run-"+Guid.NewGuid().ToString("N");
using(var store=Open())
{
    store.Migrate(catalog);var service=new DocumentService(catalog,store,[]);
    var doc=service.Create("Conformance",new(){["Name"]=name,["Amount"]="12345678901234567.89"},actor);id=doc.Id;
    Check(service.Get("Conformance",id,actor).Values["Amount"]!.ToString()=="12345678901234567.89","exact money roundtrip");
    Check(service.History("Conformance",id,actor).Count==1,"transactional audit");
    try{store.Transaction<int>(s=>{s.Delete("Conformance",id,1);throw new InvalidOperationException("rollback");});}catch(InvalidOperationException){}
    Check(service.Get("Conformance",id,actor).Version==1,"rollback restores record");
    try{service.Get("Conformance",id,new("stranger","Viewer"));throw new Exception("Authorization bypass");}catch(LoomException e){Check(e.Status==403,"read authorization");}
    service.Update("Conformance",id,1,new(){["Name"]=name,["Amount"]="1.25"},actor);
    try{service.Update("Conformance",id,1,new(){["Name"]=name},actor);throw new Exception("Lost update");}catch(LoomException e){Check(e.Status==409,"stale write rejected");}
    try{service.Create("Conformance",new(){["Name"]=name},actor);throw new Exception("Duplicate accepted");}catch(LoomException e){Check(e.Status==409||e.Status==422,"unique field enforced");}
}
using(var reopened=Open())
{
    reopened.Migrate(catalog);var service=new DocumentService(catalog,reopened,[]);
    Check(service.Get("Conformance",id,actor).Version==2,"durability after reopen");
    service.Delete("Conformance",id,2,actor);
    Check(service.History("Conformance",id,actor).Last().Action=="delete","audit survives deletion");
}
Console.WriteLine($"{count} server checks passed ({args[0]}).");
