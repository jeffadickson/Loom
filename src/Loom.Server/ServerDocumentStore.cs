using System.Data;
using System.Data.Common;
using System.Text.Json;
using Loom.Core;

namespace Loom.Server;

public enum ServerEngine { PostgreSql, SqlServer }
/// <summary>
/// Initial server provider using versioned JSON documents. Supply NpgsqlConnection or SqlConnection.
/// Owns the supplied connection. Schema changes require explicit future migration support.
/// </summary>
public sealed class ServerDocumentStore : IDocumentStore
{
    private readonly DbConnection connection;
    private readonly ServerEngine engine;
    private readonly object gate=new();
    private Catalog? catalog;
    public ProviderInfo Info=>new(engine.ToString(),true,["transactions","optimistic-concurrency","audit","json-document-storage"]);
    public ServerDocumentStore(DbConnection connection,ServerEngine engine)
    {
        if(!Enum.IsDefined(engine))throw new ArgumentOutOfRangeException(nameof(engine));
        this.connection=connection;this.engine=engine;
        if(connection.State!=ConnectionState.Open)connection.Open();
    }
    private DbCommand Command(DbTransaction tx,string sql,params (string,object?)[] args)
    {
        var command=connection.CreateCommand();command.Transaction=tx;command.CommandText=sql;
        foreach(var (key,value) in args){var parameter=command.CreateParameter();parameter.ParameterName=key;parameter.Value=value??DBNull.Value;command.Parameters.Add(parameter);}return command;
    }
    public void Migrate(Catalog next)
    {
        lock(gate)
        {
            using var tx=connection.BeginTransaction(IsolationLevel.Serializable);
            var text=engine==ServerEngine.PostgreSql?"text":"nvarchar(max)";
            var definitions=new Dictionary<string,string>
            {
                ["loom_schema"]=$"singleton int PRIMARY KEY, model {text} NOT NULL",
                ["loom_documents"]=$"kind varchar(63) NOT NULL, id varchar(32) NOT NULL, version int NOT NULL, body {text} NOT NULL, PRIMARY KEY(kind,id)",
                ["loom_audit"]=$"id varchar(32) PRIMARY KEY, kind varchar(63) NOT NULL, document_id varchar(32) NOT NULL, body {text} NOT NULL"
            };
            foreach(var (name,definition) in definitions)
            {
                var sql=engine==ServerEngine.PostgreSql?$"CREATE TABLE IF NOT EXISTS {name} ({definition})":$"IF OBJECT_ID(N'{name}', N'U') IS NULL CREATE TABLE {name} ({definition})";
                using var create=Command(tx,sql);create.ExecuteNonQuery();
            }
            var model=JsonSerializer.Serialize(next.Types.Values.OrderBy(t=>t.Name,StringComparer.Ordinal),Catalog.Json);
            using var read=Command(tx,"SELECT model FROM loom_schema WHERE singleton=1");var existing=read.ExecuteScalar() as string;
            if(existing!=null && existing!=model)throw new LoomException(409,"Server schema differs. This initial provider does not perform schema migrations.");
            if(existing==null){using var insert=Command(tx,"INSERT INTO loom_schema(singleton,model) VALUES(1,@model)",("@model",model));insert.ExecuteNonQuery();}
            tx.Commit();catalog=next;
        }
    }
    public T Transaction<T>(Func<IStoreSession,T> action)
    {
        lock(gate)
        {
            if(catalog==null)throw new InvalidOperationException("Call Migrate first.");
            using var tx=connection.BeginTransaction(IsolationLevel.Serializable);
            try{var result=action(new Session(this,tx,catalog));tx.Commit();return result;}
            catch(DbException ex)when(IsConflict(ex)){throw new LoomException(409,"Concurrent transaction or database constraint conflict; retry the whole operation.");}
        }
    }
    private static bool IsConflict(DbException exception)
    {
        var state=exception.GetType().GetProperty("SqlState")?.GetValue(exception)?.ToString();
        var number=exception.GetType().GetProperty("Number")?.GetValue(exception) as int?;
        return state is "40001" or "40P01" or "23505" or "23503" or "23514" || number is 1205 or 2601 or 2627 or 547;
    }
    public void Dispose()=>connection.Dispose();
    private sealed class Session(ServerDocumentStore owner,DbTransaction tx,Catalog catalog):IStoreSession
    {
        private void Type(string type)=>catalog.Get(type);
        public Document? Get(string type,string id)
        {
            Type(type);using var command=owner.Command(tx,"SELECT body FROM loom_documents WHERE kind=@kind AND id=@id",("@kind",type),("@id",id));
            return command.ExecuteScalar() is string json?JsonSerializer.Deserialize<Document>(json,Catalog.Json):null;
        }
        public IReadOnlyList<Document> List(string type)
        {
            Type(type);string sql=owner.engine==ServerEngine.SqlServer?"SELECT TOP (10001) body FROM loom_documents WHERE kind=@kind":"SELECT body FROM loom_documents WHERE kind=@kind LIMIT 10001";
            using var command=owner.Command(tx,sql,("@kind",type));using var reader=command.ExecuteReader();var rows=new List<Document>();
            while(reader.Read()){if(rows.Count==10000)throw new LoomException(422,"Server query exceeds initial provider scan limit.");rows.Add(JsonSerializer.Deserialize<Document>(reader.GetString(0),Catalog.Json)!);}return rows;
        }
        public void Insert(Document document)
        {
            Type(document.Type);using var command=owner.Command(tx,"INSERT INTO loom_documents(kind,id,version,body) VALUES(@kind,@id,@version,@body)",("@kind",document.Type),("@id",document.Id),("@version",document.Version),("@body",JsonSerializer.Serialize(document,Catalog.Json)));command.ExecuteNonQuery();
        }
        public void Replace(Document document,int expectedVersion)
        {
            Type(document.Type);using var command=owner.Command(tx,"UPDATE loom_documents SET version=@version,body=@body WHERE kind=@kind AND id=@id AND version=@expected",("@version",document.Version),("@body",JsonSerializer.Serialize(document,Catalog.Json)),("@kind",document.Type),("@id",document.Id),("@expected",expectedVersion));
            if(command.ExecuteNonQuery()!=1)throw new LoomException(409,"Document changed.");
        }
        public void Delete(string type,string id,int expectedVersion)
        {
            Type(type);using var command=owner.Command(tx,"DELETE FROM loom_documents WHERE kind=@kind AND id=@id AND version=@version",("@kind",type),("@id",id),("@version",expectedVersion));if(command.ExecuteNonQuery()!=1)throw new LoomException(409,"Document changed.");
        }
        public void AppendAudit(AuditEntry entry)
        {
            using var command=owner.Command(tx,"INSERT INTO loom_audit(id,kind,document_id,body) VALUES(@id,@kind,@document,@body)",("@id",entry.Id),("@kind",entry.Type),("@document",entry.DocumentId),("@body",JsonSerializer.Serialize(entry,Catalog.Json)));command.ExecuteNonQuery();
        }
        public IReadOnlyList<AuditEntry> Audit(string type,string id)
        {
            Type(type);using var command=owner.Command(tx,"SELECT body FROM loom_audit WHERE kind=@kind AND document_id=@id",("@kind",type),("@id",id));using var reader=command.ExecuteReader();var entries=new List<AuditEntry>();while(reader.Read())entries.Add(JsonSerializer.Deserialize<AuditEntry>(reader.GetString(0),Catalog.Json)!);return entries.OrderBy(e=>e.At,StringComparer.Ordinal).ThenBy(e=>e.Id,StringComparer.Ordinal).ToArray();
        }
    }
}
