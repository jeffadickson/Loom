using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Loom.Core.Science;
using Xunit;

namespace Loom.Tests;

public sealed class EvidencePackageTests
{
    private static byte[] Bytes(string value)=>Encoding.UTF8.GetBytes(value);
    [Fact] public void WriterProducesSortedInventoryAndReproducibleBytes()
    {
        var files=new Dictionary<string,byte[]>{["raw.bin"]=[0,1,2,255],["report.html"]=Bytes("<p>20.125</p>")};
        var first=EvidencePackage.Create(files);
        Assert.Equal(first,EvidencePackage.Create(files.Reverse().ToDictionary(p=>p.Key,p=>p.Value)));
        using var zip=new ZipArchive(new MemoryStream(first));
        Assert.Equal(new[]{"manifest.json","raw.bin","report.html"},zip.Entries.Select(e=>e.FullName));
        using var manifest=JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.Equal(1,manifest.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(4,manifest.RootElement.GetProperty("files")[0].GetProperty("length").GetInt64());
    }
}
