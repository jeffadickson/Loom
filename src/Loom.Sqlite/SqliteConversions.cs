using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Loom.Core;
using Microsoft.Data.Sqlite;

namespace Loom.Sqlite;
public sealed partial class SqliteStore
{
    private static long ExactInteger(string value)
    {
        if(!Regex.IsMatch(value,@"\A[+-]?[0-9]+(?:\.0+)?\z"))throw new LoomException(422,"Fractional or malformed Decimal cannot become Integer.");
        if(!long.TryParse(value.Split('.')[0],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var result))throw new LoomException(422,"Decimal is outside the signed 64-bit Integer range.");
        return result;
    }
    private static bool RepresentationChanged(Field before,Field after) =>
        before.Type!=after.Type || before.Precision!=after.Precision || before.Scale!=after.Scale || before.Currency!=after.Currency || before.Rounding!=after.Rounding || before.DateFormat!=after.DateFormat ||
        JsonSerializer.Serialize(before.Options)!=JsonSerializer.Serialize(after.Options) || before.Compression!=after.Compression || before.MaxBytes!=after.MaxBytes || before.MediaType!=after.MediaType;
    private static string SchemaLiteral(Field field)=>"'"+JsonSerializer.Serialize(field,Catalog.Json).Replace("'","''")+"'";
    private static void CheckConversion(Field before,Field after)
    {
        if(before.Unique || before.ReadOnly || after.Unique || after.ReadOnly)throw new LoomException(422,"Unique/computed field conversions are unsupported.");
        if(before.Type=="Money" && (after.Type!="Money" || before.Currency!=after.Currency))throw new LoomException(422,"Currency changes/removal require an explicit exchange or mapping, not a schema cast.");
        var allowed=before.Type==after.Type || (before.Type,after.Type) is
            ("Integer","Decimal") or ("Decimal","Integer") or ("Integer","Money") or ("Decimal","Money") or
            ("Text","Integer") or ("Text","Decimal") or ("Text","Money") or ("Text","Date") or ("Text","Select") or ("Select","Text") or ("Text","Uri") or ("Uri","Text");
        if(!allowed)throw new LoomException(422,"Unsupported conversion: "+before.Type+" to "+after.Type);
    }
    private static object? ConvertFieldValue(object? raw,Field before,Field after)
    {
        if(raw==null || raw is DBNull)return null;
        if(before.Type=="Binary")
        {
            if(raw is not byte[] envelope)throw new LoomException(422,"Invalid binary storage.");
            var bytes=BlobCodec.Decode(envelope,before.MaxBytes);
            if(bytes.Length>after.MaxBytes)throw new LoomException(422,"Binary value exceeds new byte limit.");
            return BlobCodec.Encode(bytes,after.Compression);
        }
        if(before.Type=="Integer" && raw is not long)throw new LoomException(422,"Stored Integer is not signed 64-bit.");
        if(before.Type is "Decimal" or "Money" && raw is not string)throw new LoomException(422,"Stored Decimal is not exact text.");
        var text=before.Type=="Boolean" ? (Convert.ToInt64(raw,CultureInfo.InvariantCulture)==0?"false":"true") : Convert.ToString(raw,CultureInfo.InvariantCulture)!;
        // Validate the original decimal without allowing a parser to round it first.
        if(before.Type is "Decimal" or "Money")FieldValues.Decimal(text,new Field("check","Stored value","Decimal"));
        if(before.Type=="Decimal" && after.Type=="Integer")return ExactInteger(text);
        if(before.Type=="Text" && after.Type=="Date")
        {
            if(!DateOnly.TryParseExact(text.Trim(),after.DateFormat,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))throw new LoomException(422,"Date does not match explicit format "+after.DateFormat);
            text=date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        }
        var node=FieldValues.Normalize(after,JsonValue.Create(text)!);
        return after.Type switch {"Integer"=>node.GetValue<long>(),"Boolean"=>node.GetValue<bool>()?1:0,"Decimal" or "Money"=>node.GetValue<decimal>().ToString(CultureInfo.InvariantCulture),_=>node.ToString()};
    }
    private void RegisterConversions()=>connection.CreateFunction<object?,string,string,object?>("loom_convert",(raw,before,after)=>ConvertFieldValue(raw,JsonSerializer.Deserialize<Field>(before,Catalog.Json)!,JsonSerializer.Deserialize<Field>(after,Catalog.Json)!),isDeterministic:true);
    private long PreflightConversion(DocType type,Field before,Field after,SqliteTransaction tx)
    {
        CheckConversion(before,after);
        using var command=MigrationCommand($"SELECT {Q(before.Name)} FROM {Q(type.Name)}",tx);
        using var reader=command.ExecuteReader();long count=0,invalid=0;string? reason=null;
        while(reader.Read())
        {
            if(reader.IsDBNull(0))continue;count++;
            try{ConvertFieldValue(reader.GetValue(0),before,after);}
            catch(LoomException e){invalid++;reason??=e.Message;}
        }
        if(invalid>0)throw new LoomException(422,$"{type.Name}.{before.Name}: {invalid} of {count} populated values cannot convert. {reason} No values were changed.");
        return count;
    }
}
