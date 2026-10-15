using Loom.Core;
using Loom.Core.Science;
using System.Text.Json.Nodes;
using Xunit;

namespace Loom.Tests;

// These tests execute unchanged against Memory, SQLite, PostgreSQL and SQL Server.
public abstract class RepositoryContract
{
    protected abstract IDocumentStore Open();
    protected static readonly Actor Admin=new("scientist","Admin");
    protected static Catalog Model()=>new([new("Reading","Readings",1,"Name",false,
        [new("Name","Name","Text",Required:true,Unique:true),new("Cost","Cost","Money",Currency:"USD"),
         new("Payload","Payload","Binary"),new("Temperature","Temperature","Measurement",QuantityKind:"Temperature",AllowedUnits:["degC","K"])],
        new(){["Admin"]=["read","create","write","delete","audit"]})]);
    protected static DocumentService Service(IDocumentStore store)
    {
        var model=Model();store.Migrate(model);return new(model,store,[]);
    }
    private static Catalog WorkModel()=>new([new("Work","Work",1,"Name",false,
        [new("Name","Name","Text",true),new("Status","Status","Text",true),new("Attempts","Attempts","Integer",true),new("Error","Error","Text")],
        new(){["Admin"]=["read","create","write","audit"]})]);
    [Fact] public void WorkflowGuardsEffectsAndAuditAreAtomic()
    {
        using var store=Open();var model=WorkModel();store.Migrate(model);
        var service=new DocumentService(model,store,[]);var session=new ApplicationSession(service,Admin);
        var job=session.Create("Work",new(){["Name"]="original",["Status"]="Pending",["Attempts"]=0});
        var field=model.Get("Work").Fields.Single(f=>f.Name=="Status").Id!;
        var fail=new ApplicationWorkflow(field,[new("Run",["Pending"],"Complete",["Admin"],Effect:(c,_)=>
        {
            c.Session.Create("Work",new(){["Name"]="effect",["Status"]="Pending",["Attempts"]=0});
            throw new InvalidOperationException("rollback");
        })]);
        Assert.Throws<InvalidOperationException>(()=>fail.Execute(session,"Work",job.Id,job.Version,"Run"));
        Assert.Equal(1,service.List("Work",Admin).Total);Assert.Single(service.History("Work",job.Id,Admin));
        var pass=new ApplicationWorkflow(field,[new("Run",["Pending"],"Complete",["Admin"],Guard:c=>
        {if(c.Document.Values["Name"]!.ToString()!="original")throw new InvalidOperationException();})]);
        var denied=new ApplicationSession(service,new("viewer","Viewer"));
        Assert.Equal(403,Assert.Throws<LoomException>(()=>pass.Execute(denied,"Work",job.Id,job.Version,"Run")).Status);
        var done=pass.Execute(session,"Work",job.Id,job.Version,"Run");
        Assert.Equal("Complete",done.Values["Status"]!.ToString());Assert.Equal(2,service.History("Work",job.Id,Admin).Count);
        Assert.Equal(409,Assert.Throws<LoomException>(()=>pass.Execute(session,"Work",job.Id,job.Version,"Run")).Status);
        Assert.Equal(409,Assert.Throws<LoomException>(()=>pass.Execute(session,"Work",job.Id,done.Version,"Run")).Status);
    }
    [Fact] public void DurableJobFailureRollsBackEffectsAndRetryDoesNotDuplicateCompletion()
    {
        using var store=Open();var model=WorkModel();store.Migrate(model);
        var service=new DocumentService(model,store,[]);var session=new ApplicationSession(service,Admin);
        var job=session.Create("Work",new(){["Name"]="job",["Status"]="Pending",["Attempts"]=0});
        var runner=new TransactionalJobs(2);
        void Fail(IApplicationSession s,Document _)
        {
            s.Create("Work",new(){["Name"]="rolled back",["Status"]="Pending",["Attempts"]=0});
            throw new InvalidOperationException("private diagnostic");
        }
        var outcome=runner.Run(session,job,Fail);
        Assert.NotNull(outcome.Exception);Assert.False(outcome.Failed);Assert.Equal(1,service.List("Work",Admin).Total);
        var pending=session.Get("Work",job.Id);Assert.Equal("1",pending.Values["Attempts"]!.ToString());
        Assert.DoesNotContain("private",pending.Values["Error"]!.ToString());
        Assert.False(runner.Run(session,job,(_,_)=>throw new Exception("stale handler ran")).Completed);
        Assert.Equal(pending.Version,session.Get("Work",job.Id).Version);
        Assert.True(runner.Run(session,pending,Fail).Failed);
        var failed=session.Get("Work",job.Id);Assert.Equal("Failed",failed.Values["Status"]!.ToString());
        var retry=runner.Retry(session,"Work",job.Id,failed.Version);
        var completed=runner.Run(session,retry,(s,_)=>s.Create("Work",new(){["Name"]="published",["Status"]="Complete",["Attempts"]=0}));
        Assert.True(completed.Completed);Assert.Equal(2,service.List("Work",Admin).Total);
        Assert.False(runner.Run(session,retry,(_,_)=>throw new Exception("duplicate handler ran")).Completed);
        Assert.Equal("Complete",session.Get("Work",job.Id).Values["Status"]!.ToString());
    }
    [Fact] public void JobCancellationRollsBackWithoutConsumingAttempt()
    {
        using var store=Open();var model=WorkModel();store.Migrate(model);
        var session=new ApplicationSession(new DocumentService(model,store,[]),Admin);
        var job=session.Create("Work",new(){["Name"]="job",["Status"]="Pending",["Attempts"]=0});
        Assert.Throws<OperationCanceledException>(()=>new TransactionalJobs().Run(session,job,(_,_)=>throw new OperationCanceledException()));
        Assert.Equal(job.Version,session.Get("Work",job.Id).Version);
    }

    [Fact] public void ProjectionPreservesFilteringPagingAndExactSelectedValues()
    {
        using var store=Open();var service=Service(store);var schema=Model().Get("Reading");
        var name=schema.Fields.Single(f=>f.Name=="Name").Id!;var cost=schema.Fields.Single(f=>f.Name=="Cost").Id!;
        var payload=schema.Fields.Single(f=>f.Name=="Payload").Id!;
        for(var i=0;i<3;i++)service.Create("Reading",new(){["Name"]="Run "+i,["Cost"]="12345678901234567.89",["Payload"]=Convert.ToBase64String([1,2,3])},Admin);
        var query=new DocumentQuery(name,Skip:1,Take:1,Filters:[new(cost,FilterOperator.Equal,JsonValue.Create("12345678901234567.89"))],SelectFieldIds:[name,cost]);
        var page=service.Query("Reading",Admin,query);
        Assert.Equal(3,page.Total);var row=Assert.Single(page.Items);Assert.Equal("Run 1",row.Values["Name"]!.ToString());
        Assert.Equal("12345678901234567.89",row.Values["Cost"]!.ToString());Assert.False(row.Values.ContainsKey("Payload"));
        var search=service.Query("Reading",Admin,query with{Search="Run 2",Skip=0});
        Assert.Equal("Run 2",Assert.Single(search.Items).Values["Name"]!.ToString());
        var count=service.Query("Reading",Admin,query with{SelectFieldIds=[]});
        Assert.Equal(3,count.Total);Assert.Empty(Assert.Single(count.Items).Values);
        var binary=service.Query("Reading",Admin,query with{SelectFieldIds=[payload]});
        Assert.Equal(Convert.ToBase64String([1,2,3]),Assert.Single(binary.Items).Values["Payload"]!.ToString());
        Assert.Equal(400,Assert.Throws<LoomException>(()=>service.Query("Reading",Admin,query with{SelectFieldIds=["unknown"]})).Status);
        Assert.Equal(400,Assert.Throws<LoomException>(()=>service.Query("Reading",Admin,query with{SelectFieldIds=[name,name]})).Status);
    }

    [Fact] public void Exact_values_binary_and_measurements_survive_round_trip()
    {
        using var store=Open();var service=Service(store);
        var bytes=new byte[]{0,255,128,1};var measurement=new Measurement(new(25,Units.Celsius),.1);
        var doc=service.Create("Reading",new(){["Name"]="Café 温度 ' --",["Cost"]="12345678901234567.89",["Payload"]=Convert.ToBase64String(bytes),["Temperature"]=MeasurementJson.Write(measurement)},Admin);
        var read=service.Get("Reading",doc.Id,Admin);
        Assert.Equal("12345678901234567.89",read.Values["Cost"]!.ToString());
        Assert.Equal(bytes,Convert.FromBase64String(read.Values["Payload"]!.ToString()));
        Assert.Equal(measurement,MeasurementJson.Read(read.Values["Temperature"]!));
        Assert.Equal("Café 温度 ' --",read.Values["Name"]!.ToString());
    }
    [Fact] public void Failed_transaction_rolls_back_record_and_audit_together()
    {
        using var store=Open();var service=Service(store);
        var doc=service.Create("Reading",new(){["Name"]="original"},Admin);
        Assert.Throws<InjectedFailure>(()=>store.Transaction<int>(session=>
        {
            var next=doc with{Version=2,Values=new(){["Name"]="changed"}};
            session.Replace(next,1);
            session.AppendAudit(new(Guid.NewGuid().ToString("N"),"Reading",doc.Id,Admin.Id,"update",DateTimeOffset.UtcNow.ToString("O"),doc,next));
            throw new InjectedFailure();
        }));
        Assert.Equal("original",service.Get("Reading",doc.Id,Admin).Values["Name"]!.ToString());
        Assert.Single(service.History("Reading",doc.Id,Admin));
    }
    [Fact] public void Stale_write_is_rejected_without_extra_audit()
    {
        using var store=Open();var service=Service(store);var doc=service.Create("Reading",new(){["Name"]="initial"},Admin);
        service.Update("Reading",doc.Id,1,new(){["Name"]="winner"},Admin);
        Assert.Equal(409,Assert.Throws<LoomException>(()=>service.Update("Reading",doc.Id,1,new(){["Name"]="loser"},Admin)).Status);
        Assert.Equal("winner",service.Get("Reading",doc.Id,Admin).Values["Name"]!.ToString());
        Assert.Equal(2,service.History("Reading",doc.Id,Admin).Count);
    }
    [Fact] public void Unauthorized_create_and_invalid_money_leave_no_rows()
    {
        using var store=Open();var service=Service(store);
        Assert.Equal(403,Assert.Throws<LoomException>(()=>service.Create("Reading",new(){["Name"]="denied"},new("outsider","Viewer"))).Status);
        Assert.Equal(422,Assert.Throws<LoomException>(()=>service.Create("Reading",new(){["Name"]="invalid",["Cost"]="1.005"},Admin)).Status);
        Assert.Empty(service.List("Reading",Admin).Items);
    }
    [Fact] public void Returned_documents_and_audit_snapshots_do_not_alias_storage()
    {
        using var store=Open();var service=Service(store);var doc=service.Create("Reading",new(){["Name"]="original"},Admin);
        doc.Values["Name"]="mutated";service.History("Reading",doc.Id,Admin)[0].After!.Values["Name"]="tampered";
        Assert.Equal("original",service.Get("Reading",doc.Id,Admin).Values["Name"]!.ToString());
        Assert.Equal("original",service.History("Reading",doc.Id,Admin)[0].After!.Values["Name"]!.ToString());
    }
    [Fact] public void Exact_money_filter_and_paging_agree()
    {
        using var store=Open();var service=Service(store);
        foreach(var cost in new[]{"2.00","10.00","9007199254740993.00"})service.Create("Reading",new(){["Name"]=cost,["Cost"]=cost},Admin);
        var field=Model().Get("Reading").Fields.Single(f=>f.Name=="Cost");
        var page=service.Query("Reading",Admin,new(field.Id!,Take:1,Filters:[new(field.Id!,FilterOperator.GreaterThan,JsonValue.Create("2"))]));
        Assert.Equal(2,page.Total);Assert.Equal(10m,decimal.Parse(Assert.Single(page.Items).Values["Cost"]!.ToString(),System.Globalization.CultureInfo.InvariantCulture));
    }
    [Fact] public void Duplicate_unique_field_does_not_write_a_second_record()
    {
        using var store=Open();var service=Service(store);service.Create("Reading",new(){["Name"]="same"},Admin);
        Assert.Equal(409,Assert.Throws<LoomException>(()=>service.Create("Reading",new(){["Name"]="same"},Admin)).Status);
        Assert.Single(service.List("Reading",Admin).Items);
    }
    [Fact] public void List_search_is_literal_case_insensitive_and_combines_with_filters()
    {
        using var store=Open();var service=Service(store);
        service.Create("Reading",new(){["Name"]="Café %_ sensor",["Cost"]="2.00"},Admin);
        service.Create("Reading",new(){["Name"]="other sensor",["Cost"]="10.00"},Admin);
        Assert.Single(service.List("Reading",Admin,search:"CAFÉ %_").Items);
        Assert.Empty(service.List("Reading",Admin,search:"sensor",field:"Cost",equals:"3").Items);
        Assert.Single(service.List("Reading",Admin,search:"sensor",field:"Cost",equals:"2.0").Items);
        var page=service.List("Reading",Admin,sort:"Cost",skip:1,take:1);
        Assert.Equal(2,page.Total);Assert.Equal("other sensor",Assert.Single(page.Items).Values["Name"]!.ToString());
    }
    private sealed class InjectedFailure:Exception;
}
