using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Loom.Core;
using Microsoft.Data.Sqlite;

namespace Loom.Sqlite;

public sealed partial class SqliteStore
{
    private static JsonNode NormalizeBackfill(Field field, JsonNode? value)
    {
        if(value == null || value is JsonArray or JsonObject)
            throw new LoomException(422,"A scalar backfill value is required for " + field.Name);
        var text=value.ToString().Trim();
        if(text.Length==0)throw new LoomException(422,"Backfill cannot be empty: " + field.Name);
        return FieldValues.Normalize(field,value);
    }

    private long PreflightRequired(DocType type, Field field, SqliteTransaction tx)
    {
        using var command=MigrationCommand($"SELECT {Q(field.Name)} FROM {Q(type.Name)}",tx);
        using var reader=command.ExecuteReader();long missing=0;
        while(reader.Read())
        {
            if(reader.IsDBNull(0)){missing++;continue;}
            var raw=reader.GetValue(0);
            JsonNode value;
            if(field.Type=="Boolean")
            {
                if(raw is not long bit || (bit!=0 && bit!=1))throw new LoomException(422,"Invalid stored Boolean in "+type.Name+"."+field.Name);
                value=JsonValue.Create(bit==1)!;
            }
            else value=System.Text.Json.JsonSerializer.SerializeToNode(raw)!;
            try{NormalizeBackfill(field,value);}
            catch(LoomException){throw new LoomException(422,"Invalid non-null value in "+type.Name+"."+field.Name+"; correct the data before making it required.");}
        }
        return missing;
    }

    private static string BackfillLiteral(JsonNode value, Field? field=null)
    {
        if(field?.Type=="Binary")return "X'"+Convert.ToHexString(BlobCodec.Encode(Convert.FromBase64String(value.ToString()),field.Compression))+"'";
        var scalar=(JsonValue)value;
        if(scalar.TryGetValue<bool>(out var boolean))return boolean?"1":"0";
        if(scalar.TryGetValue<long>(out var integer))return integer.ToString(CultureInfo.InvariantCulture);
        // Decimal columns use TEXT for exact decimal storage. Hex also safely handles quotes/NUL.
        return "CAST(X'"+Convert.ToHexString(Encoding.UTF8.GetBytes(value.ToString()))+"' AS TEXT)";
    }

    private void VerifyRebuildObjects(DocType type, SqliteTransaction tx)
    {
        using var query=MigrationCommand("SELECT type,name FROM sqlite_master WHERE (tbl_name=$table AND type IN ('trigger','index')) OR type='view'",tx,("$table",type.Name));
        using var reader=query.ExecuteReader();
        var allowed=type.Fields.Where(f=>f.Unique).Select(f=>"ux_"+f.Id).Append("sqlite_autoindex_"+type.Name+"_1").ToHashSet();
        while(reader.Read())
            if(reader.GetString(0)!="index" || !allowed.Contains(reader.GetString(1)))
                throw new LoomException(409,"Table rebuild requires a framework-managed schema without custom indexes, triggers or views.");
        reader.Close();
        using var tables=MigrationCommand("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'",tx);
        using var names=tables.ExecuteReader();var allTables=new List<string>();
        while(names.Read())allTables.Add(names.GetString(0));names.Close();
        foreach(var name in allTables)
        {
            using var fk=MigrationCommand("SELECT count(*) FROM pragma_foreign_key_list($table)",tx,("$table",name));
            if((long)fk.ExecuteScalar()! != 0)throw new LoomException(409,"Table rebuild with physical foreign keys is unsupported.");
        }
        using var columns=MigrationCommand("SELECT count(*) FROM pragma_table_xinfo($table) WHERE hidden<>0 OR dflt_value IS NOT NULL",tx,("$table",type.Name));
        if((long)columns.ExecuteScalar()! != 0)throw new LoomException(409,"Table rebuild with custom defaults/generated columns is unsupported.");
        using var definition=MigrationCommand("SELECT sql FROM sqlite_master WHERE type='table' AND name=$table",tx,("$table",type.Name));
        var ddl=(string)definition.ExecuteScalar()!;
        if(System.Text.RegularExpressions.Regex.IsMatch(ddl,@"\b(CHECK|COLLATE|REFERENCES|AUTOINCREMENT|WITHOUT|STRICT)\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new LoomException(409,"Table rebuild with custom constraints is unsupported.");
    }
}
