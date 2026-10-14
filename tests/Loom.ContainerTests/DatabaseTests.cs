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
