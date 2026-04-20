namespace HomeManagement.Automation;

public interface IAutomationEngine
{
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    // ── Health-report workflow (Phase 1 / 2) ────────────────────────────────
    Task<AutomationRunId> StartHealthReportAsync(HealthReportRunRequest request, CancellationToken ct = default);
    Task<AutomationRunId> StartEnsureServiceRunningAsync(EnsureServiceRunningRunRequest request, CancellationToken ct = default);
    Task<AutomationRunId> StartPatchAllAsync(PatchAllRunRequest request, CancellationToken ct = default);
    Task<AutomationRunId> StartHaosHealthStatusAsync(HaosHealthStatusRunRequest request, CancellationToken ct = default);
    Task<AutomationRunId> StartHaosEntitySnapshotAsync(HaosEntitySnapshotRunRequest request, CancellationToken ct = default);
    Task<AutomationRunId> StartAnsibleHandoffAsync(AnsibleHandoffRunRequest request, CancellationToken ct = default);
    Task<AutomationRun?> GetRunAsync(AutomationRunId runId, CancellationToken ct = default);
    Task<IReadOnlyList<AutomationRunSummary>> ListRunsAsync(int page, int pageSize, CancellationToken ct = default);

    // ── Safe NL Planning (Phase 3) ───────────────────────────────────────────

    /// <summary>
    /// Generate a structured plan from a natural-language objective and persist it
    /// with <see cref="PlanStatus.PendingApproval"/> (or <see cref="PlanStatus.Rejected"/>
    /// if policy immediately rejects it).  Never triggers execution.
    /// </summary>
    Task<WorkflowPlan> CreatePlanAsync(CreatePlanRequest request, CancellationToken ct = default);

    /// <summary>
    /// Verify the plan's integrity hash and policy, transition to
    /// <see cref="PlanStatus.Approved"/>, and enqueue execution.
    /// Throws <see cref="InvalidOperationException"/> if the hash does not match
    /// or the plan is not in <see cref="PlanStatus.PendingApproval"/> state.
    /// </summary>
    Task<WorkflowPlan> ApprovePlanAsync(WorkflowPlanId planId, ApprovePlanRequest request, CancellationToken ct = default);

    Task<WorkflowPlan?> GetPlanAsync(WorkflowPlanId planId, CancellationToken ct = default);
}

