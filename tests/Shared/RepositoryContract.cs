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
