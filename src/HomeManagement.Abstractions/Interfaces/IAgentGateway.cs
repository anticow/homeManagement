using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Manages communication with lightweight agents running on remote machines.
/// </summary>
public interface IAgentGateway
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    IReadOnlyList<ConnectedAgent> GetConnectedAgents();
    Task<RemoteResult> SendCommandAsync(string agentId, RemoteCommand command, CancellationToken ct = default);
    Task<AgentMetadata> GetMetadataAsync(string agentId, CancellationToken ct = default);
    Task RequestUpdateAsync(string agentId, AgentUpdatePackage package, CancellationToken ct = default);

    IObservable<AgentConnectionEvent> ConnectionEvents { get; }
}
