using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Repositories;

/// <summary>
/// Unit of Work — coordinates SaveChanges across all repositories sharing the same DbContext.
/// Callers must use <see cref="IUnitOfWork.SaveChangesAsync"/> rather than calling SaveChanges
/// on individual repositories.
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
}

public interface IPatchHistoryRepository
{
    Task<IReadOnlyList<PatchHistoryEntry>> GetByMachineAsync(Guid machineId, CancellationToken ct = default);
    Task AddAsync(PatchHistoryEntry entry, CancellationToken ct = default);
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
}

public interface IJobRepository
{
    Task<JobStatus?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<JobStatus?> GetByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken ct = default);
    Task<PagedResult<JobSummary>> QueryAsync(JobQuery query, CancellationToken ct = default);
    Task AddAsync(JobStatus job, CancellationToken ct = default);
    Task UpdateAsync(JobStatus job, CancellationToken ct = default);
    Task AddMachineResultAsync(Guid jobId, JobMachineResult result, CancellationToken ct = default);
}

public interface IServiceSnapshotRepository
{
    Task<IReadOnlyList<ServiceSnapshot>> GetByMachineAsync(Guid machineId, CancellationToken ct = default);
    Task<ServiceSnapshot?> GetLatestAsync(Guid machineId, string serviceName, CancellationToken ct = default);
    Task AddAsync(ServiceSnapshot snapshot, CancellationToken ct = default);
}

public interface IAutomationRunRepository
{
    Task CreateRunAsync(
        Guid runId,
        string workflowType,
        string? requestJson,
        string? correlationId,
        CancellationToken ct = default);

    Task<AutomationRun?> GetRunAsync(Guid runId, CancellationToken ct = default);

    Task<IReadOnlyList<AutomationRunSummary>> ListRunsAsync(
        int page,
        int pageSize,
        CancellationToken ct = default);

    Task UpdateRunStateAsync(
        Guid runId,
        string state,
        CancellationToken ct = default);

    Task UpdateRunCompletedAsync(
        Guid runId,
        string state,
        int completedMachines,
        int failedMachines,
        string? outputJson,
        string? outputMarkdown,
        CancellationToken ct = default);

    Task UpdateRunFailedAsync(
        Guid runId,
        string errorMessage,
        CancellationToken ct = default);

    Task AddStepAsync(
        Guid runId,
        Guid stepId,
        string stepName,
        CancellationToken ct = default);

    Task UpdateStepStateAsync(
        Guid stepId,
        string state,
        CancellationToken ct = default);

    Task UpdateStepCompletedAsync(
        Guid stepId,
        CancellationToken ct = default);

    Task UpdateStepFailedAsync(
        Guid stepId,
        string errorMessage,
        CancellationToken ct = default);

    Task AddMachineResultAsync(
        Guid runId,
        Guid machineId,
        string machineName,
        bool success,
        string? errorMessage,
        string? resultDataJson,
        CancellationToken ct = default);

    Task UpdateTotalMachinesAsync(
        Guid runId,
        int totalMachines,
        CancellationToken ct = default);
}

public interface IPlanRepository
{
    Task CreatePlanAsync(
        Guid planId,
        string objective,
        string stepsJson,
        string riskLevel,
        string planHash,
        string status,
        string? correlationId,
        CancellationToken ct = default);

    Task<WorkflowPlan?> GetPlanAsync(Guid planId, CancellationToken ct = default);

    Task UpdatePlanStatusAsync(
        Guid planId,
        string status,
        DateTime? approvedUtc = null,
        string? rejectionReason = null,
        CancellationToken ct = default);
}

public interface IAuthUserRepository
{
    /// <summary>Find a local user by username, including password hash (for login only).</summary>
    Task<AuthLocalUser?> FindLocalUserForLoginAsync(string username, CancellationToken ct = default);
    Task<AuthUser?> GetByIdAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<AuthUser>> GetAllAsync(CancellationToken ct = default);
    Task<bool> ExistsByUsernameAsync(string username, CancellationToken ct = default);
    Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default);
    Task CreateLocalUserAsync(NewLocalUserRecord user, CancellationToken ct = default);
    Task SetLastLoginAsync(Guid userId, DateTime loginUtc, CancellationToken ct = default);
    Task AssignRolesAsync(Guid userId, IReadOnlyList<Guid> roleIds, CancellationToken ct = default);
    Task<bool> UserHasRoleAsync(Guid userId, string roleName, CancellationToken ct = default);
    Task<bool> HasAnyAdminAsync(CancellationToken ct = default);
}

public interface IAuthRoleRepository
{
    Task<IReadOnlyList<RoleDefinition>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAllNamesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RoleRef>> GetByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default);
    Task SeedDefaultRolesAsync(IReadOnlyList<RoleDefinition> defaults, CancellationToken ct = default);
}

public interface IAuthRefreshTokenRepository
{
    Task<RefreshTokenState?> GetTokenStateAsync(string tokenHash, CancellationToken ct = default);
    /// <summary>Atomically revoke the token if it is still valid. Returns true if revoked, false if already revoked/expired/not found.</summary>
    Task<bool> TryRevokeTokenAsync(string tokenHash, DateTime revokedUtc, CancellationToken ct = default);
    /// <summary>Find token by hash and revoke it if not already revoked. Returns true if found.</summary>
    Task<bool> FindAndRevokeAsync(string tokenHash, CancellationToken ct = default);
    Task AddAsync(Guid userId, string tokenHash, DateTime issuedUtc, DateTime expiresUtc, CancellationToken ct = default);
}

public interface IAuthDatabaseInitializer
{
    Task MigrateAsync(CancellationToken ct = default);
}
