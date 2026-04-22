using HomeManagement.Data.Entities;

namespace HomeManagement.Data.Repositories;

public interface IAutomationRunRepository
{
    Task<AutomationRunEntity> CreateRunAsync(
        Guid runId,
        string workflowType,
        string? requestJson,
        string? correlationId,
        CancellationToken ct = default);

    Task<AutomationRunEntity?> GetRunAsync(Guid runId, CancellationToken ct = default);

    Task<IReadOnlyList<AutomationRunEntity>> ListRunsAsync(
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
