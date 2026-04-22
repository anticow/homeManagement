using System.Collections.Concurrent;

namespace HomeManagement.AgentGateway.Host.Services;

/// <summary>
/// Tracks connected agents and their metadata in memory.
/// </summary>
public sealed class AgentConnectionTracker
{
    private readonly ConcurrentDictionary<string, AgentConnectionInfo> _agents = new();

    public void Register(string agentId, AgentConnectionInfo info)
    {
        _agents.AddOrUpdate(agentId, info, (_, _) => info);
    }

    public void Unregister(string agentId)
    {
        _agents.TryRemove(agentId, out _);
    }

    public AgentConnectionInfo? Get(string agentId)
    {
        return _agents.TryGetValue(agentId, out var info) ? info : null;
    }

    public IReadOnlyList<AgentConnectionInfo> GetAll()
    {
        return _agents.Values.ToList();
    }

    public int Count => _agents.Count;
}

public sealed record AgentConnectionInfo(
    string AgentId,
    string Hostname,
    string OsType,
    string AgentVersion,
    DateTime ConnectedUtc);
