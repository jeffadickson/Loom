using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Loom.Core.Science;

/// <summary>One portable file and its uncompressed SHA-256 digest.</summary>
public sealed record EvidenceFile(string Path,long Length,string Sha256);
/// <summary>Versioned inventory. Digests provide integrity, not author authentication.</summary>
public sealed record EvidenceManifest(int Version,EvidenceFile[] Files);

/// <summary>Small, bounded evidence archives readable without a database. Never extracts files.</summary>
public static class EvidencePackage
{
    /// <summary>Maximum number of payload files, excluding the manifest.</summary>
    public const int MaximumFiles=32;
    /// <summary>Maximum size of one uncompressed payload file.</summary>
    public const int MaximumFileBytes=4*1024*1024;
    /// <summary>Maximum combined uncompressed payload and input archive size.</summary>
    public const int MaximumBytes=16*1024*1024;
    private const int ManifestLimit=64*1024;
    private const string ManifestPath="manifest.json";
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};

    /// <summary>Creates a version 1 archive with a sorted inventory and fixed ZIP timestamps.</summary>
    public static byte[] Create(IReadOnlyDictionary<string,byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if(files.Count is <1 or >MaximumFiles)throw new InvalidDataException("Invalid evidence file count.");
        var snapshot=files.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>
        {
            ValidatePath(p.Key);
            if(p.Value==null||p.Value.Length>MaximumFileBytes)throw new InvalidDataException("Evidence file exceeds the size limit.");
            return new KeyValuePair<string,byte[]>(p.Key,p.Value.ToArray());
        }).ToArray();
        if(snapshot.Sum(p=>(long)p.Value.Length)>MaximumBytes)throw new InvalidDataException("Evidence package exceeds the size limit.");
        var manifest=new EvidenceManifest(1,snapshot.Select(p=>new EvidenceFile(p.Key,p.Value.Length,Hash(p.Value))).ToArray());
        using var output=new MemoryStream();
        using(var archive=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            Write(archive,ManifestPath,JsonSerializer.SerializeToUtf8Bytes(manifest,Json));
            foreach(var file in snapshot)Write(archive,file.Key,file.Value);
        }
        if(output.Length>MaximumBytes)throw new InvalidDataException("Evidence archive exceeds the size limit.");
        return output.ToArray();
    }
    /// <summary>Checks every listed file and rejects missing or extra entries, without extraction.</summary>
    public static EvidenceManifest Verify(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if(bytes.Length>MaximumBytes)throw new InvalidDataException("Evidence archive exceeds the size limit.");
        using var input=new MemoryStream(bytes,false);
        using var archive=new ZipArchive(input,ZipArchiveMode.Read);
        if(archive.Entries.Count is <2 or >MaximumFiles+1)throw new InvalidDataException("Invalid evidence file count.");
        var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.Ordinal);
        foreach(var entry in archive.Entries)
            if(!entries.TryAdd(entry.FullName,entry))throw new InvalidDataException("Duplicate evidence entry.");
        if(!entries.Remove(ManifestPath,out var inventory))throw new InvalidDataException("Evidence manifest is missing.");
        EvidenceManifest manifest;
        try
        {
            var json=Read(inventory,ManifestLimit);
            using var document=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=8});
            ExactProperties(document.RootElement,"version","files");
            if(document.RootElement.GetProperty("files").ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("Invalid evidence inventory.");
            foreach(var file in document.RootElement.GetProperty("files").EnumerateArray())
                ExactProperties(file,"path","length","sha256");
            manifest=JsonSerializer.Deserialize<EvidenceManifest>(json,Json)!;
        }
        catch(JsonException){throw new InvalidDataException("Invalid evidence manifest.");}
        if(manifest.Version!=1||manifest.Files==null||manifest.Files.Length!=entries.Count)
            throw new InvalidDataException("Unsupported or incomplete evidence manifest.");
        long total=0;
        foreach(var file in manifest.Files)
        {
            if(file==null)throw new InvalidDataException("Invalid evidence file.");
            ValidatePath(file.Path);
            if(file.Sha256==null||file.Sha256.Length!=64||file.Sha256.Any(c=>!(c is >= '0' and <= '9' or >= 'A' and <= 'F')))
                throw new InvalidDataException("Invalid evidence digest.");
            if(file.Length<0||file.Length>MaximumFileBytes||(total+=file.Length)>MaximumBytes)
                throw new InvalidDataException("Evidence size limit exceeded.");
            if(!entries.Remove(file.Path,out var entry)||entry.Length!=file.Length)
                throw new InvalidDataException("Evidence inventory does not match archive.");
            if(Hash(Read(entry,MaximumFileBytes))!=file.Sha256)
                throw new InvalidDataException("Evidence digest mismatch.");
        }
        if(entries.Count!=0)throw new InvalidDataException("Unlisted evidence file.");
        return manifest;
    }
    /// <summary>Verifies a bounded local file without starting a host or writing extracted content.</summary>
    public static EvidenceManifest VerifyFile(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>MaximumBytes)throw new InvalidDataException("Evidence archive exceeds the size limit.");
        var bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);
        if(stream.ReadByte()!=-1)throw new InvalidDataException("Evidence archive changed while reading.");
        return Verify(bytes);
    }
    private static void ExactProperties(JsonElement value,params string[] names)
    {
        if(value.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Invalid evidence manifest object.");
        var properties=value.EnumerateObject().Select(p=>p.Name).ToArray();
        if(properties.Length!=names.Length||!properties.ToHashSet(StringComparer.Ordinal).SetEquals(names))
            throw new InvalidDataException("Invalid or duplicate evidence manifest property.");
    }
    private static byte[] Read(ZipArchiveEntry entry,int limit)
    {
        if(entry.Length<0||entry.Length>limit)throw new InvalidDataException("Evidence size limit exceeded.");
        using var stream=entry.Open();using var output=new MemoryStream();
        var buffer=new byte[8192];int count;
        while((count=stream.Read(buffer))!=0)
        {
            if(output.Length+count>limit)throw new InvalidDataException("Evidence size limit exceeded.");
            output.Write(buffer,0,count);
        }
        if(output.Length!=entry.Length)throw new InvalidDataException("Evidence length mismatch.");
        return output.ToArray();
    }
    private static void Write(ZipArchive archive,string path,byte[] bytes)
    {
        var entry=archive.CreateEntry(path,CompressionLevel.Optimal);
        entry.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
        using var stream=entry.Open();stream.Write(bytes);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static void ValidatePath(string path)
    {
        // Flat lowercase names avoid extraction ambiguity across Windows, macOS and Linux.
        if(string.IsNullOrEmpty(path)||path.Length>100||path==ManifestPath||
            path[0]=='.'||path[^1]=='.'||path.Contains("..")||
            path.Any(c=>!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')))
            throw new InvalidDataException("Invalid evidence path.");
        var stem=path.Split('.')[0];
        if(stem is "con" or "prn" or "aux" or "nul"||
            stem.Length==4&&(stem.StartsWith("com",StringComparison.Ordinal)||stem.StartsWith("lpt",StringComparison.Ordinal))&&stem[3] is >= '0' and <= '9')
            throw new InvalidDataException("Reserved evidence path.");
    }
}
