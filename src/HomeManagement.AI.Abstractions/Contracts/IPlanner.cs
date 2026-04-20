namespace HomeManagement.AI.Abstractions.Contracts;

public interface IPlanner
{
    Task<string> BuildPlanAsync(string objective, CancellationToken ct = default);
}
