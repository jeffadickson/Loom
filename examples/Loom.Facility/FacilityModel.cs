using Loom.Core;

namespace Loom.Facility;

/// <summary>Application-owned schemas. Stable Loom IDs keep storage identity independent of form labels.</summary>
public static class FacilityModel
{
    public static readonly string[] Registers = ["Campaign","Instrument","Calibration","Specimen","Procedure"];
    private static Field Text(string name,bool required=true)=>new(name,name,"Text",required,MaxLength:2000);
    private static Field Link(string name,string target)=>new(name,name,"Link",true,Target:target);
    private static Dictionary<string,string[]> Grants(params string[] writers)
    {
        var grants=new Dictionary<string,string[]>();
        foreach(var role in new[]{"Manager","Operator","Reviewer","Worker"})
            grants[role]=writers.Contains(role)?["read","create","write","audit"]:["read","audit"];
        return grants;
    }
    private static DocType Type(string name,Field[] fields,bool immutable,params string[] writers)=>
        new(name,name,1,"Name",false,fields,Grants(writers),Immutable:immutable);
    public static Catalog Catalog { get; } = new([
        Type("Campaign",[Text("Name"),Text("Project"),new("Budget","Budget (USD)","Money",true,Currency:"USD",Minimum:0)],true,"Manager"),
        Type("Instrument",[Text("Name"),new("Serial","Serial","Text",true,true),Text("Location")],true,"Manager"),
        Type("Calibration",[Text("Name"),Link("Instrument","Instrument"),new("ValidFrom","Valid from","Date",true),
            new("ValidUntil","Valid until","Date",true),Text("Certificate")],true,"Manager"),
        Type("Specimen",[Text("Name"),new("Serial","Serial","Text",true,true),Text("Configuration"),Text("Material")],true,"Manager"),
        Type("Procedure",[Text("Name"),Text("Revision"),Text("Instructions"),Text("Unit"),
            new("Lower","Lower limit","Decimal",true,Precision:28,Scale:6),new("Upper","Upper limit","Decimal",true,Precision:28,Scale:6)],true,"Manager"),
        Type("Request",[Text("Name"),Link("Campaign","Campaign"),Link("Instrument","Instrument"),Link("Calibration","Calibration"),
            Link("Specimen","Specimen"),Link("Procedure","Procedure"),Text("Operator"),Text("Starts"),Text("Ends"),
            new("Cost","Estimated cost (USD)","Money",true,Currency:"USD",Minimum:0),
            new("Status","Status","Select",true,Options:["Scheduled","Running","AwaitingReview","ChangesRequested","Approved","ReleaseQueued","Released","Cancelled"]),
            new("Attempt","Attempt","Integer",true,Minimum:0),Text("RequestedBy"),Text("StartedAt",false),Text("CancellationReason",false),
            new("Amends","Amends","Link",Target:"Request")],false,"Manager","Operator","Reviewer","Worker"),
        Type("Result",[Text("Name"),Link("Request","Request"),new("Attempt","Attempt","Integer",true),
            new("Value","Measured value","Decimal",true,Precision:28,Scale:6),Text("Unit"),Text("Outcome"),Text("Notes"),
            Text("RecordedBy"),Text("Sha256"),new("Waveform","Raw evidence","Binary",true,Compression:"brotli",MaxBytes:1048576),
            new("Dataset","External dataset","Uri"),Text("RecordedAt")],true,"Operator"),
        Type("Review",[Text("Name"),Link("Request","Request"),Link("Result","Result"),Text("Decision"),Text("Disposition"),
            Text("ReviewedBy"),Text("ReviewedAt")],true,"Reviewer"),
        Type("ReleaseJob",[Text("Name"),new("Request","Request","Link",true,true,Target:"Request"),
            Text("Status"),new("Attempts","Attempts","Integer",true),Text("Error",false)],false,"Manager","Worker"),
        new DocType("Release","Release",1,"Name",false,[Text("Name"),new("Request","Request","Link",true,true,Target:"Request"),
            Link("Result","Result"),Link("Review","Review"),Text("ReleasedAt"),Text("Sha256"),
            new("Html","Released report","Binary",true,Compression:"brotli",MaxBytes:1048576,MediaType:"text/html")],
            new(){["Manager"]=["read","audit"],["Reviewer"]=["read","audit"],["Worker"]=["read","create","audit"]},Immutable:true)
    ]);
}

/// <summary>Costs are omitted from operator documents, queries and audit snapshots.</summary>
public sealed class FacilityPolicy : IDocumentAccessPolicy
{
    public bool CanRead(Field field,Actor actor)=>field.Name is not ("Cost" or "Budget") || actor.Role!="Operator";
    public bool CanWrite(Field field,Actor actor)=>CanRead(field,actor);
}
