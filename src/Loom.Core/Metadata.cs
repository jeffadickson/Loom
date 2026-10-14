using System.Text.Json;
using System.Text.RegularExpressions;

namespace Loom.Core;

public sealed record Field(string Name, string Label, string Type, bool Required = false,
    bool Unique = false, bool ReadOnly = false, string? Target = null, string[]? Options = null, string? Id = null, int? Precision = null, int? Scale = null, string? Currency = null, string Rounding = "reject", string DateFormat = "yyyy-MM-dd", string Compression = "auto", int MaxBytes = 8388608, string MediaType = "application/octet-stream");
public sealed record DocType(string Name, string Label, int Version, string TitleField,
    bool Submittable, Field[] Fields, Dictionary<string, string[]> Permissions, string? Id = null);

public sealed class Catalog
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public IReadOnlyDictionary<string, DocType> Types { get; }
    public Catalog(IEnumerable<DocType> types)
    {
        // Legacy names are used only at initial adoption. Persist these IDs thereafter.
        Types = types.Select(d => d with { Id = d.Id ?? StableId("type:" + d.Name),
            Fields = d.Fields.Select(f => f with { Id = f.Id ?? StableId("field:" + d.Name + ":" + f.Name) }).ToArray()
        }).ToDictionary(x => x.Name, StringComparer.Ordinal);
        var ids = Types.Values.SelectMany(d => d.Fields.Select(f => f.Id).Append(d.Id)).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !Guid.TryParseExact(id, "N", out _)))
            throw new InvalidOperationException("Type and field IDs must be globally unique 32-digit GUIDs.");
        var reserved = new HashSet<string>(["id", "version", "state", "createdAt", "modifiedAt"], StringComparer.OrdinalIgnoreCase);
        foreach (var d in Types.Values)
        {
            Identifier(d.Name);
            foreach (var grant in d.Permissions.Values.SelectMany(x => x))
                if (!new[] { "read", "create", "write", "delete", "audit", "submit", "cancel" }.Contains(grant))
                    throw new InvalidOperationException("Unknown permission action: " + grant);
            if (d.Version < 1 || d.Fields.Length == 0) throw new InvalidOperationException("Invalid schema version/fields.");
            if (d.Fields.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != d.Fields.Length)
                throw new InvalidOperationException("Duplicate fields.");
            foreach (var f in d.Fields)
            {
                Identifier(f.Name);
                if (reserved.Contains(f.Name)) throw new InvalidOperationException("Reserved field: " + f.Name);
                if (!new[] { "Text", "Integer", "Decimal", "Date", "Select", "Link", "Boolean", "Money", "Binary", "Uri" }.Contains(f.Type))
                    throw new InvalidOperationException("Unknown field type: " + f.Type);
                FieldValues.ValidateDefinition(f);
                if (f.Type == "Link" && (f.Target == null || !Types.ContainsKey(f.Target))) throw new InvalidOperationException("Invalid link target.");
                if (f.Type == "Select" && (f.Options == null || f.Options.Length == 0)) throw new InvalidOperationException("Select requires options.");
            }
            if (!d.Fields.Any(f => f.Name == d.TitleField && f.Type!="Binary")) throw new InvalidOperationException("Missing title field.");
        }
        if (Types.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Types.Count)
            throw new InvalidOperationException("Case-colliding DocTypes.");
    }
    private static string StableId(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("loom:v1:" + value)))[..32].ToLowerInvariant();
    public static Catalog Load(string path) => new(Directory.GetFiles(path, "*.json").Order()
        .Select(f => JsonSerializer.Deserialize<DocType>(File.ReadAllText(f), Json)!));
    public DocType Get(string name) => Types.TryGetValue(name, out var d) ? d : throw new LoomException(404, "Unknown document type.");
    public static void Identifier(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_]{0,62}$")) throw new InvalidOperationException("Invalid identifier: " + value);
    }
}

public sealed class LoomException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
