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

    public async Task<AutomationPlanEntity> CreatePlanAsync(
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
        return entity;
    }

    public async Task<AutomationPlanEntity?> GetPlanAsync(Guid planId, CancellationToken ct = default)
    {
        return await _db.AutomationPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId, ct);
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
}
