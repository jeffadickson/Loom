using System.Security.Claims;
using System.Text.Json.Nodes;
using Loom.Core;
using Loom.Sqlite;

namespace Loom.Demo;

/// <summary>Optional tenant API. The existing single-workspace routes remain a separate workspace.</summary>
internal static class TenantEndpoints
{
    public static TenantWorkspace? Configure(WebApplicationBuilder builder,Catalog catalog,IDocumentHook[] hooks,IDocumentAccessPolicy? access,bool authenticated)
    {
        var settings=builder.Configuration.GetSection("Tenancy");
        if(!settings.GetValue<bool>("Enabled"))return null;
        if(!authenticated)throw new InvalidOperationException("Tenancy requires authenticated account mode.");
        var root=settings["DataDirectory"]??throw new InvalidOperationException("Tenancy:DataDirectory is required.");
        var full=Path.GetFullPath(root);
        var web=Path.GetFullPath(builder.Environment.WebRootPath??Path.Combine(builder.Environment.ContentRootPath,"wwwroot"));
        if(full.Equals(web,StringComparison.OrdinalIgnoreCase)||full.StartsWith(web+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tenant databases must be outside the web root.");
        var ids=settings.GetSection("Tenants").GetChildren().Select(c=>c.Value??"").ToArray();
        if(ids.Length==0)throw new InvalidOperationException("Configure explicit tenant identifiers.");
        var memberships=settings.GetSection("Memberships").GetChildren().Select(c=>
            (User:c["UserId"]??"",Tenant:c["TenantId"]??"",Role:c["Role"]??"")).ToArray();
        if(memberships.Any(m=>string.IsNullOrWhiteSpace(m.User)||!ids.Contains(m.Tenant)||string.IsNullOrWhiteSpace(m.Role))||
           memberships.GroupBy(m=>(m.User,m.Tenant)).Any(g=>g.Count()>1))
            throw new InvalidOperationException("Tenant memberships must be explicit and unique.");
        var workspace=new TenantWorkspace(root,ids,(user,tenant)=>memberships.SingleOrDefault(m=>m.User==user&&m.Tenant==tenant).Role,hooks,access);
        foreach(var id in ids)workspace.Provision(id,catalog);
        return workspace;
    }

    /// <summary>Uses the authenticated stable subject only; headers and global roles cannot grant tenant membership.</summary>
    public static void Map(WebApplication app,TenantWorkspace? workspace)
    {
        if(workspace==null)return;
        TenantSession Open(HttpContext context,string tenant)
        {
            if(context.User.Identity?.IsAuthenticated!=true)throw new LoomException(401,"Sign in to continue.");
            return workspace.Open(context.User.FindFirstValue(ClaimTypes.NameIdentifier)??"",tenant);
        }
        var routes=app.MapGroup("/api/tenants/{tenant}");
        routes.MapGet("/documents/{type}",(HttpContext c,string tenant,string type,string? search)=>
        {using var session=Open(c,tenant);return WireJson.Encode(session.List(type,search));});
        routes.MapPost("/documents/{type}/query",(HttpContext c,string tenant,string type,DocumentQuery query)=>
        {using var session=Open(c,tenant);return WireJson.Encode(session.Query(type,query));});
        routes.MapGet("/documents/{type}/{id}",(HttpContext c,string tenant,string type,string id)=>
        {using var session=Open(c,tenant);return WireJson.Encode(session.Get(type,id));});
        routes.MapPost("/documents/{type}",(HttpContext c,string tenant,string type,JsonObject values)=>
        {using var session=Open(c,tenant);return WireJson.Encode(session.Create(type,values));});
        routes.MapPut("/documents/{type}/{id}",(HttpContext c,string tenant,string type,string id,ChangeDocument request)=>
        {using var session=Open(c,tenant);return WireJson.Encode(session.Update(type,id,request.Version,request.Values));});
        routes.MapDelete("/documents/{type}/{id}",(HttpContext c,string tenant,string type,string id,int version)=>
        {using var session=Open(c,tenant);session.Delete(type,id,version);return Results.NoContent();});
        routes.MapGet("/documents/{type}/{id}/audit",(HttpContext c,string tenant,string type,string id)=>
        {using var session=Open(c,tenant);return WireJson.Encode(session.History(type,id));});
        routes.MapPost("/reports",(HttpContext c,string tenant,SavedReport report)=>
        {using var session=Open(c,tenant);return WireJson.Encode(session.Report(report));});
    }
    public sealed record ChangeDocument(int Version,JsonObject Values);
}
