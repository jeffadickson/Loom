// DDL identifiers are validated Catalog identifiers/32-hex IDs; values are never user SQL.
#pragma warning disable EF1002
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Loom.Core;
using Microsoft.EntityFrameworkCore;

namespace Loom.Server;

public sealed partial class ServerDocumentStore
{
    private readonly Dictionary<string, (string Source, string Target, string Backfills)> plans = [];
    public Catalog? ReadCatalog()
    {
        lock (gate)
        {
            using var db = Context();
            // A fresh database has no infrastructure. Do not silently swallow other failures.
            using var command = connection.CreateCommand();
            command.CommandText = engine == ServerEngine.PostgreSql
                ? "SELECT to_regclass('loom_schema') IS NOT NULL"
                : "SELECT CASE WHEN OBJECT_ID(N'loom_schema',N'U') IS NULL THEN 0 ELSE 1 END";
            if (!Convert.ToBoolean(command.ExecuteScalar())) return null;
            var found = ReadModel(db); catalog = found; return found;
        }
    }
    private static Catalog? ReadModel(LoomDbContext db)
    {
        var json = db.Models.AsNoTracking().Select(x => x.Model).SingleOrDefault();
        return json == null ? null : new(JsonSerializer.Deserialize<DocType[]>(json, Catalog.Json)!);
    }
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Catalog.Json)!;
    private MigrationPlan BuildPlan(LoomDbContext db, Catalog source, Catalog target, MigrationBackfill[] backfills)
    {
        if (backfills.Select(b => b.FieldId).Distinct().Count() != backfills.Length)
            throw new LoomException(422, "Duplicate backfill field.");
        var changes = new List<SchemaChange>(); var rows = new Dictionary<string, long>();
        foreach (var old in source.Types.Values)
        {
            var next = target.Types.Values.SingleOrDefault(t => t.Id == old.Id);
            if (next == null || next.Name != old.Name) throw new LoomException(422, "Removing or renaming document types is not supported.");
            rows[old.Name] = db.Documents.LongCount(d => d.Kind == old.Name);
            if (JsonSerializer.Serialize(old) == JsonSerializer.Serialize(next)) continue;
            if (next.Version <= old.Version) throw new LoomException(422, "Changed models require a higher version.");
            if (next.Immutable != old.Immutable || next.Submittable != old.Submittable || JsonSerializer.Serialize(next.Permissions) != JsonSerializer.Serialize(old.Permissions))
                throw new LoomException(422, "Lifecycle and permission changes require a separate migration.");
            foreach (var before in old.Fields)
            {
                var after = next.Fields.SingleOrDefault(f => f.Id == before.Id);
                if (after == null) throw new LoomException(422, "Field removal is blocked; retain its permanent ID.");
                if (JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after with { Name = before.Name, Label = before.Label }))
                    throw new LoomException(422, "Server migrations currently support field renames, labels and additions; conversions require a separate migration.");
                if (before.Name != after.Name) changes.Add(new("RenameField", old.Name, $"Rename {before.Name} to {after.Name}; preserve values.", before.Id));
                if (before.Label != after.Label) changes.Add(new("ChangeLabel", old.Name, $"Change caption to {after.Label}.", before.Id));
            }
            foreach (var field in next.Fields.Where(f => !old.Fields.Any(o => o.Id == f.Id)))
            {
                if (field.Unique || field.ReadOnly || field.Type == "Link") throw new LoomException(422, "Adding unique, computed or link fields to existing types requires a separate migration.");
                if (field.Required && !backfills.Any(b => b.FieldId == field.Id)) throw new LoomException(422, "Required additions need an explicit backfill.");
                changes.Add(new("AddField", old.Name, $"Add {field.Name}; " + (field.Required ? "backfill existing records." : "existing records receive null."), field.Id));
            }
            changes.Add(new("ModelVersion", old.Name, $"Model version {old.Version} to {next.Version}."));
        }
        foreach (var added in target.Types.Values.Where(t => !source.Types.Values.Any(o => o.Id == t.Id)))
            changes.Add(new("AddType", added.Name, "Add document type " + added.Label));
        foreach (var fill in backfills)
        {
            var field = target.Types.Values.SelectMany(t => t.Fields).SingleOrDefault(f => f.Id == fill.FieldId);
            if (field == null || source.Types.Values.SelectMany(t => t.Fields).Any(f => f.Id == fill.FieldId) || !field.Required || field.Type == "Link" || fill.Value == null)
                throw new LoomException(422, "Backfills must identify new required scalar fields.");
            FieldValues.Normalize(field, fill.Value);
        }
        var sourceHash = SchemaIdentity.Hash(source); var targetHash = SchemaIdentity.Hash(target);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceHash + targetHash + JsonSerializer.Serialize(backfills, Catalog.Json))));
        return new(id, sourceHash, targetHash, changes.ToArray(), [], rows, target.Types.Values.ToArray(), backfills);
    }
    public MigrationPlan Plan(Catalog target, MigrationBackfill[]? backfills = null)
    {
        lock (gate)
        {
            using var db = Context(); using var tx = db.Database.BeginTransaction(IsolationLevel.Serializable);
            var source = ReadModel(db) ?? throw new LoomException(409, "Initialize the database first.");
            var plan = BuildPlan(db, source, target, backfills ?? []);
            if (plans.Count >= 64) plans.Clear();
            plans[plan.Id] = (plan.SourceHash, ModelJson(target), JsonSerializer.Serialize(plan.Backfills, Catalog.Json));
            tx.Commit(); return plan;
        }
    }
    public Catalog Apply(string planId, Actor actor)
    {
        if (actor.Role != "Admin") throw new LoomException(403, "Only administrators can apply schema changes.");
        lock (gate)
        {
            using var db = Context(); using var tx = db.Database.BeginTransaction(IsolationLevel.Serializable);
            // Lock the singleton before reading documents. Every normal transaction reads it.
            db.Database.ExecuteSqlRaw("UPDATE loom_schema SET model=model WHERE singleton=1");
            var source = ReadModel(db) ?? throw new LoomException(409, "No accepted model.");
            // Adopt databases created before the migration ledger existed.
            db.Database.ExecuteSqlRaw(engine == ServerEngine.PostgreSql
                ? "CREATE TABLE IF NOT EXISTS loom_migrations(id varchar(64) PRIMARY KEY, body text NOT NULL)"
                : "IF OBJECT_ID(N'loom_migrations',N'U') IS NULL CREATE TABLE loom_migrations(id varchar(64) PRIMARY KEY, body nvarchar(max) NOT NULL)");
            var previous = db.Migrations.AsNoTracking().SingleOrDefault(x => x.Id == planId);
            if (previous != null)
            {
                if (Deserialize<MigrationReceipt>(previous.Body).TargetHash != SchemaIdentity.Hash(source)) throw new LoomException(409, "Model changed since this migration.");
                tx.Commit(); catalog = source; return source;
            }
            if (!plans.TryGetValue(planId, out var pending) || pending.Source != SchemaIdentity.Hash(source)) throw new LoomException(409, "Preview again; migration plan expired or schema changed.");
            var target = new Catalog(Deserialize<DocType[]>(pending.Target));
            var plan = BuildPlan(db, source, target, Deserialize<MigrationBackfill[]>(pending.Backfills));
            foreach (var old in source.Types.Values)
            {
                var next = target.Get(old.Name);
                if (JsonSerializer.Serialize(old) == JsonSerializer.Serialize(next)) continue;
                // Stream bounded batches; no 10,000-row limit and no whole-database materialization.
                string last = "";
                while (true)
                {
                    var batch = db.Documents.Where(d => d.Kind == old.Name && d.Id.CompareTo(last) > 0).OrderBy(d => d.Id).Take(200).ToArray();
                    if (batch.Length == 0) break;
                    foreach (var row in batch)
                    {
                        var document = Deserialize<Document>(row.Body); var values = new JsonObject();
                        foreach (var field in next.Fields)
                        {
                            var before = old.Fields.SingleOrDefault(f => f.Id == field.Id);
                            values[field.Name] = before != null ? document.Values[before.Name]?.DeepClone()
                                : plan.Backfills!.SingleOrDefault(b => b.FieldId == field.Id)?.Value?.DeepClone();
                            if (before == null && values[field.Name] != null) values[field.Name] = FieldValues.Normalize(field, values[field.Name]!);
                        }
                        row.Version = checked(row.Version + 1);
                        row.Body = JsonSerializer.Serialize(document with { Values = values, Version = row.Version }, Catalog.Json);
                    }
                    last = batch[^1].Id; db.SaveChanges(); db.ChangeTracker.Clear();
                }
            }
            var model = db.Models.Single(); model.Model = ModelJson(target);
            var receipt = new MigrationReceipt(plan.Id, plan.SourceHash, plan.TargetHash, actor.Id, DateTimeOffset.UtcNow.ToString("O"), plan.Changes, source.Types.Values.ToArray(), target.Types.Values.ToArray(), plan.Backfills);
            db.Migrations.Add(new() { Id = plan.Id, Body = JsonSerializer.Serialize(receipt, Catalog.Json) });
            // Renamed paths need rebuilt expression indexes, in the same transaction.
            if (engine == ServerEngine.PostgreSql)
                foreach (var field in source.Types.Values.SelectMany(t => t.Fields)) db.Database.ExecuteSqlRaw($"DROP INDEX IF EXISTS ix_{field.Id}");
            if (engine == ServerEngine.SqlServer)
                foreach (var field in source.Types.Values.SelectMany(t => t.Fields).Where(f => f.Type is "Integer" or "Money" || f.Type == "Decimal" && f.Precision != null))
                {
                    db.Database.ExecuteSqlRaw($"DROP INDEX IF EXISTS ix_{field.Id} ON loom_documents");
                    db.Database.ExecuteSqlRaw($"IF COL_LENGTH('loom_documents','q_{field.Id}') IS NOT NULL ALTER TABLE loom_documents DROP COLUMN q_{field.Id}");
                }
            EnsureQueryInfrastructure(db, target);
            db.SaveChanges(); tx.Commit(); catalog = target; plans.Remove(planId); return target;
        }
    }
    public IReadOnlyList<MigrationReceipt> Migrations()
    {
        lock (gate)
        {
            using var db = Context();
            return db.Migrations.AsNoTracking().Select(m => m.Body).ToArray().Select(Deserialize<MigrationReceipt>).OrderBy(m => m.At, StringComparer.Ordinal).ToArray();
        }
    }
}
