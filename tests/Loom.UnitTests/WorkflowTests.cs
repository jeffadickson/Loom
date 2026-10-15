using Loom.Core;
using Xunit;

namespace Loom.Tests;

public sealed class WorkflowTests
{
    [Fact] public void AsyncVoidCallbacksAreRejectedBeforeInvocation()
    {
        var invoked=false;
        async void Guard(WorkflowContext _) {invoked=true;await Task.Yield();}
        Assert.Throws<ArgumentException>(()=>new ApplicationWorkflow("state",[new("Run",["Pending"],"Complete",["Admin"],Guard:Guard)]));
        async void Handler(IApplicationSession _,Document __) {invoked=true;await Task.Yield();}
        Assert.Throws<ArgumentException>(()=>new TransactionalJobs().Run(null!,null!,Handler));
        Assert.False(invoked);
    }
    [Fact] public void WorkflowCopiesRoleAndSourceArrays()
    {
        var from=new[]{"Pending"};var roles=new[]{"Admin"};
        var model=new Catalog([new("Work","Work",1,"Name",false,[new("Name","Name","Text",true),new("Status","Status","Text",true)],
            new(){["Admin"]=["read","create","write"]})]);
        using var store=new MemoryStore();store.Migrate(model);
        var session=new ApplicationSession(new DocumentService(model,store,[]),new("admin","Admin"));
        var job=session.Create("Work",new(){["Name"]="job",["Status"]="Pending"});
        var workflow=new ApplicationWorkflow(model.Get("Work").Fields[1].Id!,[new("Run",from,"Complete",roles)]);
        from[0]="Changed";roles[0]="Viewer";
        Assert.Equal("Complete",workflow.Execute(session,"Work",job.Id,job.Version,"Run").Values["Status"]!.ToString());
    }
}
