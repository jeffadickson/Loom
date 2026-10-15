using System.Text.Json;
using Loom.Core;
using Microsoft.EntityFrameworkCore;

namespace Loom.Server;

// Translate only shapes whose ordering/precision match Queries exactly. Text search
// retains the reference implementation; scalar ordering is provider-side.
internal static class ServerQueries
{
    /// <summary>Strip excluded JSON fields on the server, before EF materializes the document body.</summary>
    internal static string ProjectBody(DocType schema,string[]? selected,bool sqlServer)
    {
        var body="body";
        if(selected==null)return body;
        foreach(var field in schema.Fields.Where(f=>!selected.Contains(f.Id!)))
        {
            Catalog.Identifier(field.Name);
            body=sqlServer?$"JSON_MODIFY({body}, '$.values.{field.Name}', NULL)":$"({body}::jsonb #- ARRAY['values','{field.Name}'])";
        }
        return sqlServer?body:$"({body})::text";
    }
    public static Page? TryExecute(LoomDbContext db,DocType schema,DocumentQuery query)
    {
        bool sqlServer=db.Database.IsSqlServer();
        var fields=schema.Fields.ToDictionary(f=>f.Id!);
        string? NumericType(Field f)=>f.Type switch
        {
            "Integer"=>"bigint",
            "Money"=>$"decimal({f.Precision??19},{f.Scale??2})",
            "Decimal" when f.Precision!=null=>$"decimal({f.Precision},{f.Scale})",
            "Decimal" when !sqlServer=>"numeric",
            _=>null
        };
        bool Text(Field f) => f.Type is "Text" or "Select" or "Link" or "Date" or "Uri";
        bool Supported(Field f) => NumericType(f) != null || Text(f);
        if(!string.IsNullOrWhiteSpace(query.Search) || !Supported(fields[query.SortFieldId]) ||
            query.Filters!.Any(f=>!Supported(fields[f.FieldId])))return null;
        var parameters=new List<object>();
        string Parameter(object value)
        {
            using var command=db.Database.GetDbConnection().CreateCommand();
            var parameter=command.CreateParameter();parameter.ParameterName="@q"+parameters.Count;
            parameter.Value=value;parameters.Add(parameter);return parameter.ParameterName;
        }
        string Value(Field field)
        {
            if (sqlServer && NumericType(field) != null) return "q_" + field.Id;
            if (sqlServer && Text(field)) return "j.f_" + field.Id + " COLLATE Latin1_General_100_BIN2";
            var path=Parameter(sqlServer?"$.values."+field.Name:field.Name);
            var json=sqlServer?$"(SELECT v FROM OPENJSON(body) WITH (v nvarchar(max) '$.values.{field.Name}'))":$"(body::jsonb -> 'values' ->> {path})";
            if (Text(field)) return sqlServer ? $"({json} COLLATE Latin1_General_100_BIN2)" : $"(loom_ordinal({json}) COLLATE \"C\")";
            return $"CAST({json} AS {NumericType(field)})";
        }
        var predicates=new List<string>{"kind="+Parameter(schema.Name)};
        foreach(var filter in query.Filters!)
        {
            var field=fields[filter.FieldId];var value=Value(field);
            if(filter.Operator==FilterOperator.IsNull){predicates.Add(value+" IS NULL");continue;}
            var op=filter.Operator switch{FilterOperator.Equal=>"=",FilterOperator.GreaterThan=>">",_=>"<"};
            // Text parameters plus an explicit numeric cast avoid driver-dependent
            // decimal precision inference and preserve all accepted decimal digits.
            var parameter = Parameter(filter.Value!.ToString());
            var comparison = Text(field) ? (sqlServer ? $"({parameter} COLLATE Latin1_General_100_BIN2)" : $"(loom_ordinal({parameter}) COLLATE \"C\")") : "CAST("+parameter+" AS "+NumericType(field)+")";
            predicates.Add(value+op+comparison);
        }
        var extract = sqlServer ? " OUTER APPLY OPENJSON(body) WITH (" + string.Join(",", schema.Fields.Where(Text).Select(f => $"f_{f.Id} nvarchar(max) '$.values.{f.Name}'")) + ") j" : "";
        if (sqlServer && !schema.Fields.Any(Text)) extract = "";
        var select="SELECT kind,id,version,"+ProjectBody(schema,query.SelectFieldIds,sqlServer)+" AS body FROM loom_documents" + extract + " WHERE "+string.Join(" AND ",predicates);
        int count;
        try { count=db.Documents.FromSqlRaw(select,parameters.ToArray()).Count(); }
        catch (System.Data.Common.DbException ex) { throw new InvalidOperationException("Provider query failed: " + select, ex); }
        var sort=Value(fields[query.SortFieldId]);var direction=query.Descending?"DESC":"ASC";
        var sql=select+$" ORDER BY CASE WHEN {sort} IS NULL THEN 0 ELSE 1 END {direction}, {sort} {direction}, id ASC";
        sql+=sqlServer?$" OFFSET {Parameter(query.Skip)} ROWS FETCH NEXT {Parameter(query.Take)} ROWS ONLY":$" LIMIT {Parameter(query.Take)} OFFSET {Parameter(query.Skip)}";
        var rows=db.Documents.FromSqlRaw(sql,parameters.ToArray()).AsNoTracking().ToArray();
        return new(count,rows.Select(r=>JsonSerializer.Deserialize<Document>(r.Body,Catalog.Json)!).ToArray());
    }
}
