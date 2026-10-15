using System.Globalization;
using System.Text.Json.Nodes;

namespace Loom.Core;
public enum FilterOperator { Equal, GreaterThan, LessThan, IsNull }
public sealed record FieldFilter(string FieldId,FilterOperator Operator,JsonNode? Value=null);
public sealed record DocumentQuery(string SortFieldId,bool Descending=false,int Skip=0,int Take=50,FieldFilter[]? Filters=null,string? Search=null,string[]? SelectFieldIds=null);
public static class Queries
{
    public static DocumentQuery Validate(DocType schema,DocumentQuery query)
    {
        if(query.Skip<0 || query.Take is <1 or >200 || query.Filters?.Length>32 || query.Search?.Length>2000)throw new LoomException(400,"Invalid query bounds.");
        Field Resolve(string id)=>schema.Fields.SingleOrDefault(f=>f.Id==id)??throw new LoomException(400,"Unknown query field identity.");
        void Scalar(Field f){if(f.Type is "Binary" or "Measurement")throw new LoomException(400,"Compound and binary queries require explicit scientific operators.");}
        Scalar(Resolve(query.SortFieldId));
        if(query.SelectFieldIds!=null)
        {
            if(query.SelectFieldIds.Length>schema.Fields.Length || query.SelectFieldIds.Distinct(StringComparer.Ordinal).Count()!=query.SelectFieldIds.Length)
                throw new LoomException(400,"Invalid or duplicate projection fields.");
            foreach(var id in query.SelectFieldIds)Resolve(id);
        }
        return query with{Filters=(query.Filters??[]).Select(f=>
        {
            if(f==null)throw new LoomException(400,"Query filters cannot be null.");
            var field=Resolve(f.FieldId);Scalar(field);
            if(!Enum.IsDefined(f.Operator) || f.Operator==FilterOperator.IsNull && f.Value!=null || f.Operator!=FilterOperator.IsNull && f.Value==null)
                throw new LoomException(400,"Invalid query operator/value.");
            return f with{Value=f.Value==null?null:FieldValues.Normalize(field with{Type=field.Type=="Select"?"Text":field.Type,Options=field.Type=="Select"?null:field.Options,Minimum=null,Maximum=null,MaxLength=null},f.Value)};
        }).ToArray()};
    }
    /// <summary>Projection retains document identity/version; omitted values are absent, never fabricated nulls.</summary>
    public static Document Project(Document document,DocType schema,string[]? selected)
    {
        if(selected==null)return document.Copy();
        var ids=selected.ToHashSet(StringComparer.Ordinal);var values=new JsonObject();
        foreach(var field in schema.Fields.Where(f=>ids.Contains(f.Id!)))
            if(document.Values.TryGetPropertyValue(field.Name,out var value))values[field.Name]=value?.DeepClone();
        return document with{Values=values};
    }
    public static int Compare(Field field,JsonNode? left,JsonNode? right)
    {
        if(left==null)return right==null?0:-1;if(right==null)return 1;
        return field.Type is "Integer" or "Decimal" or "Money"
            ? decimal.Parse(left.ToString(),CultureInfo.InvariantCulture).CompareTo(decimal.Parse(right.ToString(),CultureInfo.InvariantCulture))
            : StringComparer.Ordinal.Compare(left.ToString(),right.ToString());
    }
    public static Page Execute(IEnumerable<Document> source,DocType schema,DocumentQuery query)
    {
        query=Validate(schema,query);var fields=schema.Fields.ToDictionary(f=>f.Id!);
        if(!string.IsNullOrWhiteSpace(query.Search))source=source.Where(d=>schema.Fields.Where(f=>f.Type is not ("Binary" or "Measurement")).Any(f=>(d.Values[f.Name]?.ToString()??"").Contains(query.Search,StringComparison.OrdinalIgnoreCase)));
        foreach(var filter in query.Filters!)
        {
            var field=fields[filter.FieldId];
            source=source.Where(d=>filter.Operator==FilterOperator.IsNull?d.Values[field.Name]==null:d.Values[field.Name]!=null &&
                (filter.Operator switch{FilterOperator.Equal=>Compare(field,d.Values[field.Name],filter.Value)==0,FilterOperator.GreaterThan=>Compare(field,d.Values[field.Name],filter.Value)>0,_=>Compare(field,d.Values[field.Name],filter.Value)<0}));
        }
        var rows=source.ToList();var sort=fields[query.SortFieldId];var comparer=Comparer<JsonNode?>.Create((a,b)=>Compare(sort,a,b));
        var ordered=query.Descending?rows.OrderByDescending(d=>d.Values[sort.Name],comparer):rows.OrderBy(d=>d.Values[sort.Name],comparer);
        return new(rows.Count,ordered.ThenBy(d=>d.Id,StringComparer.Ordinal).Skip(query.Skip).Take(query.Take).Select(d=>Project(d,schema,query.SelectFieldIds)).ToArray());
    }
}
