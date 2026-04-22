using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Repositories;

/// <summary>
/// Unit of Work — coordinates SaveChanges across all repositories sharing the same DbContext.
/// Consumers should call <see cref="SaveChangesAsync"/> once after a batch of repository operations
/// rather than calling SaveChangesAsync on individual repositories.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    IMachineRepository Machines { get; }
    IPatchHistoryRepository PatchHistory { get; }
    IAuditEventRepository AuditEvents { get; }
    IJobRepository Jobs { get; }
    IServiceSnapshotRepository ServiceSnapshots { get; }
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IMachineRepository
{
    Task<Machine?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default);
    Task AddAsync(Machine machine, CancellationToken ct = default);
    Task AddRangeAsync(IReadOnlyList<Machine> machines, CancellationToken ct = default);
    Task UpdateAsync(Machine machine, CancellationToken ct = default);
    Task SoftDeleteAsync(Guid id, CancellationToken ct = default);
    Task SoftDeleteRangeAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IPatchHistoryRepository
{
    Task<IReadOnlyList<PatchHistoryEntry>> GetByMachineAsync(Guid machineId, CancellationToken ct = default);
    Task AddAsync(PatchHistoryEntry entry, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IAuditEventRepository
{
    /// <param name="chainVersion">0 = legacy SHA-256 (do not use for new events); 1 = HMAC-SHA256</param>
    Task AddAsync(AuditEvent auditEvent, string? previousHash, string eventHash, int chainVersion, CancellationToken ct = default);
    Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default);
    Task<long> CountAsync(AuditQuery query, CancellationToken ct = default);
    /// <summary>Returns the event hash of the most recent chain-version-1 event, or null if none exist (first v1 event will use GENESIS).</summary>
    Task<string?> GetLastEventHashAsync(CancellationToken ct = default);
    /// <summary>
    /// Verifies HMAC-SHA256 chain integrity over all chain-version-1 events.
    /// Returns (Valid=true, count, null) on success; (Valid=false, count up to failure, failedEventId) on first tamper detected.
    /// </summary>
    Task<(bool Valid, long Verified, string? FailedAtEventId)> VerifyChainAsync(byte[] hmacKey, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IJobRepository
{
    Task<JobStatus?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<JobStatus?> GetByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken ct = default);
    Task<PagedResult<JobSummary>> QueryAsync(JobQuery query, CancellationToken ct = default);
    Task AddAsync(JobStatus job, CancellationToken ct = default);
    Task UpdateAsync(JobStatus job, CancellationToken ct = default);
    Task AddMachineResultAsync(Guid jobId, JobMachineResult result, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IServiceSnapshotRepository
{
    Task<IReadOnlyList<ServiceSnapshot>> GetByMachineAsync(Guid machineId, CancellationToken ct = default);
    Task<ServiceSnapshot?> GetLatestAsync(Guid machineId, string serviceName, CancellationToken ct = default);
    Task AddAsync(ServiceSnapshot snapshot, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
