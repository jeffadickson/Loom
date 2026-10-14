using Loom.Core;
using Loom.Core.Science;

static class ScienceTests
{
    public static int Run()
    {
        int checks=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS science: "+message);}
        void Near(double actual,double expected,string message)=>Check(Math.Abs(actual-expected)<=1e-10*Math.Max(1,Math.Abs(expected)),message);
        void Reject(Action action,string message){try{action();throw new Exception("Expected rejection: "+message);}catch(LoomException e){Check(e.Status==422,message);}}
        Near(new Quantity(1000,Units.Millimetre).To(Units.Metre).Value,1,"millimetres to metres");
        Near(new Quantity(1,Units.Second).To(Units.Microsecond).Value,1e6,"seconds to microseconds");
        Near(new Quantity(2500,Units.Millivolt).To(Units.Volt).Value,2.5,"millivolts to volts");
        Near(new Quantity(0,Units.Celsius).To(Units.Kelvin).Value,273.15,"Celsius origin");
        Near(new Quantity(32,Units.Fahrenheit).To(Units.Celsius).Value,0,"Fahrenheit freezing point");
        Near(new Quantity(100,Units.Celsius).To(Units.Fahrenheit).Value,212,"Fahrenheit boiling point");
        Near(new Quantity(-40,Units.Celsius).To(Units.Fahrenheit).Value,-40,"negative temperature scale conversion");
        Near(new Quantity(10,Units.CelsiusInterval).To(Units.FahrenheitInterval).Value,18,"temperature interval has no offset");
        Reject(()=>new Quantity(20,Units.Celsius).To(Units.CelsiusInterval),"absolute temperature cannot become interval");
        Reject(()=>new Quantity(20,Units.Metre).To(Units.Second),"dimension mismatch");
        Reject(()=>Units.Get("M"),"unit symbols are case-sensitive");
        Reject(()=>new Quantity(-1,Units.Kelvin),"negative absolute kelvin rejected");
        Reject(()=>new Quantity(-274,Units.Celsius),"below absolute zero Celsius rejected");
        Near(new Quantity(-5,Units.KelvinInterval).To(Units.CelsiusInterval).Value,-5,"negative intervals are valid");
        foreach(var bad in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})
        {
            Reject(()=>new Quantity(bad,Units.Volt),"nonfinite quantity rejected");
            Reject(()=>new Measurement(new Quantity(1,Units.Volt),bad),"nonfinite uncertainty rejected");
            Reject(()=>new Measurement(new Quantity(1,Units.Volt),1).ExpandedUncertainty(bad),"nonfinite coverage rejected");
        }
        var original=new Measurement(new Quantity(25,Units.Celsius),.1);
        var kelvin=original.To(Units.Kelvin);var fahrenheit=original.To(Units.Fahrenheit);
        Near(kelvin.Value.Value,298.15,"measurement value uses temperature offset");
        Near(kelvin.StandardUncertainty!.Value,.1,"uncertainty does not receive temperature offset");
        Near(fahrenheit.StandardUncertainty!.Value,.18,"uncertainty scales to Fahrenheit interval");
        Near(original.Value.Value,25,"conversion preserves original value");
        Near(original.ExpandedUncertainty(2),.2,"expanded uncertainty applies explicit factor");
        Reject(()=>new Measurement(original.Value,-1),"negative uncertainty rejected");
        Reject(()=>original.ExpandedUncertainty(0),"zero coverage factor rejected");
        Reject(()=>original.ExpandedUncertainty(-1),"negative coverage factor rejected");
        var unknown=new Measurement(original.Value);
        Check(unknown.To(Units.Kelvin).StandardUncertainty==null,"unknown uncertainty stays unknown");
        Reject(()=>unknown.ExpandedUncertainty(2),"unknown uncertainty cannot be expanded");
        Near(new Measurement(original.Value,0).ExpandedUncertainty(2),0,"explicit zero is distinct from unknown");
        Reject(()=>new Quantity(double.MaxValue,Units.Second).To(Units.Microsecond),"conversion overflow rejected");
        Reject(()=>new Quantity(double.Epsilon,Units.Millivolt).To(Units.Volt),"scaling underflow rejected");
        Reject(()=>new Measurement(original.Value,double.MaxValue).ExpandedUncertainty(2),"uncertainty overflow rejected");
        foreach(var unit in new[]{Units.Kelvin,Units.Celsius,Units.Fahrenheit})
            Near(new Quantity(0,Units.Kelvin).To(unit).To(Units.Kelvin).Value,0,"absolute zero roundtrip "+unit.Symbol);
        foreach(var from in Units.All)
        foreach(var to in Units.All.Where(u=>u.Kind==from.Kind))
            Near(new Quantity(300,from).To(to).To(from).Value,300,"roundtrip "+from.Symbol+" / "+to.Symbol);
        return checks;
    }
}
