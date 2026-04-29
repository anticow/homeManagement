using System.Text.Json;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Repositories;

public sealed class PlanRepository : IPlanRepository
{
    private readonly HomeManagementDbContext _db;

    public PlanRepository(HomeManagementDbContext db)
    {
        _db = db;
    }

    public async Task CreatePlanAsync(
        Guid planId,
        string objective,
        string stepsJson,
        string riskLevel,
        string planHash,
        string status,
        string? correlationId,
        CancellationToken ct = default)
    {
        var entity = new AutomationPlanEntity
        {
            Id = planId,
            Objective = objective,
            StepsJson = stepsJson,
            RiskLevel = riskLevel,
            PlanHash = planHash,
            Status = status,
            CreatedUtc = DateTime.UtcNow,
            CorrelationId = correlationId,
        };

        _db.AutomationPlans.Add(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<WorkflowPlan?> GetPlanAsync(Guid planId, CancellationToken ct = default)
    {
        var entity = await _db.AutomationPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId, ct);

        return entity is null ? null : MapToDomain(entity);
    }

    public async Task UpdatePlanStatusAsync(
        Guid planId,
        string status,
        DateTime? approvedUtc = null,
        string? rejectionReason = null,
        CancellationToken ct = default)
    {
        await _db.AutomationPlans
            .Where(p => p.Id == planId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(p => p.Status, status)
                .SetProperty(p => p.ApprovedUtc, approvedUtc)
                .SetProperty(p => p.RejectionReason, rejectionReason),
                ct);
    }

    private static WorkflowPlan MapToDomain(AutomationPlanEntity entity)
    {
        List<PlanStep> steps;
        try
        {
            var dtos = JsonSerializer.Deserialize<List<JsonElement>>(entity.StepsJson) ?? [];

            steps = dtos.Select(e => new PlanStep(
                Name: e.GetProperty("name").GetString() ?? string.Empty,
                Kind: Enum.TryParse<PlanStepKind>(e.GetProperty("kind").GetString(), ignoreCase: true, out var k) ? k : PlanStepKind.Unknown,
                Description: e.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                Parameters: e.TryGetProperty("parameters", out var p)
                    ? p.EnumerateObject().ToDictionary(kv => kv.Name, kv => kv.Value.GetString() ?? string.Empty)
                    : new Dictionary<string, string>())).ToList();
        }
        catch
        {
            steps = [];
        }

        return new WorkflowPlan(
            Id: new WorkflowPlanId(entity.Id),
            Objective: entity.Objective,
            Steps: steps,
            RiskLevel: Enum.TryParse<PlanRiskLevel>(entity.RiskLevel, out var risk) ? risk : PlanRiskLevel.Low,
            PlanHash: entity.PlanHash,
            Status: Enum.TryParse<PlanStatus>(entity.Status, out var status) ? status : PlanStatus.PendingApproval,
            CreatedUtc: entity.CreatedUtc,
            ApprovedUtc: entity.ApprovedUtc,
            RejectionReason: entity.RejectionReason);
    }
}
