using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Loom.Core;

public sealed record OutboxOptions(int MaxAttempts=5,TimeSpan? LeaseDuration=null,
    TimeSpan? InitialBackoff=null,TimeSpan? MaximumBackoff=null);
public sealed record OutboxMessage(string Id,string Key,string Handler,JsonObject Payload,int Attempt);
public sealed record OutboxLease(string Type,string Id,string Token,OutboxMessage Message);
public sealed record DeliveryOutcome(bool Acknowledged,Exception? Exception=null);

/// <summary>
/// Provider-independent leased outbox. Enqueue joins business transactions; external handlers run
/// outside them. Delivery is at least once: receivers must deduplicate the stable Key.
/// </summary>
public sealed class DeliveryOutbox
{
    private readonly TimeProvider clock;
    private readonly IDeliveryPayloadProtection? protection;
    private readonly int maxAttempts;
    private readonly TimeSpan leaseDuration,initialBackoff,maximumBackoff;
    public DeliveryOutbox(OutboxOptions? options=null,TimeProvider? clock=null,IDeliveryPayloadProtection? protection=null)
    {
        this.protection=protection;options??=new();this.clock=clock??TimeProvider.System;maxAttempts=options.MaxAttempts;
        leaseDuration=options.LeaseDuration??TimeSpan.FromMinutes(2);
        initialBackoff=options.InitialBackoff??TimeSpan.FromSeconds(5);
        maximumBackoff=options.MaximumBackoff??TimeSpan.FromMinutes(15);
        if(maxAttempts<1||maxAttempts>1000||leaseDuration<TimeSpan.FromMilliseconds(1)||leaseDuration>TimeSpan.FromDays(1)||
            initialBackoff<TimeSpan.FromMilliseconds(1)||maximumBackoff<initialBackoff||maximumBackoff>TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(options));
    }
    /// <summary>Explicit application-owned schema. Grant create/read to producers; write only to trusted workers/admins.</summary>
    public static DocType Definition(string name,Dictionary<string,string[]> permissions)=>new(name,"Delivery outbox",1,"Key",false,
        [new("Key","Idempotency key","Text",true,true,MaxLength:200),new("Handler","Handler","Text",true,MaxLength:120),
         new("Payload","Payload","Binary",MaxBytes:65536),
         new("Status","Status","Select",true,Options:["Pending","Leased","Complete","Failed","Cancelled"]),
         new("Attempts","Attempts","Integer",true,Minimum:0),new("DueAt","Due at (Unix ms)","Integer",true),
         new("LeaseUntil","Lease until (Unix ms)","Integer"),new("LeaseToken","Lease token","Text"),new("Error","Error","Text",MaxLength:500)],permissions);
    private long Now=>clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static long Number(Document d,string field)=>long.Parse(d.Values[field]!.ToString(),CultureInfo.InvariantCulture);
    private static string Value(Document d,string field)=>d.Values[field]?.ToString()??"";
    private static Document Edit(IApplicationSession s,Document d,Action<JsonObject> edit)
    {var values=(JsonObject)d.Values.DeepClone();edit(values);return s.Update(d.Type,d.Id,d.Version,values);}
    private static void ClearLease(JsonObject values){values["LeaseToken"]=null;values["LeaseUntil"]=null;}
    /// <summary>Call through the same atomic session as the business write. Duplicate keys fail the unique constraint.</summary>
    public Document Enqueue(IApplicationSession session,string type,string handler,JsonObject payload,
        DateTimeOffset? due=null,string? key=null)
    {
        if(string.IsNullOrWhiteSpace(handler))throw new ArgumentException("A registered handler name is required.",nameof(handler));
        key??=Guid.NewGuid().ToString("N");
        var bytes=EncodePayload(type,key,handler,payload);
        return session.Create(type,new(){["Key"]=key,["Handler"]=handler,
            ["Payload"]=Convert.ToBase64String(bytes),["Status"]="Pending",["Attempts"]=0,["DueAt"]=(due??clock.GetUtcNow()).ToUnixTimeMilliseconds()});
    }
    internal byte[] EncodePayload(string type,string key,string handler,JsonObject payload)
    {
        var bytes=Encoding.UTF8.GetBytes(payload.ToJsonString());
        if(bytes.Length>65536)throw new LoomException(422,"Payload exceeds 64 KiB.");
        bytes=protection?.Protect(bytes,System.Text.Json.JsonSerializer.Serialize(new[]{type,key,handler}))??bytes;
        if(bytes.Length>65536)throw new LoomException(422,"Protected payload exceeds storage limit.");
        return bytes;
    }
    internal JsonObject DecodePayload(string type,string key,string handler,byte[] bytes)
    {
        bytes=protection?.Unprotect(bytes,System.Text.Json.JsonSerializer.Serialize(new[]{type,key,handler}))??bytes;
        return JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject??throw new FormatException("Invalid payload.");
    }
    /// <summary>Expire old failed payloads in bounded batches. Keeps metadata/keys and makes retries impossible.</summary>
    public int ExpireFailedPayloads(IApplicationSession session,string type,TimeSpan retention,int take=25)
    {
        if(retention<TimeSpan.FromHours(1)||retention>TimeSpan.FromDays(3650)||take is <1 or >100)throw new ArgumentOutOfRangeException(nameof(retention));
        var schema=session.Describe(type);string Id(string name)=>schema.Fields.Single(f=>f.Name==name).Id!;
        var cutoff=checked(Now-(long)retention.TotalMilliseconds);
        var rows=session.Query(type,new(Id("DueAt"),Take:take,Filters:[new(Id("Status"),FilterOperator.Equal,JsonValue.Create("Failed")),new(Id("DueAt"),FilterOperator.LessThan,JsonValue.Create(cutoff))],SelectFieldIds:[]));
        var count=0;
        foreach(var candidate in rows.Items)count+=session.Atomic(s=>
        {
            var job=s.Get(type,candidate.Id);
            if(job.Version!=candidate.Version||Value(job,"Status")!="Failed"||Number(job,"DueAt")>=cutoff||DateTimeOffset.Parse(job.ModifiedAt,CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()>=cutoff)return 0;
            Edit(s,job,v=>{v["Status"]="Cancelled";v["Payload"]=null;v["Error"]="Payload retention expired.";ClearLease(v);});return 1;
        });
        return count;
    }
    /// <summary>Bounded candidate scan excludes payloads. Expired leases are included for crash recovery.</summary>
    public IReadOnlyList<Document> Candidates(IApplicationSession session,string type,int take=25)
    {
        if(take<1||take>100)throw new ArgumentOutOfRangeException(nameof(take));
        var form=session.Describe(type);string Id(string name)=>form.Fields.Single(f=>f.Name==name).Id!;
        var before=JsonValue.Create(checked(Now+1));
        Page Find(string status,string field)=>session.Query(type,new(Id(field),Take:take,
            Filters:[new(Id("Status"),FilterOperator.Equal,JsonValue.Create(status)),new(Id(field),FilterOperator.LessThan,before)],SelectFieldIds:[]));
        // Recover abandoned work first. The host polls again after processing this bounded batch.
        return Find("Leased","LeaseUntil").Items.Concat(Find("Pending","DueAt").Items).Take(take).ToArray();
    }
    /// <summary>Claim an observed version atomically. Claims consume attempts, including claims lost to a process crash.</summary>
    public OutboxLease? Claim(IApplicationSession session,Document candidate)=>session.Atomic(s=>
    {
        var job=s.Get(candidate.Type,candidate.Id);var now=Now;
        if(job.Version!=candidate.Version)return null;
        var status=Value(job,"Status");
        if(!(status=="Pending"&&Number(job,"DueAt")<=now || status=="Leased"&&Number(job,"LeaseUntil")<=now))return null;
        if(Number(job,"Attempts")>=maxAttempts)
        {
            Edit(s,job,v=>{v["Status"]="Failed";v["DueAt"]=now;v["Error"]="Delivery attempts exhausted.";ClearLease(v);});return null;
        }
        // Decode before claiming. Invalid envelopes fail closed and remain visible to operators.
        JsonObject payload;
        try{payload=DecodePayload(job.Type,Value(job,"Key"),Value(job,"Handler"),Convert.FromBase64String(Value(job,"Payload")));}
        catch(Exception ex) when(ex is FormatException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {Edit(s,job,v=>{v["Status"]="Failed";v["DueAt"]=now;v["Error"]="Invalid delivery payload.";ClearLease(v);});return null;}
        var token=Guid.NewGuid().ToString("N");var attempt=checked((int)Number(job,"Attempts")+1);
        Edit(s,job,v=>{v["Status"]="Leased";v["Attempts"]=attempt;v["LeaseToken"]=token;v["LeaseUntil"]=checked(now+(long)leaseDuration.TotalMilliseconds);});
        return new OutboxLease(job.Type,job.Id,token,new(job.Id,Value(job,"Key"),Value(job,"Handler"),payload,attempt));
    });
    private bool Owns(Document job,OutboxLease lease)=>Value(job,"Status")=="Leased"&&Value(job,"LeaseToken")==lease.Token&&Number(job,"LeaseUntil")>Now;
    /// <summary>Renew only an unexpired owned lease. An expired worker cannot revive its claim.</summary>
    public bool Renew(IApplicationSession session,OutboxLease lease)=>session.Atomic(s=>
    {
        var job=s.Get(lease.Type,lease.Id);if(!Owns(job,lease))return false;
        Edit(s,job,v=>v["LeaseUntil"]=checked(Now+(long)leaseDuration.TotalMilliseconds));return true;
    });
    /// <summary>Acknowledge only the live lease and erase the terminal payload. Stale acknowledgements return false.</summary>
    public bool Complete(IApplicationSession session,OutboxLease lease)=>session.Atomic(s=>
    {
        var job=s.Get(lease.Type,lease.Id);if(!Owns(job,lease))return false;
        Edit(s,job,v=>{v["Status"]="Complete";v["Payload"]=null;v["Error"]=null;ClearLease(v);});return true;
    });
    /// <summary>Persist capped exponential backoff or terminal failure, without persisting exception details.</summary>
    public bool Fail(IApplicationSession session,OutboxLease lease,string publicError="Delivery failed. Consult the server log.")=>session.Atomic(s=>
    {
        var job=s.Get(lease.Type,lease.Id);if(!Owns(job,lease))return false;
        var attempts=Number(job,"Attempts");
        var delay=Math.Min(maximumBackoff.TotalMilliseconds,initialBackoff.TotalMilliseconds*Math.Pow(2,Math.Min(attempts-1,30)));
        Edit(s,job,v=>{v["Status"]=attempts>=maxAttempts?"Failed":"Pending";v["DueAt"]=attempts>=maxAttempts?Now:checked(Now+(long)delay);v["Error"]=publicError;ClearLease(v);});return true;
    });
    /// <summary>Host-authorized manual retry preserves the idempotency key and resets the attempt budget.</summary>
    public Document Retry(IApplicationSession session,string type,string id,int version)=>session.Atomic(s=>
    {
        var job=s.Get(type,id);if(job.Version!=version||Value(job,"Status")!="Failed")throw new LoomException(409,"Only a current failed delivery can be retried.");
        return Edit(s,job,v=>{v["Status"]="Pending";v["Attempts"]=0;v["DueAt"]=Now;v["Error"]=null;ClearLease(v);});
    });
    /// <summary>Cancel only pending or failed work; cannot recall a delivery already in flight.</summary>
    public Document Cancel(IApplicationSession session,string type,string id,int version)=>session.Atomic(s=>
    {
        var job=s.Get(type,id);if(job.Version!=version||Value(job,"Status") is not ("Pending" or "Failed"))throw new LoomException(409,"Only current pending or failed deliveries can be cancelled.");
        return Edit(s,job,v=>{v["Status"]="Cancelled";v["Payload"]=null;v["Error"]=null;ClearLease(v);});
    });
    /// <summary>
    /// Invoke an allowlisted asynchronous transport outside a transaction. Host cancellation leaves the lease
    /// for recovery. Cancellation is cooperative; receivers must deduplicate even when a worker times out.
    /// </summary>
    public async Task<DeliveryOutcome> DeliverAsync(IApplicationSession session,OutboxLease lease,
        IReadOnlyDictionary<string,Func<OutboxMessage,CancellationToken,Task>> handlers,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current=session.Get(lease.Type,lease.Id);if(!Owns(current,lease))return new(false);
        var remaining=Number(current,"LeaseUntil")-Now;if(remaining<=0)return new(false);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(remaining),clock);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,deadline.Token);
        try
        {
            if(!handlers.TryGetValue(lease.Message.Handler,out var handler))throw new InvalidOperationException("No registered delivery handler.");
            await handler(lease.Message,linked.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {Fail(session,lease);return new(false,ex);}
        return new(Complete(session,lease));
    }
}
