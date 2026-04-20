using HomeManagement.Data.Entities;

namespace HomeManagement.Data.Repositories;

public interface IPlanRepository
{
    Task<AutomationPlanEntity> CreatePlanAsync(
        Guid planId,
        string objective,
        string stepsJson,
        string riskLevel,
        string planHash,
        string status,
        string? correlationId,
        CancellationToken ct = default);

    Task<AutomationPlanEntity?> GetPlanAsync(Guid planId, CancellationToken ct = default);

    Task UpdatePlanStatusAsync(
        Guid planId,
        string status,
        DateTime? approvedUtc = null,
        string? rejectionReason = null,
        CancellationToken ct = default);
}
