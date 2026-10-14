namespace Loom.Core.Science;

public enum TimeScale { Utc, Tai, Gps, Monotonic }
[Flags] public enum SampleQuality : byte { Good=0, Missing=1, Saturated=2, ClockUnsynchronized=4, Suspect=8 }
public sealed record SampleClock(string Epoch,TimeScale TimeScale,decimal ResolutionSeconds,string Source,decimal? SynchronizationUncertaintySeconds)
{
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Epoch) || string.IsNullOrWhiteSpace(Source) || !Enum.IsDefined(TimeScale) || ResolutionSeconds<=0 || SynchronizationUncertaintySeconds<0)
            throw new LoomException(422,"Clock requires an explicit epoch, scale, positive resolution and nonnegative uncertainty.");
    }
}
/// <summary>Relative seconds in an explicitly declared clock; never implicitly converts UTC/TAI/GPS.</summary>
public sealed class SampleTiming
{
    private readonly decimal[] coordinates;
    private readonly SampleQuality[] quality;
    public SampleClock Clock {get;}
    public int Count=>coordinates.Length;
    public SampleTiming(SampleClock clock,IReadOnlyList<decimal> seconds,IReadOnlyList<SampleQuality> quality,int maxSamples=1_000_000)
    {
        clock.Validate();
        if(maxSamples<0 || seconds.Count>maxSamples || seconds.Count!=quality.Count)throw new LoomException(422,"Timing and quality lengths must match within the sample budget.");
        coordinates=seconds.ToArray();this.quality=quality.ToArray();Clock=clock;
        for(int i=0;i<Count;i++)
            if((i>0 && coordinates[i]<=coordinates[i-1]) || ((byte)this.quality[i]&~15)!=0)throw new LoomException(422,"Coordinates must increase strictly and quality flags must be known.");
    }
    public static SampleTiming Uniform(SampleClock clock,int count,decimal start,decimal interval,SampleQuality[]? quality=null,int maxSamples=1_000_000)
    {
        if(count<0 || count>maxSamples || interval<=0)throw new LoomException(422,"Invalid uniform sample grid.");
        try{return new(clock,Enumerable.Range(0,count).Select(i=>checked(start+i*interval)).ToArray(),quality??new SampleQuality[count],maxSamples);}
        catch(OverflowException){throw new LoomException(422,"Sample grid exceeds decimal timing range.");}
    }
    public decimal Coordinate(int index)=>coordinates[index];
    public SampleQuality Quality(int index)=>quality[index];
    public int[] Select(SampleQuality excluded)=>Enumerable.Range(0,Count).Where(i=>(quality[i]&excluded)==0).ToArray();
    public SampleTiming WithClockOffset(decimal offset)
    {
        try{return new(Clock,coordinates.Select(t=>checked(t+offset)).ToArray(),quality,Count);}
        catch(OverflowException){throw new LoomException(422,"Clock offset exceeds timing range.");}
    }
}
