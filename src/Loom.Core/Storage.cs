using System.Text.Json;
using System.Text.Json.Nodes;

namespace Loom.Core;

public sealed record Document(string Id, string Type, int Version, string State,
    string CreatedAt, string ModifiedAt, JsonObject Values)
{
    public Document Copy() => this with { Values = (JsonObject)Values.DeepClone() };
}
public sealed record AuditEntry(string Id, string Type, string DocumentId, string Actor,
    string Action, string At, Document? Before, Document? After);
public sealed record ProviderInfo(string Name, bool Durable, string[] Capabilities);

// A single transaction covers validation, links, uniqueness, write, and audit.
public interface IDocumentStore : IDisposable
{
    ProviderInfo Info { get; }
    void Migrate(Catalog catalog);
    T Transaction<T>(Func<IStoreSession, T> action);
}
public interface IStoreSession
{
    Page Query(DocType schema,DocumentQuery query)=>Queries.Execute(List(schema.Name),schema,query);
    Document? Get(string type, string id);
    IReadOnlyList<Document> List(string type);
    void Insert(Document document);
    void Replace(Document document, int expectedVersion);
    void Delete(string type, string id, int expectedVersion);
    void AppendAudit(AuditEntry entry);
    IReadOnlyList<AuditEntry> Audit(string type, string id);
}

public sealed class MemoryStore : IDocumentStore, IStoreSession
{
    private Dictionary<string, Document> documents = [];
    private List<AuditEntry> audit = [];
    private readonly object gate = new();
    public ProviderInfo Info => new("Memory", false, ["transactions", "optimistic-concurrency", "audit"]);
    public void Migrate(Catalog catalog)
    {
        lock(gate)
            foreach(var document in documents.Values)
                foreach(var field in catalog.Get(document.Type).Fields.Where(f=>f.Type=="Measurement"))
                    if(document.Values[field.Name] is JsonNode value)FieldValues.Normalize(field,value);
    }
    public T Transaction<T>(Func<IStoreSession, T> action)
    {
        lock (gate)
        {
            var old = documents.ToDictionary(x => x.Key, x => x.Value.Copy());
            var oldAudit = audit.ToList();
            try { return action(this); }
            catch { documents = old; audit = oldAudit; throw; }
        }
    }
    public Page Query(DocType schema,DocumentQuery query)
    {
        lock(gate)return Queries.Execute(documents.Values.Where(d=>d.Type==schema.Name),schema,query);
    }
    private static string Key(string type, string id) => type + "/" + id;
    public Document? Get(string type, string id) => documents.GetValueOrDefault(Key(type, id))?.Copy();
    public IReadOnlyList<Document> List(string type) => documents.Values.Where(x => x.Type == type).Select(x => x.Copy()).ToList();
    public void Insert(Document d)
    {
        if (!documents.TryAdd(Key(d.Type, d.Id), d.Copy())) throw new LoomException(409, "Duplicate document.");
    }
    public void Replace(Document d, int expectedVersion)
    {
        if (Get(d.Type, d.Id)?.Version != expectedVersion) throw new LoomException(409, "Document changed. Reload before saving.");
        documents[Key(d.Type, d.Id)] = d.Copy();
    }
    public void Delete(string type, string id, int expectedVersion)
    {
        if (Get(type, id)?.Version != expectedVersion) throw new LoomException(409, "Document changed.");
        documents.Remove(Key(type, id));
    }
    public void AppendAudit(AuditEntry entry) => audit.Add(JsonSerializer.Deserialize<AuditEntry>(JsonSerializer.Serialize(entry))!);
    public IReadOnlyList<AuditEntry> Audit(string type, string id) => audit.Where(x => x.Type == type && x.DocumentId == id)
        .Select(x => JsonSerializer.Deserialize<AuditEntry>(JsonSerializer.Serialize(x))!).ToList();
    public void Dispose() { }
}
