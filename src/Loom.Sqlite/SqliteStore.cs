using System.Text.Json;
using System.Text.Json.Nodes;
using Loom.Core;
using Microsoft.Data.Sqlite;

namespace Loom.Sqlite;

public sealed partial class SqliteStore : IDocumentStore, IMigrationStore
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private Catalog catalog = null!;
    public ProviderInfo Info => new("SQLite", true, ["transactions", "optimistic-concurrency", "audit", "additive-migrations", "unique-indexes", "migration-plans", "field-renames", "migration-ledger"]);
    public SqliteStore(string connectionString)
    {
        connection = new(connectionString); connection.Open(); RegisterConversions();
        connection.CreateCollation("loom_decimal",(a,b)=>decimal.Parse(a,System.Globalization.CultureInfo.InvariantCulture).CompareTo(decimal.Parse(b,System.Globalization.CultureInfo.InvariantCulture)));
        connection.CreateCollation("loom_ordinal",StringComparer.Ordinal.Compare);
        connection.CreateFunction<object?,string,string,bool>("loom_contains",(value,type,search)=>
            (value==null?"":type=="Boolean"?(Convert.ToInt64(value)==0?"false":"true"):Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)??"").Contains(search,StringComparison.OrdinalIgnoreCase),isDeterministic:true);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; cmd.ExecuteNonQuery();
    }
    internal static string Q(string name) { Catalog.Identifier(name); return "\"" + name + "\""; }
    public void Migrate(Catalog next)
    {
        lock (gate)
        {
            EnsureInfrastructure();
            var existing = ReadCatalog();
            if (existing == null) Initialize(next);
            else Apply(Plan(next).Id, new Actor("startup", "Admin"));
        }
    }
    private static string SqlType(Field f)=>f.Type is "Integer" or "Boolean"?"INTEGER":f.Type=="Binary"?"BLOB":"TEXT";
    private static string Column(Field f) => Q(f.Name) + " " + SqlType(f) + (f.Required ? " NOT NULL" : "");
    public T Transaction<T>(Func<IStoreSession, T> action)
    {
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            try {
                var accepted = ReadCatalogCore(tx);
                if (accepted == null || SchemaIdentity.Hash(accepted) != SchemaIdentity.Hash(catalog)) throw new LoomException(409, "Database model changed in another process; restart this host.");
                var result = action(new Session(connection, tx, catalog)); tx.Commit(); return result;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new LoomException(409, "Database constraint rejected the change."); }
        }
    }
    public void Dispose() => connection.Dispose();
    private sealed class Session(SqliteConnection connection, SqliteTransaction tx, Catalog catalog) : IStoreSession
    {
        private SqliteCommand Command(string sql, params (string, object?)[] args)
        {
            var c = connection.CreateCommand(); c.Transaction = tx; c.CommandText = sql;
            foreach (var (key, value) in args) c.Parameters.AddWithValue(key, value ?? DBNull.Value);
            return c;
        }
        public Page Query(DocType schema,DocumentQuery query)
        {
            query=Queries.Validate(schema,query);var fields=schema.Fields.ToDictionary(f=>f.Id!);
            string Column(Field f)=>Q(f.Name)+(f.Type is "Decimal" or "Money"?" COLLATE loom_decimal":f.Type is "Integer" or "Boolean"?"":" COLLATE loom_ordinal");
            var clauses=new List<string>();var parameters=new List<(string,object?)>();
            foreach(var filter in query.Filters!)
            {
                var field=fields[filter.FieldId];var column=Column(field);
                if(filter.Operator==FilterOperator.IsNull){clauses.Add(column+" IS NULL");continue;}
                var parameter="$p"+parameters.Count;parameters.Add((parameter,Value(field,filter.Value)));
                clauses.Add(column+(filter.Operator switch{FilterOperator.Equal=>" = ",FilterOperator.GreaterThan=>" > ",_=>" < "})+parameter);
            }
            if(!string.IsNullOrWhiteSpace(query.Search))
            {
                parameters.Add(("$search",query.Search));
                var searchable=schema.Fields.Where(f=>f.Type is not ("Binary" or "Measurement"));
                clauses.Add("("+string.Join(" OR ",searchable.Select(f=>"loom_contains("+Q(f.Name)+", '"+f.Type+"', $search)"))+")");
            }
            var where=clauses.Count==0?"":" WHERE "+string.Join(" AND ",clauses);
            using var count=Command("SELECT COUNT(*) FROM "+Q(schema.Name)+where,parameters.ToArray());
            int total=checked(Convert.ToInt32(count.ExecuteScalar()));
            var selected=query.SelectFieldIds==null?schema.Fields:schema.Fields.Where(f=>query.SelectFieldIds.Contains(f.Id!)).ToArray();
            var columns=string.Join(",",new[]{"id","version","state","createdAt","modifiedAt"}.Concat(selected.Select(f=>f.Name)).Select(Q));
            using var command=Command("SELECT "+columns+" FROM "+Q(schema.Name)+where+" ORDER BY "+Column(fields[query.SortFieldId])+(query.Descending?" DESC":" ASC")+", id COLLATE loom_ordinal LIMIT $take OFFSET $skip",[..parameters,("$take",query.Take),("$skip",query.Skip)]);
            using var reader=command.ExecuteReader();var rows=new List<Document>();while(reader.Read())rows.Add(Read(reader,schema,selected));return new(total,rows);
        }
        public Document? Get(string type, string id)
        {
            var schema = catalog.Get(type);
            using var c = Command($"SELECT * FROM {Q(type)} WHERE id=$id", ("$id", id));
            using var r = c.ExecuteReader(); return r.Read() ? Read(r, schema) : null;
        }
        public IReadOnlyList<Document> List(string type)
        {
            var schema = catalog.Get(type);
            using var c = Command($"SELECT * FROM {Q(type)} LIMIT 10001");
            using var r = c.ExecuteReader(); var rows = new List<Document>();
            while (r.Read()) rows.Add(Read(r, schema));
            if (rows.Count > 10000) throw new LoomException(422, "Prototype scan limit reached; implement provider-side queries before scaling.");
            return rows;
        }
        private static Document Read(SqliteDataReader r, DocType schema,Field[]? selected=null)
        {
            var values = new JsonObject();
            foreach (var f in selected??schema.Fields)
            {
                var i = r.GetOrdinal(f.Name);
                values[f.Name] = r.IsDBNull(i) ? null : f.Type switch
                {
                    "Integer" => JsonValue.Create(r.GetInt64(i)),
                    "Boolean" => JsonValue.Create(r.GetInt64(i) != 0),
                    "Measurement" => JsonNode.Parse(r.GetString(i)),
                    "Binary" => JsonValue.Create(Convert.ToBase64String(BlobCodec.Decode((byte[])r.GetValue(i),f.MaxBytes))),
                    "Decimal" or "Money" => JsonValue.Create(decimal.Parse(r.GetString(i), System.Globalization.CultureInfo.InvariantCulture)),
                    _ => JsonValue.Create(r.GetString(i))
                };
            }
            return new(r.GetString(r.GetOrdinal("id")), schema.Name, r.GetInt32(r.GetOrdinal("version")), r.GetString(r.GetOrdinal("state")), r.GetString(r.GetOrdinal("createdAt")), r.GetString(r.GetOrdinal("modifiedAt")), values);
        }
        private static object? Value(Field f, JsonNode? node) => node == null ? null : f.Type switch
        {
            "Integer" => node.GetValue<long>(), "Boolean" => node.GetValue<bool>() ? 1 : 0,
            "Binary" => BlobCodec.Encode(Convert.FromBase64String(node.ToString()),f.Compression),
            "Decimal" or "Money" => node.GetValue<decimal>().ToString(System.Globalization.CultureInfo.InvariantCulture), _ => node.ToString()
        };
        public void Insert(Document d)
        {
            var schema = catalog.Get(d.Type);
            var columns = new[] { "id", "version", "state", "createdAt", "modifiedAt" }.Concat(schema.Fields.Select(f => f.Name)).ToArray();
            using var c = Command($"INSERT INTO {Q(d.Type)} ({string.Join(",", columns.Select(Q))}) VALUES ({string.Join(",", columns.Select((_, i) => "$p" + i))})");
            object?[] values = [d.Id, d.Version, d.State, d.CreatedAt, d.ModifiedAt, .. schema.Fields.Select(f => Value(f, d.Values[f.Name]))];
            for (int i = 0; i < values.Length; i++) c.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
        public void Replace(Document d, int expectedVersion)
        {
            var schema = catalog.Get(d.Type);
            using var c = Command($"UPDATE {Q(d.Type)} SET version=$version,state=$state,modifiedAt=$at{string.Join("", schema.Fields.Select((f, i) => "," + Q(f.Name) + "=$p" + i))} WHERE id=$id AND version=$expected", ("$id", d.Id), ("$version", d.Version), ("$state", d.State), ("$at", d.ModifiedAt), ("$expected", expectedVersion));
            for (var i = 0; i < schema.Fields.Length; i++) c.Parameters.AddWithValue("$p" + i, Value(schema.Fields[i], d.Values[schema.Fields[i].Name]) ?? DBNull.Value);
            if (c.ExecuteNonQuery() != 1) throw new LoomException(409, "Document changed. Reload before saving.");
        }
        public void Delete(string type, string id, int expectedVersion)
        {
            catalog.Get(type);
            using var c = Command($"DELETE FROM {Q(type)} WHERE id=$id AND version=$version", ("$id", id), ("$version", expectedVersion));
            if (c.ExecuteNonQuery() != 1) throw new LoomException(409, "Document changed.");
        }
        private static byte[] AuditBytes(AuditEntry entry)
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(entry,Catalog.Json);
            if(bytes.Length>32*1024*1024)throw new LoomException(422,"Audit snapshot exceeds size limit.");
            return BlobCodec.Encode(bytes);
        }
        public void AppendAudit(AuditEntry e)
        {
            using var c = Command("INSERT INTO _audit(id,type,documentId,json) VALUES($id,$type,$doc,$json)", ("$id", e.Id), ("$type", e.Type), ("$doc", e.DocumentId), ("$json", AuditBytes(e))); c.ExecuteNonQuery();
        }
        public IReadOnlyList<AuditEntry> Audit(string type, string id)
        {
            using var c = Command("SELECT json FROM _audit WHERE type=$type AND documentId=$id ORDER BY rowid", ("$type", type), ("$id", id));
            using var r = c.ExecuteReader(); var entries = new List<AuditEntry>();
            while (r.Read()) entries.Add(JsonSerializer.Deserialize<AuditEntry>(r.GetValue(0) is byte[] packed ? System.Text.Encoding.UTF8.GetString(BlobCodec.Decode(packed,32*1024*1024)) : r.GetString(0), Catalog.Json)!); return entries;
        }
    }
}
