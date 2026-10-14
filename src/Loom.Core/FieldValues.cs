using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Loom.Core;

public static class FieldValues
{
    public const int MaxBinaryBytes=8*1024*1024;
    public static void ValidateDefinition(Field f)
    {
        Science.MeasurementJson.ValidateDefinition(f);
        if(f.MaxLength is <1 or >2000 || f.MaxLength!=null && f.Type!="Text" || f.Minimum>f.Maximum || (f.Minimum!=null || f.Maximum!=null) && f.Type is not ("Integer" or "Decimal" or "Money"))throw new InvalidOperationException("Invalid field bounds or text length.");
        if(f.Options!=null && f.Type!="Select" || f.Target!=null && f.Type!="Link")throw new InvalidOperationException("Options belong to Select fields; targets belong to Link fields.");
        if(f.Type!="Binary" && (f.Compression!="auto" || f.MaxBytes!=MaxBinaryBytes || f.MediaType!="application/octet-stream"))throw new InvalidOperationException("Compression, byte limits and media types apply to Binary fields only.");
        if(f.Type!="Date" && f.DateFormat!="yyyy-MM-dd")throw new InvalidOperationException("Date formats apply to Date fields only.");
        if((f.Precision==null)!=(f.Scale==null) || f.Precision is <1 or >28 || f.Scale is <0 or >28 || f.Scale>f.Precision)
            throw new InvalidOperationException("Specify precision 1–28 and scale 0–precision together.");
        if((f.Precision!=null || f.Rounding!="reject") && f.Type is not ("Decimal" or "Money"))throw new InvalidOperationException("Precision and rounding apply to Decimal/Money only.");
        if(f.Rounding is not ("reject" or "even" or "away"))throw new InvalidOperationException("Rounding must be reject, even or away.");
        if(f.Rounding!="reject" && f.Scale==null && f.Type!="Money")throw new InvalidOperationException("Rounding requires an explicit scale.");
        if(f.Type=="Money" ? !Regex.IsMatch(f.Currency??"","^[A-Z]{3}$") : f.Currency!=null)throw new InvalidOperationException("Money needs a fixed three-letter uppercase currency; other types cannot specify currency.");
        if(f.DateFormat is not ("yyyy-MM-dd" or "MM/dd/yyyy" or "dd/MM/yyyy"))throw new InvalidOperationException("Unsupported explicit date format.");
        if(f.Compression is not ("auto" or "none" or "brotli") || f.MaxBytes<1 || f.MaxBytes>MaxBinaryBytes)throw new InvalidOperationException("Invalid binary compression or byte limit.");
        if(!Regex.IsMatch(f.MediaType,"^[A-Za-z0-9.+-]+/[A-Za-z0-9.+-]+$"))throw new InvalidOperationException("Invalid media type.");
        if(f.Type=="Binary" && (f.Unique || f.ReadOnly))throw new InvalidOperationException("Binary unique/computed fields are unsupported.");
        if(f.Type=="Select" && f.Options is not {Length:>0})throw new InvalidOperationException("Select requires options.");
        if(f.Default!=null){if(f.ReadOnly || f.Type is "Binary" or "Measurement" or "Link")throw new InvalidOperationException("Defaults are unsupported for computed, binary, measurement and link fields.");Normalize(f with{Default=null},JsonValue.Create(f.Default)!);}
    }

    // Parse the coefficient before constructing decimal: decimal.TryParse can round long fractions.
    public static decimal Decimal(string text, Field f)
    {
        text=text.Trim();
        if(!Regex.IsMatch(text,@"\A[+-]?[0-9]+(?:\.[0-9]+)?\z") || text.Length>2048)throw new LoomException(422,f.Label+" requires plain decimal notation (dot separator, no grouping or exponent).");
        var negative=text[0]=='-';var unsigned=text.TrimStart('+','-');var parts=unsigned.Split('.');
        int scale=parts.Length==2?parts[1].Length:0;
        var coefficient=BigInteger.Parse(string.Concat(parts),CultureInfo.InvariantCulture);
        while(scale>0 && coefficient%10==0){coefficient/=10;scale--;}
        var allowedScale=f.Scale??(f.Type=="Money"?2:(int?)null);
        if(allowedScale is int s && scale>s)
        {
            var divisor=BigInteger.Pow(10,scale-s);var quotient=BigInteger.DivRem(coefficient,divisor,out var remainder);
            if(remainder!=0 && f.Rounding=="reject")throw new LoomException(422,f.Label+" exceeds scale "+s+"; explicit rounding is required.");
            if(remainder!=0 && (remainder*2>divisor || remainder*2==divisor && (f.Rounding=="away" || !quotient.IsEven)))quotient++;
            coefficient=quotient;scale=s;
        }
        var precision=f.Precision??(f.Type=="Money"?19:(int?)null);
        if(precision is int p && coefficient*BigInteger.Pow(10,allowedScale!.Value-scale)>=BigInteger.Pow(10,p))throw new LoomException(422,f.Label+" exceeds configured precision.");
        while(scale>0 && coefficient%10==0){coefficient/=10;scale--;}
        if(scale>28 || coefficient>new BigInteger(decimal.MaxValue))throw new LoomException(422,f.Label+" cannot be represented exactly as Decimal.");
        var low=(uint)(coefficient&uint.MaxValue);var mid=(uint)((coefficient>>32)&uint.MaxValue);var high=(uint)((coefficient>>64)&uint.MaxValue);
        return new decimal((int)low,(int)mid,(int)high,negative,(byte)scale);
    }

    public static decimal SumExact(IEnumerable<decimal> values)
    {
        BigInteger total=0;
        foreach(var value in values)
        {
            var bits=decimal.GetBits(value);var scale=(bits[3]>>16)&255;
            var coefficient=(new BigInteger((uint)bits[2])<<64)+(new BigInteger((uint)bits[1])<<32)+(uint)bits[0];
            total+=((bits[3]&int.MinValue)!=0?-coefficient:coefficient)*BigInteger.Pow(10,28-scale);
        }
        var digits=BigInteger.Abs(total).ToString(CultureInfo.InvariantCulture).PadLeft(29,'0');
        return Decimal((total.Sign<0?"-":"")+digits[..^28]+"."+digits[^28..],new("total","Report total","Decimal"));
    }

    public static JsonNode Normalize(Field f, JsonNode value)
    {
        if(f.Type=="Measurement")return Science.MeasurementJson.Normalize(f,value);
        if(value is JsonObject or JsonArray)throw new LoomException(422,f.Label+" must be scalar.");
        var text=value.ToString().Trim();
        if(f.MaxLength is int max && text.Length>max)throw new LoomException(422,f.Label+" exceeds maximum length.");
        if(f.Minimum!=null || f.Maximum!=null){var number=Decimal(text,f.Type=="Integer"?new Field(f.Name,f.Label,"Decimal"):f);if(number<f.Minimum || number>f.Maximum)throw new LoomException(422,f.Label+" is outside its bounds.");}
        switch(f.Type)
        {
            case "Integer":
                if(long.TryParse(text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var integer))return JsonValue.Create(integer)!;
                break;
            case "Decimal": case "Money": return JsonValue.Create(Decimal(text,f))!;
            case "Boolean": if(bool.TryParse(text,out var boolean))return JsonValue.Create(boolean)!;break;
            case "Date":
                if(DateOnly.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))return JsonValue.Create(text)!;
                break;
            case "Select": if(f.Options!.Contains(text))return JsonValue.Create(text)!;break;
            case "Uri":
                if(text.Length<=2048 && Uri.TryCreate(text,UriKind.Absolute,out var uri) && new[]{"https","http","tsdb","influxdb"}.Contains(uri.Scheme) && uri.UserInfo.Length==0 && !string.IsNullOrEmpty(uri.Host))return JsonValue.Create(text)!;
                break;
            case "Binary":
                if(text.Length>4*((f.MaxBytes+2)/3))throw new LoomException(422,f.Label+" exceeds its byte limit.");
                try{var bytes=Convert.FromBase64String(text);if(bytes.Length<=f.MaxBytes)return JsonValue.Create(Convert.ToBase64String(bytes))!;}
                catch(FormatException){ }
                break;
            default: if(text.Length<=2000)return JsonValue.Create(text)!;break;
        }
        throw new LoomException(422,"Invalid "+f.Type+" value for "+f.Label);
    }
}
