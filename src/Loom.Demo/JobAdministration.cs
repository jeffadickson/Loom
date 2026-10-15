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
    private readonly string? connectionString;
    private readonly TenantWorkspace? workspace;
    private readonly string? tenant;
    private readonly TimeSpan retention=TimeSpan.FromDays(7);
    private readonly DeliveryOutbox outbox=new();
    private readonly RecurringSchedules schedules;
    public const string Password="Loom-jobs-demo!";
    public JobAdministration(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString=new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString();
        using var store=new SqliteStore(connectionString);store.Migrate(Model);schedules=new(outbox);
    }
    private SqliteStore Open(){var store=new SqliteStore(connectionString!);store.ReadCatalog();return store;}
    private static ApplicationSession Session(SqliteStore store,Actor actor)=>new(new(Model,store,[]),actor);
    private JobAdministration(TenantWorkspace workspace,string tenant,IDeliveryPayloadProtection protection,TimeSpan retention)
    {this.workspace=workspace;this.tenant=tenant;this.retention=retention;outbox=new(protection:protection);schedules=new(outbox);}
    private ApplicationSessionScope OpenScope(HttpContext? context=null)
    {
        if(workspace==null)
        {
            var actor=context==null?new Actor("jobs-worker","Worker"):Admin(context);
            var store=Open();return new(Session(store,actor),store);
        }
        if(context!=null&&context.User.Identity?.IsAuthenticated!=true)throw new LoomException(401,"Sign in to continue.");
        var user=context==null?"jobs-worker":context.User.FindFirstValue(ClaimTypes.NameIdentifier)??"";
        var session=workspace.Open(user,tenant!);
        if(context!=null&&session.Role!="Admin"){session.Dispose();throw new LoomException(403,"Administrator access required.");}
        return new(session,session);
    }
    private static Actor Admin(HttpContext c)=>c.User.Identity?.IsAuthenticated!=true?throw new LoomException(401,"Sign in to continue."):
        c.User.IsInRole("Admin")?new(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!,"Admin"):throw new LoomException(403,"Administrator access required.");
    public static void ConfigureDemo(WebApplicationBuilder builder)
    {
        if(builder.Configuration["Authentication:Mode"] is "Accounts" or "Identity")throw new InvalidOperationException("--jobs-demo cannot replace configured authentication.");
        builder.Configuration["Authentication:Mode"]="Accounts";
        foreach(var (name,role,index) in new[]{("jobs-admin","Admin",0),("jobs-viewer","Viewer",1),("jobs-south","Admin",2),("jobs-global","Admin",3),("jobs-both","Admin",4)})
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
    public void Map(WebApplication app,string prefix="/api/jobs")
    {
        if(tenant==null)app.MapGet("/api/jobs/contexts",(HttpContext c)=>{Admin(c);return new[]{new{Id="default",Name="Default workspace",BasePath="/api/jobs"}};});
        var routes=app.MapGroup(prefix);
        routes.MapGet("/records/{type}",(HttpContext c,string type,int? skip,string? status)=>
        {
            if(type is not ("Delivery" or "Schedule" or "Receipt"))throw new LoomException(404,"Unknown operations view.");
            using var scope=OpenScope(c);var s=scope.Session;var schema=Model.Get(type);
            var filters=status==null||status==""?Array.Empty<FieldFilter>():type=="Delivery"?
                [new FieldFilter(schema.Fields.Single(f=>f.Name=="Status").Id!,FilterOperator.Equal,JsonValue.Create(status))]:throw new LoomException(400,"Status filter applies to deliveries.");
            var sort=type=="Schedule"?"NextDueAt":type=="Delivery"?"DueAt":"Key";
            return WireJson.Encode(s.Query(type,new(schema.Fields.Single(f=>f.Name==sort).Id!,Skip:skip??0,Take:25,Filters:filters,
                SelectFieldIds:schema.Fields.Where(f=>f.Name is not ("Payload" or "LeaseToken")).Select(f=>f.Id!).ToArray())));
        });
        routes.MapPost("/schedules",(HttpContext c,NewSchedule input)=>
        {
            Handler(input.Handler);using var scope=OpenScope(c);
            var document=schedules.Create(scope.Session,"Schedule",input.Name,input.Handler,new(){["message"]=input.Message},input.IntervalSeconds,Instant(input.FirstDue),input.MissedRuns);return Results.Json(new{document.Id,document.Version});
        });
        routes.MapPost("/schedules/{id}/state",(HttpContext c,string id,ScheduleState input)=>
        {using var scope=OpenScope(c);schedules.SetEnabled(scope.Session,"Schedule",id,input.VersionNumber,input.Enabled);return Results.NoContent();});
        routes.MapPost("/deliveries",(HttpContext c,NewDelivery input)=>
        {Handler(input.Handler);using var scope=OpenScope(c);var document=outbox.Enqueue(scope.Session,"Delivery",input.Handler,new(){["message"]=input.Message},Instant(input.Due));return Results.Json(new{document.Id,document.Version});});
        routes.MapPost("/deliveries/{id}/{action}",(HttpContext c,string id,string action,Version input)=>
        {
            using var scope=OpenScope(c);var s=scope.Session;
            if(action=="retry")outbox.Retry(s,"Delivery",id,input.VersionNumber);
            else if(action=="cancel")outbox.Cancel(s,"Delivery",id,input.VersionNumber);
            else throw new LoomException(404,"Unknown delivery action.");
            return Results.NoContent();
        });
    }
    /// <summary>Register trusted handlers once; each worker scope owns a fresh database connection.</summary>
    private DeliveryWorker CreateWorker()
    {
        var handlers=new Dictionary<string,Func<OutboxMessage,CancellationToken,Task>>
        {
            ["demo.receipt"]=(message,token)=>
            {
                token.ThrowIfCancellationRequested();
                // Simulate a receiver in an independent transaction with persisted deduplication.
                using var receiver=OpenScope();var s=receiver.Session;
                s.Atomic(t=>
                {
                    var key=Model.Get("Receipt").Fields[0].Id!;
                    if(t.Query("Receipt",new(key,Take:1,Filters:[new(key,FilterOperator.Equal,JsonValue.Create(message.Key))],SelectFieldIds:[])).Total==0)
                        t.Create("Receipt",new(){["Key"]=message.Key,["Message"]=tenant==null?message.Payload["message"]?.ToString()??"Receipt":"Protected delivery received"});
                    return 0;
                });return Task.CompletedTask;
            },
            ["demo.failure"]=(_,_)=>throw new InvalidOperationException("Demonstration receiver unavailable.")
        };
        return new DeliveryWorker(()=>
        {
            return OpenScope();
        },outbox,"Delivery",handlers,prepare:s=>{if(tenant!=null)outbox.ExpireFailedPayloads(s,"Delivery",retention);schedules.Tick(s,"Schedule","Delivery");});
    }
    /// <summary>Creates separate tenant operations stores with live configured memberships and protected payloads.</summary>
    public static Action<WebApplication>? ConfigureTenants(WebApplicationBuilder builder,bool demo,bool authenticated)
    {
        if(!demo&&!builder.Configuration.GetValue<bool>("Jobs:TenantsEnabled"))return null;
        if(!authenticated)throw new InvalidOperationException("Tenant jobs require authentication.");
        var settings=builder.Configuration.GetSection("Jobs");
        var root=Path.GetFullPath(settings["TenantDataDirectory"]??Path.Combine(builder.Environment.ContentRootPath,"data","tenant-jobs"));
        var keyPath=Path.GetFullPath(settings["KeyRingFile"]??Path.Combine(root,"keys.json"));
        var web=Path.GetFullPath(builder.Environment.WebRootPath??Path.Combine(builder.Environment.ContentRootPath,"wwwroot"));
        foreach(var path in new[]{root,keyPath})
            if(path.Equals(web,StringComparison.OrdinalIgnoreCase)||path.StartsWith(web+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Jobs storage and keys must be outside the web root.");
        if(demo)
        {
            builder.Configuration["Jobs:Tenants:0"]="north";builder.Configuration["Jobs:Tenants:1"]="south";
            foreach(var (user,id,role,index) in new[]{("jobs-admin","north","Admin",0),("jobs-south","south","Admin",1),("jobs-worker","north","Worker",2),("jobs-worker","south","Worker",3),("jobs-both","north","Admin",4),("jobs-both","south","Admin",5)})
            {var p=$"Jobs:Memberships:{index}:";builder.Configuration[p+"UserId"]=user;builder.Configuration[p+"TenantId"]=id;builder.Configuration[p+"Role"]=role;}
        }
        var ids=settings.GetSection("Tenants").GetChildren().Select(c=>c.Value??"").ToArray();
        if(ids.Length==0||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length)throw new InvalidOperationException("Configure unique tenant identifiers.");
        string? Membership(string user,string id)
        {
            var matches=settings.GetSection("Memberships").GetChildren().Where(c=>c["UserId"]==user&&c["TenantId"]==id).ToArray();
            return matches.Length==1?matches[0]["Role"]:null;
        }
        var retention=TimeSpan.FromHours(settings.GetValue<double?>("FailedPayloadRetentionHours")??168);
        if(retention<TimeSpan.FromHours(1)||retention>TimeSpan.FromDays(3650))throw new InvalidOperationException("Retention must be 1 hour to 3650 days.");
        if(!File.Exists(keyPath))
        {
            if(!demo)throw new InvalidOperationException("Provision Jobs:KeyRingFile before starting protected tenant jobs.");
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            DeliveryPayloadKeyRingFile.Create(keyPath);
        }
        DeliveryPayloadKeyRingFile.Inspect(keyPath);
        var redaction=new AuditRedactionPolicy(Model.Types.Values.SelectMany(t=>t.Fields).Where(f=>f.Name is "Payload" or "LeaseToken").Select(f=>f.Id!));
        var workspace=new TenantWorkspace(root,ids,Membership,auditRedaction:redaction);
        var hosts=ids.Select(id=>
        {
            workspace.Provision(id,Model);
            return new JobAdministration(workspace,id,DeliveryPayloadKeyRingFile.Open(keyPath,"loom-jobs:"+id),retention);
        }).ToArray();
        if(!settings.GetValue<bool>("PauseWorker"))builder.Services.AddHostedService(_=>new TenantWorker(hosts));
        return app=>
        {
            app.MapGet("/api/jobs/contexts",(HttpContext c)=>
            {
                if(c.User.Identity?.IsAuthenticated!=true)throw new LoomException(401,"Sign in to continue.");
                var result=ids.Where(id=>Membership(c.User.FindFirstValue(ClaimTypes.NameIdentifier)??"",id)=="Admin")
                    .Select(id=>new{Id=id,Name=id,BasePath="/api/jobs/tenants/"+id}).ToArray();
                if(result.Length==0)throw new LoomException(403,"Administrator access required.");return result;
            });
            foreach(var host in hosts)host.Map(app,"/api/jobs/tenants/"+host.tenant);
        };
    }
    private sealed class TenantWorker(JobAdministration[] hosts):BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            var workers=hosts.Select(host=>host.CreateWorker()).ToArray();
            try
            {
                while(true)
                {
                    foreach(var worker in workers)
                    {
                        var batch=await worker.RunOnceAsync(stop);
                        // Avoid putting arbitrary exception messages or decrypted contents into logs.
                        foreach(var result in batch.Results.Where(r=>r.Error!=null))Console.Error.WriteLine($"Tenant jobs: {result.Outcome} {result.Id} ({result.Error!.GetType().Name})");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1),stop);
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        }
    }
    private sealed class Worker(JobAdministration host):BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            try
            {
                await host.CreateWorker().RunAsync(batch=>
                {
                    foreach(var result in batch.Results.Where(r=>r.Error!=null))
                        Console.Error.WriteLine($"Jobs {result.Outcome} {result.Id}: {result.Error}");
                },stop);
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        }
    }
}
