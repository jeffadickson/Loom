using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Loom.Core.Science;

public sealed record DocumentRevision(string Type,string Id,int Version,string Sha256)
{
    // Goes through ordinary read authorization; captures the version actually read.
    public static DocumentRevision Capture(DocumentService service,Actor actor,string type,string id)
    {
        var document=service.Get(type,id,actor);
        return new(type,id,document.Version,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document,Catalog.Json)))).ToLowerInvariant());
    }
}
public sealed record RunProvenance(DocumentRevision Specimen,DocumentRevision Instrument,DocumentRevision Calibration,
    DocumentRevision Procedure,string Operator,string SoftwareVersion,DocumentRevision[] Inputs,string? AlgorithmVersion=null,string? ParametersJson=null)
{
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Operator) || string.IsNullOrWhiteSpace(SoftwareVersion) || Inputs==null || Inputs.Length>10000)
            throw new LoomException(422,"Run provenance requires operator, software version and bounded input references.");
        foreach(var reference in Inputs.Concat(new[]{Specimen,Instrument,Calibration,Procedure}))
            if(reference==null || string.IsNullOrWhiteSpace(reference.Type) || string.IsNullOrWhiteSpace(reference.Id) || reference.Version<1 ||
                reference.Sha256==null || reference.Sha256.Length!=64 || !reference.Sha256.All(Uri.IsHexDigit))throw new LoomException(422,"Invalid versioned provenance reference.");
        if(Inputs.Length>0 && string.IsNullOrWhiteSpace(AlgorithmVersion))throw new LoomException(422,"Derived runs require an algorithm version.");
        if(ParametersJson!=null)
        {
            if(ParametersJson.Length>65536)throw new LoomException(422,"Algorithm parameters exceed limit.");
            try{using var parsed=JsonDocument.Parse(ParametersJson);if(parsed.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException();}
            catch(JsonException){throw new LoomException(422,"Algorithm parameters must be a JSON object.");}
        }
    }
}
