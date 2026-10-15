using System.Text;
using System.Text.Json.Nodes;
using Loom.Core;
using Loom.Facility;
using Loom.Sqlite;
using Xunit;

namespace Loom.Tests;

/// <summary>End-to-end application invariants against real tenant databases, with an injected clock.</summary>
public sealed class FacilityApplicationTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"loom-facility-"+Guid.NewGuid().ToString("N"));
    private readonly FixedClock clock=new(DateTimeOffset.UtcNow);
    private FacilityApplication App()=>new(root,FacilitySeed.Members,clock);
    public FacilityApplicationTests()=>FacilitySeed.Populate(App());
    private Document Request(FacilityApplication app)=>Assert.Single(app.Overview("north-manager","north").Records["Request"]);
    private static string V(Document d,string key)=>d.Values[key]!.ToString();
    private static ResultInput Evidence(Document d,string value="20.125")=>new(d.Version,value,"degC","Reference probe stable.",
        Convert.ToBase64String(Encoding.UTF8.GetBytes("time,temperature\n0,20.125\n1,20.125\n")),"tsdb://example.test/run-1");
    private RequestInput Input(Document d,string name="Second reservation")=>new(name,V(d,"Campaign"),V(d,"Instrument"),V(d,"Calibration"),
        V(d,"Specimen"),V(d,"Procedure"),V(d,"Operator"),clock.GetUtcNow().AddHours(9).UtcDateTime.ToString("O"),
        clock.GetUtcNow().AddHours(10).UtcDateTime.ToString("O"),"1234567890123.45");

    [Fact] public void CompleteCycleSurvivesRestartAndTenantRecovery()
    {
        var app=App();var request=Request(app);
        request=app.Start("north-operator","north",request.Id,request.Version);
        request=app.Record("north-operator","north",request.Id,Evidence(request));
        request=app.Review("north-reviewer","north",request.Id,new(request.Version,true,"Independent review of raw evidence completed."));
        request=app.QueueRelease("north-manager","north",request.Id,request.Version);
        var archive=Path.Combine(root,"north.zip");app.Backup("north",archive);
        // A new process can resume the pending job; repeated execution creates no duplicate release.
        app=App();Assert.Equal(1,app.ProcessReleases());Assert.Equal(0,app.ProcessReleases());
        var release=Assert.Single(app.Overview("north-manager","north").Records["Release"]);
        var html=app.ReleasedReport("north-reviewer","north",release.Id);
        Assert.Contains("20.125",html);Assert.Contains("SYNTHETIC-CAL",html);Assert.Contains("1250",html);
        Assert.Contains("north-reviewer",html);Assert.Contains("degC",html);
        Assert.Equal("Released",V(Request(app),"Status"));
        Assert.Equal(6,app.History("north-manager","north","Request",request.Id).Count);
        var recovery=Path.Combine(root,"recovery");
        new TenantWorkspace(recovery,["north"],(_,_)=>null).Restore("north",archive);
        var restored=new FacilityApplication(recovery,FacilitySeed.Members.Where(m=>m.TenantId=="north"),clock);
        Assert.Equal(1,restored.ProcessReleases());
        var restoredRelease=Assert.Single(restored.Overview("north-manager","north").Records["Release"]);
        Assert.Equal(html,restored.ReleasedReport("north-manager","north",restoredRelease.Id));
        var amendment=app.Schedule("north-manager","north",Input(request,"Thermal stability / amendment") with{Amends=request.Id});
        Assert.Equal(request.Id,V(amendment,"Amends"));
        Assert.Equal(html,app.ReleasedReport("north-manager","north",release.Id));
        Assert.Equal(Assert.Single(app.Overview("north-manager","north").Records["Result"]).Values["Sha256"]!.ToString(),
            Assert.Single(restored.Overview("north-manager","north").Records["Result"]).Values["Sha256"]!.ToString());
    }
    [Fact] public void TenantRolesCostRedactionAndIndependentReviewFailClosed()
    {
        var app=App();var d=Request(app);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>app.Overview("north-manager","south")).Status);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>app.Start("north-manager","north",d.Id,d.Version)).Status);
        Assert.False(Assert.Single(app.Overview("north-operator","north").Records["Request"]).Values.ContainsKey("Cost"));
        Assert.All(app.History("north-operator","north","Request",d.Id),e=>Assert.False(e.After!.Values.ContainsKey("Cost")));
        Assert.Equal(403,Assert.Throws<LoomException>(()=>app.Register("north-operator","north","Instrument",new())).Status);
        d=app.Start("north-operator","north",d.Id,d.Version);
        Assert.Equal(409,Assert.Throws<LoomException>(()=>app.Record("north-operator","north",d.Id,Evidence(d) with{Version=1})).Status);
        d=app.Record("north-operator","north",d.Id,Evidence(d));
        var changedMembership=FacilitySeed.Members.Select(m=>m.UserId=="north-operator"?m with{Role="Reviewer"}:m);
        var changed=new FacilityApplication(root,changedMembership,clock);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>changed.Review("north-operator","north",d.Id,new(d.Version,true,"Self review"))).Status);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>app.ReleasedReport("north-operator","north","anything")).Status);
    }
    [Fact] public void FailedAttemptRequiresCorrectionAndPreservesPriorEvidence()
    {
        var app=App();var d=Request(app);
        d=app.Start("north-operator","north",d.Id,d.Version);
        Assert.Equal(422,Assert.Throws<LoomException>(()=>app.Record("north-operator","north",d.Id,Evidence(d) with{Unit="kelvin"})).Status);
        Assert.Empty(app.Overview("north-manager","north").Records["Result"]);
        d=app.Record("north-operator","north",d.Id,Evidence(d,"25"));
        Assert.Equal(422,Assert.Throws<LoomException>(()=>app.Review("north-reviewer","north",d.Id,new(d.Version,true,"Attempted approval"))).Status);
        d=app.Review("north-reviewer","north",d.Id,new(d.Version,false,"Repeat after equilibration."));
        d=app.Start("north-operator","north",d.Id,d.Version);
        d=app.Record("north-operator","north",d.Id,Evidence(d));
        d=app.Review("north-reviewer","north",d.Id,new(d.Version,true,"Repeat is acceptable."));
        Assert.Equal("Approved",V(d,"Status"));
        var results=app.Overview("north-manager","north").Records["Result"];
        Assert.Equal(2,results.Count);Assert.Contains(results,r=>V(r,"Outcome")=="Fail");Assert.Contains(results,r=>V(r,"Outcome")=="Pass");
        using var s=new TenantWorkspace(root,["north"],(_,_)=>"Operator",access:new FacilityPolicy()).Open("north-operator","north");
        Assert.Equal(409,Assert.Throws<LoomException>(()=>s.Update("Result",results[0].Id,1,results[0].Values)).Status);
    }
    [Fact] public async Task ConcurrentReservationsCannotDoubleBookAndExactCostSurvives()
    {
        var app=App();var original=Request(app);var input=Input(original);
        var tasks=Enumerable.Range(0,2).Select(i=>Task.Run(()=>
        {
            try{App().Schedule("north-manager","north",input with{Name="Reservation "+i});return 200;}
            catch(LoomException e){return e.Status;}
        }));
        var outcomes=await Task.WhenAll(tasks);Assert.Equal(new[]{200,409},outcomes.Order().ToArray());
        var created=app.Overview("north-manager","north").Records["Request"].Single(d=>d.Id!=original.Id);
        Assert.Equal("1234567890123.45",V(created,"Cost"));
        Assert.Equal(422,Assert.Throws<LoomException>(()=>app.Schedule("north-manager","north",input with{Ends=input.Starts})).Status);
        var expired=app.Register("north-manager","north","Calibration",new(){["Name"]="Expired",["Instrument"]=V(original,"Instrument"),
            ["ValidFrom"]="2020-01-01",["ValidUntil"]="2020-12-31",["Certificate"]="Old certificate"});
        Assert.Equal(422,Assert.Throws<LoomException>(()=>app.Schedule("north-manager","north",input with{Calibration=expired.Id})).Status);
    }
    [Fact] public async Task AtomicOperationsRollbackWritesAuditAndExpireEscapedCapability()
    {
        var workspace=new TenantWorkspace(root,["north"],(_,_)=>"Manager",access:new FacilityPolicy());
        using var session=workspace.Open("north-manager","north");TenantSession? escaped=null;
        Assert.Throws<InvalidOperationException>(()=>session.Atomic<int>(s=>
        {
            escaped=s;s.Create("Instrument",new(){["Name"]="Rolled back",["Serial"]="ROLLBACK",["Location"]="Bench"});
            throw new InvalidOperationException("Interruption before commit");
        }));
        Assert.DoesNotContain(session.List("Instrument").Items,d=>V(d,"Serial")=="ROLLBACK");
        Assert.Throws<ObjectDisposedException>(()=>escaped!.List("Instrument"));
        var invoked=false;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.Atomic(s=>{invoked=true;return Task.FromResult(1);}));
        Assert.False(invoked);
    }
    [Fact] public void CancellationReleasesReservationWithoutDeletingItsHistory()
    {
        var app=App();var original=Request(app);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>app.Cancel("north-operator","north",original.Id,original.Version,"No authority")).Status);
        var cancelled=app.Cancel("north-manager","north",original.Id,original.Version,"Fixture configuration changed.");
        Assert.Equal("Cancelled",V(cancelled,"Status"));
        var replacement=app.Schedule("north-manager","north",Input(original) with
        {Starts=V(original,"Starts"),Ends=V(original,"Ends")});
        Assert.Equal("Scheduled",V(replacement,"Status"));
        Assert.Equal(2,app.History("north-manager","north","Request",original.Id).Count);
        Assert.Equal(422,Assert.Throws<LoomException>(()=>app.Schedule("north-manager","north",Input(original) with{Amends=original.Id})).Status);
    }

    public void Dispose()=>Directory.Delete(root,true);
    private sealed class FixedClock(DateTimeOffset now):TimeProvider
    {public override DateTimeOffset GetUtcNow()=>now;}
}
