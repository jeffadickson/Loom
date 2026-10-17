using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Loom.Core;
using Loom.Facility;
using Microsoft.AspNetCore.Identity;

namespace Loom.Demo;

/// <summary>Hosts the example application using Loom authentication and a separate tenant-bound catalog.</summary>
internal static class FacilityEndpoints
{
    public static void ConfigureDemo(WebApplicationBuilder builder)
    {
        if(builder.Configuration["Authentication:Mode"] is "Identity" or "Accounts")
            throw new InvalidOperationException("--facility-demo cannot replace configured authentication. Use Facility:Enabled instead.");
        builder.Configuration["Authentication:Mode"]="Accounts";
        builder.Configuration["Facility:Enabled"]="true";
        for(var i=0;i<FacilitySeed.Members.Length;i++)
        {
            var m=FacilitySeed.Members[i];var prefix=$"Authentication:Accounts:{i}:";
            builder.Configuration[prefix+"Id"]=m.UserId;builder.Configuration[prefix+"Role"]="Viewer";
            builder.Configuration[prefix+"PasswordHash"]=new PasswordHasher<LoomAccount>().HashPassword(new(m.UserId,"Viewer",""),FacilitySeed.Password);
        }
    }
    public static FacilityApplication? Configure(WebApplicationBuilder builder,bool demo,bool authenticated)
    {
        if(!builder.Configuration.GetValue<bool>("Facility:Enabled"))return null;
        if(!authenticated)throw new InvalidOperationException("Facility requires authenticated accounts.");
        var directory=Path.GetFullPath(builder.Configuration["Facility:DataDirectory"]??Path.Combine(builder.Environment.ContentRootPath,"data","facility"));
        var web=Path.GetFullPath(builder.Environment.WebRootPath??Path.Combine(builder.Environment.ContentRootPath,"wwwroot"));
        if(directory.Equals(web,StringComparison.OrdinalIgnoreCase)||directory.StartsWith(web+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Facility data must be outside the web root.");
        var members=demo?FacilitySeed.Members:builder.Configuration.GetSection("Facility:Memberships").Get<FacilityMember[]>()??[];
        var facility=new FacilityApplication(directory,members);
        if(demo)FacilitySeed.Populate(facility);
        facility.ReleaseFailed+=(tenant,job,error)=>Console.Error.WriteLine($"Facility report {tenant}/{job}: {error}");
        if(!builder.Configuration.GetValue<bool>("Facility:PauseWorker"))
            builder.Services.AddHostedService(_=>new ReleaseWorker(facility));
        return facility;
    }
    public static void Map(WebApplication app,FacilityApplication? facility,bool demo)
    {
        app.MapGet("/facility-config",()=>new {enabled=facility!=null,demo,identity=app.Configuration["Authentication:Mode"]=="Identity",accounts=demo?FacilitySeed.Members:[],password=demo?FacilitySeed.Password:null});
        if(facility==null)return;
        string User(HttpContext c)=>c.User.Identity?.IsAuthenticated==true
            ?c.User.FindFirstValue(ClaimTypes.NameIdentifier)??throw new LoomException(401,"Sign in to continue.")
            :throw new LoomException(401,"Sign in to continue.");
        app.MapGet("/api/facility/memberships",(HttpContext c)=>facility.Memberships(User(c)));
        var routes=app.MapGroup("/api/facility/{tenant}");
        routes.MapGet("/overview",(HttpContext c,string tenant)=>WireJson.Encode(facility.Overview(User(c),tenant)));
        routes.MapGet("/records/{type}",(HttpContext c,string tenant,string type,int? skip,int? take,string? search,string? status)=>WireJson.Encode(facility.Browse(User(c),tenant,type,skip??0,take??25,search,status)));
        routes.MapGet("/requests/{id}",(HttpContext c,string tenant,string id)=>WireJson.Encode(facility.Detail(User(c),tenant,id)));
        routes.MapGet("/operators",(HttpContext c,string tenant)=>facility.Operators(User(c),tenant));
        routes.MapPost("/register/{type}",(HttpContext c,string tenant,string type,JsonObject values)=>WireJson.Encode(facility.Register(User(c),tenant,type,values)));
        routes.MapPost("/requests",(HttpContext c,string tenant,RequestInput input)=>WireJson.Encode(facility.Schedule(User(c),tenant,input)));
        routes.MapPost("/requests/{id}/cancel",(HttpContext c,string tenant,string id,CancelInput input)=>WireJson.Encode(facility.Cancel(User(c),tenant,id,input.Version,input.Reason)));
        routes.MapPost("/requests/{id}/start",(HttpContext c,string tenant,string id,VersionInput input)=>WireJson.Encode(facility.Start(User(c),tenant,id,input.Version)));
        routes.MapPost("/requests/{id}/results",(HttpContext c,string tenant,string id,ResultInput input)=>WireJson.Encode(facility.Record(User(c),tenant,id,input)));
        routes.MapPost("/requests/{id}/review",(HttpContext c,string tenant,string id,ReviewInput input)=>WireJson.Encode(facility.Review(User(c),tenant,id,input)));
        routes.MapPost("/requests/{id}/release",(HttpContext c,string tenant,string id,VersionInput input)=>WireJson.Encode(facility.QueueRelease(User(c),tenant,id,input.Version)));
        routes.MapPost("/jobs/{id}/retry",(HttpContext c,string tenant,string id,VersionInput input)=>{facility.RetryRelease(User(c),tenant,id,input.Version);return Results.NoContent();});
        routes.MapGet("/releases/{id}",(HttpContext c,string tenant,string id)=>Results.File(Encoding.UTF8.GetBytes(facility.ReleasedReport(User(c),tenant,id)),"text/html; charset=utf-8","Test_Release.html"));
        routes.MapGet("/releases/{id}/package",(HttpContext c,string tenant,string id)=>
        {
            c.Response.Headers.CacheControl="no-store";
            c.Response.Headers["X-Content-Type-Options"]="nosniff";
            return Results.File(facility.ExportRelease(User(c),tenant,id),"application/zip","Test_Evidence.zip");
        });
        routes.MapGet("/evidence/{id}",(HttpContext c,string tenant,string id)=>Results.File(facility.Evidence(User(c),tenant,id),"application/octet-stream","Evidence.bin"));
        routes.MapGet("/history/{type}/{id}",(HttpContext c,string tenant,string type,string id)=>WireJson.Encode(facility.History(User(c),tenant,type,id)));
    }
    public sealed record CancelInput(int Version,string Reason);
    public sealed record VersionInput(int Version);
    /// <summary>Bounded local worker; durable job state lives in the tenant database, not this timer.</summary>
    private sealed class ReleaseWorker(FacilityApplication facility):BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                while(await timer.WaitForNextTickAsync(stop))
                {
                    try { facility.ProcessReleases(); }
                    catch(Exception e) { Console.Error.WriteLine("Facility release worker: "+e); }
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        }
    }
}
