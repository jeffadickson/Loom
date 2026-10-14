using System.Data;
using System.Data.Common;
using System.Text.Json;
using Loom.Core;
using Microsoft.EntityFrameworkCore;

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
    public ProviderInfo Info=>new(engine.ToString(),true,["transactions","optimistic-concurrency","audit","json-document-storage","ef-core"]);
    public ServerDocumentStore(DbConnection connection,ServerEngine engine)
    {
        if(!Enum.IsDefined(engine))throw new ArgumentOutOfRangeException(nameof(engine));
        this.connection=connection;this.engine=engine;
        if(connection.State!=ConnectionState.Open)connection.Open();
    }
    private LoomDbContext Context()
    {
        var options=new DbContextOptionsBuilder<LoomDbContext>();
        if(engine==ServerEngine.PostgreSql)options.UseNpgsql(connection);
        else options.UseSqlServer(connection);
        return new(options.Options);
    }
    private static string ModelJson(Catalog model)=>JsonSerializer.Serialize(model.Types.Values.OrderBy(t=>t.Name,StringComparer.Ordinal),Catalog.Json);
    public void Migrate(Catalog next)
    {
        lock(gate)
        {
            using var db=Context();
            using var tx=db.Database.BeginTransaction(IsolationLevel.Serializable);
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
                // Only bootstrap DDL remains explicit to adopt pre-EF databases unchanged.
                db.Database.ExecuteSqlRaw(sql);
            }
            var model=ModelJson(next);
            var existing=db.Models.AsNoTracking().Where(x=>x.Singleton==1).Select(x=>x.Model).SingleOrDefault();
            if(existing!=null && existing!=model)throw new LoomException(409,"Server schema differs. This provider does not yet perform application schema migrations.");
            if(existing==null){db.Models.Add(new(){Singleton=1,Model=model});db.SaveChanges();}
            tx.Commit();catalog=next;
        }
    }
    public T Transaction<T>(Func<IStoreSession,T> action)
    {
        lock(gate)
        {
            if(catalog==null)throw new InvalidOperationException("Call Migrate first.");
            using var db=Context();
            using var tx=db.Database.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                var accepted=db.Models.AsNoTracking().Where(x=>x.Singleton==1).Select(x=>x.Model).SingleOrDefault();
                if(accepted!=ModelJson(catalog))throw new LoomException(409,"Database model changed; restart this host with the accepted model.");
                var result=action(new Session(db,catalog));tx.Commit();return result;
            }
            catch(DbUpdateConcurrencyException){throw new LoomException(409,"Document changed. Reload before saving.");}
            // Provider execution strategies can wrap transient conflicts even when retries
            // are disabled. Never replay callbacks/hooks automatically.
            catch(InvalidOperationException ex)when(ex.InnerException is DbUpdateException or DbException && HasDatabaseConflict(ex))
            {throw new LoomException(409,"Concurrent transaction or database constraint conflict; retry the whole operation.");}
            catch(DbUpdateException ex)when(ex.InnerException is DbException database && IsConflict(database))
            {throw new LoomException(409,"Concurrent transaction or database constraint conflict; retry the whole operation.");}
            catch(DbException ex)when(IsConflict(ex)){throw new LoomException(409,"Concurrent transaction or database constraint conflict; retry the whole operation.");}
        }
    }
    private static bool HasDatabaseConflict(Exception exception)
    {
        for(Exception? current=exception;current!=null;current=current.InnerException)
            if(current is DbException database && IsConflict(database))return true;
        return false;
    }
    private static bool IsConflict(DbException exception)
    {
        var state=exception.GetType().GetProperty("SqlState")?.GetValue(exception)?.ToString();
        var number=exception.GetType().GetProperty("Number")?.GetValue(exception) as int?;
        return state is "40001" or "40P01" or "23505" or "23503" or "23514" || number is 1205 or 2601 or 2627 or 547;
    }
    public void Dispose()=>connection.Dispose();
    private sealed class Session(LoomDbContext db,Catalog catalog):IStoreSession
    {
        private void Type(string type)=>catalog.Get(type);
        private static string Json<T>(T value)=>JsonSerializer.Serialize(value,Catalog.Json);
        // Flush inside the outer transaction: later session reads see these writes. EF's
        // tracker is cleared after each flush; no stale tracked entity escapes a callback.
        private void Flush(){db.SaveChanges();db.ChangeTracker.Clear();}
        public Document? Get(string type,string id)
        {
            Type(type);var json=db.Documents.AsNoTracking().Where(d=>d.Kind==type && d.Id==id).Select(d=>d.Body).SingleOrDefault();
            return json==null?null:JsonSerializer.Deserialize<Document>(json,Catalog.Json);
        }
        public IReadOnlyList<Document> List(string type)
        {
            Type(type);var rows=db.Documents.AsNoTracking().Where(d=>d.Kind==type).OrderBy(d=>d.Id).Select(d=>d.Body).Take(10001).ToArray();
            if(rows.Length>10000)throw new LoomException(422,"Server query exceeds initial provider scan limit.");
            return rows.Select(json=>JsonSerializer.Deserialize<Document>(json,Catalog.Json)!).ToArray();
        }
        public void Insert(Document document)
        {
            Type(document.Type);db.Documents.Add(new(){Kind=document.Type,Id=document.Id,Version=document.Version,Body=Json(document)});Flush();
        }
        public void Replace(Document document,int expectedVersion)
        {
            Type(document.Type);var row=new StoredDocument{Kind=document.Type,Id=document.Id,Version=document.Version,Body=Json(document)};
            var entry=db.Attach(row);entry.Property(d=>d.Version).OriginalValue=expectedVersion;
            entry.Property(d=>d.Version).IsModified=true;entry.Property(d=>d.Body).IsModified=true;Flush();
        }
        public void Delete(string type,string id,int expectedVersion)
        {
            Type(type);db.Documents.Remove(new StoredDocument{Kind=type,Id=id,Version=expectedVersion});Flush();
        }
        public void AppendAudit(AuditEntry entry)
        {
            db.Audits.Add(new(){Id=entry.Id,Kind=entry.Type,DocumentId=entry.DocumentId,Body=Json(entry)});Flush();
        }
        public IReadOnlyList<AuditEntry> Audit(string type,string id)
        {
            Type(type);return db.Audits.AsNoTracking().Where(a=>a.Kind==type && a.DocumentId==id).Select(a=>a.Body).ToArray()
                .Select(json=>JsonSerializer.Deserialize<AuditEntry>(json,Catalog.Json)!).OrderBy(e=>e.At,StringComparer.Ordinal).ThenBy(e=>e.Id,StringComparer.Ordinal).ToArray();
        }
    }
}
