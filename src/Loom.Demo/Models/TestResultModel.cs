using Loom.Core;
namespace Loom.Demo.Models;

[Document("cc000000000000000000000000000001",nameof(Name),Name="TestResult",Label="Test results")]
[Grant("Admin","read","create","write","delete","audit")]
[Grant("Editor","read","create","write","audit")]
[Grant("Viewer","read")]
public sealed class TestResultModel
{
    [Field("cc000000000000000000000000000002",Required=true)] public string Name {get;set;}="";
    [Field("cc000000000000000000000000000003",Options=new[]{"Pass","Review","Fail"},Required=true)] public string Outcome {get;set;}="Pass";
    [Field("cc000000000000000000000000000004")] public long? SampleCount {get;set;}
    [Field("cc000000000000000000000000000005",Currency="USD",Precision=19,Scale=2)] public decimal? Cost {get;set;}
    [Field("cc000000000000000000000000000006",Compression="auto",MaxBytes=1048576,MediaType="application/octet-stream")] public byte[]? Waveform {get;set;}
    [Field("cc000000000000000000000000000008",QuantityKind="Temperature",AllowedUnits=new[]{"degC","K","degF"})] public Loom.Core.Science.Measurement? Temperature {get;set;}
    [Field("cc000000000000000000000000000007")] public Uri? Series {get;set;}
}
