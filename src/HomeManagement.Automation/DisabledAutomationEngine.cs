using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Options;

namespace HomeManagement.Automation;

internal sealed class DisabledAutomationEngine : IAutomationEngine
{
    private readonly IOptionsMonitor<AutomationOptions> _options;

    public DisabledAutomationEngine(IOptionsMonitor<AutomationOptions> options)
    {
        _options = options;
    }

    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_options.CurrentValue.Enabled);
    }

    public Task<AutomationRunId> StartHealthReportAsync(HealthReportRunRequest request, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Automation is disabled.");
    }

    public Task<AutomationRunId> StartEnsureServiceRunningAsync(EnsureServiceRunningRunRequest request, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Automation is disabled.");
    }

    public Task<AutomationRunId> StartPatchAllAsync(PatchAllRunRequest request, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Automation is disabled.");
    }

    public Task<AutomationRunId> StartHaosHealthStatusAsync(HaosHealthStatusRunRequest request, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Automation is disabled.");
    }

    public Task<AutomationRunId> StartHaosEntitySnapshotAsync(HaosEntitySnapshotRunRequest request, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Automation is disabled.");
    }

    public Task<AutomationRunId> StartAnsibleHandoffAsync(AnsibleHandoffRunRequest request, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Automation is disabled.");
    }

    public Task<AutomationRun?> GetRunAsync(AutomationRunId runId, CancellationToken ct = default)
    {
        return Task.FromResult<AutomationRun?>(null);
    }

    public Task<IReadOnlyList<AutomationRunSummary>> ListRunsAsync(int page, int pageSize, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<AutomationRunSummary>>([]);
    }

    public Task<WorkflowPlan> CreatePlanAsync(CreatePlanRequest request, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Automation is disabled.");
    }

    public Task<WorkflowPlan> ApprovePlanAsync(WorkflowPlanId planId, ApprovePlanRequest request, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Automation is disabled.");
    }

    public Task<WorkflowPlan?> GetPlanAsync(WorkflowPlanId planId, CancellationToken ct = default)
    {
        return Task.FromResult<WorkflowPlan?>(null);
    }
}

