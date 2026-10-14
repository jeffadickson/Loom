using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Loom.Core;

public sealed record TypedDocument<T>(string Id, int Version, string State, string CreatedAt, string ModifiedAt, T Value);
public sealed record TypedPage<T>(int Total, IReadOnlyList<TypedDocument<T>> Items);

// A facade over DocumentService: there is no second persistence/authorization path.
public sealed class TypedRepository<T> where T : class, new()
{
    private readonly DocumentService service;
    private readonly Actor actor;
    private readonly DocType schema;
    private readonly (PropertyInfo Property, Field Field)[] bindings;

    internal TypedRepository(DocumentService service, Catalog catalog, Actor actor)
    {
        this.service = service; this.actor = actor;
        var declared = AttributeCatalog.Describe(typeof(T));
        schema = catalog.Types.Values.SingleOrDefault(t => t.Id == declared.Id)
            ?? throw new InvalidOperationException("Typed model identity is not in this catalog.");
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.IsDefined(typeof(FieldAttribute))).ToArray();
        if (properties.Length != schema.Fields.Length || properties.Select(p => p.GetCustomAttribute<FieldAttribute>()!.Id).Distinct().Count() != schema.Fields.Length)
            throw new InvalidOperationException("Typed model must describe every field exactly once. Refresh it after adding fields.");
        bindings = properties.Select(p =>
        {
            var id = p.GetCustomAttribute<FieldAttribute>()!.Id;
            var field = schema.Fields.SingleOrDefault(f => f.Id == id)
                ?? throw new InvalidOperationException("Typed property has an unknown field identity: " + p.Name);
            var source = declared.Fields.Single(f => f.Id == id);
            if (source.Type != field.Type || source.Target != field.Target || source.Currency!=field.Currency)
                throw new InvalidOperationException("Typed property no longer matches the field type: " + p.Name);
            if (p.SetMethod?.IsPublic != true || p.GetMethod?.IsPublic != true)
                throw new InvalidOperationException("Typed properties need public getters and setters: " + p.Name);
            var underlying = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            // Database Integer covers signed 64-bit values. Do not risk an overflow after committing a write.
            if (underlying == typeof(int)) throw new InvalidOperationException("Use long/long? for typed Integer properties: " + p.Name);
            if (!field.Required && p.PropertyType.IsValueType && Nullable.GetUnderlyingType(p.PropertyType) == null)
                throw new InvalidOperationException("Optional value properties must be nullable: " + p.Name);
            return (p, field);
        }).ToArray();
    }
    public TypedDocument<T> Create(T value) => Read(service.Create(schema.Name, Write(value), actor));
    public TypedDocument<T> Get(string id) => Read(service.Get(schema.Name, id, actor));
    public TypedDocument<T> Save(TypedDocument<T> document) => Read(service.Update(schema.Name, document.Id, document.Version, Write(document.Value), actor));
    public void Delete(TypedDocument<T> document) => service.Delete(schema.Name, document.Id, document.Version, actor);
    public TypedDocument<T> Submit(TypedDocument<T> document) => Read(service.Transition(schema.Name, document.Id, document.Version, "submit", actor));
    public TypedDocument<T> Cancel(TypedDocument<T> document) => Read(service.Transition(schema.Name, document.Id, document.Version, "cancel", actor));
    public IReadOnlyList<AuditEntry> History(string id) => service.History(schema.Name, id, actor);
    public TypedPage<T> List(Expression<Func<T, object?>>? sortBy = null, bool descending = false, int skip = 0, int take = 50, string? search = null)
        => ListCore(sortBy, descending, skip, take, search, null, null);
    public TypedPage<T> Where<TValue>(Expression<Func<T, TValue>> property, TValue value,
        Expression<Func<T, object?>>? sortBy = null, bool descending = false, int skip = 0, int take = 50)
    {
        var field = Resolve(property);
        var node = JsonSerializer.SerializeToNode(value, Catalog.Json);
        return ListCore(sortBy, descending, skip, take, null, field.Name, node?.ToString() ?? "");
    }
    private TypedPage<T> ListCore(Expression<Func<T, object?>>? sort, bool descending, int skip, int take, string? search, string? field, string? value)
    {
        var page = service.List(schema.Name, actor, search, sort == null ? null : Resolve(sort).Name, descending, skip, take, field, value);
        return new(page.Total, page.Items.Select(Read).ToArray());
    }
    private Field Resolve(LambdaExpression selector)
    {
        Expression body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } conversion) body = conversion.Operand;
        if (body is not MemberExpression { Member: PropertyInfo property } member || member.Expression != selector.Parameters[0])
            throw new ArgumentException("Select a direct decorated property, such as x => x.Tag.");
        return bindings.SingleOrDefault(b => b.Property == property).Field
            ?? throw new ArgumentException("Select a decorated property.");
    }
    private JsonObject Write(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var values = new JsonObject();
        foreach (var (property, field) in bindings)
            if (!field.ReadOnly) values[field.Name] = JsonSerializer.SerializeToNode(property.GetValue(value), property.PropertyType, Catalog.Json);
        return values;
    }
    private TypedDocument<T> Read(Document document)
    {
        var value = new T();
        foreach (var (property, field) in bindings)
        {
            var node = document.Values[field.Name];
            property.SetValue(value, node == null ? null : node.Deserialize(property.PropertyType, Catalog.Json));
        }
        return new(document.Id, document.Version, document.State, document.CreatedAt, document.ModifiedAt, value);
    }
}
