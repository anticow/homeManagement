using HomeManagement.Abstractions.Models;
using Refit;

namespace HomeManagement.Web.Services;

/// <summary>
/// Refit-generated typed HTTP client for the Broker REST API.
/// </summary>
public interface IBrokerApi
{
    // ── Machines ──
    [Get("/api/machines")]
    Task<PagedResult<Machine>> GetMachinesAsync(int page = 1, int pageSize = 25, CancellationToken ct = default);

    [Get("/api/machines/{id}")]
    Task<Machine> GetMachineAsync(Guid id, CancellationToken ct = default);

    [Post("/api/machines")]
    Task<Machine> CreateMachineAsync([Body] MachineCreateRequest request, CancellationToken ct = default);

    [Delete("/api/machines/{id}")]
    Task DeleteMachineAsync(Guid id, CancellationToken ct = default);

    [Get("/api/machines/{id}/state")]
    Task<MachineStateSnapshot> GetMachineStateAsync(Guid id, CancellationToken ct = default);

    [Get("/api/machines/summary")]
    Task<MachineSummary> GetMachineSummaryAsync(CancellationToken ct = default);

    // ── Patching ──
    [Post("/api/patching/scan")]
    Task<IReadOnlyList<PatchInfo>> ScanPatchesAsync([Body] PatchScanRequest request, CancellationToken ct = default);

    [Get("/api/patching/{machineId}/history")]
    Task<IReadOnlyList<PatchHistoryEntry>> GetPatchHistoryAsync(Guid machineId, CancellationToken ct = default);

    // ── Services ──
    [Get("/api/services/{machineId}")]
    Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(Guid machineId, CancellationToken ct = default);

    // ── Jobs ──
    [Get("/api/jobs")]
    Task<PagedResult<JobSummary>> GetJobsAsync(int page = 1, int pageSize = 25, CancellationToken ct = default);

    [Get("/api/jobs/{id}")]
    Task<JobStatus> GetJobAsync(Guid id, CancellationToken ct = default);

    // ── Credentials ──
    [Get("/api/credentials")]
    Task<IReadOnlyList<CredentialEntry>> GetCredentialsAsync(CancellationToken ct = default);

    // ── Audit ──
    [Get("/api/audit")]
    Task<PagedResult<AuditEvent>> GetAuditEventsAsync(int page = 1, int pageSize = 50, CancellationToken ct = default);
}

public sealed record PatchScanRequest(Guid MachineId);
