using System.IO.Compression;
using System.Text.Json.Nodes;
using Loom.Core;
using Loom.Sqlite;
using Xunit;

namespace Loom.Tests;

/// <summary>Physical isolation and clean-instance recovery, against real SQLite databases.</summary>
public sealed class TenantRecoveryTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"loom-tenants-"+Guid.NewGuid().ToString("N"));
    private readonly Dictionary<(string,string),string> grants=new(){[("alice","north")]="Admin",[("bob","south")]="Admin"};
    private readonly Catalog catalog=new([
        new("Asset","Assets",1,"Name",false,[new("Name","Name","Text",true,true),new("Cost","Cost","Money",Currency:"USD"),new("Data","Data","Binary",Compression:"brotli")],
            new(){["Admin"]=["read","create","write","delete","audit"],["Viewer"]=["read"]}),
        new("Run","Runs",1,"Name",false,[new("Name","Name","Text",true),new("Asset","Asset","Link",Target:"Asset")],
            new(){["Admin"]=["read","create","write","delete","audit"]})
    ]);
    private TenantWorkspace Workspace(string? path=null)=>new(path??root,["north","south"],(user,tenant)=>grants.GetValueOrDefault((user,tenant)));
    public TenantRecoveryTests()
    {
        var workspace=Workspace();workspace.Provision("north",catalog);workspace.Provision("south",catalog);
    }

    [Fact] public void MembershipIsMandatoryAndRevocationAffectsExistingSessions()
    {
        var workspace=Workspace();
        Assert.Equal(403,Assert.Throws<LoomException>(()=>workspace.Open("global-admin","north")).Status);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>workspace.Open("alice","south")).Status);
        Assert.Equal(400,Assert.Throws<LoomException>(()=>workspace.Open("alice","../south")).Status);
        using var north=workspace.Open("alice","north");
        north.Create("Asset",new(){["Name"]="Probe"});
        grants.Remove(("alice","north"));
        Assert.Equal(403,Assert.Throws<LoomException>(()=>north.List("Asset")).Status);
    }

    [Fact] public void DocumentsCountsUniquenessLinksAuditReportsAndRolesStayInTheirTenant()
    {
        var workspace=Workspace();using var north=workspace.Open("alice","north");using var south=workspace.Open("bob","south");
        var asset=north.Create("Asset",new(){["Name"]="Probe",["Cost"]="1234567890123.45"});
        Assert.Equal(0,south.List("Asset").Total);
        Assert.Equal(404,Assert.Throws<LoomException>(()=>south.Get("Asset",asset.Id)).Status);
        Assert.Equal(404,Assert.Throws<LoomException>(()=>south.Update("Asset",asset.Id,1,new(){["Name"]="stolen"})).Status);
        Assert.Equal(404,Assert.Throws<LoomException>(()=>south.Delete("Asset",asset.Id,1)).Status);
        Assert.Empty(south.History("Asset",asset.Id));
        Assert.Throws<LoomException>(()=>south.Create("Run",new(){["Name"]="Invalid cross-link",["Asset"]=asset.Id}));
        south.Create("Asset",new(){["Name"]="Probe"}); // Uniqueness is tenant-local.
        var type=catalog.Get("Asset");var report=new SavedReport("Assets",type.Id!,[new(type.Fields[0].Id!)]);
        Assert.Equal(1,south.Report(report).Total);
        Assert.Single(north.History("Asset",asset.Id));
        grants[("alice","north")]="Viewer";
        Assert.Equal(403,Assert.Throws<LoomException>(()=>north.Create("Asset",new(){["Name"]="Denied"})).Status);
        Assert.Equal(1,north.List("Asset").Total);
    }

    [Fact] public void BackupRestoresExactValuesBinaryLinksAuditAndReportLayoutsToCleanInstance()
    {
        var workspace=Workspace();string assetId,runId;
        var type=catalog.Get("Asset");var report=new SavedReport("Assets",type.Id!,[new(type.Fields[0].Id!),new(type.Fields[1].Id!)]);
        var bytes=Enumerable.Range(0,8192).Select(i=>(byte)(i%13)).ToArray();
        using(var north=workspace.Open("alice","north"))
        {
            var asset=north.Create("Asset",new(){["Name"]="Probe",["Cost"]="1234567890123.45",["Data"]=Convert.ToBase64String(bytes)});
            assetId=asset.Id;
            runId=north.Create("Run",new(){["Name"]="Thermal run",["Asset"]=asset.Id}).Id;
            north.SaveReportDesign(type.Id!,new(0,report,new("Calibration costs")));
            // Back up while another tenant connection remains open, including its committed WAL.
            workspace.Backup("north",Path.Combine(root,"north.zip"));
        }
        var restored=Workspace(Path.Combine(root,"recovered"));
        restored.Restore("north",Path.Combine(root,"north.zip"));
        using(var north=restored.Open("alice","north"))
        {
            var asset=north.Get("Asset",assetId);
            Assert.Equal("1234567890123.45",asset.Values["Cost"]!.ToString());
            Assert.Equal(bytes,Convert.FromBase64String(asset.Values["Data"]!.ToString()));
            Assert.Equal(assetId,north.Get("Run",runId).Values["Asset"]!.ToString());
            Assert.Single(north.History("Asset",assetId));
            Assert.Equal("Calibration costs",north.ReadReportDesign(type.Id!)!.Layout.Title);
            Assert.Equal(1,north.Report(report).Total);
        }
        Assert.Throws<IOException>(()=>restored.Restore("north",Path.Combine(root,"north.zip")));
        Assert.Throws<InvalidDataException>(()=>restored.Restore("south",Path.Combine(root,"north.zip")));
    }

    [Fact] public void TamperingOversizedArchivesAndWrongDatabaseBindingsFailClosed()
    {
        var workspace=Workspace();var archive=Path.Combine(root,"backup.zip");workspace.Backup("north",archive);
        Assert.Throws<IOException>(()=>workspace.Backup("north",archive));
        var destination=Path.Combine(root,"restore.db");
        Assert.Throws<InvalidDataException>(()=>SqliteStore.RestoreBackup(archive,destination,"north",1));
        using(var zip=ZipFile.Open(archive,ZipArchiveMode.Update))
        {
            zip.GetEntry("documents.db")!.Delete();
            using var stream=new StreamWriter(zip.CreateEntry("documents.db").Open());stream.Write("corrupt");
        }
        Assert.Throws<InvalidDataException>(()=>SqliteStore.RestoreBackup(archive,destination,"north"));
        Assert.False(File.Exists(destination));
        File.Copy(Path.Combine(root,"north","documents.db"),Path.Combine(root,"south","documents.db"),true);
        Assert.Equal(409,Assert.Throws<LoomException>(()=>workspace.Open("bob","south")).Status);
    }

    public void Dispose()=>Directory.Delete(root,true);
}
