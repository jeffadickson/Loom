using System.Data.Common;
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
