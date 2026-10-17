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
    [Fact] public void VerificationRejectsTamperingMissingExtraAndTruncatedFiles()
    {
        var original=EvidencePackage.Create(new Dictionary<string,byte[]>{["raw.bin"]=Bytes("data")});
        Assert.Equal("raw.bin",Assert.Single(EvidencePackage.Verify(original).Files).Path);
        foreach(var action in new Action<ZipArchive>[] {
            z=>{z.GetEntry("raw.bin")!.Delete();Write(z,"raw.bin",Bytes("evil"));},
            z=>z.GetEntry("raw.bin")!.Delete(),z=>Write(z,"extra.bin",Bytes("extra")),
            z=>Write(z,"raw.bin",Bytes("data")),z=>z.GetEntry("manifest.json")!.Delete()})
            Assert.Throws<InvalidDataException>(()=>EvidencePackage.Verify(Edit(original,action)));
        Assert.Throws<InvalidDataException>(()=>EvidencePackage.Verify(original[..30]));
    }
    [Fact] public void UnsafeNamesAndResourceLimitsAreRejected()
    {
        foreach(var name in new[]{"../raw.bin","/raw.bin","folder/raw.bin","C:raw.bin","RAW.bin","con.txt","lpt1.bin","raw.",".raw","manifest.json","raw\\data"})
            Assert.Throws<InvalidDataException>(()=>EvidencePackage.Create(new Dictionary<string,byte[]>{[name]=[1]}));
        Assert.Throws<InvalidDataException>(()=>EvidencePackage.Create(new Dictionary<string,byte[]>()));
        Assert.Throws<InvalidDataException>(()=>EvidencePackage.Create(Enumerable.Range(0,33).ToDictionary(i=>"f"+i,_=>new byte[1])));
        Assert.Throws<InvalidDataException>(()=>EvidencePackage.Create(new Dictionary<string,byte[]>{["raw.bin"]=new byte[EvidencePackage.MaximumFileBytes+1]}));
        Assert.Throws<InvalidDataException>(()=>EvidencePackage.Create(Enumerable.Range(0,5).ToDictionary(i=>"f"+i,_=>new byte[EvidencePackage.MaximumFileBytes])));
        Assert.Throws<InvalidDataException>(()=>EvidencePackage.Verify(new byte[EvidencePackage.MaximumBytes+1]));
        var original=EvidencePackage.Create(new Dictionary<string,byte[]>{["raw.bin"]=[1]});
        Assert.Throws<InvalidDataException>(()=>EvidencePackage.Verify(Edit(original,z=>{
            z.GetEntry("raw.bin")!.Delete();Write(z,"raw.bin",new byte[EvidencePackage.MaximumFileBytes+1]);})));
    }
    [Fact] public void ManifestRequiresExactVersionedUnambiguousShape()
    {
        var original=EvidencePackage.Create(new Dictionary<string,byte[]>{["raw.bin"]=[1]});
        foreach(var json in new[]{"null","{}","{broken", "{\"version\":1,\"version\":1,\"files\":[]}",
            "{\"version\":2,\"files\":[]}","{\"version\":1,\"files\":null}",
            "{\"version\":1,\"files\":[null]}","{\"version\":1,\"files\":[{}]}",
            "{\"version\":1,\"files\":[],\"extra\":0}"})
            Assert.Throws<InvalidDataException>(()=>EvidencePackage.Verify(Edit(original,z=>{
                z.GetEntry("manifest.json")!.Delete();Write(z,"manifest.json",Bytes(json));})));
    }
    private static byte[] Edit(byte[] bytes,Action<ZipArchive> edit)
    {
        using var stream=new MemoryStream();stream.Write(bytes);
        using(var archive=new ZipArchive(stream,ZipArchiveMode.Update,true))edit(archive);
        return stream.ToArray();
    }
    private static void Write(ZipArchive zip,string name,byte[] bytes)
    {using var stream=zip.CreateEntry(name).Open();stream.Write(bytes);}
}
