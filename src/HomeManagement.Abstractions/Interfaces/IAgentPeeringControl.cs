namespace HomeManagement.Abstractions.Interfaces;

public interface IAgentPeeringControl
{
    Task RevokeAsync(string agentId, CancellationToken ct = default);
}
