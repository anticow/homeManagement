using Microsoft.EntityFrameworkCore;
using HomeManagement.Data.Entities;

namespace HomeManagement.Data;

public class HomeManagementDbContext : DbContext
{
    public HomeManagementDbContext(DbContextOptions<HomeManagementDbContext> options)
        : base(options)
    {
    }

    public DbSet<MachineEntity> Machines => Set<MachineEntity>();
    public DbSet<MachineTagEntity> MachineTags => Set<MachineTagEntity>();
    public DbSet<PatchHistoryEntity> PatchHistory => Set<PatchHistoryEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<JobMachineResultEntity> JobMachineResults => Set<JobMachineResultEntity>();
    public DbSet<ScheduledJobEntity> ScheduledJobs => Set<ScheduledJobEntity>();
    public DbSet<ServiceSnapshotEntity> ServiceSnapshots => Set<ServiceSnapshotEntity>();
    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();
    public DbSet<AuthUserEntity> AuthUsers => Set<AuthUserEntity>();
    public DbSet<AuthRoleEntity> AuthRoles => Set<AuthRoleEntity>();
    public DbSet<AuthUserRoleEntity> AuthUserRoles => Set<AuthUserRoleEntity>();
    public DbSet<AuthRefreshTokenEntity> AuthRefreshTokens => Set<AuthRefreshTokenEntity>();

    public DbSet<AutomationRunEntity> AutomationRuns => Set<AutomationRunEntity>();
    public DbSet<AutomationRunStepEntity> AutomationRunSteps => Set<AutomationRunStepEntity>();
    public DbSet<AutomationMachineResultEntity> AutomationMachineResults => Set<AutomationMachineResultEntity>();
    public DbSet<AutomationPlanEntity> AutomationPlans => Set<AutomationPlanEntity>();

    /// <summary>
    /// Intercept save to enforce audit event immutability — audit records
    /// must never be modified or deleted to preserve HMAC chain integrity.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAuditImmutability();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceAuditImmutability();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceAuditImmutability()
    {
        var violatingEntries = ChangeTracker.Entries<AuditEventEntity>()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (violatingEntries.Count > 0)
        {
            // Reset state so the context isn't poisoned
            foreach (var entry in violatingEntries)
                entry.State = EntityState.Unchanged;

            throw new InvalidOperationException(
                "Audit events are append-only. Modification and deletion are prohibited to preserve HMAC chain integrity.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── Machines ──
        modelBuilder.Entity<MachineEntity>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => m.Hostname).IsUnique();
            e.HasIndex(m => m.CreatedUtc);
            e.HasQueryFilter(m => !m.IsDeleted); // Global soft-delete filter
            e.Property(m => m.OsType).HasConversion<string>();
            e.Property(m => m.State).HasConversion<string>();
            e.Property(m => m.ConnectionMode).HasConversion<string>();
            e.Property(m => m.Protocol).HasConversion<string>();
            e.Property(m => m.IpAddressesJson).HasColumnName("IpAddresses");
            e.HasMany(m => m.Tags).WithOne(t => t.Machine).HasForeignKey(t => t.MachineId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(m => m.PatchHistory).WithOne(p => p.Machine).HasForeignKey(p => p.MachineId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(m => m.ServiceSnapshots).WithOne(s => s.Machine).HasForeignKey(s => s.MachineId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Machine Tags (normalized) ──
        modelBuilder.Entity<MachineTagEntity>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.MachineId, t.Key }).IsUnique();
            e.HasIndex(t => t.Key);             // "find all machines tagged 'role'"
            e.HasIndex(t => new { t.Key, t.Value }); // "find machines where role=web"
            e.HasQueryFilter(t => !t.Machine.IsDeleted);
        });

        // ── Patch History ──
        modelBuilder.Entity<PatchHistoryEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.MachineId);
            e.HasIndex(p => p.TimestampUtc);
            e.HasIndex(p => p.State);           // "find all pending patches"
            e.HasIndex(p => new { p.MachineId, p.TimestampUtc }); // "patch history for machine X"
            e.HasIndex(p => new { p.MachineId, p.PatchId });       // "is this patch known for machine?"
            e.HasQueryFilter(p => !p.Machine.IsDeleted);
            e.Property(p => p.State).HasConversion<string>();
            e.Property(p => p.Severity).HasConversion<string>();
            e.Property(p => p.Category).HasConversion<string>();
            e.HasOne(p => p.Job)
                .WithMany()
                .HasForeignKey(p => p.JobId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Audit Events ──
        modelBuilder.Entity<AuditEventEntity>(e =>
        {
            e.HasKey(a => a.EventId);
            e.HasIndex(a => a.TimestampUtc);
            e.HasIndex(a => a.CorrelationId);
            e.HasIndex(a => a.Action);
            e.HasIndex(a => new { a.Action, a.Outcome });                // "all successful patch installs"
            e.HasIndex(a => new { a.TargetMachineId, a.TimestampUtc });  // "audit trail for machine X"
            e.HasIndex(a => a.ChainVersion);                             // "query only v1 HMAC chain events"
            e.Property(a => a.Action).HasConversion<string>();
            e.Property(a => a.Outcome).HasConversion<string>();
            e.Property(a => a.PropertiesJson).HasColumnName("Properties");
            e.Property(a => a.ChainVersion).HasDefaultValue(0);
        });

        // ── Jobs ──
        modelBuilder.Entity<JobEntity>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasIndex(j => j.SubmittedUtc);
            e.HasIndex(j => new { j.Type, j.State }); // "find running patch jobs"
            e.Property(j => j.State).HasConversion<string>();
            e.Property(j => j.Type).HasConversion<string>();
            e.HasMany(j => j.MachineResults).WithOne(r => r.Job).HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Job Machine Results (normalized) ──
        modelBuilder.Entity<JobMachineResultEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.JobId);
            e.HasIndex(r => r.MachineId);
            e.HasIndex(r => new { r.MachineId, r.Success }); // "jobs that failed on machine X"
        });

        // ── Scheduled Jobs ──
        modelBuilder.Entity<ScheduledJobEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Type).HasConversion<string>();
        });

        // ── Service Snapshots ──
        modelBuilder.Entity<ServiceSnapshotEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.MachineId);
            e.HasIndex(s => new { s.MachineId, s.ServiceName });  // "services on machine X"
            e.HasIndex(s => s.CapturedUtc);
            e.HasQueryFilter(s => !s.Machine.IsDeleted);
            e.Property(s => s.State).HasConversion<string>();
            e.Property(s => s.StartupType).HasConversion<string>();
        });

        // ── App Settings ──
        modelBuilder.Entity<AppSettingEntity>(e =>
        {
            e.HasKey(a => a.Key);
        });

        // ── Auth Users ──
        modelBuilder.Entity<AuthUserEntity>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.Provider);
            e.Property(u => u.Username).HasMaxLength(128);
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.Provider).HasMaxLength(64);
            e.HasMany(u => u.UserRoles).WithOne(ur => ur.User).HasForeignKey(ur => ur.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(u => u.RefreshTokens).WithOne(t => t.User).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Auth Roles ──
        modelBuilder.Entity<AuthRoleEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.Name).HasMaxLength(64);
            e.HasMany(r => r.UserRoles).WithOne(ur => ur.Role).HasForeignKey(ur => ur.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Auth User Roles ──
        modelBuilder.Entity<AuthUserRoleEntity>(e =>
        {
            e.HasKey(ur => new { ur.UserId, ur.RoleId });
            e.HasIndex(ur => ur.RoleId);
        });

        // ── Auth Refresh Tokens ──
        modelBuilder.Entity<AuthRefreshTokenEntity>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => new { t.UserId, t.ExpiresUtc });
        });

        // ── Automation Runs ──
        modelBuilder.Entity<AutomationRunEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.StartedUtc);
            e.HasIndex(a => a.WorkflowType);
            e.HasIndex(a => a.State);
            e.HasIndex(a => a.CorrelationId);
            e.Property(a => a.State).HasConversion<string>();
            e.HasMany(a => a.Steps).WithOne(s => s.Run).HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.MachineResults).WithOne(r => r.Run).HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Automation Run Steps ──
        modelBuilder.Entity<AutomationRunStepEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.RunId);
            e.HasIndex(s => new { s.RunId, s.StepName });
            e.Property(s => s.State).HasConversion<string>();
        });

        // ── Automation Machine Results ──
        modelBuilder.Entity<AutomationMachineResultEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.RunId);
            e.HasIndex(r => r.MachineId);
            e.HasIndex(r => new { r.RunId, r.MachineId });
            e.HasIndex(r => new { r.RunId, r.Success });
        });
    }
}
