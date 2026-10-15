using System.Security.Cryptography;
using System.Text;

namespace Loom.Core;

/// <summary>Host-configured authenticated protection; purpose binds ciphertext to its queue identity.</summary>
public interface IDeliveryPayloadProtection
{
    /// <summary>Protect plaintext bytes for the exact supplied purpose.</summary>
    byte[] Protect(byte[] plaintext,string purpose);
    /// <summary>Authenticate and decrypt, throwing CryptographicException for invalid or unavailable keys.</summary>
    byte[] Unprotect(byte[] ciphertext,string purpose);
}

/// <summary>AES-256-GCM envelope with a version, key ID, random nonce and authentication tag.</summary>
public sealed class AesDeliveryPayloadProtection:IDeliveryPayloadProtection
{
    private readonly string scope,currentKey;
    private readonly Dictionary<string,byte[]> keys;
    /// <summary>Copies a host-owned key ring. Retain old keys for outstanding messages; store keys separately from databases.</summary>
    public AesDeliveryPayloadProtection(string scope,string currentKey,IReadOnlyDictionary<string,byte[]> keys)
    {
        if(string.IsNullOrWhiteSpace(scope))throw new ArgumentException("A tenant/application scope is required.",nameof(scope));
        ArgumentNullException.ThrowIfNull(keys);
        if(keys.Any(k=>k.Key.Length is <1 or >64||k.Key.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-')||k.Value.Length!=32)||!keys.ContainsKey(currentKey))
            throw new ArgumentException("Configure named 256-bit keys and an active key.",nameof(keys));
        this.scope=scope;this.currentKey=currentKey;this.keys=keys.ToDictionary(k=>k.Key,k=>k.Value.ToArray(),StringComparer.Ordinal);
    }
    private byte[] Associated(string purpose)=>Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[]{scope,purpose}));
    /// <inheritdoc/>
    public byte[] Protect(byte[] plaintext,string purpose)
    {
        var name=Encoding.ASCII.GetBytes(currentKey);var result=new byte[30+name.Length+plaintext.Length];
        result[0]=1;result[1]=(byte)name.Length;name.CopyTo(result,2);
        var offset=2+name.Length;RandomNumberGenerator.Fill(result.AsSpan(offset,12));
        using var aes=new AesGcm(keys[currentKey],16);
        aes.Encrypt(result.AsSpan(offset,12),plaintext,result.AsSpan(offset+28),result.AsSpan(offset+12,16),Associated(purpose));
        return result;
    }
    /// <inheritdoc/>
    public byte[] Unprotect(byte[] ciphertext,string purpose)
    {
        if(ciphertext.Length<31||ciphertext[0]!=1||ciphertext[1] is <1 or >64||ciphertext.Length<30+ciphertext[1])throw new CryptographicException("Invalid protected payload.");
        var name=Encoding.ASCII.GetString(ciphertext,2,ciphertext[1]);
        if(!keys.TryGetValue(name,out var key))throw new CryptographicException("Payload key is unavailable.");
        var offset=2+ciphertext[1];var plaintext=new byte[ciphertext.Length-offset-28];
        using var aes=new AesGcm(key,16);
        aes.Decrypt(ciphertext.AsSpan(offset,12),ciphertext.AsSpan(offset+28),ciphertext.AsSpan(offset+12,16),plaintext,Associated(purpose));
        return plaintext;
    }
}

/// <summary>Omit configured stable field IDs from newly written audit snapshots without changing live records.</summary>
public sealed class AuditRedactionPolicy(IEnumerable<string> fieldIds)
{
    private readonly HashSet<string> ids=new(fieldIds,StringComparer.Ordinal);
    /// <summary>Returns a copy with sensitive values removed; schema renames retain stable field identity.</summary>
    public Document? Redact(Catalog catalog,Document? document)
    {
        if(document==null)return null;
        var copy=document.Copy();
        foreach(var field in catalog.Get(document.Type).Fields.Where(f=>ids.Contains(f.Id!)))copy.Values.Remove(field.Name);
        return copy;
    }
}
