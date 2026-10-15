using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Loom.Core;
using Xunit;

namespace Loom.Tests;

/// <summary>Offline key publication, rotation and strict input validation against real files.</summary>
public sealed class DeliveryKeyRingFileTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"loom-keyring-"+Guid.NewGuid().ToString("N"));
    public DeliveryKeyRingFileTests()=>Directory.CreateDirectory(root);
    private string PathFor(string name)=>Path.Combine(root,name);
    [Fact] public void CreateNeverOverwritesAndPublishesOnlyOwnerReadableUnixFiles()
    {
        var path=PathFor("keys.json");var info=DeliveryPayloadKeyRingFile.Create(path);var original=File.ReadAllBytes(path);
        Assert.Single(info.KeyIds);Assert.Equal(info.CurrentKey,info.KeyIds[0]);
        Assert.Throws<IOException>(()=>DeliveryPayloadKeyRingFile.Create(path));Assert.Equal(original,File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(root));
        if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(path));
    }
    [Fact] public void RotationRetainsOldKeysAndRequiresExplicitHostSwitchForNewMessages()
    {
        var source=PathFor("old.json");var destination=PathFor("new.json");DeliveryPayloadKeyRingFile.Create(source);
        var original=File.ReadAllBytes(source);var old=DeliveryPayloadKeyRingFile.Open(source,"north");var data=Encoding.UTF8.GetBytes("evidence");
        var envelope=old.Protect(data,"queue-key");var rotated=DeliveryPayloadKeyRingFile.Rotate(source,destination);
        Assert.Equal(2,rotated.KeyIds.Count);Assert.Equal(original,File.ReadAllBytes(source));
        var restarted=DeliveryPayloadKeyRingFile.Open(destination,"north");Assert.Equal(data,restarted.Unprotect(envelope,"queue-key"));
        var newEnvelope=restarted.Protect(data,"queue-key");Assert.Throws<CryptographicException>(()=>old.Unprotect(newEnvelope,"queue-key"));
        Assert.ThrowsAny<CryptographicException>(()=>DeliveryPayloadKeyRingFile.Open(destination,"south").Unprotect(envelope,"queue-key"));
        Assert.Throws<IOException>(()=>DeliveryPayloadKeyRingFile.Rotate(source,source));Assert.Equal(original,File.ReadAllBytes(source));
        Assert.Equal(2,Directory.GetFiles(root).Length);
        var recovery=PathFor("restored.json");File.Copy(destination,recovery);
        Assert.Equal(data,DeliveryPayloadKeyRingFile.Open(recovery,"north").Unprotect(newEnvelope,"queue-key"));
    }
    [Fact] public void DuplicateUnknownMissingAndMalformedPropertiesFailClosedWithoutExposingSecrets()
    {
        var path=PathFor("bad.json");var secret=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var inputs=new[]{
            "{\"CurrentKey\":\"a\",\"CurrentKey\":\"a\",\"Keys\":{\"a\":\""+secret+"\"}}",
            "{\"CurrentKey\":\"a\",\"Keys\":{\"a\":\""+secret+"\",\"a\":\""+secret+"\"}}",
            "{\"CurrentKey\":\"missing\",\"Keys\":{\"a\":\""+secret+"\"}}",
            "{\"CurrentKey\":\"a\",\"Keys\":{\"a\":\"short\"}}",
            "{\"CurrentKey\":\"a\",\"Keys\":null}",
            "{\"CurrentKey\":\"a\",\"Keys\":{\"a\":\""+secret+"\"},\"Extra\":true}"
        };
        foreach(var input in inputs)
        {
            File.WriteAllText(path,input);var error=Assert.Throws<InvalidDataException>(()=>DeliveryPayloadKeyRingFile.Inspect(path));
            Assert.DoesNotContain(secret,error.ToString());Assert.Throws<InvalidDataException>(()=>DeliveryPayloadKeyRingFile.Rotate(path,PathFor("unused.json")));
            Assert.False(File.Exists(PathFor("unused.json")));
        }
    }
    [Fact] public void OversizedAndLinkedFilesAreRejected()
    {
        var path=PathFor("oversized.json");File.WriteAllBytes(path,new byte[65537]);
        Assert.Throws<InvalidDataException>(()=>DeliveryPayloadKeyRingFile.Inspect(path));
        if(!OperatingSystem.IsWindows())
        {
            var valid=PathFor("valid.json");DeliveryPayloadKeyRingFile.Create(valid);
            var link=PathFor("linked.json");File.CreateSymbolicLink(link,valid);
            Assert.Throws<IOException>(()=>DeliveryPayloadKeyRingFile.Inspect(link));
        }
    }
    [Fact] public void ConcurrentPublicationHasOneWinnerAndNeverLeavesPartialFiles()
    {
        var path=PathFor("keys.json");var winners=0;
        Parallel.For(0,4,_=>{try{DeliveryPayloadKeyRingFile.Create(path);Interlocked.Increment(ref winners);}catch(IOException){}});
        Assert.Equal(1,winners);Assert.Single(DeliveryPayloadKeyRingFile.Inspect(path).KeyIds);Assert.Single(Directory.GetFiles(root));
    }
    [Fact] public void StalePublicationLockFailsClosedWithoutDeletingOperatorEvidence()
    {
        var path=PathFor("keys.json");File.WriteAllText(path+".lock","");
        Assert.Throws<IOException>(()=>DeliveryPayloadKeyRingFile.Create(path));
        Assert.False(File.Exists(path));Assert.True(File.Exists(path+".lock"));Assert.Single(Directory.GetFiles(root));
    }
    public void Dispose()=>Directory.Delete(root,true);
}
