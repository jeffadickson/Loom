using System.Globalization;
using System.Text.Json.Nodes;

namespace Loom.Core;

public sealed record Actor(string Id, string Role);
public interface IDocumentHook
{
    string Type { get; }
    IReadOnlyCollection<string> RequiredFields => [];
    void BeforeSave(JsonObject values);
}
public sealed record Page(int Total, IReadOnlyList<Document> Items);
public sealed class DocumentService(Catalog catalog, IDocumentStore store, IEnumerable<IDocumentHook> hooks)
{
    public TypedRepository<T> Repository<T>(Actor actor) where T : class, new() => new(this, catalog, actor);
    public bool Can(string type, Actor actor, string action) => catalog.Get(type).Permissions.TryGetValue(actor.Role, out var grants) && grants.Contains(action);
    private DocType Authorize(string type, Actor actor, string action)
    {
        var schema = catalog.Get(type);
        if (!Can(type, actor, action)) throw new LoomException(403, "This role cannot " + action + " " + schema.Label + ".");
        return schema;
    }
    private static Document Required(IStoreSession session, string type, string id) => session.Get(type, id) ?? throw new LoomException(404, "Document not found.");
    public Page Query(string type,Actor actor,DocumentQuery query)
    {
        var schema=Authorize(type,actor,"read");var validated=Queries.Validate(schema,query);
        return store.Transaction(s=>s.Query(schema,validated));
    }
    public Document Get(string type, string id, Actor actor)
    {
        Authorize(type, actor, "read");
        return store.Transaction(s => Required(s, type, id));
    }
    public Page List(string type, Actor actor, string? search = null, string? sort = null,
        bool descending = false, int skip = 0, int take = 50, string? field = null, string? equals = null)
    {
        var schema = Authorize(type, actor, "read");
        sort ??= schema.TitleField;
        if (!schema.Fields.Any(f => f.Name == sort && f.Type is not ("Binary" or "Measurement"))) throw new LoomException(400, "Invalid sort field.");
        if (field != null && !schema.Fields.Any(f => f.Name == field)) throw new LoomException(400, "Invalid filter field.");
        if (skip < 0 || take < 1 || take > 200) throw new LoomException(400, "Page size must be 1–200 and skip nonnegative.");
        return store.Transaction(s =>
        {
            IEnumerable<Document> rows = s.List(type);
            if (!string.IsNullOrWhiteSpace(search)) rows = rows.Where(d => d.Values.Any(v => schema.Fields.Single(f=>f.Name==v.Key).Type!="Binary" && (v.Value?.ToString() ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)));
            if (field != null) rows = rows.Where(d => (d.Values[field]?.ToString() ?? "") == equals);
            var list = rows.ToList();
            var definition = schema.Fields.Single(f => f.Name == sort);
            Func<Document, IComparable> key = definition.Type is "Integer" or "Decimal" or "Money"
                ? d => decimal.TryParse(d.Values[sort]?.ToString(), CultureInfo.InvariantCulture, out var n) ? n : decimal.MinValue
                : d => d.Values[sort]?.ToString() ?? "";
            var ordered = descending ? list.OrderByDescending(key) : list.OrderBy(key);
            return new Page(list.Count, ordered.ThenBy(d => d.Id).Skip(skip).Take(take).ToList());
        });
    }
    public Document Create(string type, JsonObject values, Actor actor)
    {
        var schema = Authorize(type, actor, "create");
        return store.Transaction(s =>
        {
            var normalized = Validate(schema, values, s, null, actor);
            var now = DateTimeOffset.UtcNow.ToString("O");
            var d = new Document(Guid.NewGuid().ToString("N"), type, 1, "Draft", now, now, normalized);
            s.Insert(d); Audit(s, actor, "create", null, d); return d;
        });
    }
    public Document Update(string type, string id, int version, JsonObject values, Actor actor)
    {
        var schema = Authorize(type, actor, "write");
        if(schema.Immutable)throw new LoomException(409,"Immutable records require a new amendment record.");
        return store.Transaction(s =>
        {
            var old = Required(s, type, id);
            if (old.Version != version) throw new LoomException(409, "Document changed. Reload before saving.");
            if (old.State != "Draft") throw new LoomException(409, "Only draft documents can be edited.");
            var d = old with { Version = old.Version + 1, ModifiedAt = DateTimeOffset.UtcNow.ToString("O"), Values = Validate(schema, values, s, id, actor) };
            s.Replace(d, version); Audit(s, actor, "update", old, d); return d;
        });
    }
    public Document Transition(string type, string id, int version, string action, Actor actor)
    {
        if (action is not ("submit" or "cancel")) throw new LoomException(400, "Unknown transition.");
        var schema = Authorize(type, actor, action);
        if (!schema.Submittable) throw new LoomException(400, "Document type is not submittable.");
        return store.Transaction(s =>
        {
            var old = Required(s, type, id);
            if (old.Version != version) throw new LoomException(409, "Document changed.");
            if (old.State != (action == "submit" ? "Draft" : "Submitted")) throw new LoomException(409, "Invalid state transition.");
            var d = old with { Version = old.Version + 1, State = action == "submit" ? "Submitted" : "Cancelled", ModifiedAt = DateTimeOffset.UtcNow.ToString("O") };
            s.Replace(d, version); Audit(s, actor, action, old, d); return d;
        });
    }
    public void Delete(string type, string id, int version, Actor actor)
    {
        if(Authorize(type, actor, "delete").Immutable)throw new LoomException(409,"Immutable records cannot be deleted.");
        store.Transaction(s =>
        {
            var old = Required(s, type, id);
            if (old.State != "Draft") throw new LoomException(409, "Only drafts can be deleted.");
            foreach (var other in catalog.Types.Values)
                foreach (var f in other.Fields.Where(f => f.Type == "Link" && f.Target == type))
                    if (s.List(other.Name).Any(d => d.Values[f.Name]?.ToString() == id)) throw new LoomException(409, "Document is referenced; remove links first.");
            s.Delete(type, id, version); Audit(s, actor, "delete", old, null); return 0;
        });
    }
    public IReadOnlyList<AuditEntry> History(string type, string id, Actor actor)
    {
        Authorize(type, actor, "audit");
        return store.Transaction(s => s.Audit(type, id));
    }
    private JsonObject Validate(DocType schema, JsonObject input, IStoreSession session, string? id, Actor actor)
    {
        foreach (var key in input.Select(x => x.Key))
            if (!schema.Fields.Any(f => f.Name == key && !f.ReadOnly)) throw new LoomException(400, "Unknown or read-only field: " + key);
        var values = (JsonObject)input.DeepClone();
        if(id==null)foreach(var f in schema.Fields.Where(f=>f.Default!=null && !values.ContainsKey(f.Name)))values[f.Name]=f.Default;
        foreach (var hook in hooks.Where(h => h.Type == schema.Name)) hook.BeforeSave(values);
        foreach (var f in schema.Fields)
        {
            var node = values[f.Name];
            var text = node?.ToString().Trim() ?? "";
            if (text.Length == 0 && !(f.Type=="Binary" && node!=null))
            {
                if (f.Required) throw new LoomException(422, f.Label + " is required.");
                values[f.Name] = null; continue;
            }
            if (f.Type!="Measurement" && (node is JsonArray or JsonObject)) throw new LoomException(422, f.Label + " must be a scalar.");
            if(f.Type=="Link")
            {
                Authorize(f.Target!,actor,"read");
                if(session.Get(f.Target!,text)==null)throw new LoomException(422,f.Label+" references a missing document.");
                values[f.Name]=text;
            }
            else values[f.Name]=FieldValues.Normalize(f,node!);
            if (f.Unique && session.List(schema.Name).Any(d => d.Id != id && d.Values[f.Name]?.ToString() == values[f.Name]?.ToString()))
                throw new LoomException(409, f.Label + " must be unique.");
        }
        if(schema.Fields.Where(f=>f.Type=="Binary").Sum(f=>(long)(values[f.Name]==null?0:Convert.FromBase64String(values[f.Name]!.ToString()).Length))>FieldValues.MaxBinaryBytes)
            throw new LoomException(422,"Combined binary data exceeds the 8 MiB document limit.");
        return values;
    }
    private static void Audit(IStoreSession s, Actor actor, string action, Document? before, Document? after)
    {
        var d = after ?? before!;
        s.AppendAudit(new(Guid.NewGuid().ToString("N"), d.Type, d.Id, actor.Id, action, DateTimeOffset.UtcNow.ToString("O"), before, after));
    }
}
