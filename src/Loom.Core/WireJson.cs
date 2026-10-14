using System.Text.Json;
using System.Text.Json.Nodes;

namespace Loom.Core;

public static class WireJson
{
    // Document field numbers and saved-report cells are strings on the HTTP boundary.
    // Metadata, versions and pagination counts retain their ordinary JSON types.
    public static JsonNode? Encode(object value)
    {
        var node=JsonSerializer.SerializeToNode(value,Catalog.Json);
        void Walk(JsonNode? item,bool cells=false)
        {
            if(item is JsonObject obj)
                foreach(var (key,child) in obj.ToArray())
                {
                    if(cells && child is JsonValue number && number.GetValueKind()==JsonValueKind.Number)obj[key]=number.ToJsonString();
                    else Walk(child,cells || key is "values" or "rows");
                }
            else if(item is JsonArray array)
                for(var i=0;i<array.Count;i++)
                    if(cells && array[i] is JsonValue number && number.GetValueKind()==JsonValueKind.Number)array[i]=number.ToJsonString();else Walk(array[i],cells);
        }
        Walk(node);return node;
    }
}
