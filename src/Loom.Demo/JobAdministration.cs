using System.Security.Claims;
using System.Text.Json.Nodes;
using Loom.Core;
using Loom.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace Loom.Demo;

/// <summary>Authenticated local operations host with isolated storage and an allowlisted demonstration receiver.</summary>
internal sealed class JobAdministration
{
    private static Dictionary<string,string[]> Permissions()=>new(){["Admin"]=["read","create","write","audit"],["Worker"]=["read","create","write","audit"]};
    private static readonly Catalog Model=new([
        DeliveryOutbox.Definition("Delivery",Permissions()),RecurringSchedules.Definition("Schedule",Permissions()),
        new("Receipt","Receipts",1,"Key",false,[new("Key","Key","Text",true,true),new("Message","Message","Text",true)],Permissions(),Immutable:true)]);
    private readonly string connectionString;
    private readonly DeliveryOutbox outbox=new();
    private readonly RecurringSchedules schedules;
    public const string Password="Loom-jobs-demo!";
    public JobAdministration(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString=new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString();
        using var store=new SqliteStore(connectionString);store.Migrate(Model);schedules=new(outbox);
    }
    private SqliteStore Open(){var store=new SqliteStore(connectionString);store.ReadCatalog();return store;}
    private static ApplicationSession Session(SqliteStore store,Actor actor)=>new(new(Model,store,[]),actor);
    private static Actor Admin(HttpContext c)=>c.User.Identity?.IsAuthenticated!=true?throw new LoomException(401,"Sign in to continue."):
        c.User.IsInRole("Admin")?new(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!,"Admin"):throw new LoomException(403,"Administrator access required.");
    public static void ConfigureDemo(WebApplicationBuilder builder)
    {
        if(builder.Configuration["Authentication:Mode"] is "Accounts" or "Identity")throw new InvalidOperationException("--jobs-demo cannot replace configured authentication.");
        builder.Configuration["Authentication:Mode"]="Accounts";
        foreach(var (name,role,index) in new[]{("jobs-admin","Admin",0),("jobs-viewer","Viewer",1)})
        {
            var prefix=$"Authentication:Accounts:{index}:";builder.Configuration[prefix+"Id"]=name;builder.Configuration[prefix+"Role"]=role;
            builder.Configuration[prefix+"PasswordHash"]=new PasswordHasher<LoomAccount>().HashPassword(new(name,role,""),Password);
        }
    }
    public static JobAdministration? Configure(WebApplicationBuilder builder,bool demo,bool authenticated)
    {
        if(!demo&&!builder.Configuration.GetValue<bool>("Jobs:Enabled"))return null;
        if(!authenticated)throw new InvalidOperationException("Jobs administration requires authentication.");
        var path=Path.GetFullPath(builder.Configuration["Jobs:Database"]??Path.Combine(builder.Environment.ContentRootPath,"data","jobs.db"));
        var web=Path.GetFullPath(builder.Environment.WebRootPath??Path.Combine(builder.Environment.ContentRootPath,"wwwroot"));
        if(path.Equals(web,StringComparison.OrdinalIgnoreCase)||path.StartsWith(web+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Jobs database must be outside the web root.");
        var host=new JobAdministration(path);
        if(!builder.Configuration.GetValue<bool>("Jobs:PauseWorker"))builder.Services.AddHostedService(_=>new Worker(host));
        return host;
    }
    private static void Handler(string value)
    {if(value is not ("demo.receipt" or "demo.failure"))throw new LoomException(422,"Choose an available handler.");}
    public sealed record NewSchedule(string Name,int IntervalSeconds,string FirstDue,string MissedRuns,string Handler,string Message);
    public sealed record NewDelivery(string Handler,string Message,string? Due);
    public sealed record Version(int VersionNumber);
    public sealed record ScheduleState(int VersionNumber,bool Enabled);
    private static DateTimeOffset Instant(string? text)
    {
        if(text==null)return DateTimeOffset.UtcNow;
        if(!text.EndsWith('Z')||!DateTimeOffset.TryParse(text,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out var result))
            throw new LoomException(422,"Supply a UTC date and time ending in Z.");
        return result;
    }
    public void Map(WebApplication app)
    {
        var routes=app.MapGroup("/api/jobs");
        routes.MapGet("/records/{type}",(HttpContext c,string type,int? skip,string? status)=>
        {
            var actor=Admin(c);if(type is not ("Delivery" or "Schedule" or "Receipt"))throw new LoomException(404,"Unknown operations view.");
            using var store=Open();var s=Session(store,actor);var schema=Model.Get(type);
            var filters=status==null||status==""?Array.Empty<FieldFilter>():type=="Delivery"?
                [new FieldFilter(schema.Fields.Single(f=>f.Name=="Status").Id!,FilterOperator.Equal,JsonValue.Create(status))]:throw new LoomException(400,"Status filter applies to deliveries.");
            var sort=type=="Schedule"?"NextDueAt":type=="Delivery"?"DueAt":"Key";
            return WireJson.Encode(s.Query(type,new(schema.Fields.Single(f=>f.Name==sort).Id!,Skip:skip??0,Take:25,Filters:filters,
                SelectFieldIds:schema.Fields.Where(f=>f.Name is not ("Payload" or "LeaseToken")).Select(f=>f.Id!).ToArray())));
        });
        routes.MapPost("/schedules",(HttpContext c,NewSchedule input)=>
        {
            var actor=Admin(c);Handler(input.Handler);using var store=Open();
            return WireJson.Encode(schedules.Create(Session(store,actor),"Schedule",input.Name,input.Handler,new(){["message"]=input.Message},input.IntervalSeconds,Instant(input.FirstDue),input.MissedRuns));
        });
        routes.MapPost("/schedules/{id}/state",(HttpContext c,string id,ScheduleState input)=>
        {var actor=Admin(c);using var store=Open();schedules.SetEnabled(Session(store,actor),"Schedule",id,input.VersionNumber,input.Enabled);return Results.NoContent();});
        routes.MapPost("/deliveries",(HttpContext c,NewDelivery input)=>
        {var actor=Admin(c);Handler(input.Handler);using var store=Open();return WireJson.Encode(outbox.Enqueue(Session(store,actor),"Delivery",input.Handler,new(){["message"]=input.Message},Instant(input.Due)));});
        routes.MapPost("/deliveries/{id}/{action}",(HttpContext c,string id,string action,Version input)=>
        {
            var actor=Admin(c);using var store=Open();var s=Session(store,actor);
            if(action=="retry")outbox.Retry(s,"Delivery",id,input.VersionNumber);
            else if(action=="cancel")outbox.Cancel(s,"Delivery",id,input.VersionNumber);
            else throw new LoomException(404,"Unknown delivery action.");
            return Results.NoContent();
        });
    }
    /// <summary>Each tick owns its connection; no session crosses worker/request boundaries.</summary>
    public async Task Tick(CancellationToken stop)
    {
        using var store=Open();var session=Session(store,new("jobs-worker","Worker"));
        try{schedules.Tick(session,"Schedule","Delivery");}
        catch(Exception e) when(e is not OperationCanceledException){Console.Error.WriteLine("Schedule materialization: "+e);}
        // A malformed/conflicting schedule must not block already queued deliveries.
        var handlers=new Dictionary<string,Func<OutboxMessage,CancellationToken,Task>>
        {
            ["demo.receipt"]=(message,token)=>
            {
                token.ThrowIfCancellationRequested();
                // Simulate a receiver in an independent transaction with persisted deduplication.
                using var receiver=Open();var s=Session(receiver,new("demo-receiver","Worker"));
                s.Atomic(t=>
                {
                    var key=Model.Get("Receipt").Fields[0].Id!;
                    if(t.Query("Receipt",new(key,Take:1,Filters:[new(key,FilterOperator.Equal,JsonValue.Create(message.Key))],SelectFieldIds:[])).Total==0)
                        t.Create("Receipt",new(){["Key"]=message.Key,["Message"]=message.Payload["message"]?.ToString()??"Receipt"});
                    return 0;
                });return Task.CompletedTask;
            },
            ["demo.failure"]=(_,_)=>throw new InvalidOperationException("Demonstration receiver unavailable.")
        };
        foreach(var candidate in outbox.Candidates(session,"Delivery"))
        {
            stop.ThrowIfCancellationRequested();var lease=outbox.Claim(session,candidate);if(lease==null)continue;
            var result=await outbox.DeliverAsync(session,lease,handlers,stop);
            if(result.Exception!=null)Console.Error.WriteLine($"Delivery {candidate.Id}: {result.Exception}");
        }
    }
    private sealed class Worker(JobAdministration host):BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
            try{while(await timer.WaitForNextTickAsync(stop))try{await host.Tick(stop);}catch(Exception e) when(e is not OperationCanceledException){Console.Error.WriteLine("Jobs worker: "+e);}}
            catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        }
    }
}
