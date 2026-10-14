using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loom.Core;

namespace Loom.Demo;

public sealed record FieldEdit(string Id, string Name, string Label, bool? Required = null, JsonNode? Backfill = null, string? Type = null, FieldSettings? Settings = null);
public sealed record FieldAddition(string Name, string Label, string Type, string? Id = null, bool Required = false, JsonNode? Backfill = null, FieldSettings? Settings = null);
public sealed record FieldSettings(int? Precision=null,int? Scale=null,string? Currency=null,string Rounding="reject",string DateFormat="yyyy-MM-dd",string[]? Options=null,string Compression="auto",int MaxBytes=8388608,string MediaType="application/octet-stream")
{
    public Field Apply(Field f)=>f with {Precision=Precision,Scale=Scale,Currency=Currency,Rounding=Rounding,DateFormat=DateFormat,Options=Options,Compression=Compression,MaxBytes=MaxBytes,MediaType=MediaType};
}
public sealed record DesignRequest(string SourceHash, string Type, FieldEdit[] Fields, FieldAddition[] Additions);
public sealed record ApplyRequest(string PlanId);

public static class Designer
{
    public static Catalog Edit(Catalog current, DesignRequest request, IEnumerable<IDocumentHook> hooks)
    {
        if (request.SourceHash != SchemaIdentity.Hash(current)) throw new LoomException(409, "Model changed. Reopen the designer before editing.");
        var old = current.Get(request.Type);
        if (request.Fields == null || request.Additions == null || request.Fields.Length != old.Fields.Length || request.Fields.Select(f => f.Id).Distinct().Count() != old.Fields.Length)
            throw new LoomException(400, "Preserve all existing field identities exactly once.");
        var fields = new List<Field>();
        foreach (var f in old.Fields)
        {
            var edit = request.Fields.SingleOrDefault(e => e.Id == f.Id) ?? throw new LoomException(400, "Unknown/missing field identity.");
            if (string.IsNullOrWhiteSpace(edit.Label)) throw new LoomException(422, "Field captions cannot be empty.");
            if (f.Name != edit.Name && hooks.Any(h => h.Type == old.Name && h.RequiredFields.Contains(f.Name)))
                throw new LoomException(422, f.Name + " is used by a C# hook. Update the hook and its dependency declaration before renaming it.");
            var fieldType=edit.Type??f.Type;
            if(fieldType!=f.Type && hooks.Any(h=>h.Type==old.Name && h.RequiredFields.Contains(f.Name)))
                throw new LoomException(422,f.Name+" is used by a C# hook. Update the hook before changing its type.");
            var required=edit.Required??f.Required;
            if(edit.Backfill!=null && (f.Required || !required))throw new LoomException(422,"Existing-field backfills require an optional-to-required change.");
            var updatedField=f with { Name = edit.Name, Label = edit.Label, Required = required, Type = fieldType, Options = fieldType=="Select"?f.Options:null };
            fields.Add(edit.Settings?.Apply(updatedField)??updatedField);
        }
        foreach (var added in request.Additions)
        {
            if (!new[] { "Text", "Integer", "Decimal", "Date", "Boolean", "Money", "Binary", "Uri", "Select" }.Contains(added.Type)) throw new LoomException(422, "Unsupported field type.");
            if (string.IsNullOrWhiteSpace(added.Label)) throw new LoomException(422, "New fields need a caption.");
            if(!added.Required && added.Backfill!=null)throw new LoomException(422,"Backfills apply to required additions only.");
            var newField=new Field(added.Name, added.Label, added.Type, Required: added.Required, Id: added.Id ?? Guid.NewGuid().ToString("N"));
            fields.Add(added.Settings?.Apply(newField)??newField);
        }
        var titleId = old.Fields.Single(f => f.Name == old.TitleField).Id;
        var updated = old with { Version = checked(old.Version + 1), Fields = fields.ToArray(), TitleField = fields.Single(f => f.Id == titleId).Name };
        return new Catalog(current.Types.Values.Select(t => t.Id == old.Id ? updated : t));
    }
    public static byte[] Export(Catalog catalog)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var t in catalog.Types.Values)
            {
                var entry = zip.CreateEntry("Metadata/" + t.Name + ".json");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8); writer.Write(JsonSerializer.Serialize(t, Catalog.Json));
            }
            var readme = zip.CreateEntry("README.txt");
            using var text = new StreamWriter(readme.Open(), Encoding.UTF8);
            text.Write("Exported accepted Loom model. Preserve all permanent IDs. Copy the Metadata files into the source project, run --generate, and rebuild. Generated C# data shapes are not updated inside a running process. Deploy the reviewed model using migration planning; changing connection strings does not transfer data.\nSchema fingerprint: " + SchemaIdentity.Hash(catalog));
        }
        return memory.ToArray();
    }
}
