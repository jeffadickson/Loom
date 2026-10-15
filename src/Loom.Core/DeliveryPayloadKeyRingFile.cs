using System.Security.Cryptography;
using System.Text.Json;

namespace Loom.Core;

/// <summary>Public key-ring metadata. Contains identifiers only, never secret key bytes.</summary>
/// <param name="CurrentKey">Identifier used for new envelopes.</param>
/// <param name="KeyIds">Identifiers retained for decoding older envelopes.</param>
public sealed record DeliveryKeyRingInfo(string CurrentKey,IReadOnlyList<string> KeyIds);

/// <summary>
/// Offline, bounded key-ring file operations. Rotation publishes a new file without modifying the
/// source; every retained key is preserved. Parent directories must be controlled by the operator.
/// </summary>
public static class DeliveryPayloadKeyRingFile
{
    private const int MaximumBytes=65536,MaximumKeys=256;
    private sealed record Ring(string CurrentKey,Dictionary<string,string> Keys);

    /// <summary>Validates the complete file and returns non-secret metadata.</summary>
    public static DeliveryKeyRingInfo Inspect(string path)=>Info(Read(path));

    /// <summary>Loads an immutable protector snapshot bound to the supplied application/tenant scope.</summary>
    public static AesDeliveryPayloadProtection Open(string path,string scope)
    {
        var ring=Read(path);
        return new(scope,ring.CurrentKey,ring.Keys.ToDictionary(k=>k.Key,k=>Convert.FromBase64String(k.Value),StringComparer.Ordinal));
    }

    /// <summary>Atomically publishes a new random 256-bit key ring. Never overwrites an existing destination.</summary>
    public static DeliveryKeyRingInfo Create(string destination)
    {
        var id=NewId();var ring=new Ring(id,new(StringComparer.Ordinal){[id]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))});
        WriteNew(destination,ring);return Info(ring);
    }

    /// <summary>
    /// Copies every source key and adds a fresh active key to a new destination. The source remains
    /// unchanged; select the new file in host configuration and restart all producers/workers together.
    /// </summary>
    public static DeliveryKeyRingInfo Rotate(string source,string destination)
    {
        var ring=Read(source);
        if(ring.Keys.Count>=MaximumKeys)throw new InvalidOperationException("Key ring is full; review retained-key dependencies before retirement.");
        var id=NewId();ring.Keys.Add(id,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        ring=ring with{CurrentKey=id};WriteNew(destination,ring);return Info(ring);
    }
    private static string NewId()=>"key-"+Guid.NewGuid().ToString("N");
    private static DeliveryKeyRingInfo Info(Ring ring)=>new(ring.CurrentKey,Array.AsReadOnly(ring.Keys.Keys.Order(StringComparer.Ordinal).ToArray()));
    private static bool ValidId(string id)=>id.Length is >=1 and <=64&&id.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-');

    // Strict parsing avoids duplicate-key ambiguity and accidental acceptance of the wrong secret format.
    private static Ring Read(string path)
    {
        var full=Path.GetFullPath(path);
        if(File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Key ring files cannot be symbolic links.");
        using var stream=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);
        var buffer=new byte[MaximumBytes+1];var count=0;
        while(count<buffer.Length)
        {
            var read=stream.Read(buffer,count,buffer.Length-count);if(read==0)break;count+=read;
        }
        if(count>MaximumBytes)throw new InvalidDataException("Key ring exceeds the 64 KiB limit.");
        try
        {
            using var document=JsonDocument.Parse(buffer.AsMemory(0,count),new(){MaxDepth=4});
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)throw new FormatException();
            var properties=root.EnumerateObject().ToArray();
            if(properties.Length!=2||properties.Count(p=>p.Name=="CurrentKey")!=1||properties.Count(p=>p.Name=="Keys")!=1)throw new FormatException();
            var current=root.GetProperty("CurrentKey").GetString()??throw new FormatException();
            if(!ValidId(current)||root.GetProperty("Keys").ValueKind!=JsonValueKind.Object)throw new FormatException();
            var keys=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var property in root.GetProperty("Keys").EnumerateObject())
            {
                var value=property.Value.GetString()??throw new FormatException();
                if(!ValidId(property.Name)||Convert.FromBase64String(value).Length!=32||!keys.TryAdd(property.Name,value)||keys.Count>MaximumKeys)throw new FormatException();
            }
            if(!keys.ContainsKey(current))throw new FormatException();
            return new(current,keys);
        }
        catch(Exception error) when(error is JsonException or FormatException or InvalidOperationException)
        {throw new InvalidDataException("Invalid key ring: expected one active identifier and unique named 256-bit keys.");}
    }
    private static void WriteNew(string destination,Ring ring)
    {
        var full=Path.GetFullPath(destination);var parent=Path.GetDirectoryName(full)!;
        // No automatic directory creation: deployment owns the parent directory and its permissions.
        if(!Directory.Exists(parent))throw new DirectoryNotFoundException("Provision the key directory before creating a ring.");
        // File.Move(overwrite:false) alone can race on some filesystems. Serialize cooperating
        // publishers with exclusive creation; a crash leaves a fail-closed lock for operator review.
        var lockPath=full+".lock";
        var lockOptions=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
        if(!OperatingSystem.IsWindows())lockOptions.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        using var publicationLock=new FileStream(lockPath,lockOptions);
        var temporary=Path.Combine(parent,".loom-key-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
            if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using(var stream=new FileStream(temporary,options))
            {JsonSerializer.Serialize(stream,ring);stream.Flush(flushToDisk:true);}
            // Under the publication lock, same-directory rename exposes only complete files without replacement.
            File.Move(temporary,full,overwrite:false);
        }
        finally
        {
            try{if(File.Exists(temporary))File.Delete(temporary);}
            finally{publicationLock.Dispose();File.Delete(lockPath);}
        }
    }
}
