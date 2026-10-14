using System.Globalization;
using Loom.Core;
using Loom.Core.Science;

var reading=new Measurement(new Quantity(25,Units.Celsius),0.1);
foreach(var unit in new[]{Units.Celsius,Units.Kelvin,Units.Fahrenheit})
{
    var converted=reading.To(unit);
    Console.WriteLine(FormattableString.Invariant($"{converted.Value.Value:F3} {unit.Symbol}; standard uncertainty {converted.StandardUncertainty:F3} in the corresponding unit interval"));
}
Console.WriteLine("Expanded uncertainty, k=2: "+reading.ExpandedUncertainty(2).ToString("F3",CultureInfo.InvariantCulture)+" degC interval; no coverage probability inferred.");
try{reading.Value.To(Units.Second);throw new Exception("Expected dimension mismatch");}
catch(LoomException e){Console.WriteLine("Expected refusal: "+e.Message);}
Console.WriteLine("Original remains: "+reading.Value);
