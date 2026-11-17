using Ascent.Web.Planning;
using Microsoft.EntityFrameworkCore;
namespace Ascent.Web.Infrastructure;

public sealed class ModuleRow
{
    public string SessionId {get;set;}="";
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public int Revision { get; set; }
    public string SnapshotJson { get; set; } = "";
}
public sealed class RequirementRow
{
    public Guid ModuleId { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Mandatory { get; set; }
    public int Count { get; set; }
    public string EvidenceJson { get; set; } = "";
}
public sealed class RouteRow
{
    public string Id { get; set; } = "";
    public Guid ModuleId { get; set; }
    public string Name { get; set; } = "";
    public bool Approved { get; set; }
}
public sealed class MilestoneRow
{
    public string Id { get; set; } = "";
    public string RouteId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Complete { get; set; }
    public bool Optional { get; set; }
    public string Notes { get; set; } = "";
}
public sealed class DependencyRow
{
    public string MilestoneId { get; set; } = "";
    public string PrerequisiteId { get; set; } = "";
}
public sealed class DocumentFingerprintRow
{
    public Guid ModuleId {get;set;}
    public string TextHash {get;set;}="";
}
public sealed class WorkspaceRow
{
    public string Id {get;set;}="";
    public DateTime CreatedUtc {get;set;}
}
public sealed class DemoRuntimeRow
{
    public int Id {get;set;}
    public string RunId {get;set;}="";
}
public sealed class PlanningDb(DbContextOptions<PlanningDb> options) : DbContext(options)
{
    public DbSet<WorkspaceRow> Workspaces=>Set<WorkspaceRow>();
    public DbSet<DemoRuntimeRow> DemoRuntime=>Set<DemoRuntimeRow>();
    public DbSet<ModuleRow> Modules => Set<ModuleRow>();
    public DbSet<RequirementRow> Requirements => Set<RequirementRow>();
    public DbSet<RouteRow> Routes => Set<RouteRow>();
    public DbSet<MilestoneRow> Milestones => Set<MilestoneRow>();
    public DbSet<DependencyRow> Dependencies => Set<DependencyRow>();
    public DbSet<DocumentFingerprintRow> DocumentFingerprints => Set<DocumentFingerprintRow>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<WorkspaceRow>(e=>{e.ToTable("Workspaces");e.HasKey(x=>x.Id);e.Property(x=>x.Id).HasMaxLength(64);});
        model.Entity<DemoRuntimeRow>(e=>{e.ToTable("DemoRuntime");e.HasKey(x=>x.Id);e.Property(x=>x.Id).ValueGeneratedNever();e.Property(x=>x.RunId).HasMaxLength(32);});
        model.Entity<ModuleRow>(e=>{e.Property(x=>x.SessionId).HasMaxLength(64);e.HasIndex(x=>x.SessionId);e.HasOne<WorkspaceRow>().WithMany().HasForeignKey(x=>x.SessionId).OnDelete(DeleteBehavior.Cascade);});
        model.Entity<DocumentFingerprintRow>(e=>
        {
            e.ToTable("DocumentFingerprints");e.HasKey(x=>x.ModuleId);
            e.Property(x=>x.TextHash).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            e.HasIndex(x=>x.TextHash);
            e.HasOne<ModuleRow>().WithMany().HasForeignKey(x=>x.ModuleId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<ModuleRow>(e => { e.ToTable("Modules"); e.HasKey(x => x.Id); e.Property(x => x.Title).HasMaxLength(500); e.Property(x => x.Revision).IsConcurrencyToken(); });
        model.Entity<RequirementRow>(e =>
        {
            e.ToTable("Requirements"); e.HasKey(x => new { x.ModuleId, x.Id }); e.Property(x => x.Id).HasMaxLength(100); e.Property(x => x.Kind).HasMaxLength(30);
            e.HasOne<ModuleRow>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<RouteRow>(e =>
        {
            e.ToTable("Routes"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(32);
            e.HasOne<ModuleRow>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<MilestoneRow>(e =>
        {
            e.ToTable("Milestones"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(32); e.Property(x => x.RouteId).HasMaxLength(32);
            e.HasOne<RouteRow>().WithMany().HasForeignKey(x => x.RouteId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<DependencyRow>(e =>
        {
            e.ToTable("MilestoneDependencies"); e.HasKey(x => new { x.MilestoneId, x.PrerequisiteId });
            e.Property(x => x.MilestoneId).HasMaxLength(32); e.Property(x => x.PrerequisiteId).HasMaxLength(32);
            e.HasOne<MilestoneRow>().WithMany().HasForeignKey(x => x.MilestoneId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MilestoneRow>().WithMany().HasForeignKey(x => x.PrerequisiteId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}
