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
    private static void Write(ZipArchive archive,string path,byte[] bytes)
    {
        var entry=archive.CreateEntry(path,CompressionLevel.Optimal);
        entry.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
        using var stream=entry.Open();stream.Write(bytes);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static void ValidatePath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||path==ManifestPath||path.StartsWith('/')||path.Contains("..")||path.Contains('\\'))
            throw new InvalidDataException("Invalid evidence path.");
    }
}
