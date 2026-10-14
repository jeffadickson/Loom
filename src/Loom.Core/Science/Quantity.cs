using System.Globalization;

namespace Loom.Core.Science;

/// <summary>Immutable finite IEEE double measurement value. Not a financial decimal.</summary>
public sealed record Quantity
{
    public double Value { get; }
    public Unit Unit { get; }
    public Quantity(double value,Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        RequireFinite(value,"Quantity");
        if(unit.Kind==QuantityKind.Temperature && value*unit.Scale+unit.Offset<0)
            throw new LoomException(422,"Absolute temperature cannot be below zero kelvin.");
        Value=value;Unit=unit;
    }
    public Quantity To(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if(Unit.Kind!=target.Kind)throw new LoomException(422,"Cannot convert "+Unit.Kind+" to "+target.Kind+".");
        if(ReferenceEquals(Unit,target))return this;
        // Apply scale and origin separately: intervals have no offset.
        var scaled=ScaleFinite(Value,Unit.Scale/target.Scale);
        var result=scaled+(Unit.Offset-target.Offset)/target.Scale;
        RequireFinite(result,"Converted quantity");
        return new Quantity(result,target);
    }
    internal static double ScaleFinite(double value,double factor)
    {
        var result=value*factor;RequireFinite(result,"Scaled value");
        if(value!=0 && result==0)throw new LoomException(422,"Unit scaling underflows the representable double range.");
        return result;
    }
    internal static void RequireFinite(double value,string name)
    {
        if(!double.IsFinite(value))throw new LoomException(422,name+" must be finite.");
    }
    public override string ToString()=>Value.ToString("G17",CultureInfo.InvariantCulture)+" "+Unit.Symbol;
}

/// <summary>Standard uncertainty in the value's unit interval; null means unspecified.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(MeasurementConverter))]
public sealed record Measurement
{
    public Quantity Value { get; }
    public double? StandardUncertainty { get; }
    public Measurement(Quantity value,double? standardUncertainty=null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(standardUncertainty is double u)
        {
            Quantity.RequireFinite(u,"Standard uncertainty");
            if(u<0)throw new LoomException(422,"Standard uncertainty cannot be negative.");
        }
        Value=value;StandardUncertainty=standardUncertainty;
    }
    public Measurement To(Unit target)
    {
        var converted=Value.To(target); // Also verifies kind compatibility.
        var uncertainty=StandardUncertainty is double u
            ? Quantity.ScaleFinite(u,Math.Abs(Value.Unit.Scale/target.Scale)) : (double?)null;
        return new Measurement(converted,uncertainty);
    }
    // Returns a width in the current unit interval, not an absolute temperature or probability.
    public double ExpandedUncertainty(double coverageFactor)
    {
        Quantity.RequireFinite(coverageFactor,"Coverage factor");
        if(coverageFactor<=0)throw new LoomException(422,"Coverage factor must be positive.");
        if(StandardUncertainty is not double u)throw new LoomException(422,"Standard uncertainty is unspecified.");
        return Quantity.ScaleFinite(u,coverageFactor);
    }
}
