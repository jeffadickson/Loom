using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Loom.Core;

public sealed record MigrationBackfill(string FieldId, JsonNode? Value);
public sealed record SchemaChange(string Kind, string Type, string Description, string? FieldId = null);
public sealed record MigrationPlan(string Id, string SourceHash, string TargetHash,
    SchemaChange[] Changes, string[] Sql, Dictionary<string, long> Rows, DocType[] Target, MigrationBackfill[]? Backfills = null);
public sealed record MigrationReceipt(string Id, string SourceHash, string TargetHash, string Actor,
    string At, SchemaChange[] Changes, DocType[] Before, DocType[] After, MigrationBackfill[]? Backfills = null);
public interface IMigrationStore
{
    Catalog? ReadCatalog();
    MigrationPlan Plan(Catalog target, MigrationBackfill[]? backfills = null);
    Catalog Apply(string planId, Actor actor);
    IReadOnlyList<MigrationReceipt> Migrations();
}
public static class SchemaIdentity
{
    public static string Serialize(Catalog c) => JsonSerializer.Serialize(c.Types.Values.OrderBy(d => d.Name), Catalog.Json);
    public static string Hash(Catalog c) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(c))));
}
