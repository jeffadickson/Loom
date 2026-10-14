namespace Loom.Core.Science;

// Absolute temperatures and temperature differences are intentionally different kinds.
public enum QuantityKind { Length, Duration, Voltage, Temperature, TemperatureInterval }

public sealed class Unit
{
    public string Symbol { get; }
    public QuantityKind Kind { get; }
    internal double Scale { get; }
    internal double Offset { get; }
    internal Unit(string symbol, QuantityKind kind, double scale, double offset=0)
        => (Symbol,Kind,Scale,Offset)=(symbol,kind,scale,offset);
    public override string ToString()=>Symbol;
}

// Deliberately bounded registry. No ambiguous aliases, expression parsing or mutable definitions.
public static class Units
{
    public static Unit Metre { get; }=new("m",QuantityKind.Length,1);
    public static Unit Millimetre { get; }=new("mm",QuantityKind.Length,0.001);
    public static Unit Centimetre { get; }=new("cm",QuantityKind.Length,0.01);
    public static Unit Kilometre { get; }=new("km",QuantityKind.Length,1000);
    public static Unit Second { get; }=new("s",QuantityKind.Duration,1);
    public static Unit Millisecond { get; }=new("ms",QuantityKind.Duration,0.001);
    public static Unit Microsecond { get; }=new("us",QuantityKind.Duration,0.000001);
    public static Unit Volt { get; }=new("V",QuantityKind.Voltage,1);
    public static Unit Millivolt { get; }=new("mV",QuantityKind.Voltage,0.001);
    public static Unit Kelvin { get; }=new("K",QuantityKind.Temperature,1);
    public static Unit Celsius { get; }=new("degC",QuantityKind.Temperature,1,273.15);
    public static Unit Fahrenheit { get; }=new("degF",QuantityKind.Temperature,5d/9,273.15-32*(5d/9));
    public static Unit KelvinInterval { get; }=new("delta_K",QuantityKind.TemperatureInterval,1);
    public static Unit CelsiusInterval { get; }=new("delta_degC",QuantityKind.TemperatureInterval,1);
    public static Unit FahrenheitInterval { get; }=new("delta_degF",QuantityKind.TemperatureInterval,5d/9);
    public static IReadOnlyList<Unit> All { get; }=Array.AsReadOnly(new[]
    {
        Metre,Millimetre,Centimetre,Kilometre,Second,Millisecond,Microsecond,
        Volt,Millivolt,Kelvin,Celsius,Fahrenheit,KelvinInterval,CelsiusInterval,FahrenheitInterval
    });
    public static Unit Get(string symbol)=>All.SingleOrDefault(u=>u.Symbol==symbol)
        ?? throw new LoomException(422,"Unknown unit: "+symbol);
}
