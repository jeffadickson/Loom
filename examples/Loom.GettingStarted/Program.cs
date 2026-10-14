using Loom.Core;
using Loom.Sqlite;
using System.Text.Json.Nodes;

// A complete console program. It uses its own temporary SQLite database.
// The three classes show three releases of the SAME model, with the SAME permanent IDs.
var directory=Path.Combine(Path.GetTempPath(),"loom-start-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    using var store=new SqliteStore("Pooling=False;Data Source="+Path.Combine(directory,"example.db"));
    var actor=new Actor("getting-started","Admin");
    var v1=AttributeCatalog.From(typeof(InstrumentV1));
    store.Migrate(v1);
    var instruments=new DocumentService(v1,store,[]).Repository<InstrumentV1>(actor);
    var meter=instruments.Create(new InstrumentV1{Name="Multimeter",Site=null,Measurement=42});
    var scope=instruments.Create(new InstrumentV1{Name="Oscilloscope",Site="Annex"});
    Console.WriteLine("Before: Multimeter = (missing), Oscilloscope = Annex");

    var v2=AttributeCatalog.From(typeof(InstrumentV2));
    var plan=store.Plan(v2,[new MigrationBackfill(ModelIds.Site,JsonValue.Create("Main laboratory"))]);
    foreach(var change in plan.Changes)Console.WriteLine("PLAN: "+change.Description);
    // In an application, show the plan for review before calling Apply.
    // This disposable teaching example applies its own known demonstration plan.
    var accepted=store.Apply(plan.Id,actor);
    var required=new DocumentService(accepted,store,[]).Repository<InstrumentV2>(actor);
    var meterAfter=required.Get(meter.Id);
    var scopeAfter=required.Get(scope.Id);
    if(meterAfter.Value.Site!="Main laboratory" || scopeAfter.Value.Site!="Annex")throw new Exception("Migration result mismatch.");
    Console.WriteLine($"After: Multimeter = {meterAfter.Value.Site}, Oscilloscope = {scopeAfter.Value.Site}");
    try
    {
        required.Create(new InstrumentV2{Name="Missing site"});
        throw new Exception("Expected required-field validation.");
    }
    catch(LoomException e) when(e.Status==422){Console.WriteLine("Expected validation: "+e.Message);}
    required.Create(new InstrumentV2{Name="Power supply",Site="Lab B"});
    Console.WriteLine($"PASS: {required.List().Total} instruments; existing values preserved; missing site filled; future site required.");

    // Release 3 widens a numeric field while keeping its permanent identity.
    var v3=AttributeCatalog.From(typeof(InstrumentV3));
    var numericPlan=store.Plan(v3);
    foreach(var change in numericPlan.Changes)Console.WriteLine("PLAN: "+change.Description);
    var numericCatalog=store.Apply(numericPlan.Id,actor);
    var decimalReadings=new DocumentService(numericCatalog,store,[]).Repository<InstrumentV3>(actor);
    var reading=decimalReadings.Get(meter.Id);
    if(reading.Value.Measurement!=42m)throw new Exception("Integer conversion lost its value.");
    reading.Value.Measurement=42.5m;
    decimalReadings.Save(reading);
    var reverse=new Catalog(numericCatalog.Types.Values.Select(t=>t with{Version=4,Fields=t.Fields.Select(f=>f.Id==ModelIds.Measurement?f with{Type="Integer"}:f).ToArray()}));
    try{store.Plan(reverse);throw new Exception("Expected fractional conversion refusal.");}
    catch(LoomException e)when(e.Status==422){Console.WriteLine("Expected conversion refusal: "+e.Message);}
    if(decimalReadings.Get(meter.Id).Value.Measurement!=42.5m)throw new Exception("Refused conversion changed data.");
    Console.WriteLine("PASS: Integer widened to Decimal; narrowing 42.5 was refused; 42.5 remains stored.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(directory,true);
}

public static class ModelIds
{
    public const string Instrument="11111111111111111111111111111111";
    public const string Name="22222222222222222222222222222222";
    public const string Measurement="44444444444444444444444444444444";
    public const string Site="33333333333333333333333333333333";
}

[Document(ModelIds.Instrument,nameof(Name),Name="Instrument",Version=1)]
[Grant("Admin","read","create","write","audit")]
public sealed class InstrumentV1
{
    [Field(ModelIds.Name,Required=true)] public string? Name {get;set;}
    [Field(ModelIds.Site)] public string? Site {get;set;}
    [Field(ModelIds.Measurement)] public long? Measurement {get;set;}
}

[Document(ModelIds.Instrument,nameof(Name),Name="Instrument",Version=2)]
[Grant("Admin","read","create","write","audit")]
public sealed class InstrumentV2
{
    [Field(ModelIds.Name,Required=true)] public string? Name {get;set;}
    [Field(ModelIds.Site,Required=true)] public string? Site {get;set;}
    [Field(ModelIds.Measurement)] public long? Measurement {get;set;}
}

[Document(ModelIds.Instrument,nameof(Name),Name="Instrument",Version=3)]
[Grant("Admin","read","create","write","audit")]
public sealed class InstrumentV3
{
    [Field(ModelIds.Name,Required=true)] public string? Name {get;set;}
    [Field(ModelIds.Site,Required=true)] public string? Site {get;set;}
    [Field(ModelIds.Measurement)] public decimal? Measurement {get;set;}
}
