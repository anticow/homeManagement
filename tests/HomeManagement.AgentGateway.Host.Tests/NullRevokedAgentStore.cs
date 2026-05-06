using HomeManagement.AgentGateway.Host.Services;

namespace HomeManagement.AgentGateway.Host.Tests;

/// <summary>
/// No-op revocation store for unit tests — allows all agents.
/// </summary>
internal sealed class NullRevokedAgentStore : IRevokedAgentStore
{
    public void Revoke(string agentId, string reason) { }
    public void Reinstate(string agentId) { }
    public bool IsRevoked(string agentId) => false;
    public IReadOnlyList<RevokedAgentEntry> GetAll() => [];
}
