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

    [Get("/api/machines/{id}/processes")]
    Task<IReadOnlyList<ProcessInfo>> GetMachineProcessesAsync(Guid id, CancellationToken ct = default);

    // ── Patching ──
    [Post("/api/patching/scan")]
    Task<IReadOnlyList<PatchInfo>> ScanPatchesAsync([Body] PatchScanRequest request, CancellationToken ct = default);

    [Get("/api/patching/{machineId}/history")]
    Task<IReadOnlyList<PatchHistoryEntry>> GetPatchHistoryAsync(Guid machineId, CancellationToken ct = default);

    // ── Services ──
    [Get("/api/services/{machineId}")]
    Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(Guid machineId, CancellationToken ct = default);

    // ── Action1 ──
    [Get("/api/action1/endpoints/{endpointId}/patches")]
    Task<IReadOnlyList<Action1PatchDto>> GetAction1PatchesAsync(string endpointId, CancellationToken ct = default);

    [Post("/api/action1/endpoints/{endpointId}/deploy")]
    Task<Action1DeploymentCreatedDto> DeployAction1PatchesAsync(string endpointId, [Body] Action1DeployRequestDto request, CancellationToken ct = default);

    [Get("/api/action1/deployments/{deploymentId}")]
    Task<Action1DeploymentStatusDto> GetAction1DeploymentAsync(string deploymentId, CancellationToken ct = default);

    // ── Action1 Fleet (single pane of glass) ──
    [Get("/api/action1/fleet")]
    Task<IReadOnlyList<FleetMachineStatusDto>> GetFleetStatusAsync(CancellationToken ct = default);

    [Get("/api/action1/fleet/summary")]
    Task<FleetPatchSummaryDto> GetFleetSummaryAsync(CancellationToken ct = default);

    [Get("/api/action1/fleet/{machineId}/patches")]
    Task<IReadOnlyList<Action1PatchDto>> GetMachinePatchesAsync(Guid machineId, CancellationToken ct = default);

    [Post("/api/action1/fleet/{machineId}/approve")]
    Task<ApproveDeploymentResultDto> ApprovePatchesAsync(Guid machineId, [Body] Action1DeployRequestDto request, CancellationToken ct = default);

    [Get("/api/action1/fleet/vulnerabilities")]
    Task<IReadOnlyList<Action1VulnerabilityDto>> GetFleetVulnerabilitiesAsync(CancellationToken ct = default);

    // ── Action1 Schedule Management ──
    [Get("/api/action1/schedules")]
    Task<ScheduleListDto> GetSchedulesAsync(CancellationToken ct = default);

    [Post("/api/action1/schedules/sync")]
    Task<ScheduleSyncResultDto> SyncSchedulesAsync(CancellationToken ct = default);

    [Patch("/api/action1/schedules/{scheduleId}")]
    Task PatchScheduleAsync(string scheduleId, [Body] SchedulePatchRequestDto request, CancellationToken ct = default);

    [Delete("/api/action1/schedules/{scheduleId}")]
    Task DeleteScheduleAsync(string scheduleId, CancellationToken ct = default);

    // ── Action1 Clients (organizations + enrolled endpoints) ──
    [Get("/api/action1/clients")]
    Task<IReadOnlyList<Action1OrgDto>> GetAction1ClientsAsync(CancellationToken ct = default);

    [Get("/api/action1/fleet/pending-patches")]
    Task<IReadOnlyList<MachinePendingPatchesDto>> GetAllPendingPatchesAsync(CancellationToken ct = default);

    // ── Action1 Catalog (org-level update approval queue) ──
    [Get("/api/action1/catalog")]
    Task<IReadOnlyList<CatalogUpdateDto>> GetCatalogUpdatesAsync(string approvalStatus = "New", CancellationToken ct = default);

    [Get("/api/action1/catalog/test")]
    Task<CatalogTestResultDto> TestCatalogConnectionAsync(CancellationToken ct = default);

    [Post("/api/action1/catalog/approve")]
    Task<CatalogApproveResultDto> ApproveCatalogUpdatesAsync([Body] CatalogApproveRequestDto request, CancellationToken ct = default);

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

// ── Action1 DTOs (web-layer copies; broker serialises from HomeManagement.Integration.Action1.Models) ──

public sealed record Action1PatchDto(
    string Id,
    string Name,
    string? Version,
    string? Description,
    string Severity,
    string Category,
    long SizeBytes,
    bool RequiresReboot,
    DateTime? PublishedUtc,
    string? KbArticleId);

public sealed record Action1PatchItemDto(string Id, string? Version);

public sealed record Action1DeployRequestDto(
    IReadOnlyList<Action1PatchItemDto> Patches,
    bool AllowReboot);

public sealed record Action1DeploymentCreatedDto(string DeploymentId);

public sealed record Action1DeploymentStatusDto(
    string Id,
    string Status,
    DateTime CreatedUtc,
    DateTime? CompletedUtc);

public sealed record FleetMachineStatusDto(
    Guid MachineId,
    string Hostname,
    string OsType,
    string MachineState,
    string? Action1EndpointId,
    string Action1Status,
    DateTime? Action1LastSeen,
    string? AgentVersion,
    int CriticalPatchCount,
    int OtherPatchCount,
    string? LastLoggedInUser,
    DateTime? LastPatchedUtc,
    string PatchRiskLevel);

public sealed record FleetPatchSummaryDto(
    int TotalMachines,
    int EnrolledInAction1,
    int TotalCriticalPatches,
    int TotalOtherPatches,
    int FullyPatched,
    int Online,
    int PatchedWithin30Days,
    int PatchedWithin90Days,
    int OverdueCount);

public sealed record Action1VulnerabilityDto(
    string CveId,
    string? Description,
    double? CvssScore,
    DateTime? PublishedUtc,
    IReadOnlyList<Action1VulnerableSoftwareDto>? Software);

public sealed record Action1VulnerableSoftwareDto(
    string? Name,
    IReadOnlyList<Action1VulnerabilityUpdateDto>? AvailableUpdates);

public sealed record Action1VulnerabilityUpdateDto(
    string PackageId,
    string? Version,
    string? Name);

public sealed record ApproveDeploymentResultDto(string DeploymentId, string EndpointId);

public sealed record ScheduleListDto(
    bool SyncConfigured,
    int RuleCount,
    IReadOnlyList<ScheduleDto> Schedules);

public sealed record ScheduleDto(
    string Id,
    string Name,
    string? Settings,
    string? RetryMinutes,
    DateTime? LastRun,
    DateTime? NextRun,
    bool IsSystem,
    bool IsManagedByHm,
    string? UpdateApproval,
    int DeferDays,
    bool AllowReboot);

public sealed record SchedulePatchRequestDto(string? Settings, string? Name);

public sealed record ScheduleSyncResultDto(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Updated);

// ── Action1 Clients (organizations + enrolled endpoints) ─────────────────────

public sealed record Action1OrgDto(
    string OrgId,
    string Name,
    string? Description,
    bool IsConfiguredOrg,
    int EndpointCount,
    string? Status,
    DateTime? CreatedUtc,
    IReadOnlyList<Action1EnrolledEndpointDto> Endpoints,
    IReadOnlyList<Action1GroupDto> Groups);

public sealed record Action1GroupDto(
    string GroupId,
    string Name,
    string? Description,
    int EndpointCount);

public sealed record Action1EnrolledEndpointDto(
    string EndpointId,
    string Name,
    string? AgentVersion,
    bool IsAgentCurrent,
    string Status,
    string? IpAddress,
    string? ExternalAddress,
    string? OsName,
    string? OsType,
    string? LastLoggedInUser,
    DateTime? LastSeenUtc,
    int MissingCriticalPatches,
    int MissingOtherPatches,
    IReadOnlyList<Action1ClientScheduleDto> Schedules);

public sealed record Action1ClientScheduleDto(
    string ScheduleId,
    string ScheduleName,
    bool IsManagedByHm,
    string? Settings);

/// <summary>
/// Aggregate pending-patch view for one fleet machine.
/// Mirrors the broker-side MachinePendingPatchesDto serialized over HTTP.
/// </summary>
public sealed record MachinePendingPatchesDto(
    Guid MachineId,
    string Hostname,
    string? Action1EndpointId,
    string? OsType,
    string PatchRiskLevel,
    int CriticalCount,
    int OtherCount,
    IReadOnlyList<Action1PatchDto> Patches);

// ── Action1 Catalog DTOs ─────────────────────────────────────────────────────

/// <summary>An update in the org-level Action1 update catalog with its approval status.</summary>
public sealed record CatalogUpdateDto(
    string Id,
    string Name,
    string? Version,
    string? Description,
    string Severity,
    string Category,
    string? UpdateType,
    string ApprovalStatus,
    bool RequiresReboot,
    DateTime? PublishedUtc,
    string? KbArticleId);

public sealed record CatalogApproveRequestDto(
    IReadOnlyList<string> UpdateIds,
    string ApprovalStatus = "Approved");

public sealed record CatalogApproveResultDto(
    int Approved,
    int Failed,
    IReadOnlyList<string> FailedIds);

public sealed record CatalogTestResultDto(
    bool Success,
    bool Enabled,
    int ItemCount,
    long ElapsedMs,
    string? Error);
