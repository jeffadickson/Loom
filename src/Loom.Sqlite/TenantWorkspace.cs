using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Loom.Core;
using Microsoft.Data.Sqlite;

namespace Loom.Sqlite;

/// <summary>
/// Operator-configured database-per-tenant routing. Membership comes from a trusted
/// host resolver, never an Actor or role supplied by the request.
/// </summary>
public sealed class TenantWorkspace
{
    private readonly string root;
    private readonly HashSet<string> tenants;
    private readonly Func<string,string,string?> membership;
    private readonly IDocumentHook[] hooks;
    private readonly IDocumentAccessPolicy? access;

    public TenantWorkspace(string directory, IEnumerable<string> tenantIds,
        Func<string,string,string?> membership, IEnumerable<IDocumentHook>? hooks = null,
        IDocumentAccessPolicy? access = null)
    {
        root=Path.GetFullPath(directory);
        tenants=new(tenantIds,StringComparer.Ordinal);
        foreach(var id in tenants)ValidateId(id);
        this.membership=membership;this.hooks=hooks?.ToArray()??[];this.access=access;
        Directory.CreateDirectory(root);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    }

    /// <summary>Restricts identifiers to path-safe, case-normalized slugs on every supported OS.</summary>
    public static void ValidateId(string id)
    {
        if(id==null || !Regex.IsMatch(id,@"\A[a-z][a-z0-9-]{0,47}\z"))
            throw new LoomException(400,"Invalid tenant identifier.");
    }

    private string DatabasePath(string tenant)
    {
        ValidateId(tenant);
        if(!tenants.Contains(tenant))throw new LoomException(403,"Tenant access denied.");
        var directory=Path.Combine(root,tenant);
        if(Directory.Exists(directory)&&File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Tenant directories cannot be symbolic links.");
        var path=Path.Combine(directory,"documents.db");
        if(File.Exists(path)&&File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Tenant database cannot be a symbolic link.");
        return path;
    }

    private string Role(string user,string tenant)
    {
        if(string.IsNullOrWhiteSpace(user)||!tenants.Contains(tenant))throw new LoomException(403,"Tenant access denied.");
        return membership(user,tenant) is { Length: >0 } role?role:throw new LoomException(403,"Tenant access denied.");
    }

    /// <summary>Local operator provisioning; existing databases must retain their binding and schema.</summary>
    public void Provision(string tenant,Catalog catalog)
    {
        var path=DatabasePath(tenant);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var store=new SqliteStore(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
        store.BindTenant(tenant,allowCreate:true);
        var accepted=store.ReadCatalog();
        if(accepted!=null&&SchemaIdentity.Hash(accepted)!=SchemaIdentity.Hash(catalog))
            throw new LoomException(409,"Tenant schema differs; apply a reviewed migration before startup.");
        store.Migrate(catalog);
    }

    /// <summary>Authorization happens before opening storage and is checked again on every session operation.</summary>
    public TenantSession Open(string authenticatedUserId,string tenant)
    {
        ValidateId(tenant);Role(authenticatedUserId,tenant);
        var path=DatabasePath(tenant);
        if(!File.Exists(path))throw new LoomException(503,"Tenant has not been provisioned.");
        var store=new SqliteStore(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false,Mode=SqliteOpenMode.ReadWrite}.ToString());
        try
        {
            store.BindTenant(tenant);
            var catalog=store.ReadCatalog()??throw new LoomException(409,"Missing tenant schema.");
            return new(store,catalog,()=>new Actor(authenticatedUserId,Role(authenticatedUserId,tenant)),hooks,access);
        }
        catch {store.Dispose();throw;}
    }

    /// <summary>Creates a trusted worker route; every scope reopens this tenant and resolves current membership.</summary>
    /// <param name="authenticatedUserId">Host-configured service identity, never a user-supplied role.</param>
    /// <param name="tenant">Operator-allowlisted tenant identifier.</param>
    /// <param name="prepare">Optional synchronous tenant-local schedule materialization.</param>
    /// <returns>A route that owns a fresh TenantSession for each scan and active delivery.</returns>
    public DeliveryTenant DeliveryRoute(string authenticatedUserId,string tenant,Action<IApplicationSession>? prepare=null)
    {
        ValidateId(tenant);
        if(!tenants.Contains(tenant))throw new LoomException(403,"Tenant access denied.");
        if(string.IsNullOrWhiteSpace(authenticatedUserId))throw new ArgumentException("Configure a service identity.",nameof(authenticatedUserId));
        return new(tenant,()=>
        {
            var session=Open(authenticatedUserId,tenant);
            return new ApplicationSessionScope(session,session);
        },prepare);
    }

    /// <summary>Trusted local operator backup; not exposed as a tenant HTTP endpoint.</summary>
    public void Backup(string tenant,string destination)
    {
        var path=DatabasePath(tenant);
        if(!File.Exists(path))throw new FileNotFoundException("Tenant database does not exist.");
        using var store=new SqliteStore(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false,Mode=SqliteOpenMode.ReadWrite}.ToString());
        store.BindTenant(tenant);store.Backup(destination,tenant);
    }

    /// <summary>Restores only to an absent database for the explicitly expected tenant.</summary>
    public void Restore(string tenant,string archive)
    {
        var path=DatabasePath(tenant);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SqliteStore.RestoreBackup(archive,path,tenant);
    }
}

/// <summary>
/// Authorized tenant capability. No caller-supplied role, raw store or unscoped service
/// escapes this wrapper. Dispose at the end of a request; do not cache across requests.
/// </summary>
public sealed class TenantSession : IDisposable, IApplicationSession
{
    private readonly SqliteStore store;
    private readonly Catalog catalog;
    private readonly Func<Actor> actor;
    private readonly DocumentService service;
    internal TenantSession(SqliteStore store,Catalog catalog,Func<Actor> actor,IDocumentHook[] hooks,IDocumentAccessPolicy? access)
    {this.store=store;this.catalog=catalog;this.actor=actor;service=new(catalog,store,hooks,access);}
    private bool ownsStore = true;
    private TenantSession(SqliteStore store,Catalog catalog,Func<Actor> actor,DocumentService service)
    { this.store=store;this.catalog=catalog;this.actor=actor;this.service=service;ownsStore=false; }
    /// <summary>Atomic, tenant-bound application workflow; the callback session expires on return.</summary>
    public T Atomic<T>(Func<TenantSession,T> action) => service.Atomic(scoped =>
    {
        using var session=new TenantSession(store,catalog,actor,scoped);
        return action(session);
    });
    public Actor Identity=>actor();
    T IApplicationSession.Atomic<T>(Func<IApplicationSession,T> action)=>Atomic(s=>action(s));
    public string Role=>actor().Role;
    public DocumentForm Describe(string type)=>service.Describe(type,actor());
    public Page List(string type,string? search=null)=>service.List(type,actor(),search);
    public Page Query(string type,DocumentQuery query)=>service.Query(type,actor(),query);
    public Document Get(string type,string id)=>service.Get(type,id,actor());
    public Document Create(string type,JsonObject values)=>service.Create(type,values,actor());
    public Document Update(string type,string id,int version,JsonObject values)=>service.Update(type,id,version,values,actor());
    public void Delete(string type,string id,int version)=>service.Delete(type,id,version,actor());
    public Document Transition(string type,string id,int version,string action)=>service.Transition(type,id,version,action,actor());
    public IReadOnlyList<AuditEntry> History(string type,string id)=>service.History(type,id,actor());
    public ReportResult Report(SavedReport report)=>SavedReports.Run(report,catalog,service,actor());
    public ReportDesign? ReadReportDesign(string typeId)
    {
        if(!ownsStore)throw new InvalidOperationException("Report designs cannot join a document transaction.");
        if(actor().Role!="Admin")throw new LoomException(403,"Tenant administrator required.");
        return store.ReadReportDesign(typeId);
    }
    public ReportDesign SaveReportDesign(string typeId,ReportDesign design)
    {
        if(!ownsStore)throw new InvalidOperationException("Report designs cannot join a document transaction.");
        return store.SaveReportDesign(typeId,design,actor());
    }
    public void Dispose(){if(ownsStore)store.Dispose();}
}
