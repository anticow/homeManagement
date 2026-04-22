namespace HomeManagement.Automation;

public readonly record struct AutomationRunId(Guid Value)
{
    public static AutomationRunId New() => new(Guid.NewGuid());
}

public enum AutomationRunStateKind
{
    Queued,
    Running,
    Completed,
    Failed
}

public enum AutomationStepState
{
    Queued,
    Running,
    Completed,
    Failed
}

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

public sealed record AutomationStepResult(
    string Name,
    AutomationStepState State,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    string Message);

public sealed record AutomationMachineResult(
    Guid MachineId,
    string MachineName,
    bool Success,
    int CpuCores,
    long? RamBytes,
    string? Architecture,
    int RunningServices,
    string? ErrorMessage);

public sealed record AutomationRun(
    AutomationRunId Id,
    string WorkflowName,
    AutomationRunStateKind State,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    IReadOnlyList<AutomationStepResult> Steps,
    IReadOnlyList<AutomationMachineResult> MachineResults,
    string? OutputJson,
    string? OutputMarkdown,
    string? ErrorMessage);

public sealed record AutomationRunSummary(
    AutomationRunId Id,
    string WorkflowName,
    AutomationRunStateKind State,
    DateTime CreatedUtc,
    DateTime? CompletedUtc,
    int TotalTargets,
    int SuccessCount,
    int FailedCount);

// ── Phase 3 — Safe NL Planning ───────────────────────────────────────────────

public readonly record struct WorkflowPlanId(Guid Value)
{
    public static WorkflowPlanId New() => new(Guid.NewGuid());
}

/// <summary>Well-known kinds of steps a generated plan may contain.</summary>
public enum PlanStepKind
{
    /// <summary>Read CPU/RAM/disk metrics from target machines.</summary>
    GatherMetrics,
    /// <summary>Enumerate running services on target machines.</summary>
    ListServices,
    /// <summary>Restart a named service on one or more machines.</summary>
    RestartService,
    /// <summary>Install available OS patches on target machines.</summary>
    ApplyPatch,
    /// <summary>Shut down a machine — requires explicit approval.</summary>
    ShutdownMachine,
    /// <summary>Execute an arbitrary shell script — requires explicit approval.</summary>
    RunScript,
    /// <summary>Unrecognised step kind returned by the LLM.</summary>
    Unknown,
}

public enum PlanRiskLevel { Low, Medium, High, Critical }

public enum PlanStatus
{
    /// <summary>Plan generated; awaiting human approval.</summary>
    PendingApproval,
    /// <summary>Policy accepted and hash verified; ready to execute.</summary>
    Approved,
    /// <summary>Policy rejected or hash mismatch detected.</summary>
    Rejected,
    /// <summary>Execution in progress.</summary>
    Executing,
    /// <summary>All steps completed successfully.</summary>
    Completed,
    /// <summary>Execution halted due to an error.</summary>
    Failed,
}

public sealed record PlanStep(
    string Name,
    PlanStepKind Kind,
    string Description,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record WorkflowPlan(
    WorkflowPlanId Id,
    string Objective,
    IReadOnlyList<PlanStep> Steps,
    PlanRiskLevel RiskLevel,
    /// <summary>SHA-256 hex of the canonical steps JSON — used to verify integrity at approval time.</summary>
    string PlanHash,
    PlanStatus Status,
    DateTime CreatedUtc,
    DateTime? ApprovedUtc,
    string? RejectionReason);

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

