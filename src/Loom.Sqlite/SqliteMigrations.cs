using System.Text.Json;
using System.Globalization;
using System.Text.Json.Nodes;
using Loom.Core;
using Microsoft.Data.Sqlite;

namespace Loom.Sqlite;

public sealed partial class SqliteStore
{
    // Pending plans are server-owned; clients submit only a plan ID, never SQL.
    private readonly Dictionary<string, (string SourceHash, string Json, string Backfills)> pendingPlans = [];
    private SqliteCommand MigrationCommand(string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        var c = connection.CreateCommand(); c.Transaction = tx; c.CommandText = sql;
        foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return c;
    }
    private void EnsureInfrastructure()
    {
        using var c = MigrationCommand("CREATE TABLE IF NOT EXISTS _schemas(name TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS _audit(id TEXT PRIMARY KEY,type TEXT NOT NULL,documentId TEXT NOT NULL,json TEXT NOT NULL); CREATE INDEX IF NOT EXISTS audit_document ON _audit(type,documentId); CREATE TABLE IF NOT EXISTS _migrations(id TEXT PRIMARY KEY,json TEXT NOT NULL);");
        c.ExecuteNonQuery();
    }
    private Catalog? ReadCatalogCore(SqliteTransaction? tx = null)
    {
        using var c = MigrationCommand("SELECT json FROM _schemas ORDER BY name", tx);
        using var r = c.ExecuteReader(); var types = new List<DocType>();
        while (r.Read()) types.Add(JsonSerializer.Deserialize<DocType>(r.GetString(0), Catalog.Json)!);
        return types.Count == 0 ? null : new Catalog(types);
    }
    public Catalog? ReadCatalog()
    {
        lock (gate) { EnsureInfrastructure(); var found = ReadCatalogCore(); if (found != null) catalog = found; return found; }
    }
    private void WriteCatalog(Catalog target, SqliteTransaction tx)
    {
        foreach (var d in target.Types.Values)
        {
            using var c = MigrationCommand("INSERT INTO _schemas(name,json) VALUES($name,$json) ON CONFLICT(name) DO UPDATE SET json=excluded.json", tx,
                ("$name", d.Name), ("$json", JsonSerializer.Serialize(d, Catalog.Json)));
            c.ExecuteNonQuery();
        }
    }
    private void Initialize(Catalog target)
    {
        using var tx = connection.BeginTransaction();
        foreach (var d in target.Types.Values)
        {
            using var c = MigrationCommand($"CREATE TABLE {Q(d.Name)}(id TEXT PRIMARY KEY,version INTEGER NOT NULL,state TEXT NOT NULL,createdAt TEXT NOT NULL,modifiedAt TEXT NOT NULL{string.Join("", d.Fields.Select(f => "," + Column(f)))})", tx);
            c.ExecuteNonQuery();
            foreach (var f in d.Fields.Where(f => f.Unique))
            {
                using var ix = MigrationCommand($"CREATE UNIQUE INDEX {Q("ux_" + f.Id)} ON {Q(d.Name)}({Q(f.Name)})", tx); ix.ExecuteNonQuery();
            }
        }
        WriteCatalog(target, tx); tx.Commit(); catalog = target;
    }
    private void VerifyPhysical(Catalog source, SqliteTransaction tx)
    {
        foreach (var d in source.Types.Values)
        {
            using var c = MigrationCommand($"PRAGMA table_info({Q(d.Name)})", tx);
            using var r = c.ExecuteReader(); var actual = new Dictionary<string, (string Type, bool Required)>();
            while (r.Read()) actual[r.GetString(1)] = (r.GetString(2), r.GetInt32(3) != 0);
            var expected = new Dictionary<string, (string Type, bool Required)>
            { ["id"] = ("TEXT", false), ["version"] = ("INTEGER", true), ["state"] = ("TEXT", true), ["createdAt"] = ("TEXT", true), ["modifiedAt"] = ("TEXT", true) };
            foreach (var f in d.Fields) expected[f.Name] = (SqlType(f), f.Required);
            if (actual.Count != expected.Count || expected.Any(x => !actual.TryGetValue(x.Key, out var a) || a != x.Value))
                throw new LoomException(409, "Physical schema drift detected for " + d.Name + ". Restore/reconcile it before migrating.");
            r.Close();
            foreach (var f in d.Fields.Where(f => f.Unique))
            {
                using var query = MigrationCommand("SELECT il.name FROM pragma_index_list($table) il WHERE il.\"unique\"=1 AND il.partial=0 AND (SELECT count(*) FROM pragma_index_info(il.name))=1 AND EXISTS(SELECT 1 FROM pragma_index_info(il.name) WHERE name=$field)", tx, ("$table", d.Name), ("$field", f.Name));
                if (query.ExecuteScalar() == null) throw new LoomException(409, "Unique-index drift detected for " + d.Name + "." + f.Name);
            }
        }
    }
    private MigrationPlan BuildPlan(Catalog source, Catalog target, SqliteTransaction tx, MigrationBackfill[] backfills)
    {
        VerifyPhysical(source, tx);
        if(backfills.Any(b=>b==null) || backfills.Select(b=>b.FieldId).Distinct().Count()!=backfills.Length)
            throw new LoomException(422,"Backfills require distinct field identities.");
        var normalized = backfills.OrderBy(b=>b.FieldId).Select(b=>
        {
            var field=target.Types.Values.SelectMany(t=>t.Fields).SingleOrDefault(f=>f.Id==b.FieldId);
            var existing=source.Types.Values.SelectMany(t=>t.Fields).SingleOrDefault(f=>f.Id==b.FieldId);
            if(field==null || existing?.Required==true || !field.Required || field.Unique || field.ReadOnly || field.Type=="Link")
                throw new LoomException(422,"Backfills support new required fields or optional-to-required nonunique scalar fields only.");
            return new MigrationBackfill(b.FieldId,NormalizeBackfill(field,b.Value));
        }).ToArray();
        var changes = new List<SchemaChange>(); var sql = new List<string>(); var rows = new Dictionary<string, long>();
        if (source.Types.Count != target.Types.Count) throw new InvalidOperationException("Adding/removing document types is outside the v0.2 designer scope.");
        foreach (var old in source.Types.Values)
        {
            var next = target.Types.Values.SingleOrDefault(t => t.Id == old.Id);
            if (next == null || next.Name != old.Name) throw new InvalidOperationException("Document-type identity changes/renames require an explicit future migration.");
            using var count = MigrationCommand($"SELECT COUNT(*) FROM {Q(old.Name)}", tx); rows[old.Name] = (long)count.ExecuteScalar()!;
            if (JsonSerializer.Serialize(old, Catalog.Json) == JsonSerializer.Serialize(next, Catalog.Json)) continue;
            if (next.Version <= old.Version) throw new InvalidOperationException("Schema changes require a higher model version: " + old.Name);
            if (next.Submittable != old.Submittable || JsonSerializer.Serialize(next.Permissions) != JsonSerializer.Serialize(old.Permissions))
                throw new InvalidOperationException("Lifecycle and permission changes require a separate migration.");
            var sqlStart=sql.Count;
            var renames = new List<(Field Old, Field Next)>();
            foreach (var before in old.Fields)
            {
                var after = next.Fields.SingleOrDefault(f => f.Id == before.Id);
                if (after == null) throw new InvalidOperationException("Field removal/identity replacement is blocked; retain the permanent field ID: " + before.Name);
                var tightening=!before.Required && after.Required;
                var converting=RepresentationChanged(before,after);
                var comparable = after with { Name=before.Name, Label=before.Label, Required=before.Required, Type=before.Type, Precision=before.Precision, Scale=before.Scale, Currency=before.Currency, Rounding=before.Rounding, DateFormat=before.DateFormat, Options=before.Options, Compression=before.Compression, MaxBytes=before.MaxBytes, MediaType=before.MediaType };
                if (JsonSerializer.Serialize(before) != JsonSerializer.Serialize(comparable))
                    throw new InvalidOperationException("Type/constraint changes require conversion or backfill planning: " + before.Name);
                if(converting)
                {
                    var populated=PreflightConversion(old,before,after,tx);
                    changes.Add(new("ConvertField",old.Name,$"Convert {before.Name}: {before.Type} → {after.Type}; {populated} populated values checked. Rounding policy: {after.Rounding}. Precision/scale: {after.Precision}/{after.Scale}. Currency: {after.Currency}. Date input: {after.DateFormat}. Nulls preserved unless explicitly backfilled.",before.Id));
                }
                if(before.Required && !after.Required)changes.Add(new("RelaxRequired",old.Name,$"Make {after.Name} optional; existing values unchanged.",after.Id));
                if(tightening)
                {
                    if(after.Unique || after.ReadOnly || after.Type=="Link")throw new LoomException(422,"Making unique, computed or Link fields required is unsupported.");
                    var missing=PreflightRequired(old,before,tx);
                    var fill=normalized.SingleOrDefault(b=>b.FieldId==after.Id);
                    if(missing>0 && fill==null)throw new LoomException(422,$"{old.Name}.{before.Name} has {missing} missing values; supply an explicit backfill.");
                    changes.Add(new("RequireField",old.Name,$"Make {after.Name} required; {missing} missing values. "+(fill==null?"No backfill; refuse apply if missing values appear.":$"Fill only nulls with {fill.Value!.ToJsonString()}; preserve existing values."),after.Id));
                }
                if (before.Name != after.Name)
                {
                    if (old.Fields.Any(f => f.Id != before.Id && string.Equals(f.Name, after.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Rename cycles/name reuse are not supported in one migration; choose a new unused name.");
                    renames.Add((before, after));
                    changes.Add(new("RenameField", old.Name, $"Rename {before.Name} → {after.Name}; preserve all values.", before.Id));
                }
                if (before.Label != after.Label) changes.Add(new("ChangeLabel", old.Name, $"Caption: {before.Label} → {after.Label}; no column change.", before.Id));
            }
            // Use temporary names to support case-only column renames safely in SQLite.
            foreach (var (before, after) in renames)
            {
                var temporary = "loomtmp_" + before.Id;
                if (next.Fields.Any(f => string.Equals(f.Name, temporary, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Reserved temporary migration name.");
                sql.Add($"ALTER TABLE {Q(old.Name)} RENAME COLUMN {Q(before.Name)} TO {Q(temporary)};");
                sql.Add($"ALTER TABLE {Q(old.Name)} RENAME COLUMN {Q(temporary)} TO {Q(after.Name)};");
            }
            foreach (var added in next.Fields.Where(f => !old.Fields.Any(o => o.Id == f.Id)))
            {
                if (added.Unique || added.ReadOnly || added.Type == "Link")
                    throw new InvalidOperationException("Unique/computed/link additions need further planning.");
                if(added.Required)
                {
                    var fill=normalized.SingleOrDefault(b=>b.FieldId==added.Id) ?? throw new LoomException(422,"Required addition needs an explicit backfill: "+added.Name);
                    changes.Add(new("BackfillField",old.Name,$"Add required {added.Name} ({added.Type}); fill existing records with {fill.Value!.ToJsonString()}. No default for future records.",added.Id));
                    continue;
                }
                sql.Add($"ALTER TABLE {Q(old.Name)} ADD COLUMN {Column(added)};");
                changes.Add(new("AddField", old.Name, $"Add optional {added.Name} ({added.Type}); existing records receive null.", added.Id));
            }
            if(next.Fields.Any(f=>(f.Required && !old.Fields.Any(o=>o.Id==f.Id && o.Required)) || old.Fields.Any(o=>o.Id==f.Id && (o.Required!=f.Required || RepresentationChanged(o,f)))))
            {
                // Rebuild removes the need for a permanent SQL DEFAULT on a required addition.
                VerifyRebuildObjects(old,tx);
                sql.RemoveRange(sqlStart,sql.Count-sqlStart);
                var temporary="loomtmp_"+old.Id;
                using var exists=MigrationCommand("SELECT count(*) FROM sqlite_master WHERE name=$name",tx,("$name",temporary));
                if((long)exists.ExecuteScalar()! != 0 || target.Types.ContainsKey(temporary))throw new LoomException(409,"Migration temporary table already exists.");
                sql.Add($"CREATE TABLE {Q(temporary)}(id TEXT PRIMARY KEY,version INTEGER NOT NULL,state TEXT NOT NULL,createdAt TEXT NOT NULL,modifiedAt TEXT NOT NULL{string.Join("",next.Fields.Select(f=>","+Column(f)))});");
                var system=new[]{"id","version","state","createdAt","modifiedAt"};
                var columns=system.Concat(next.Fields.Select(f=>f.Name)).Select(Q);
                var values=system.Select(Q).Concat(next.Fields.Select(f=>
                {
                    var existing=old.Fields.SingleOrDefault(o=>o.Id==f.Id);
                    if(existing!=null)
                    {
                        var fill=normalized.SingleOrDefault(b=>b.FieldId==f.Id);
                        var expression=!RepresentationChanged(existing,f)?Q(existing.Name):$"loom_convert({Q(existing.Name)},{SchemaLiteral(existing)},{SchemaLiteral(f)})";
                        return fill==null?expression:$"COALESCE({expression},{BackfillLiteral(fill.Value!,f)})";
                    }
                    return f.Required?BackfillLiteral(normalized.Single(b=>b.FieldId==f.Id).Value!,f):"NULL";
                }));
                sql.Add($"INSERT INTO {Q(temporary)}({string.Join(",",columns)}) SELECT {string.Join(",",values)} FROM {Q(old.Name)};");
                sql.Add($"DROP TABLE {Q(old.Name)};");
                sql.Add($"ALTER TABLE {Q(temporary)} RENAME TO {Q(old.Name)};");
                foreach(var f in next.Fields.Where(f=>f.Unique))sql.Add($"CREATE UNIQUE INDEX {Q("ux_"+f.Id)} ON {Q(old.Name)}({Q(f.Name)});");
            }
            changes.Add(new("ModelVersion", old.Name, $"Model version {old.Version} → {next.Version}. Title/display metadata refreshed."));
        }
        var sourceHash = SchemaIdentity.Hash(source); var targetHash = SchemaIdentity.Hash(target);
        var planId = SchemaIdentityHash(sourceHash + ":" + targetHash + (normalized.Length==0?"":":"+JsonSerializer.Serialize(normalized,Catalog.Json)));
        return new(planId, sourceHash, targetHash, changes.ToArray(), sql.ToArray(), rows, target.Types.Values.ToArray(), normalized);
    }
    private static string SchemaIdentityHash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public MigrationPlan Plan(Catalog target, MigrationBackfill[]? backfills = null)
    {
        lock (gate)
        {
            EnsureInfrastructure(); using var tx = connection.BeginTransaction();
            var source = ReadCatalogCore(tx) ?? throw new InvalidOperationException("Initialize a database before planning changes.");
            var plan = BuildPlan(source, target, tx, backfills ?? []); tx.Commit();
            if (pendingPlans.Count >= 64) pendingPlans.Clear();
            pendingPlans[plan.Id] = (plan.SourceHash, SchemaIdentity.Serialize(target), JsonSerializer.Serialize(plan.Backfills,Catalog.Json));
            return plan;
        }
    }
    public Catalog Apply(string planId, Actor actor)
    {
        lock (gate)
        {
            if (actor.Role != "Admin") throw new LoomException(403, "Only administrators can apply schema changes.");
            using var tx = connection.BeginTransaction();
            var source = ReadCatalogCore(tx) ?? throw new InvalidOperationException("No accepted schema.");
            VerifyPhysical(source, tx);
            using var previous = MigrationCommand("SELECT json FROM _migrations WHERE id=$id", tx, ("$id", planId));
            if (previous.ExecuteScalar() is string json)
            {
                var receipt = JsonSerializer.Deserialize<MigrationReceipt>(json, Catalog.Json)!;
                if (receipt.TargetHash != SchemaIdentity.Hash(source)) throw new LoomException(409, "This plan was applied previously, but the model has since changed.");
                tx.Commit(); catalog = source; return source;
            }
            if (!pendingPlans.TryGetValue(planId, out var pending)) throw new LoomException(409, "Preview this change again; the plan expired or the server restarted.");
            if (pending.SourceHash != SchemaIdentity.Hash(source)) throw new LoomException(409, "The schema changed after preview. Preview a new migration.");
            var target = new Catalog(JsonSerializer.Deserialize<DocType[]>(pending.Json, Catalog.Json)!);
            var plan = BuildPlan(source, target, tx, JsonSerializer.Deserialize<MigrationBackfill[]>(pending.Backfills,Catalog.Json)!);
            if (plan.Id != planId) throw new LoomException(409, "The schema changed after preview. Preview a new migration.");
            foreach (var statement in plan.Sql) { using var c = MigrationCommand(statement, tx); c.ExecuteNonQuery(); }
            WriteCatalog(target, tx);
            VerifyPhysical(target, tx);
            var receiptNew = new MigrationReceipt(plan.Id, plan.SourceHash, plan.TargetHash, actor.Id, DateTimeOffset.UtcNow.ToString("O"), plan.Changes, source.Types.Values.ToArray(), target.Types.Values.ToArray(), plan.Backfills);
            using var ledger = MigrationCommand("INSERT INTO _migrations(id,json) VALUES($id,$json)", tx, ("$id", plan.Id), ("$json", JsonSerializer.Serialize(receiptNew, Catalog.Json)));
            ledger.ExecuteNonQuery(); tx.Commit(); catalog = target; pendingPlans.Remove(planId); return target;
        }
    }
    public IReadOnlyList<MigrationReceipt> Migrations()
    {
        lock (gate)
        {
            EnsureInfrastructure(); using var c = MigrationCommand("SELECT json FROM _migrations ORDER BY rowid");
            using var r = c.ExecuteReader(); var list = new List<MigrationReceipt>();
            while (r.Read()) list.Add(JsonSerializer.Deserialize<MigrationReceipt>(r.GetString(0), Catalog.Json)!); return list;
        }
    }
}
