using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Loom.Core.Science;

// Numbers are round-trip invariant strings, including when consumed by JavaScript.
public static class MeasurementJson
{
    public static void ValidateDefinition(Field field)
    {
        if(field.Type!="Measurement")
        {
            if(field.QuantityKind!=null || field.AllowedUnits!=null)throw new InvalidOperationException("Quantity metadata belongs to Measurement fields.");
            return;
        }
        if(!Enum.TryParse<QuantityKind>(field.QuantityKind,out var kind) || !Enum.IsDefined(kind) ||
            field.AllowedUnits is not {Length:>0} || field.AllowedUnits.Distinct().Count()!=field.AllowedUnits.Length ||
            field.AllowedUnits.Any(s=>!Units.All.Any(u=>u.Symbol==s && u.Kind==kind)))
            throw new InvalidOperationException("Measurement requires a quantity kind and compatible, distinct allowed units.");
        if(field.Unique)throw new InvalidOperationException("Measurement uniqueness requires an explicit comparison unit.");
    }
    public static JsonObject Write(Measurement measurement)=>new()
    {
        ["value"]=measurement.Value.Value.ToString("R",CultureInfo.InvariantCulture),
        ["unit"]=measurement.Value.Unit.Symbol,
        ["standardUncertainty"]=measurement.StandardUncertainty?.ToString("R",CultureInfo.InvariantCulture)
    };
    public static Measurement Read(JsonNode node)
    {
        if(node is not JsonObject obj || obj.Any(p=>p.Key is not ("value" or "unit" or "standardUncertainty")) ||
            obj["unit"] is not JsonValue unit || !unit.TryGetValue<string>(out var symbol))
            throw new LoomException(422,"Measurement needs value, unit and optional standardUncertainty; unknown members are rejected.");
        double Parse(JsonNode? value)
        {
            if(value is not JsonValue || !double.TryParse(value.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var number) || !double.IsFinite(number))
                throw new LoomException(422,"Measurement numbers must be finite invariant values.");
            if(number==0 && value.ToString().Split('e','E')[0].Any(c=>c>='1' && c<='9'))throw new LoomException(422,"Measurement number underflows the double range.");
            return number;
        }
        return new(new Quantity(Parse(obj["value"]),Units.Get(symbol)),obj["standardUncertainty"]==null?null:Parse(obj["standardUncertainty"]));
    }
    public static JsonObject Normalize(Field field,JsonNode node)
    {
        var measurement=Read(node);
        if(measurement.Value.Unit.Kind.ToString()!=field.QuantityKind || !field.AllowedUnits!.Contains(measurement.Value.Unit.Symbol))
            throw new LoomException(422,field.Label+" has an incompatible or disallowed unit.");
        return Write(measurement);
    }
    public static string Format(JsonNode node)
    {
        var m=Read(node);
        return m.Value.Value.ToString("R",CultureInfo.InvariantCulture)+(m.StandardUncertainty is double u?" ± "+u.ToString("R",CultureInfo.InvariantCulture):"")+" "+m.Value.Unit.Symbol;
    }
}
public sealed class MeasurementConverter : JsonConverter<Measurement>
{
    public override Measurement Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>MeasurementJson.Read(JsonNode.Parse(ref reader)!);
    public override void Write(Utf8JsonWriter writer,Measurement value,JsonSerializerOptions options)=>MeasurementJson.Write(value).WriteTo(writer,options);
}
