using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Repositories;

public class AutomationRunRepository : IAutomationRunRepository
{
    private readonly HomeManagementDbContext _db;

    public AutomationRunRepository(HomeManagementDbContext db)
    {
        _db = db;
    }

    public async Task<AutomationRunEntity> CreateRunAsync(
        Guid runId,
        string workflowType,
        string? requestJson,
        string? correlationId,
        CancellationToken ct = default)
    {
        var run = new AutomationRunEntity
        {
            Id = runId,
            WorkflowType = workflowType,
            State = "Queued",
            StartedUtc = DateTime.UtcNow,
            RequestJson = requestJson,
            CorrelationId = correlationId,
            TotalMachines = 0,
            CompletedMachines = 0,
            FailedMachines = 0
        };

        _db.AutomationRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<AutomationRunEntity?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        return await _db.AutomationRuns
            .AsNoTracking()
            .Include(r => r.Steps)
            .Include(r => r.MachineResults)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
    }

    public async Task<IReadOnlyList<AutomationRunEntity>> ListRunsAsync(
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var runs = await _db.AutomationRuns
            .AsNoTracking()
            .OrderByDescending(r => r.StartedUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return runs;
    }

    public async Task UpdateRunStateAsync(
        Guid runId,
        string state,
        CancellationToken ct = default)
    {
        await _db.AutomationRuns
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, state), ct);
    }

    public async Task UpdateRunCompletedAsync(
        Guid runId,
        string state,
        int completedMachines,
        int failedMachines,
        string? outputJson,
        string? outputMarkdown,
        CancellationToken ct = default)
    {
        await _db.AutomationRuns
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.State, state)
                .SetProperty(r => r.CompletedUtc, DateTime.UtcNow)
                .SetProperty(r => r.CompletedMachines, completedMachines)
                .SetProperty(r => r.FailedMachines, failedMachines)
                .SetProperty(r => r.OutputJson, outputJson)
                .SetProperty(r => r.OutputMarkdown, outputMarkdown),
                ct);
    }

    public async Task UpdateRunFailedAsync(
        Guid runId,
        string errorMessage,
        CancellationToken ct = default)
    {
        await _db.AutomationRuns
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.State, "Failed")
                .SetProperty(r => r.CompletedUtc, DateTime.UtcNow)
                .SetProperty(r => r.ErrorMessage, errorMessage),
                ct);
    }

    public async Task AddStepAsync(
        Guid runId,
        Guid stepId,
        string stepName,
        CancellationToken ct = default)
    {
        var step = new AutomationRunStepEntity
        {
            Id = stepId,
            RunId = runId,
            StepName = stepName,
            State = "Queued",
            StartedUtc = DateTime.UtcNow
        };

        _db.AutomationRunSteps.Add(step);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateStepStateAsync(
        Guid stepId,
        string state,
        CancellationToken ct = default)
    {
        await _db.AutomationRunSteps
            .Where(s => s.Id == stepId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, state), ct);
    }

    public async Task UpdateStepCompletedAsync(
        Guid stepId,
        CancellationToken ct = default)
    {
        await _db.AutomationRunSteps
            .Where(s => s.Id == stepId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.State, "Completed")
                .SetProperty(s => s.CompletedUtc, DateTime.UtcNow),
                ct);
    }

    public async Task UpdateStepFailedAsync(
        Guid stepId,
        string errorMessage,
        CancellationToken ct = default)
    {
        await _db.AutomationRunSteps
            .Where(s => s.Id == stepId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.State, "Failed")
                .SetProperty(s => s.CompletedUtc, DateTime.UtcNow)
                .SetProperty(s => s.ErrorMessage, errorMessage),
                ct);
    }

    public async Task AddMachineResultAsync(
        Guid runId,
        Guid machineId,
        string machineName,
        bool success,
        string? errorMessage,
        string? resultDataJson,
        CancellationToken ct = default)
    {
        var result = new AutomationMachineResultEntity
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            MachineId = machineId,
            MachineName = machineName,
            Success = success,
            ErrorMessage = errorMessage,
            ResultDataJson = resultDataJson
        };

        _db.AutomationMachineResults.Add(result);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateTotalMachinesAsync(
        Guid runId,
        int totalMachines,
        CancellationToken ct = default)
    {
        await _db.AutomationRuns
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.TotalMachines, totalMachines), ct);
    }
}
