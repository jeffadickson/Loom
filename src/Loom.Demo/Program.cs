using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Loom.Demo.Models;
using Loom.Core;
using Loom.Sqlite;
using Loom.Demo;

var builder = WebApplication.CreateBuilder(args);
var root = builder.Environment.ContentRootPath;
var memoryDemo=args.Contains("--memory-demo");
if(memoryDemo)builder.Configuration["Database:Provider"]="Memory";
var modelSource = builder.Configuration["Models:Source"] ?? "Json";
var codeModels = modelSource.Equals("CSharp", StringComparison.OrdinalIgnoreCase);
if (!codeModels && !modelSource.Equals("Json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Models:Source must be Json or CSharp.");
var catalog = codeModels ? AttributeCatalog.From(typeof(AssetModel), typeof(WorkOrderModel)) : Catalog.Load(Path.Combine(root, "Metadata"));
if(memoryDemo)catalog=new Catalog(catalog.Types.Values.Concat(AttributeCatalog.From(typeof(TestResultModel)).Types.Values));
if (args.Contains("--export-model")) { Console.WriteLine(JsonSerializer.Serialize(catalog.Types.Values, Catalog.Json)); return; }
if (args.Contains("--generate"))
{
    if (codeModels) throw new InvalidOperationException("CSharp models are already authored types; use --export-model to inspect their catalog.");
    TypeGenerator.Generate(catalog, Path.Combine(root, "Generated"));
    Console.WriteLine("Generated C# types from authoritative metadata."); return;
}
if (args.Contains("--validate")) { Console.WriteLine($"Validated {catalog.Types.Count} DocTypes."); return; }
if (args.Contains("--typed-demo")) { TypedExample.Run(catalog); return; }
if(!string.Equals(builder.Configuration["Database:Provider"],"Memory",StringComparison.OrdinalIgnoreCase))Directory.CreateDirectory(Path.Combine(root, "data"));
IDocumentStore store = builder.Configuration["Database:Provider"]?.ToLowerInvariant() switch
{
    "sqlite" => new SqliteStore(builder.Configuration["Database:ConnectionString"] ?? "Data Source=data/loom.db"),
    "memory" => new MemoryStore(),
    _ => throw new InvalidOperationException("Unsupported provider. Implemented providers: Sqlite, Memory. No automatic fallback.")
};
var storedCatalog = (store as IMigrationStore)?.ReadCatalog();
var applyModel = args.FirstOrDefault(a => a.StartsWith("--apply-model=", StringComparison.Ordinal));
if (args.Contains("--plan-model") || applyModel != null)
{
    using (store)
    {
        if (storedCatalog == null || store is not IMigrationStore migrations) throw new InvalidOperationException("Model planning needs an initialized SQLite database.");
        var required = new WorkOrderHook();
        if (required.RequiredFields.Any(n => !catalog.Get(required.Type).Fields.Any(f => f.Name == n))) throw new InvalidOperationException("Target model breaks C# hook dependencies.");
        var backfillFile=args.FirstOrDefault(a=>a.StartsWith("--backfills=",StringComparison.Ordinal));
        var backfills=backfillFile==null?null:JsonSerializer.Deserialize<MigrationBackfill[]>(File.ReadAllText(backfillFile["--backfills=".Length..]),Catalog.Json);
        var plan = migrations.Plan(catalog,backfills);
        if (applyModel == null) Console.WriteLine(JsonSerializer.Serialize(plan, Catalog.Json));
        else
        {
            if (applyModel["--apply-model=".Length..] != plan.Id) throw new InvalidOperationException("Plan changed. Preview again before applying.");
            migrations.Apply(plan.Id, new Actor("local-cli", "Admin"));
            Console.WriteLine("Reviewed model applied.");
        }
    }
    return;
}
if (storedCatalog == null) store.Migrate(catalog);
else
{
    if (codeModels && SchemaIdentity.Hash(catalog) != SchemaIdentity.Hash(storedCatalog))
        throw new InvalidOperationException("C# model differs from accepted database schema. Use --plan-model and --apply-model=<id>, then restart.");
    catalog = storedCatalog;
}
IDocumentHook[] hooks = [new WorkOrderHook()];
foreach (var hook in hooks)
    if (hook.RequiredFields.Any(name => !catalog.Get(hook.Type).Fields.Any(f => f.Name == name)))
        throw new InvalidOperationException("Stored model is incompatible with C# hook dependencies.");
var service = new DocumentService(catalog, store, hooks);
var admin = new Actor("demo-admin", "Admin");
if (storedCatalog == null && service.List("Asset", admin).Total == 0)
{
    var first = service.Create("Asset", new JsonObject { ["name"] = "Bench oscilloscope", ["tag"] = "LAB-001", ["category"] = "Electronics", ["location"] = "Lab A / Bench 3", ["active"] = true }, admin);
    service.Create("Asset", new JsonObject { ["name"] = "Vacuum pump", ["tag"] = "LAB-002", ["category"] = "Mechanical", ["location"] = "Lab B", ["active"] = true }, admin);
    service.Create("Asset", new JsonObject { ["name"] = "Environmental chamber", ["tag"] = "LAB-003", ["category"] = "Facilities", ["location"] = "Test facility", ["active"] = true }, admin);
    service.Create("WorkOrder", new JsonObject { ["title"] = "Annual calibration", ["asset"] = first.Id, ["priority"] = "High", ["due"] = "2026-11-02", ["hours"] = 3, ["rate"] = 125 }, admin);
}
if(memoryDemo)
{
    var results=service.Repository<TestResultModel>(admin);
    results.Create(new(){Name="Thermal cycle • channel 1",Outcome="Pass",Temperature=new(new(25,Loom.Core.Science.Units.Celsius),.1),SampleCount=9007199254740993,Cost=1234.56m,Waveform=Enumerable.Range(0,4096).Select(i=>(byte)(i%16)).ToArray(),Series=new Uri("tsdb://example.test/thermal/run-001?channel=1")});
    results.Create(new(){Name="Vibration sweep • channel 2",Outcome="Review",SampleCount=64000,Cost=87.40m,Waveform=Encoding.UTF8.GetBytes("Example test result payload"),Series=new Uri("https://example.test/results/run-002")});
}
// This is an explicitly local demonstration, not an authentication implementation.
builder.WebHost.UseUrls("http://127.0.0.1:" + (Environment.GetEnvironmentVariable("LOOM_PORT") ?? "5080"));
var app = builder.Build();
var schemaGate = new SemaphoreSlim(1, 1);
app.Lifetime.ApplicationStopped.Register(store.Dispose);
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
        || context.Request.Host.Host is not ("127.0.0.1" or "localhost"))
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Headers["X-Loom-Demo"] != "local") { context.Response.StatusCode = 403; return; }
    }
    // Single-host coordination: no request sees half-swapped model/service objects.
    await schemaGate.WaitAsync(context.RequestAborted);
    try {
        if (context.Request.Path.StartsWithSegments("/api/documents") && context.Request.Method is "POST" or "PUT" or "DELETE")
        {
            var expected = context.Request.Headers["X-Loom-Schema"].ToString();
            if (expected.Length > 0 && expected != SchemaIdentity.Hash(catalog)) throw new LoomException(409, "The model changed. Reload the page before saving.");
        }
        await next();
    }
    catch (LoomException ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (BadHttpRequestException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid request." }); }
    catch (InvalidOperationException ex) when (context.Request.Path.StartsWithSegments("/api/designer"))
    { context.Response.StatusCode = 422; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    finally { schemaGate.Release(); }
});
Actor Current(HttpContext ctx)
{
    var role = ctx.Request.Headers["X-Demo-Role"].ToString();
    if (role is not ("Admin" or "Editor" or "Viewer")) throw new LoomException(403, "Select a valid demonstration role.");
    return new Actor("demo-" + role.ToLowerInvariant(), role);
}
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/meta", (HttpContext c) =>
{
    var actor = Current(c);
    return Results.Ok(new { name = "Loom", version = "0.12", schemaHash = SchemaIdentity.Hash(catalog), reportDesigner = actor.Role == "Admin" && store is IReportDesignStore, modelSource, designer = !codeModels && actor.Role == "Admin" && store is IMigrationStore, mode = "Local demonstration", provider = store.Info, actor,
        types = catalog.Types.Values.Where(t => service.Can(t.Name, actor, "read")).Select(t => new { t.Id, t.Name, t.Label, t.Version, t.TitleField, t.Submittable, t.Fields, actions = t.Permissions.GetValueOrDefault(actor.Role, []) }) });
});
app.MapGet("/api/documents/{type}", (string type, HttpContext c, string? search, string? sort, bool? descending, int? skip, int? take, string? field, string? equals) =>
    WireJson.Encode(service.List(type, Current(c), search, sort, descending ?? false, skip ?? 0, take ?? 50, field, equals)));
app.MapPost("/api/documents/{type}/query", (string type, DocumentQuery query, HttpContext c) => WireJson.Encode(service.Query(type,Current(c),query)));
app.MapGet("/api/documents/{type}/{id}", (string type, string id, HttpContext c) => WireJson.Encode(service.Get(type, id, Current(c))));
app.MapPost("/api/documents/{type}", (string type, JsonObject values, HttpContext c) =>
{
    var d = service.Create(type, values, Current(c)); return Results.Created($"/api/documents/{type}/{d.Id}", WireJson.Encode(d));
});
app.MapPut("/api/documents/{type}/{id}", (string type, string id, SaveRequest request, HttpContext c) => WireJson.Encode(service.Update(type, id, request.Version, request.Values ?? throw new LoomException(400, "Values object is required."), Current(c))));
app.MapDelete("/api/documents/{type}/{id}", (string type, string id, int version, HttpContext c) => { service.Delete(type, id, version, Current(c)); return Results.NoContent(); });
app.MapPost("/api/documents/{type}/{id}/actions/{action}", (string type, string id, string action, VersionRequest request, HttpContext c) => WireJson.Encode(service.Transition(type, id, request.Version, action, Current(c))));
app.MapGet("/api/documents/{type}/{id}/audit", (string type, string id, HttpContext c) => WireJson.Encode(service.History(type, id, Current(c))));
app.MapGet("/api/reports/{type}", (string type, HttpContext c, string? field, string? equals) => WireJson.Encode(service.List(type, Current(c), take:200, field:field, equals:equals)));
app.MapGet("/api/reports/{type}/csv", (string type, HttpContext c, string? field, string? equals, string? search, string? sort, bool? descending) =>
{
    var result = service.List(type, Current(c), search:search, sort:sort, descending:descending ?? false, take:200, field:field, equals:equals);
    if (result.Total > 200) throw new LoomException(422, "Demo export limit is 200 rows; filter first.");
    var schema = catalog.Get(type);
    string Csv(string s) => "\"" + ((s.Length > 0 && "=+-@\t\r".Contains(s[0])) ? "'" + s : s).Replace("\"", "\"\"") + "\"";
    var b = new StringBuilder(); b.AppendLine(string.Join(",", schema.Fields.Select(f => Csv(f.Label))));
    foreach (var d in result.Items) b.AppendLine(string.Join(",", schema.Fields.Select(f => Csv(f.Type=="Binary" && d.Values[f.Name]!=null ? "[Binary: "+Convert.FromBase64String(d.Values[f.Name]!.ToString()).Length+" bytes]" : d.Values[f.Name]?.ToString() ?? ""))));
    return Results.File(Encoding.UTF8.GetBytes(b.ToString()), "text/csv", type + ".csv");
});
app.MapGet("/api/documents/{type}/{id}/binary/{field}", (string type,string id,string field,HttpContext c) =>
{
    var document=service.Get(type,id,Current(c));
    var definition=catalog.Get(type).Fields.SingleOrDefault(f=>f.Name==field && f.Type=="Binary") ?? throw new LoomException(404,"Unknown binary field.");
    var value=document.Values[field]?.ToString() ?? throw new LoomException(404,"No binary data.");
    return Results.File(Convert.FromBase64String(value),definition.MediaType,field+".bin");
});
IMigrationStore MigrationAdmin(HttpContext c)
{
    if (Current(c).Role != "Admin") throw new LoomException(403, "Only administrators can edit models.");
    if (codeModels) throw new LoomException(422, "CSharp owns these models. Edit the properties and use the model planning CLI.");
    return store as IMigrationStore ?? throw new LoomException(422, "The selected provider does not support the model designer.");
}
app.MapPost("/api/designer/plan", (DesignRequest request, HttpContext c) =>
{
    var migrations = MigrationAdmin(c);
    var target = Designer.Edit(catalog, request, hooks);
    return migrations.Plan(target,request.Additions.Where(a=>a.Required).Select(a=>new MigrationBackfill(target.Get(request.Type).Fields.Single(f=>f.Name==a.Name).Id!,a.Backfill)).Concat(request.Fields.Where(f=>f.Backfill!=null).Select(f=>new MigrationBackfill(f.Id,f.Backfill))).ToArray());
});
app.MapPost("/api/designer/apply", (ApplyRequest request, HttpContext c) =>
{
    var migrations = MigrationAdmin(c);
    catalog = migrations.Apply(request.PlanId, Current(c));
    service = new DocumentService(catalog, store, hooks);
    return Results.Ok(new { schemaHash = SchemaIdentity.Hash(catalog), message = "Model and database updated. Existing records preserved." });
});
app.MapGet("/api/designer/history", (HttpContext c) => MigrationAdmin(c).Migrations());
app.MapGet("/api/designer/export", (HttpContext c) =>
{
    MigrationAdmin(c); return Results.File(Designer.Export(catalog), "application/zip", "Loom_Model.zip");
});
app.MapGet("/api/saved-reports/{name}", (string name, HttpContext c) =>
{
    if (name != "EquipmentRegister") throw new LoomException(404, "Unknown saved report.");
    var report = JsonSerializer.Deserialize<SavedReport>(File.ReadAllText(Path.Combine(root, "Reports", name + ".json")), Catalog.Json)!;
    return WireJson.Encode(SavedReports.Run(report, catalog, service, Current(c)));
});
ReportDesign DefaultReport(string type)
{
    var model=catalog.Get(type);
    return new(0,new SavedReport(type+"Register",model.Id!,model.Fields.Select(f=>new ReportColumn(f.Id!)).ToArray(),model.Fields.Single(f=>f.Name==model.TitleField).Id),
        new ReportLayout(model.Label,GroupFieldId:model.Fields.FirstOrDefault(f=>f.Type=="Select")?.Id,SumFieldIds:model.Fields.Where(f=>f.Type=="Money" || f.Type=="Decimal" && f.ReadOnly).Select(f=>f.Id!).ToArray()));
}
IReportDesignStore ReportAdmin(HttpContext c)
{
    if(Current(c).Role!="Admin")throw new LoomException(403,"Only administrators can edit report layouts.");
    return store as IReportDesignStore??throw new LoomException(422,"This provider does not persist report layouts.");
}
app.MapGet("/api/report-designs/{type}",(string type,HttpContext c)=>
{
    var designs=ReportAdmin(c);var baseline=DefaultReport(type);
    return designs.ReadReportDesign(baseline.Definition.TypeId)??baseline;
});
app.MapPost("/api/report-designs/{type}/preview",(string type,ReportDesign design,HttpContext c)=>
{
    ReportAdmin(c);
    if(design.Definition==null || design.Definition.TypeId!=catalog.Get(type).Id)throw new LoomException(422,"Report model mismatch.");
    return Results.Ok(new {html=ReportWriter.Write(design.Definition,design.Layout,catalog,service,Current(c))});
});
app.MapPut("/api/report-designs/{type}",(string type,ReportDesign design,HttpContext c)=>
    ReportAdmin(c).SaveReportDesign(catalog.Get(type).Id!,design,Current(c)));
app.MapGet("/api/reports/{type}/print", (string type, HttpContext c) =>
{
    var baseline=DefaultReport(type);
    var design=(store as IReportDesignStore)?.ReadReportDesign(baseline.Definition.TypeId)??baseline;
    var html = ReportWriter.Write(design.Definition, design.Layout, catalog, service, Current(c));
    return Results.File(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", type + "_Report.html");
});
app.MapGet("/health", () => new { status = "ok", provider = store.Info.Name });
app.Run();

record SaveRequest(int Version, JsonObject Values);
record VersionRequest(int Version);
