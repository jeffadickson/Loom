using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
namespace Loom.Identity;

public sealed class LoomUser : IdentityUser
{
    public bool Disabled { get; set; }
}
public sealed class IdentityAudit
{
    public long Id {get;set;}
    public string Actor {get;set;}="";
    public string Subject {get;set;}="";
    public string Action {get;set;}="";
    public string At {get;set;}="";
}
/// <summary>Separate EF-owned account schema; never part of an editable Loom DocType catalog.</summary>
public sealed class IdentityDatabase(DbContextOptions<IdentityDatabase> options) : IdentityDbContext<LoomUser>(options)
{
    public DbSet<IdentityAudit> Audit => Set<IdentityAudit>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<IdentityAudit>().ToTable("LoomIdentityAudit");
        builder.Entity<LoomUser>().HasIndex(u=>u.NormalizedEmail).IsUnique();
    }
}
