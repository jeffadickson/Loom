using System.Reflection;

namespace Loom.Core;

[AttributeUsage(AttributeTargets.Class)]
public sealed class DocumentAttribute(string id, string titleProperty) : Attribute
{
    public string Id { get; } = id;
    public string TitleProperty { get; } = titleProperty;
    public string? Name { get; set; }
    public string? Label { get; set; }
    public int Version { get; set; } = 1;
    public bool Submittable { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public sealed class FieldAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public string? Name { get; set; }
    public string? Label { get; set; }
    public bool Required { get; set; }
    public bool Unique { get; set; }
    public bool ReadOnly { get; set; }
    public string? Target { get; set; }
    public string[]? Options { get; set; }
    public string? Kind { get; set; }
    public string? Currency { get; set; }
    public int Precision { get; set; } = -1;
    public int Scale { get; set; } = -1;
    public string Rounding { get; set; } = "reject";
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public string Compression { get; set; } = "auto";
    public int MaxBytes { get; set; } = FieldValues.MaxBinaryBytes;
    public string MediaType { get; set; } = "application/octet-stream";
}
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class GrantAttribute(string role, params string[] actions) : Attribute
{
    public string Role { get; } = role;
    public string[] Actions { get; } = actions;
}
public static class AttributeCatalog
{
    public static Catalog From(params Type[] models) => new(models.Select(Describe));
    internal static DocType Describe(Type model)
    {
        var doc = model.GetCustomAttribute<DocumentAttribute>() ?? throw new InvalidOperationException(model.Name + " needs DocumentAttribute.");
        var properties = model.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.IsDefined(typeof(FieldAttribute))).OrderBy(p => p.MetadataToken).ToArray();
        var fields = properties.Select(p =>
        {
            var f = p.GetCustomAttribute<FieldAttribute>()!;
            if (p.GetIndexParameters().Length != 0 || p.GetMethod == null) throw new InvalidOperationException("Fields must be readable, non-indexed properties.");
            var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var kind = type == typeof(string) ? "Text" : type == typeof(int) || type == typeof(long) ? "Integer" :
                type == typeof(decimal) ? "Decimal" : type == typeof(bool) ? "Boolean" : type == typeof(DateOnly) ? "Date" : type == typeof(byte[]) ? "Binary" : type == typeof(Uri) ? "Uri" :
                throw new InvalidOperationException("Unsupported field CLR type: " + type.Name);
            if (f.Target != null || f.Options != null)
            {
                if (type != typeof(string) || (f.Target != null && f.Options != null)) throw new InvalidOperationException("Link/select require string and mutually exclusive metadata.");
                kind = f.Target != null ? "Link" : "Select";
            }
            if(f.Currency!=null){if(type!=typeof(decimal))throw new InvalidOperationException("Money properties use decimal.");kind="Money";}
            if(f.Kind!=null){if(f.Kind!="Uri" || type!=typeof(string))throw new InvalidOperationException("Only string Uri override is supported.");kind="Uri";}
            if (!Guid.TryParseExact(f.Id, "N", out _)) throw new InvalidOperationException("An explicit permanent field ID is required.");
            return new Field(f.Name ?? p.Name, f.Label ?? p.Name, kind, f.Required, f.Unique, f.ReadOnly, f.Target, f.Options, f.Id, f.Precision<0?null:f.Precision, f.Scale<0?null:f.Scale, f.Currency, f.Rounding, f.DateFormat, f.Compression, f.MaxBytes, f.MediaType);
        }).ToArray();
        var title = Array.FindIndex(properties, p => p.Name == doc.TitleProperty);
        if (title < 0) throw new InvalidOperationException("TitleProperty must identify a decorated property.");
        return new DocType(doc.Name ?? model.Name, doc.Label ?? model.Name, doc.Version, fields[title].Name,
            doc.Submittable, fields, model.GetCustomAttributes<GrantAttribute>().ToDictionary(g => g.Role, g => g.Actions), doc.Id);
    }
}
