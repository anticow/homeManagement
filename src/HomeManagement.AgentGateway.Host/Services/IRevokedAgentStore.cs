namespace HomeManagement.AgentGateway.Host.Services;

/// <summary>
/// Stores the set of revoked agent IDs. Revoked agents are immediately disconnected
/// on their next heartbeat and rejected on all future connection attempts.
/// </summary>
public interface IRevokedAgentStore
{
    /// <summary>Revokes an agent by ID. Idempotent — revoking an already-revoked agent is a no-op.</summary>
    void Revoke(string agentId, string reason);

    /// <summary>Restores access for a previously revoked agent.</summary>
    void Reinstate(string agentId);

    /// <summary>Returns true when the agent ID appears in the revocation list.</summary>
    bool IsRevoked(string agentId);

    /// <summary>Returns all currently revoked entries.</summary>
    IReadOnlyList<RevokedAgentEntry> GetAll();
}

/// <summary>One entry in the revocation list.</summary>
public sealed record RevokedAgentEntry(string AgentId, string Reason, DateTimeOffset RevokedAt);
