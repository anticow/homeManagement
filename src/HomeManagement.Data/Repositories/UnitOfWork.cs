using HomeManagement.Abstractions.Repositories;

namespace HomeManagement.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>.
/// All repositories share the same underlying <see cref="HomeManagementDbContext"/>.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly HomeManagementDbContext _db;

    public UnitOfWork(
        HomeManagementDbContext db,
        IMachineRepository machines,
        IPatchHistoryRepository patchHistory,
        IAuditEventRepository auditEvents,
        IJobRepository jobs,
        IServiceSnapshotRepository serviceSnapshots)
    {
        _db = db;
        Machines = machines;
        PatchHistory = patchHistory;
        AuditEvents = auditEvents;
        Jobs = jobs;
        ServiceSnapshots = serviceSnapshots;
    }

    public IMachineRepository Machines { get; }
    public IPatchHistoryRepository PatchHistory { get; }
    public IAuditEventRepository AuditEvents { get; }
    public IJobRepository Jobs { get; }
    public IServiceSnapshotRepository ServiceSnapshots { get; }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }

    public void Dispose()
    {
        // DbContext lifecycle is managed by DI — do not dispose here
    }
}
