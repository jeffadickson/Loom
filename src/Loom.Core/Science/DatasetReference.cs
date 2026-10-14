namespace Loom.Core.Science;

public sealed record DatasetReference(int Version,string Provider,string Dataset,string Revision,string[] Channels,
    decimal? StartSeconds=null,decimal? EndSeconds=null)
{
    public void Validate()
    {
        // Locator is an opaque adapter key, never a URL carrying credentials or a local file path.
        bool Key(string value)=>!string.IsNullOrWhiteSpace(value) && value.Length<=256 && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/');
        if(Version!=1 || !Key(Provider) || !Key(Dataset) || !Key(Revision) || Dataset.Split('/').Any(x=>x is "" or "." or "..") ||
            Channels is not {Length:>0 and <=256} || Channels.Any(c=>!Key(c)) || Channels.Distinct().Count()!=Channels.Length ||
            (StartSeconds==null)!=(EndSeconds==null) || StartSeconds>=EndSeconds)
            throw new LoomException(422,"Invalid versioned dataset reference.");
    }
}
public sealed record DatasetReadBudget(int MaxBytes=8*1024*1024,int MaxSamples=1_000_000)
{
    public void Validate(){if(MaxBytes<1 || MaxBytes>64*1024*1024 || MaxSamples<1 || MaxSamples>1_000_000)throw new LoomException(422,"Invalid dataset read budget.");}
}
// Adapter configuration owns credentials. References and browser requests never contain them.
public interface IDatasetAdapter
{
    string Provider {get;}
    ScientificArray Read(DatasetReference reference,DatasetReadBudget budget,CancellationToken cancellationToken);
}
public sealed class DatasetGateway(IEnumerable<IDatasetAdapter> adapters,Func<Actor,DatasetReference,bool> authorize)
{
    private readonly IReadOnlyDictionary<string,IDatasetAdapter> adapters=adapters.ToDictionary(a=>a.Provider,StringComparer.Ordinal);
    public ScientificArray Read(Actor actor,DatasetReference reference,DatasetReadBudget budget,CancellationToken cancellationToken=default)
    {
        reference.Validate();budget.Validate();
        // Detach mutable caller arrays before authorization and adapter dispatch.
        reference=reference with{Channels=(string[])reference.Channels.Clone()};
        if(!authorize(actor,reference))throw new LoomException(403,"Dataset read denied.");
        cancellationToken.ThrowIfCancellationRequested();
        if(!adapters.TryGetValue(reference.Provider,out var adapter))throw new LoomException(422,"Dataset provider is not configured.");
        var result=adapter.Read(reference,budget,cancellationToken);cancellationToken.ThrowIfCancellationRequested();
        var descriptor=result.Descriptor;var bytes=descriptor.ByteLength(budget.MaxBytes);
        if(bytes/descriptor.ElementBytes>budget.MaxSamples)throw new LoomException(422,"Adapter exceeded sample budget.");
        return result;
    }
}
