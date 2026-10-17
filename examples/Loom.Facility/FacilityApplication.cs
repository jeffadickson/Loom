using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Loom.Core;
using Loom.Sqlite;

namespace Loom.Facility;

/// <summary>Trusted membership; never construct this from a browser's role header.</summary>
public sealed record FacilityMember(string UserId,string TenantId,string Role);
public sealed record RequestInput(string Name,string Campaign,string Instrument,string Calibration,string Specimen,
    string Procedure,string Operator,string Starts,string Ends,string Cost,string? Amends=null);
public sealed record ResultInput(int Version,string Value,string Unit,string Notes,string Waveform,string? Dataset=null);
public sealed record ReviewInput(int Version,bool Approve,string Disposition);
public sealed record FacilityOverview(string Tenant,string Role,Dictionary<string,IReadOnlyList<Document>> Records,
    Dictionary<string,int> Totals,Dictionary<string,int> RequestStates,DocumentForm[] Forms);
public sealed record FacilityRequestDetail(Document Request,IReadOnlyList<Document> Results,IReadOnlyList<Document> Reviews,Dictionary<string,Document> References);

/// <summary>
/// The application boundary owns reservations, state transitions and evidence release.
/// It exposes no generic mutation endpoint for controlled workflow records.
/// </summary>
public sealed class FacilityApplication
{
    private readonly TenantWorkspace workspace;
    private readonly FacilityMember[] members;
    private readonly TimeProvider clock;
    private const string Worker = "$facility-worker";
    public FacilityApplication(string directory,IEnumerable<FacilityMember> members,TimeProvider? clock=null)
    {
        this.members=members.ToArray();this.clock=clock??TimeProvider.System;
        if(this.members.Any(m=>string.IsNullOrWhiteSpace(m.UserId)||m.UserId==Worker||m.Role is not ("Manager" or "Operator" or "Reviewer")) ||
            this.members.GroupBy(m=>(m.UserId,m.TenantId)).Any(g=>g.Count()>1))
            throw new InvalidOperationException("Facility memberships must have distinct users per tenant and valid roles.");
        var tenants=this.members.Select(m=>m.TenantId).Distinct().ToArray();
        if(tenants.Length==0)throw new InvalidOperationException("Configure at least one facility membership.");
        workspace=new(directory,tenants,(user,tenant)=>user==Worker?"Worker":this.members.SingleOrDefault(m=>m.UserId==user&&m.TenantId==tenant)?.Role,access:new FacilityPolicy());
        foreach(var tenant in tenants)workspace.Provision(tenant,FacilityModel.Catalog);
    }
    public IEnumerable<FacilityMember> Memberships(string user)=>members.Where(m=>m.UserId==user);
    private TenantSession Open(string user,string tenant)
    {
        if(user==Worker)throw new LoomException(403,"Reserved service identity.");
        return workspace.Open(user,tenant);
    }
    private static void Require(TenantSession s,params string[] roles)
    {if(!roles.Contains(s.Role))throw new LoomException(403,"This action requires "+string.Join(" or ",roles)+".");}
    private static string Value(Document d,string field)=>d.Values[field]?.ToString()??"";
    private static decimal Number(string value)
    {
        return FieldValues.Decimal(value,new("Value","Value","Decimal",Precision:28,Scale:6));
    }
    private static DateTimeOffset Instant(string text)
    {
        if(!text.EndsWith('Z')||!DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var result))
            throw new LoomException(422,"Times must be ISO 8601 UTC values ending in Z.");
        return result;
    }
    private static Document Expected(TenantSession s,string id,int version,params string[] states)
    {
        var request=s.Get("Request",id);
        if(request.Version!=version)throw new LoomException(409,"This request changed. Reload before continuing.");
        if(!states.Contains(Value(request,"Status")))throw new LoomException(409,"This action is not available in the current state.");
        return request;
    }
    private static Document Change(IApplicationSession s,Document d,string status,Action<JsonObject>? edit=null)
    {
        var values=(JsonObject)d.Values.DeepClone();values["Status"]=status;edit?.Invoke(values);
        return s.Update(d.Type,d.Id,d.Version,values);
    }
    /// <summary>Reads all pages for invariant checks; never truncates reservations to the visible page.</summary>
    private static IReadOnlyList<Document> All(IApplicationSession s,string type)
    {
        var schema=FacilityModel.Catalog.Get(type);var items=new List<Document>();Page page;
        do {
            page=SummaryPage(s,type,items.Count,200);
            items.AddRange(page.Items);
        } while(items.Count<page.Total);
        return items;
    }
    private static DocumentQuery SummaryQuery(string type,int skip=0,int take=25,string? search=null,FieldFilter[]? filters=null)
    {
        var schema=FacilityModel.Catalog.Get(type);
        return new(schema.Fields.Single(f=>f.Name==schema.TitleField).Id!,Skip:skip,Take:take,Search:search,Filters:filters,
            SelectFieldIds:schema.Fields.Where(f=>f.Type!="Binary").Select(f=>f.Id!).ToArray());
    }
    private static Page SummaryPage(IApplicationSession s,string type,int skip=0,int take=25,string? search=null,FieldFilter[]? filters=null)
    {
        var query=SummaryQuery(type,skip,take,search,filters);
        var visible=s.Describe(type).Fields.Select(f=>f.Id).ToHashSet();
        return s.Query(type,query with{SelectFieldIds=query.SelectFieldIds!.Where(visible.Contains).ToArray()});
    }
    /// <summary>Bounded initial pages and independent totals; no evidence/report payload is materialized.</summary>
    public FacilityOverview Overview(string user,string tenant)
    {
        using var s=Open(user,tenant);
        return s.Atomic(t=>
        {
            var pages=FacilityModel.Catalog.Types.Keys.Where(type=>type!="Release"||t.Role!="Operator")
                .ToDictionary(type=>type,type=>SummaryPage(t,type));
            var schema=FacilityModel.Catalog.Get("Request");
            var statusField=schema.Fields.Single(f=>f.Name=="Status");
            var counts=statusField.Options!.ToDictionary(status=>status,status=>t.Query("Request",
                SummaryQuery("Request",take:1,filters:[new(statusField.Id!,FilterOperator.Equal,JsonValue.Create(status))])
                    with{SelectFieldIds=[]}).Total);
            return new FacilityOverview(tenant,t.Role,pages.ToDictionary(p=>p.Key,p=>p.Value.Items),
                pages.ToDictionary(p=>p.Key,p=>p.Value.Total),counts,FacilityModel.Registers.Select(t.Describe).ToArray());
        });
    }
    public Page Browse(string user,string tenant,string type,int skip=0,int take=25,string? search=null,string? status=null)
    {
        using var s=Open(user,tenant);
        FieldFilter[]? filters=null;
        if(status!=null)
        {
            if(type!="Request")throw new LoomException(400,"Status filtering applies to requests.");
            filters=[new(FacilityModel.Catalog.Get(type).Fields.Single(f=>f.Name=="Status").Id!,FilterOperator.Equal,JsonValue.Create(status))];
        }
        return SummaryPage(s,type,skip,take,search,filters);
    }
    /// <summary>Fetch related evidence summaries only for the selected request, with all attempts preserved.</summary>
    public FacilityRequestDetail Detail(string user,string tenant,string id)
    {
        using var s=Open(user,tenant);
        return s.Atomic(t=>
        {
            var request=t.Get("Request",id);
            IReadOnlyList<Document> Related(string type)
            {
                var field=FacilityModel.Catalog.Get(type).Fields.Single(f=>f.Name=="Request");var rows=new List<Document>();Page page;
                do{page=SummaryPage(t,type,rows.Count,200,filters:[new(field.Id!,FilterOperator.Equal,JsonValue.Create(id))]);rows.AddRange(page.Items);}while(rows.Count<page.Total);
                return rows;
            }
            return new FacilityRequestDetail(request,Related("Result"),Related("Review"),
                new[]{"Campaign","Instrument","Calibration","Specimen","Procedure"}.ToDictionary(type=>type,type=>t.Get(type,Value(request,type))));
        });
    }
    public IReadOnlyList<FacilityMember> Operators(string user,string tenant)
    {using var s=Open(user,tenant);_ = s.Role;return members.Where(m=>m.TenantId==tenant&&m.Role=="Operator").ToArray();}
    /// <summary>Register revisions are immutable; correction means a new revision.</summary>
    public Document Register(string user,string tenant,string type,JsonObject values)
    {
        using var s=Open(user,tenant);Require(s,"Manager");
        if(!FacilityModel.Registers.Contains(type))throw new LoomException(403,"Use the workflow action for this record.");
        if(type=="Procedure"&&Number(values["Lower"]?.ToString()??"")>Number(values["Upper"]?.ToString()??""))
            throw new LoomException(422,"Lower limit must not exceed upper limit.");
        if(type=="Calibration"&&string.CompareOrdinal(values["ValidFrom"]?.ToString(),values["ValidUntil"]?.ToString())>0)
            throw new LoomException(422,"Calibration validity dates are reversed.");
        return s.Create(type,values);
    }
    /// <summary>Reservation checks and request insertion share a database write transaction.</summary>
    public Document Schedule(string user,string tenant,RequestInput input)
    {
        using var s=Open(user,tenant);Require(s,"Manager");
        return s.Atomic(t=>
        {
            var starts=Instant(input.Starts);var ends=Instant(input.Ends);
            if(ends<=starts)throw new LoomException(422,"End must be after start.");
            if(!members.Any(m=>m.UserId==input.Operator&&m.TenantId==tenant&&m.Role=="Operator"))
                throw new LoomException(422,"Assign an operator who belongs to this laboratory.");
            var calibration=t.Get("Calibration",input.Calibration);
            CheckCalibration(calibration,input.Instrument,starts,ends);
            var schema=FacilityModel.Catalog.Get("Request");
            FieldFilter Filter(string field,FilterOperator op,string value)=>new(schema.Fields.Single(f=>f.Name==field).Id!,op,JsonValue.Create(value));
            foreach(var status in new[]{"Scheduled","Running","ChangesRequested"})
                foreach(var resource in new[]{("Instrument",input.Instrument),("Operator",input.Operator)})
                {
                    var overlaps=t.Query("Request",SummaryQuery("Request",take:1,filters:[
                        Filter("Status",FilterOperator.Equal,status),Filter(resource.Item1,FilterOperator.Equal,resource.Item2),
                        Filter("Starts",FilterOperator.LessThan,ends.UtcDateTime.ToString("O")),
                        Filter("Ends",FilterOperator.GreaterThan,starts.UtcDateTime.ToString("O"))]) with{SelectFieldIds=[]});
                    if(overlaps.Total>0)throw new LoomException(409,"The instrument or operator already has an overlapping reservation.");
                }
            if(input.Amends!=null && Value(t.Get("Request",input.Amends),"Status")!="Released")
                throw new LoomException(422,"An amendment must refer to a released request.");
            return t.Create("Request",new()
            {
                ["Name"]=input.Name,["Campaign"]=input.Campaign,["Instrument"]=input.Instrument,["Calibration"]=input.Calibration,
                ["Specimen"]=input.Specimen,["Procedure"]=input.Procedure,["Operator"]=input.Operator,
                ["Starts"]=starts.UtcDateTime.ToString("O"),["Ends"]=ends.UtcDateTime.ToString("O"),["Cost"]=input.Cost,["Status"]="Scheduled",
                ["Attempt"]=0,["RequestedBy"]=user,["Amends"]=input.Amends
            });
        });
    }
    private static void CheckCalibration(Document calibration,string instrument,DateTimeOffset starts,DateTimeOffset ends)
    {
        if(Value(calibration,"Instrument")!=instrument ||
           DateOnly.FromDateTime(starts.UtcDateTime)<DateOnly.ParseExact(Value(calibration,"ValidFrom"),"yyyy-MM-dd",CultureInfo.InvariantCulture) ||
           DateOnly.FromDateTime(ends.UtcDateTime)>DateOnly.ParseExact(Value(calibration,"ValidUntil"),"yyyy-MM-dd",CultureInfo.InvariantCulture))
            throw new LoomException(422,"Selected calibration does not cover this instrument and test interval.");
    }
    public Document Start(string user,string tenant,string id,int version)
    {
        using var s=Open(user,tenant);Require(s,"Operator");
        var workflow=new ApplicationWorkflow(RequestStatusId,[new("Start",["Scheduled","ChangesRequested"],"Running",["Operator"],
            Guard:context=>
            {
                var d=context.Document;Assigned(d,user);var now=clock.GetUtcNow();
                if(now<Instant(Value(d,"Starts"))||now>=Instant(Value(d,"Ends")))
                    throw new LoomException(422,"Start within the reserved interval; ask the manager for a new request if it has expired.");
                CheckCalibration(context.Session.Get("Calibration",Value(d,"Calibration")),Value(d,"Instrument"),now,now);
            },Effect:(context,values)=>
            {
                values["Attempt"]=int.Parse(Value(context.Document,"Attempt"))+1;
                values["StartedAt"]=clock.GetUtcNow().ToString("O");
            })]);
        return workflow.Execute(s,"Request",id,version,"Start");
    }
    private static string RequestStatusId=>FacilityModel.Catalog.Get("Request").Fields.Single(f=>f.Name=="Status").Id!;

    /// <summary>Explicit cancellation releases a reservation while retaining its request and evidence audit.</summary>
    public Document Cancel(string user,string tenant,string id,int version,string reason)
    {
        using var s=Open(user,tenant);Require(s,"Manager");
        if(string.IsNullOrWhiteSpace(reason))throw new LoomException(422,"Explain why this request is being cancelled.");
        var workflow=new ApplicationWorkflow(RequestStatusId,[new("Cancel",["Scheduled","Running","ChangesRequested"],"Cancelled",["Manager"],
            Effect:(_,values)=>values["CancellationReason"]=reason)]);
        return workflow.Execute(s,"Request",id,version,"Cancel");
    }
    private static void Assigned(Document request,string user)
    {if(Value(request,"Operator")!=user)throw new LoomException(403,"Only the assigned operator may execute this request.");}
    /// <summary>Append raw evidence and submit its attempt for review atomically. Previous attempts are retained.</summary>
    public Document Record(string user,string tenant,string id,ResultInput input)
    {
        using var s=Open(user,tenant);Require(s,"Operator");
        return s.Atomic(t=>
        {
            var d=Expected(t,id,input.Version,"Running");Assigned(d,user);
            var now=clock.GetUtcNow();
            if(now>=Instant(Value(d,"Ends")))throw new LoomException(422,"Reservation expired. Cancel this attempt and schedule a new request.");
            CheckCalibration(t.Get("Calibration",Value(d,"Calibration")),Value(d,"Instrument"),now,now);
            var procedure=t.Get("Procedure",Value(d,"Procedure"));var value=Number(input.Value);
            if(input.Unit!=Value(procedure,"Unit"))throw new LoomException(422,"Measurement unit must match the procedure.");
            byte[] bytes;
            try{bytes=Convert.FromBase64String(input.Waveform);}catch(FormatException){throw new LoomException(422,"Evidence must be base64 encoded.");}
            if(bytes.Length==0||bytes.Length>1048576)throw new LoomException(422,"Attach between 1 byte and 1 MiB of evidence.");
            var outcome=value>=Number(Value(procedure,"Lower"))&&value<=Number(Value(procedure,"Upper"))?"Pass":"Fail";
            t.Create("Result",new(){["Name"]=Value(d,"Name")+" / attempt "+Value(d,"Attempt"),["Request"]=id,
                ["Attempt"]=d.Values["Attempt"]!.DeepClone(),["Value"]=input.Value,["Unit"]=input.Unit,["Outcome"]=outcome,
                ["Notes"]=input.Notes,["RecordedBy"]=user,["Sha256"]=Hash(bytes),["Waveform"]=input.Waveform,
                ["Dataset"]=string.IsNullOrWhiteSpace(input.Dataset)?null:input.Dataset,["RecordedAt"]=clock.GetUtcNow().ToString("O")});
            return Change(t,d,"AwaitingReview");
        });
    }
    public Document Review(string user,string tenant,string id,ReviewInput input)
    {
        using var s=Open(user,tenant);Require(s,"Reviewer");
        return s.Atomic(t=>
        {
            var d=Expected(t,id,input.Version,"AwaitingReview");
            if(user==Value(d,"Operator")||user==Value(d,"RequestedBy"))throw new LoomException(403,"Review must be independent of execution and request authoring.");
            var result=CurrentResult(t,d);
            if(input.Approve&&Value(result,"Outcome")!="Pass")throw new LoomException(422,"Failed measurements require correction and a new attempt.");
            t.Create("Review",new(){["Name"]=Value(d,"Name")+" / review "+Value(d,"Attempt"),["Request"]=id,["Result"]=result.Id,
                ["Decision"]=input.Approve?"Approved":"ChangesRequested",["Disposition"]=input.Disposition,["ReviewedBy"]=user,
                ["ReviewedAt"]=clock.GetUtcNow().ToString("O")});
            return Change(t,d,input.Approve?"Approved":"ChangesRequested");
        });
    }
    private static Document CurrentResult(IApplicationSession s,Document request)=>All(s,"Result")
        .Single(d=>Value(d,"Request")==request.Id&&Value(d,"Attempt")==Value(request,"Attempt"));
    public Document QueueRelease(string user,string tenant,string id,int version)
    {
        using var s=Open(user,tenant);Require(s,"Manager");
        var workflow=new ApplicationWorkflow(RequestStatusId,[new("QueueRelease",["Approved"],"ReleaseQueued",["Manager"],
            Effect:(context,_)=>context.Session.Create("ReleaseJob",new(){["Name"]=Value(context.Document,"Name"),["Request"]=id,["Status"]="Pending",["Attempts"]=0}))]);
        return workflow.Execute(s,"Request",id,version,"QueueRelease");
    }
    public void RetryRelease(string user,string tenant,string jobId,int version)
    {
        using var s=Open(user,tenant);Require(s,"Manager");
        new TransactionalJobs().Retry(s,"ReleaseJob",jobId,version);
    }
    /// <summary>
    /// Local durable report worker: claim, snapshot, publication and completion share one SQLite transaction.
    /// A process failure rolls back to Pending; competing workers recheck under the write lock.
    /// </summary>
    public int ProcessReleases()
    {
        var count=0;
        foreach(var tenant in members.Select(m=>m.TenantId).Distinct())
        {
            using var s=workspace.Open(Worker,tenant);
            foreach(var candidate in All(s,"ReleaseJob").Where(j=>Value(j,"Status")=="Pending"))
            {
                var outcome=new TransactionalJobs().Run(s,candidate,(t,job)=>
                {
                    var request=t.Get("Request",Value(job,"Request"));var result=CurrentResult(t,request);
                    var review=All(t,"Review").Single(r=>Value(r,"Result")==result.Id&&Value(r,"Decision")=="Approved");
                    if(Value(request,"Status")!="ReleaseQueued")throw new InvalidOperationException("Request is not queued.");
                    var at=clock.GetUtcNow().ToString("O");
                    var html=Report(t,tenant,request,result,review,at);
                    t.Create("Release",new(){["Name"]=Value(request,"Name")+" / release 1",["Request"]=request.Id,
                        ["Result"]=result.Id,["Review"]=review.Id,["ReleasedAt"]=at,["Html"]=Convert.ToBase64String(Encoding.UTF8.GetBytes(html)),["Sha256"]=Hash(Encoding.UTF8.GetBytes(html))});
                    Change(t,request,"Released");
                },"Report generation failed. Check the server log and request evidence.");
                if(outcome.Completed)count++;
                if(outcome.Exception!=null)ReleaseFailed?.Invoke(tenant,candidate.Id,outcome.Exception);
            }
        }
        return count;
    }
    public event Action<string,string,Exception>? ReleaseFailed;
    public string ReleasedReport(string user,string tenant,string id)
    {using var s=Open(user,tenant);Require(s,"Manager","Reviewer");return Encoding.UTF8.GetString(Convert.FromBase64String(Value(s.Get("Release",id),"Html")));}
    /// <summary>Exports only the immutable report and selected approved attempt, in one authorized snapshot.</summary>
    public byte[] ExportRelease(string user,string tenant,string id)
    {
        using var s=Open(user,tenant);Require(s,"Manager","Reviewer");
        return s.Atomic(t=>
        {
            var release=t.Get("Release",id);
            var result=t.Get("Result",Value(release,"Result"));
            var request=t.Get("Request",Value(release,"Request"));
            var review=t.Get("Review",Value(release,"Review"));
            if(Value(request,"Status")!="Released"||Value(result,"Request")!=request.Id||
                Value(review,"Request")!=request.Id||Value(review,"Result")!=result.Id||Value(review,"Decision")!="Approved")
                throw new LoomException(409,"Released evidence references are inconsistent.");
            Document WithoutPayload(Document document)
            {var copy=document.Copy();copy.Values.Remove("Waveform");copy.Values.Remove("Html");return copy;}
            var records=new[]{request,result,review,release}.Concat(new[]{"Campaign","Instrument","Calibration","Specimen","Procedure"}
                .Select(type=>t.Get(type,Value(request,type)))).Select(WithoutPayload).ToArray();
            var provenance=JsonSerializer.SerializeToUtf8Bytes(new {format="loom-facility-release",version=1,tenant,
                releaseId=release.Id,records},Catalog.Json);
            return Loom.Core.Science.EvidencePackage.Create(new Dictionary<string,byte[]>
            {
                ["report.html"]=Convert.FromBase64String(Value(release,"Html")),
                ["evidence.bin"]=Convert.FromBase64String(Value(result,"Waveform")),
                ["provenance.json"]=provenance,
                ["readme.txt"]=Encoding.UTF8.GetBytes("Loom Facility evidence package v1\nreport.html: released report\nevidence.bin: selected attempt raw bytes\nprovenance.json: record snapshots, exact decimal values and external dataset references\nmanifest.json: file lengths and SHA-256 digests\nVerify: dotnet Loom.Demo.dll --verify-evidence FILE.zip\nChecksums do not authenticate the author. External datasets are references only; they were not downloaded.\nContains project costs and potentially sensitive evidence. Share with authorized recipients only.\n")
            });
        });
    }
    public byte[] Evidence(string user,string tenant,string id)
    {using var s=Open(user,tenant);return Convert.FromBase64String(Value(s.Get("Result",id),"Waveform"));}
    public IReadOnlyList<AuditEntry> History(string user,string tenant,string type,string id)
    {
        using var s=Open(user,tenant);
        return s.History(type,id).Select(a=>a with {Before=Strip(a.Before),After=Strip(a.After)}).ToArray();
        static Document? Strip(Document? d)
        {if(d==null)return null;var copy=d.Copy();copy.Values.Remove("Waveform");copy.Values.Remove("Html");return copy;}
    }
    public void Backup(string tenant,string archive)=>workspace.Backup(tenant,archive);
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static string Report(IApplicationSession s,string tenant,Document request,Document result,Document review,string at)
    {
        string E(string text)=>WebUtility.HtmlEncode(text);
        var html=new StringBuilder("<!doctype html><html lang=en><meta charset=utf-8><title>Test release</title><style>body{font:16px system-ui;max-width:900px;margin:40px auto;color:#172a39}h1{font-size:32px}table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:10px;border-bottom:1px solid #ccc;overflow-wrap:anywhere}th{width:28%}section{break-inside:avoid}pre{white-space:pre-wrap}@media print{.evidence{break-before:page}body{margin:0}}</style><body>");
        html.Append("<p>TEST FACILITY OPERATIONS · RELEASED EVIDENCE</p><h1>").Append(E(Value(request,"Name"))).Append("</h1><p>Laboratory: ").Append(E(tenant)).Append(" · Released ").Append(E(at)).Append("</p>");
        foreach(var pair in new[]{("Request",request),("Campaign",s.Get("Campaign",Value(request,"Campaign"))),
            ("Instrument",s.Get("Instrument",Value(request,"Instrument"))),("Calibration",s.Get("Calibration",Value(request,"Calibration"))),
            ("Specimen",s.Get("Specimen",Value(request,"Specimen"))),("Procedure",s.Get("Procedure",Value(request,"Procedure"))),("Result",result),("Review",review)})
        {
            html.Append("<section").Append(pair.Item1=="Result"?" class=evidence":"").Append("><h2>").Append(E(pair.Item1)).Append("</h2><p>Record ").Append(E(pair.Item2.Id)).Append(" · version ").Append(pair.Item2.Version).Append("</p><table>");
            foreach(var field in pair.Item2.Values.Where(v=>v.Key!="Waveform"))
                html.Append("<tr><th>").Append(E(field.Key)).Append("</th><td>").Append(E(field.Value?.ToString()??"—")).Append("</td></tr>");
            html.Append("</table></section>");
        }
        return html.Append("<p>Evidence is retained with its SHA-256 hash. External dataset availability is managed by its owner. This report records application approval, not a regulatory certification.</p></body></html>").ToString();
    }
}
