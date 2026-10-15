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
public sealed class DocumentService(Catalog catalog, IDocumentStore store, IEnumerable<IDocumentHook> hooks, IDocumentAccessPolicy? access = null)
{
    /// <summary>Runs multiple authorized operations in one provider transaction. Do not retain the scoped service.</summary>
    public T Atomic<T>(Func<DocumentService,T> action)
    {
        // Async callbacks would commit before awaited work finishes. Reject before invoking user code.
        var resultType=typeof(T);
        if(typeof(Task).IsAssignableFrom(resultType) || resultType==typeof(ValueTask) ||
           resultType.IsGenericType && resultType.GetGenericTypeDefinition()==typeof(ValueTask<>))
            throw new InvalidOperationException("Atomic callbacks must be synchronous.");
        return store.Transaction(session =>
        {
            using var scoped = new TransactionStore(session, store.Info);
            return action(new DocumentService(catalog, scoped, hooks, access));
        });
    }

    // The provider owns commit/rollback; an escaped callback capability expires on return.
    private sealed class TransactionStore(IStoreSession session, ProviderInfo info) : IDocumentStore
    {
        private bool active = true;
        public ProviderInfo Info => info;
        public T Transaction<T>(Func<IStoreSession,T> action)
        {
            if(!active)throw new ObjectDisposedException(nameof(TransactionStore));
            return action(session);
        }
        public void Migrate(Catalog _) => throw new InvalidOperationException("Schema changes cannot join a document transaction.");
        public void Dispose() => active = false;
    }

    public bool CanReadField(Field field, Actor actor) => access?.CanRead(field, actor) ?? true;
    private Document Visible(DocType schema, Document document, Actor actor)
    {
        var copy = document.Copy();
        foreach (var field in schema.Fields.Where(f => !CanReadField(f, actor))) copy.Values.Remove(field.Name);
        return copy;
    }
    private void CheckRecord(DocType schema, Document document, Actor actor)
    {
        if (access == null) return;
        var query = new DocumentQuery(schema.Fields.Single(f => f.Name == schema.TitleField).Id!, Filters: access.Scope(schema, actor));
        if (Queries.Execute([document], schema, query).Total != 1) throw new LoomException(404, "Document not found.");
    }
    private JsonObject Writable(DocType schema, JsonObject values, Actor actor, Document? old = null)
    {
        var copy = (JsonObject)values.DeepClone();
        foreach (var field in schema.Fields.Where(f => !(access?.CanWrite(f, actor) ?? true)))
        {
            if (copy.ContainsKey(field.Name)) throw new LoomException(403, "This account cannot write " + field.Label + ".");
            if (old != null) copy[field.Name] = old.Values[field.Name]?.DeepClone();
        }
        return copy;
    }
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
        var hidden = schema.Fields.Where(f => !CanReadField(f, actor)).Select(f => f.Id).ToHashSet();
        if (hidden.Contains(validated.SortFieldId) || validated.Filters!.Any(f => hidden.Contains(f.FieldId)) || hidden.Count > 0 && !string.IsNullOrWhiteSpace(validated.Search))
            throw new LoomException(403, "Query would inspect a restricted field.");
        validated = validated with { Filters = validated.Filters!.Concat(access?.Scope(schema, actor) ?? []).ToArray() };
        return store.Transaction(s => { var page = s.Query(schema, validated); return page with { Items = page.Items.Select(d => Visible(schema, d, actor)).ToArray() }; });
    }
    public Document Get(string type, string id, Actor actor)
    {
        var schema = Authorize(type, actor, "read");
        return store.Transaction(s => { var d = Required(s, type, id); CheckRecord(schema, d, actor); return Visible(schema, d, actor); });
    }
    public Page List(string type, Actor actor, string? search = null, string? sort = null,
        bool descending = false, int skip = 0, int take = 50, string? field = null, string? equals = null)
    {
        var schema = Authorize(type, actor, "read");
        sort ??= schema.TitleField;
        if (!schema.Fields.Any(f => f.Name == sort && f.Type is not ("Binary" or "Measurement"))) throw new LoomException(400, "Invalid sort field.");
        if (field != null && !schema.Fields.Any(f => f.Name == field)) throw new LoomException(400, "Invalid filter field.");
        if (skip < 0 || take < 1 || take > 200) throw new LoomException(400, "Page size must be 1–200 and skip nonnegative.");
        var sortField=schema.Fields.Single(f=>f.Name==sort);
        var filterField=field==null?null:schema.Fields.Single(f=>f.Name==field);
        FieldFilter[] filters=filterField==null?[]:[new(filterField.Id!,string.IsNullOrEmpty(equals)?FilterOperator.IsNull:FilterOperator.Equal,string.IsNullOrEmpty(equals)?null:JsonValue.Create(equals))];
        return Query(type,actor,new(sortField.Id!,descending,skip,take,filters,search));
    }

    public Document Create(string type, JsonObject values, Actor actor)
    {
        var schema = Authorize(type, actor, "create");
        return store.Transaction(s =>
        {
            var normalized = Validate(schema, Writable(schema, values, actor), s, null, actor);
            var now = DateTimeOffset.UtcNow.ToString("O");
            var d = new Document(Guid.NewGuid().ToString("N"), type, 1, "Draft", now, now, normalized);
            CheckRecord(schema, d, actor); s.Insert(d); Audit(s, actor, "create", null, d); return Visible(schema, d, actor);
        });
    }
    public Document Update(string type, string id, int version, JsonObject values, Actor actor)
    {
        var schema = Authorize(type, actor, "write");
        if(schema.Immutable)throw new LoomException(409,"Immutable records require a new amendment record.");
        return store.Transaction(s =>
        {
            var old = Required(s, type, id);
            CheckRecord(schema, old, actor);
            if (old.Version != version) throw new LoomException(409, "Document changed. Reload before saving.");
            if (old.State != "Draft") throw new LoomException(409, "Only draft documents can be edited.");
            var d = old with { Version = old.Version + 1, ModifiedAt = DateTimeOffset.UtcNow.ToString("O"), Values = Validate(schema, Writable(schema, values, actor, old), s, id, actor) };
            CheckRecord(schema, d, actor); s.Replace(d, version); Audit(s, actor, "update", old, d); return Visible(schema, d, actor);
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
            CheckRecord(schema, old, actor);
            if (old.Version != version) throw new LoomException(409, "Document changed.");
            if (old.State != (action == "submit" ? "Draft" : "Submitted")) throw new LoomException(409, "Invalid state transition.");
            var d = old with { Version = old.Version + 1, State = action == "submit" ? "Submitted" : "Cancelled", ModifiedAt = DateTimeOffset.UtcNow.ToString("O") };
            s.Replace(d, version); Audit(s, actor, action, old, d); return Visible(schema, d, actor);
        });
    }
    public void Delete(string type, string id, int version, Actor actor)
    {
        var schema = Authorize(type, actor, "delete");
        if(schema.Immutable)throw new LoomException(409,"Immutable records cannot be deleted.");
        store.Transaction(s =>
        {
            var old = Required(s, type, id);
            CheckRecord(schema, old, actor);
            if (old.State != "Draft") throw new LoomException(409, "Only drafts can be deleted.");
            foreach (var other in catalog.Types.Values)
                foreach (var f in other.Fields.Where(f => f.Type == "Link" && f.Target == type))
                    if (s.Query(other, new DocumentQuery(f.Id!, Take:1, Filters:[new(f.Id!,FilterOperator.Equal,JsonValue.Create(id))])).Total > 0) throw new LoomException(409, "Document is referenced; remove links first.");
            s.Delete(type, id, version); Audit(s, actor, "delete", old, null); return 0;
        });
    }
    public IReadOnlyList<AuditEntry> History(string type, string id, Actor actor)
    {
        var schema = Authorize(type, actor, "audit");
        return store.Transaction(s =>
        {
            var entries = s.Audit(type, id);
            if(access == null) return entries;
            var current = s.Get(type, id) ?? entries.LastOrDefault()?.Before ?? throw new LoomException(404, "Document not found.");
            CheckRecord(schema, current, actor);
            // Fail closed if historical ownership differs; never expose another owner's snapshots.
            foreach (var e in entries) { if(e.Before != null) CheckRecord(schema,e.Before,actor); if(e.After != null) CheckRecord(schema,e.After,actor); }
            return (IReadOnlyList<AuditEntry>)entries.Select(e => e with { Before = e.Before == null ? null : Visible(schema,e.Before,actor), After = e.After == null ? null : Visible(schema,e.After,actor) }).ToArray();
        });
    }
    private JsonObject Validate(DocType schema, JsonObject input, IStoreSession session, string? id, Actor actor)
    {
        foreach (var key in input.Select(x => x.Key))
            if (!schema.Fields.Any(f => f.Name == key && (!f.ReadOnly || id != null && access?.CanWrite(f, actor) == false))) throw new LoomException(400, "Unknown or read-only field: " + key);
        var values = (JsonObject)input.DeepClone();
        if(id==null)foreach(var f in schema.Fields.Where(f=>f.Default!=null && !values.ContainsKey(f.Name)))values[f.Name]=f.Default;
        foreach (var hook in hooks.Where(h => h.Type == schema.Name)) hook.BeforeSave(values);
        if(values.Any(v=>!schema.Fields.Any(f=>f.Name==v.Key)))throw new LoomException(422,"Hook produced an undeclared field.");
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
                var linked = session.Get(f.Target!,text) ?? throw new LoomException(422,f.Label+" references a missing document.");
                CheckRecord(catalog.Get(f.Target!), linked, actor);
                values[f.Name]=text;
            }
            else values[f.Name]=FieldValues.Normalize(f,node!);
            if (f.Unique && session.Query(schema, new DocumentQuery(f.Id!, Take:2, Filters:[new(f.Id!,FilterOperator.Equal,values[f.Name]?.DeepClone())])).Items.Any(d => d.Id != id))
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
