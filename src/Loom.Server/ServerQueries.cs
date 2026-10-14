using System.Text.Json;
using Loom.Core;
using Microsoft.EntityFrameworkCore;

namespace Loom.Server;

// Translate only shapes whose ordering/precision match Queries exactly. Text search
// and unrestricted Decimal on SQL Server retain the reference implementation.
internal static class ServerQueries
{
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
        if(!string.IsNullOrWhiteSpace(query.Search) || NumericType(fields[query.SortFieldId])==null ||
            query.Filters!.Any(f=>NumericType(fields[f.FieldId])==null))return null;
        var parameters=new List<object>();
        string Parameter(object value)
        {
            using var command=db.Database.GetDbConnection().CreateCommand();
            var parameter=command.CreateParameter();parameter.ParameterName="@q"+parameters.Count;
            parameter.Value=value;parameters.Add(parameter);return parameter.ParameterName;
        }
        string Value(Field field)
        {
            var path=Parameter(sqlServer?"$.values."+field.Name:field.Name);
            var json=sqlServer?$"JSON_VALUE(body,{path})":$"(body::jsonb -> 'values' ->> {path})";
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
            predicates.Add(value+op+"CAST("+Parameter(filter.Value!.ToString())+" AS "+NumericType(field)+")");
        }
        var select="SELECT kind,id,version,body FROM loom_documents WHERE "+string.Join(" AND ",predicates);
        int count=db.Documents.FromSqlRaw(select,parameters.ToArray()).Count();
        var sort=Value(fields[query.SortFieldId]);var direction=query.Descending?"DESC":"ASC";
        var sql=select+$" ORDER BY CASE WHEN {sort} IS NULL THEN 0 ELSE 1 END {direction}, {sort} {direction}, id ASC";
        sql+=sqlServer?$" OFFSET {Parameter(query.Skip)} ROWS FETCH NEXT {Parameter(query.Take)} ROWS ONLY":$" LIMIT {Parameter(query.Take)} OFFSET {Parameter(query.Skip)}";
        var rows=db.Documents.FromSqlRaw(sql,parameters.ToArray()).AsNoTracking().ToArray();
        return new(count,rows.Select(r=>JsonSerializer.Deserialize<Document>(r.Body,Catalog.Json)!).ToArray());
    }
}
