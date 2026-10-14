// DDL identifiers are validated Catalog identifiers/32-hex IDs; values are never user SQL.
#pragma warning disable EF1002
using Loom.Core;
using Microsoft.EntityFrameworkCore;
namespace Loom.Server;

public sealed partial class ServerDocumentStore
{
    private void EnsureQueryInfrastructure(LoomDbContext db, Catalog model)
    {
        if (engine == ServerEngine.PostgreSql)
        {
            // UTF-16 code-unit key reproduces .NET Ordinal, including supplementary characters.
            db.Database.ExecuteSqlRaw("""
                CREATE OR REPLACE FUNCTION loom_ordinal(value text) RETURNS text LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE AS $$
                SELECT COALESCE(string_agg(CASE WHEN cp < 65536 THEN lpad(to_hex(cp),4,'0')
                ELSE lpad(to_hex(55296 + ((cp-65536)/1024)),4,'0') || lpad(to_hex(56320 + ((cp-65536)%1024)),4,'0') END,'' ORDER BY n),'')
                FROM (SELECT n,ascii(substr(value,n,1)) cp FROM generate_series(1,length(value)) n) chars
                $$
                """);
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS loom_audit_document ON loom_audit(kind,document_id)");
            foreach (var type in model.Types.Values)
                foreach (var field in type.Fields.Where(f => f.Type is "Integer" or "Money" || f.Type == "Decimal" && f.Precision != null))
                {
                    // Identifiers and paths originate exclusively from the validated catalog.
                    var cast = field.Type == "Integer" ? "bigint" : $"numeric({field.Precision ?? 19},{field.Scale ?? 2})";
                    db.Database.ExecuteSqlRaw($"CREATE INDEX IF NOT EXISTS ix_{field.Id} ON loom_documents ((CAST(body::jsonb -> 'values' ->> '{field.Name}' AS {cast})),id) WHERE kind='{type.Name}'");
                }
        }
        else
        {
            db.Database.ExecuteSqlRaw("IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='loom_audit_document' AND object_id=OBJECT_ID('loom_audit')) CREATE INDEX loom_audit_document ON loom_audit(kind,document_id)");
            foreach (var type in model.Types.Values)
                foreach (var field in type.Fields.Where(f => f.Type is "Integer" or "Money" || f.Type == "Decimal" && f.Precision != null))
                {
                    var cast = field.Type == "Integer" ? "bigint" : $"decimal({field.Precision ?? 19},{field.Scale ?? 2})";
                    var column = "q_" + field.Id;
                    db.Database.ExecuteSqlRaw($"IF COL_LENGTH('loom_documents','{column}') IS NULL ALTER TABLE loom_documents ADD {column} AS CAST(CASE WHEN kind='{type.Name}' THEN JSON_VALUE(body,'$.values.{field.Name}') END AS {cast}) PERSISTED");
                    db.Database.ExecuteSqlRaw($"IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='ix_{field.Id}' AND object_id=OBJECT_ID('loom_documents')) CREATE INDEX ix_{field.Id} ON loom_documents(kind,{column},id)");
                }
        }
    }
}
