using System.Data.Common;
using System.Text.Json;
using Loom.Core;
using Loom.Server;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Loom.Tests;

public interface IDatabaseFixture
{
    ServerEngine Engine {get;}
    DbConnection CreateDatabase();
}
// Container instances are shared per test class; every test owns a fresh database.
// Missing Docker is a failure, never a silently skipped compatibility test.
public sealed class PostgreSqlFixture:IAsyncLifetime,IDatabaseFixture
{
    private readonly PostgreSqlContainer container=new PostgreSqlBuilder().WithImage("postgres:16.6-alpine").Build();
    public ServerEngine Engine=>ServerEngine.PostgreSql;
    public Task InitializeAsync()=>container.StartAsync();
    public Task DisposeAsync()=>container.DisposeAsync().AsTask();
    public DbConnection CreateDatabase()
    {
        string name="loom_"+Guid.NewGuid().ToString("N");
        using var admin=new NpgsqlConnection(container.GetConnectionString());admin.Open();
        using var command=admin.CreateCommand();command.CommandText="CREATE DATABASE "+name;command.ExecuteNonQuery();
        return new NpgsqlConnection(new NpgsqlConnectionStringBuilder(container.GetConnectionString()){Database=name}.ConnectionString);
    }
}
public sealed class SqlServerFixture:IAsyncLifetime,IDatabaseFixture
{
    private readonly MsSqlContainer container=new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
    public ServerEngine Engine=>ServerEngine.SqlServer;
    public Task InitializeAsync()=>container.StartAsync();
    public Task DisposeAsync()=>container.DisposeAsync().AsTask();
    public DbConnection CreateDatabase()
    {
        string name="loom_"+Guid.NewGuid().ToString("N");
        using var admin=new SqlConnection(container.GetConnectionString());admin.Open();
        using var command=admin.CreateCommand();command.CommandText="CREATE DATABASE "+name;command.ExecuteNonQuery();
        return new SqlConnection(new SqlConnectionStringBuilder(container.GetConnectionString()){InitialCatalog=name}.ConnectionString);
    }
}
public abstract class ServerRepositoryContract(IDatabaseFixture fixture):RepositoryContract
{
    protected override IDocumentStore Open()=>new ServerDocumentStore(fixture.CreateDatabase(),fixture.Engine);
    private sealed class WorkerClock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    [Fact] public async Task IndependentDeliveryWorkersRecoverProviderContentionWithoutLosingMessages()
    {
        using var database=fixture.CreateDatabase();var connectionString=database.ConnectionString;
        var model=new Catalog([DeliveryOutbox.Definition("Delivery",new(){["Admin"]=["read","create","write"]})]);
        ServerDocumentStore Store()=>new(fixture.Engine==ServerEngine.PostgreSql?new NpgsqlConnection(connectionString):new SqlConnection(connectionString),fixture.Engine);
        ApplicationSession Session(ServerDocumentStore store)=>new(new(model,store,[]),Admin);
        var clock=new WorkerClock();var outbox=new DeliveryOutbox(new(MaxAttempts:10),clock);
        using(var store=Store())
        {
            store.Migrate(model);var session=Session(store);
            for(var i=0;i<8;i++)outbox.Enqueue(session,"Delivery","send",new(){["index"]=i});
        }
        ApplicationSessionScope Scope()
        {
            var store=Store();try{store.ReadCatalog();return new(Session(store),store);}catch{store.Dispose();throw;}
        }
        var accepted=new System.Collections.Concurrent.ConcurrentDictionary<string,bool>();
        var handlers=new Dictionary<string,Func<OutboxMessage,CancellationToken,Task>>{["send"]=async(message,token)=>
        {await Task.Yield();token.ThrowIfCancellationRequested();accepted.TryAdd(message.Key,true);}};
        var first=new DeliveryWorker(Scope,outbox,"Delivery",handlers,new(2,8));
        var second=new DeliveryWorker(Scope,outbox,"Delivery",handlers,new(2,8));
        await Task.WhenAll(first.RunOnceAsync(),second.RunOnceAsync()).WaitAsync(TimeSpan.FromSeconds(90));
        // Serializable providers may reject a claim or acknowledgement. Expiry plus a later poll
        // recovers it; the receiver deduplicates any acknowledgement-loss replay.
        var drain=new DeliveryWorker(Scope,outbox,"Delivery",handlers,new(1,8));
        for(var i=0;i<3;i++){clock.Now=clock.Now.AddMinutes(3);await drain.RunOnceAsync();}
        using var check=Scope();var rows=check.Session.Query("Delivery",new(model.Get("Delivery").Fields[0].Id!));
        Assert.Equal(8,accepted.Count);Assert.Equal(8,rows.Total);
        Assert.All(rows.Items,d=>Assert.Equal("Complete",d.Values["Status"]!.ToString()));
    }
    [Fact] public void Migration_preserves_identity_data_audit_and_reports_and_rejects_stale_plans()
    {
        using var store=(ServerDocumentStore)Open();var model=Model();var service=Service(store);
        var before=service.Create("Reading",new(){["Name"]="Calibration run",["Cost"]="123.45"},Admin);
        var old=model.Get("Reading");
        var target=new Catalog([old with { Version=2,TitleField="Caption",Fields=old.Fields.Select(f=>f.Name=="Name"?f with{Name="Caption"}:f).Append(new("Calibration","Calibration","Text",Required:true)).ToArray() }]);
        var fill=new MigrationBackfill(target.Get("Reading").Fields.Single(f=>f.Name=="Calibration").Id!,System.Text.Json.Nodes.JsonValue.Create("CAL-001"));
        var plan=store.Plan(target,[fill]);Assert.Contains(plan.Changes,c=>c.Kind=="RenameField");
        Assert.Equal(403,Assert.Throws<LoomException>(()=>store.Apply(plan.Id,new("viewer","Viewer"))).Status);
        var accepted=store.Apply(plan.Id,Admin);var updated=new DocumentService(accepted,store,[]).Get("Reading",before.Id,Admin);
        Assert.Equal("Calibration run",updated.Values["Caption"]!.ToString());Assert.Equal("CAL-001",updated.Values["Calibration"]!.ToString());
        Assert.Equal(before.Id,updated.Id);Assert.Equal(before.Version+1,updated.Version);Assert.False(updated.Values.ContainsKey("Name"));
        Assert.Single(store.Migrations());Assert.Equal(SchemaIdentity.Hash(target),SchemaIdentity.Hash(store.ReadCatalog()!));
        Assert.Equal(SchemaIdentity.Hash(target),SchemaIdentity.Hash(store.Apply(plan.Id,Admin)));
        Assert.Equal("Calibration run",store.Transaction(s=>s.Audit("Reading",before.Id)).Single().After!.Values["Name"]!.ToString());
        var report=new SavedReport("Results",old.Id!,[new(old.Fields.Single(f=>f.Name=="Name").Id!)]);
        Assert.Equal("Caption",SavedReports.Run(report,target,new(target,store,[]),Admin).Columns.Single().Name);
        Assert.Throws<LoomException>(()=>store.Plan(new Catalog([target.Get("Reading") with{Version=3,Fields=target.Get("Reading").Fields.Where(f=>f.Name!="Caption").ToArray(),TitleField="Calibration"}])));
    }
    [Fact] public void Failed_migration_rolls_back_prior_batches_schema_and_ledger()
    {
        using var store=(ServerDocumentStore)Open();var model=Model();store.Migrate(model);
        store.Transaction(s=> { for(var i=0;i<201;i++) s.Insert(new(i.ToString("D32"),"Reading",i==200?int.MaxValue:1,"Draft","now","now",new(){["Name"]="row"+i}));return 0; });
        var old=model.Get("Reading");var target=new Catalog([old with{Version=2,TitleField="Caption",Fields=old.Fields.Select(f=>f.Name=="Name"?f with{Name="Caption"}:f).ToArray()}]);
        var plan=store.Plan(target);
        Assert.Throws<OverflowException>(()=>store.Apply(plan.Id,Admin));
        Assert.Equal(SchemaIdentity.Hash(model),SchemaIdentity.Hash(store.ReadCatalog()!));Assert.Empty(store.Migrations());
        Assert.Equal(1,store.Transaction(s=>s.Get("Reading",0.ToString("D32")))!.Version);
        Assert.Equal("row0",store.Transaction(s=>s.Get("Reading",0.ToString("D32")))!.Values["Name"]!.ToString());
    }
    [Fact] public void Text_queries_match_utf16_ordinal_order_and_exact_filters()
    {
        using var store=Open();var service=Service(store);var schema=Model().Get("Reading");
        var docs=new[]{"A","a","é","Ω","😀","\uE000","x%_'"}.Select(v=>service.Create("Reading",new(){["Name"]=v},Admin)).ToArray();
        var field=schema.Fields.Single(f=>f.Name=="Name").Id!;
        foreach(var descending in new[]{false,true})
        {
            var query=new DocumentQuery(field,descending,1,4);
            Assert.Equal(Queries.Execute(docs,schema,query).Items.Select(d=>d.Id),service.Query("Reading",Admin,query).Items.Select(d=>d.Id));
        }
        Assert.Single(service.Query("Reading",Admin,new(field,Filters:[new(field,FilterOperator.Equal,System.Text.Json.Nodes.JsonValue.Create("x%_'"))])).Items);
    }
    [Fact] public void Numeric_queries_match_reference_order_filters_nulls_and_exact_money()
    {
        using var store=Open();var service=Service(store);var model=Model();var schema=model.Get("Reading");
        var values=new string?[]{null,"-2.50","0","2.00","2.00","12345678901234567.89"};
        var docs=values.Select((value,index)=>service.Create("Reading",new(){["Name"]="row"+index,["Cost"]=value},Admin)).ToArray();
        var field=schema.Fields.Single(f=>f.Name=="Cost").Id!;
        var filters=new FieldFilter[][]{[],[new(field,FilterOperator.Equal,System.Text.Json.Nodes.JsonValue.Create("2.0"))],
            [new(field,FilterOperator.GreaterThan,System.Text.Json.Nodes.JsonValue.Create("0"))],
            [new(field,FilterOperator.LessThan,System.Text.Json.Nodes.JsonValue.Create("12345678901234567.89"))],
            [new(field,FilterOperator.IsNull)]};
        foreach(var descending in new[]{false,true})foreach(var filter in filters)foreach(var skip in new[]{0,1,7})
        {
            var query=new DocumentQuery(field,descending,skip,2,filter);
            var expected=Queries.Execute(docs,schema,query);var actual=store.Transaction(session=>session.Query(schema,query));
            Assert.Equal(expected.Total,actual.Total);Assert.Equal(expected.Items.Select(d=>d.Id),actual.Items.Select(d=>d.Id));
        }
    }
    [Fact] public void Integer_extremes_and_fixed_decimal_scale_match_reference_queries()
    {
        var model=new Catalog([new("Numeric","Numeric",1,"Sequence",false,
            [new("Sequence","Sequence","Integer"),new("Exact","Exact","Decimal",Precision:28,Scale:20)],
            new(){["Admin"]=["read","create"]})]);
        using var store=Open();store.Migrate(model);var service=new DocumentService(model,store,[]);
        var docs=new[]{
            service.Create("Numeric",new(){["Sequence"]=long.MinValue,["Exact"]="-0.00000000000000000001"},Admin),
            service.Create("Numeric",new(){["Sequence"]=0,["Exact"]="0"},Admin),
            service.Create("Numeric",new(){["Sequence"]=long.MaxValue,["Exact"]="99999999.12345678901234567890"},Admin)};
        var schema=model.Get("Numeric");
        foreach(var field in schema.Fields)foreach(var descending in new[]{false,true})
        {
            var query=new DocumentQuery(field.Id!,descending,Filters:[new(field.Id!,FilterOperator.GreaterThan,System.Text.Json.Nodes.JsonValue.Create("0"))]);
            var expected=Queries.Execute(docs,schema,query);var actual=store.Transaction(s=>s.Query(schema,query));
            Assert.Equal(expected.Total,actual.Total);Assert.Equal(expected.Items.Select(d=>d.Id),actual.Items.Select(d=>d.Id));
        }
    }
    [Fact] public void Numeric_paging_reads_beyond_the_legacy_scan_limit()
    {
        var connection=fixture.CreateDatabase();using var store=new ServerDocumentStore(connection,fixture.Engine);var service=Service(store);
        var template=service.Create("Reading",new(){["Name"]="template",["Cost"]="123.45"},Admin);
        using(var command=connection.CreateCommand())
        {
            var parameter=command.CreateParameter();parameter.ParameterName="@body";
            parameter.Value=JsonSerializer.Serialize(template with{Id="DOCUMENT_ID"},Catalog.Json);command.Parameters.Add(parameter);
            command.CommandText=fixture.Engine==ServerEngine.PostgreSql?
                "INSERT INTO loom_documents SELECT 'Reading',lpad(n::text,32,'0'),1,replace(@body,'DOCUMENT_ID',lpad(n::text,32,'0')) FROM generate_series(1,10005) n":
                "WITH numbers AS (SELECT 1 n UNION ALL SELECT n+1 FROM numbers WHERE n<10005) INSERT INTO loom_documents SELECT 'Reading',RIGHT(REPLICATE('0',32)+CAST(n AS varchar(32)),32),1,REPLACE(@body,'DOCUMENT_ID',RIGHT(REPLICATE('0',32)+CAST(n AS varchar(32)),32)) FROM numbers OPTION (MAXRECURSION 0)";
            Assert.Equal(10005,command.ExecuteNonQuery());
        }
        var schema=Model().Get("Reading");var field=schema.Fields.Single(f=>f.Name=="Cost").Id!;
        var page=store.Transaction(s=>s.Query(schema,new(field,Skip:10000,Take:5)));
        Assert.Equal(10006,page.Total);Assert.Equal(5,page.Items.Count);
        Assert.Equal(Enumerable.Range(10001,5).Select(n=>n.ToString("D32")),page.Items.Select(d=>d.Id));
    }
    [Fact] public void Existing_pre_EF_tables_and_payloads_remain_readable_and_writable()
    {
        using var memory=new MemoryStore();var original=Service(memory);
        var doc=original.Create("Reading",new(){["Name"]="legacy Café",["Cost"]="12345678901234567.89",["Payload"]="AP+A"},Admin);
        var audit=original.History("Reading",doc.Id,Admin).Single();
        var connection=fixture.CreateDatabase();connection.Open();
        void Execute(string sql,params (string Name,object Value)[] parameters)
        {
            using var command=connection.CreateCommand();command.CommandText=sql;
            foreach(var (name,value) in parameters){var parameter=command.CreateParameter();parameter.ParameterName=name;parameter.Value=value;command.Parameters.Add(parameter);}
            command.ExecuteNonQuery();
        }
        var text=fixture.Engine==ServerEngine.PostgreSql?"text":"nvarchar(max)";
        Execute($"CREATE TABLE loom_schema (singleton int PRIMARY KEY, model {text} NOT NULL)");
        Execute($"CREATE TABLE loom_documents (kind varchar(63) NOT NULL, id varchar(32) NOT NULL, version int NOT NULL, body {text} NOT NULL, PRIMARY KEY(kind,id))");
        Execute($"CREATE TABLE loom_audit (id varchar(32) PRIMARY KEY, kind varchar(63) NOT NULL, document_id varchar(32) NOT NULL, body {text} NOT NULL)");
        Execute("INSERT INTO loom_schema VALUES (1,@model)",("@model",JsonSerializer.Serialize(Model().Types.Values.OrderBy(t=>t.Name,StringComparer.Ordinal),Catalog.Json)));
        Execute("INSERT INTO loom_documents VALUES (@kind,@id,1,@body)",("@kind",doc.Type),("@id",doc.Id),("@body",JsonSerializer.Serialize(doc,Catalog.Json)));
        Execute("INSERT INTO loom_audit VALUES (@id,@kind,@document,@body)",("@id",audit.Id),("@kind",doc.Type),("@document",doc.Id),("@body",JsonSerializer.Serialize(audit,Catalog.Json)));
        using var store=new ServerDocumentStore(connection,fixture.Engine);var service=Service(store);
        Assert.Equal(doc.Values.ToJsonString(),service.Get("Reading",doc.Id,Admin).Values.ToJsonString());
        Assert.Equal(audit.Id,service.History("Reading",doc.Id,Admin).Single().Id);
        service.Update("Reading",doc.Id,1,new(){["Name"]="updated"},Admin);
        Assert.Equal(2,service.Get("Reading",doc.Id,Admin).Version);
        Assert.Equal(2,service.History("Reading",doc.Id,Admin).Count);
    }
    [Fact] public void Storage_enforces_versions_and_recovers_after_constraint_failures()
    {
        using var store=Open();var service=Service(store);
        var doc=service.Create("Reading",new(){["Name"]="original"},Admin);
        Assert.Equal(409,Assert.Throws<LoomException>(()=>store.Transaction(s=>{s.Replace(doc with{Version=2},99);return 0;})).Status);
        Assert.Equal(409,Assert.Throws<LoomException>(()=>store.Transaction(s=>{s.Delete("Reading",doc.Id,99);return 0;})).Status);
        Assert.Equal(409,Assert.Throws<LoomException>(()=>store.Transaction(s=>{s.Insert(doc);return 0;})).Status);
        Assert.Equal(1,service.Get("Reading",doc.Id,Admin).Version);
        service.Update("Reading",doc.Id,1,new(){["Name"]="recovered"},Admin);
        Assert.Equal("recovered",service.Get("Reading",doc.Id,Admin).Values["Name"]!.ToString());
        Assert.Equal(2,service.History("Reading",doc.Id,Admin).Count);
    }
    [Fact] public void Session_reads_its_own_insert_update_and_delete()
    {
        using var store=Open();var service=Service(store);
        var doc=service.Create("Reading",new(){["Name"]="source"},Admin) with{Id=Guid.NewGuid().ToString("N")};
        store.Transaction(s=>
        {
            s.Insert(doc);Assert.Equal(1,s.Get("Reading",doc.Id)!.Version);
            s.Replace(doc with{Version=2},1);Assert.Equal(2,s.Get("Reading",doc.Id)!.Version);
            s.Delete("Reading",doc.Id,2);Assert.Null(s.Get("Reading",doc.Id));return 0;
        });
    }
    [Fact] public async Task Independent_connections_cannot_both_win_a_stale_update()
    {
        var firstConnection=fixture.CreateDatabase();var connectionString=firstConnection.ConnectionString;
        using var firstStore=new ServerDocumentStore(firstConnection,fixture.Engine);var first=Service(firstStore);
        DbConnection secondConnection=fixture.Engine==ServerEngine.PostgreSql?new NpgsqlConnection(connectionString):new SqlConnection(connectionString);
        using var secondStore=new ServerDocumentStore(secondConnection,fixture.Engine);var second=Service(secondStore);
        var doc=first.Create("Reading",new(){["Name"]="initial"},Admin);
        using var barrier=new Barrier(2);
        Task<int> Attempt(DocumentService service,string name)=>Task.Run(()=>
        {
            if(!barrier.SignalAndWait(TimeSpan.FromSeconds(20)))throw new TimeoutException("Concurrent test did not rendezvous.");
            try{service.Update("Reading",doc.Id,1,new(){["Name"]=name},Admin);return 200;}
            catch(LoomException ex){return ex.Status;}
        });
        var results=await Task.WhenAll(Attempt(first,"first"),Attempt(second,"second"));
        Assert.Equal(new[]{200,409},results.OrderBy(x=>x));
        Assert.Equal(2,first.Get("Reading",doc.Id,Admin).Version);
        Assert.Equal(2,first.History("Reading",doc.Id,Admin).Count);
    }
    [Fact] public void Changed_schema_is_rejected_before_running_transaction_callback()
    {
        // Keep the connection only to simulate an out-of-process schema change.
        var connection=fixture.CreateDatabase();using var store=new ServerDocumentStore(connection,fixture.Engine);store.Migrate(Model());
        using(var command=connection.CreateCommand()){command.CommandText="UPDATE loom_schema SET model='{}' WHERE singleton=1";Assert.Equal(1,command.ExecuteNonQuery());}
        bool called=false;
        Assert.Equal(409,Assert.Throws<LoomException>(()=>store.Transaction(session=>{called=true;return 0;})).Status);
        Assert.False(called);
    }
}
[Trait("Database","PostgreSQL")]
public sealed class PostgreSqlTests(PostgreSqlFixture fixture):ServerRepositoryContract(fixture),IClassFixture<PostgreSqlFixture>;
[Trait("Database","SQLServer")]
public sealed class SqlServerTests(SqlServerFixture fixture):ServerRepositoryContract(fixture),IClassFixture<SqlServerFixture>;
