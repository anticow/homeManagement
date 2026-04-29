using HomeManagement.Abstractions.Models;

namespace HomeManagement.Automation;

public sealed record HealthReportRunRequest(
    IReadOnlyList<Guid>? TargetMachineIds = null,
    string? Tag = null,
    int MaxTargets = 100);

public sealed record EnsureServiceRunningRunRequest(
    string ServiceName,
    IReadOnlyList<Guid>? TargetMachineIds = null,
    string? Tag = null,
    int MaxTargets = 100,
    bool AttemptRestart = true);

public sealed record PatchAllRunRequest(
    IReadOnlyList<Guid>? TargetMachineIds = null,
    string? Tag = null,
    int MaxTargets = 100,
    bool DryRun = false,
    bool AllowReboot = false);

public sealed record HaosHealthStatusRunRequest(
    string? InstanceName = null);

public sealed record HaosEntitySnapshotRunRequest(
    string? DomainFilter = null,
    int MaxEntities = 250,
    string? InstanceName = null);

public sealed record AnsibleHandoffRunRequest(
    string Operation,
    string? TargetScope = null,
    string? ExtraVarsJson = null,
    bool DryRun = true,
    int? ExecutionTimeoutSeconds = null,
    bool CancelOnTimeout = true,
    bool ApproveAndRun = false,
    string? ApprovedBy = null,
    string? ApprovalReason = null,
    string? ChangeTicket = null);

public sealed record CreatePlanRequest(string Objective);

public sealed record ApprovePlanRequest(
    /// <summary>
    /// The SHA-256 hex hash the caller computed from the plan steps.
    /// Must match <see cref="WorkflowPlan.PlanHash"/> exactly or the approval is rejected.
    /// </summary>
    string ExpectedHash);

public sealed record PolicyEvaluation(
    bool Allowed,
    PlanRiskLevel RiskLevel,
    IReadOnlyList<string> Violations);
