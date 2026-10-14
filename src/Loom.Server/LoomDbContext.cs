using Microsoft.EntityFrameworkCore;

namespace Loom.Server;

// Fixed infrastructure model. Dynamic Loom fields remain inside the versioned payload;
// no EF model cache is keyed by an editable application catalog.
internal sealed class LoomDbContext(DbContextOptions<LoomDbContext> options):DbContext(options)
{
    public DbSet<StoredModel> Models=>Set<StoredModel>();
    public DbSet<StoredDocument> Documents=>Set<StoredDocument>();
    public DbSet<StoredAudit> Audits=>Set<StoredAudit>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        string text=Database.IsSqlServer()?"nvarchar(max)":"text";
        var schema=model.Entity<StoredModel>();schema.ToTable("loom_schema");schema.HasKey(x=>x.Singleton);
        schema.Property(x=>x.Singleton).HasColumnName("singleton").ValueGeneratedNever();
        schema.Property(x=>x.Model).HasColumnName("model").HasColumnType(text).IsRequired();
        var document=model.Entity<StoredDocument>();document.ToTable("loom_documents");document.HasKey(x=>new{x.Kind,x.Id});
        document.Property(x=>x.Kind).HasColumnName("kind").HasColumnType("varchar(63)").IsRequired();
        document.Property(x=>x.Id).HasColumnName("id").HasColumnType("varchar(32)").IsRequired();
        document.Property(x=>x.Version).HasColumnName("version").IsConcurrencyToken();
        document.Property(x=>x.Body).HasColumnName("body").HasColumnType(text).IsRequired();
        var audit=model.Entity<StoredAudit>();audit.ToTable("loom_audit");audit.HasKey(x=>x.Id);
        audit.Property(x=>x.Id).HasColumnName("id").HasColumnType("varchar(32)").IsRequired();
        audit.Property(x=>x.Kind).HasColumnName("kind").HasColumnType("varchar(63)").IsRequired();
        audit.Property(x=>x.DocumentId).HasColumnName("document_id").HasColumnType("varchar(32)").IsRequired();
        audit.Property(x=>x.Body).HasColumnName("body").HasColumnType(text).IsRequired();
    }
}
internal sealed class StoredModel
{
    public int Singleton {get;set;}
    public string Model {get;set;}="";
}
internal sealed class StoredDocument
{
    public string Kind {get;set;}="";
    public string Id {get;set;}="";
    public int Version {get;set;}
    public string Body {get;set;}="";
}
internal sealed class StoredAudit
{
    public string Id {get;set;}="";
    public string Kind {get;set;}="";
    public string DocumentId {get;set;}="";
    public string Body {get;set;}="";
}
