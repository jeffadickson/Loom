using Loom.Core;
namespace Loom.Demo.Models;

[Document("dd000000000000000000000000000001", nameof(Name), Name="Instrument",Label="Instruments")]
[Grant("Admin","read","create","write","delete","audit")]
[Grant("Editor","read","create","write","audit")]
[Grant("Viewer","read")]
public sealed class InstrumentModel
{
    [Field("dd000000000000000000000000000002",Required=true)] public string Name {get;set;}="";
    [Field("dd000000000000000000000000000003",Required=true,Unique=true)] public string Serial {get;set;}="";
    [Field("dd000000000000000000000000000004")] public DateOnly? CalibrationDue {get;set;}
}
[Document("dd000000000000000000000000000011",nameof(Name),Name="Specimen",Label="Specimens")]
[Grant("Admin","read","create","write","delete","audit")]
[Grant("Editor","read","create","write","audit")]
[Grant("Viewer","read")]
public sealed class SpecimenModel
{
    [Field("dd000000000000000000000000000012",Required=true)] public string Name {get;set;}="";
    [Field("dd000000000000000000000000000013")] public string? Material {get;set;}
    [Field("dd000000000000000000000000000014")] public string? Batch {get;set;}
}
[Document("dd000000000000000000000000000021",nameof(Name),Name="LabRun",Label="Laboratory runs")]
[Grant("Admin","read","create","write","delete","audit")]
[Grant("Editor","read","create","write","audit")]
[Grant("Viewer","read")]
public sealed class LabRunModel
{
    [Field("dd000000000000000000000000000022",Required=true)] public string Name {get;set;}="";
    [Field("dd000000000000000000000000000023",Required=true,Target="Instrument")] public string Instrument {get;set;}="";
    [Field("dd000000000000000000000000000024",Required=true,Target="Specimen")] public string Specimen {get;set;}="";
    [Field("dd000000000000000000000000000025",Required=true,Target="TestResult")] public string Result {get;set;}="";
    [Field("dd000000000000000000000000000026",Required=true)] public string Operator {get;set;}="";
    [Field("dd000000000000000000000000000027",Required=true,Options=new[]{"Planned","Complete","Review"})] public string Status {get;set;}="Planned";
    [Field("dd000000000000000000000000000028")] public DateOnly? PerformedOn {get;set;}
    [Field("dd000000000000000000000000000029")] public string? Notes {get;set;}
}
