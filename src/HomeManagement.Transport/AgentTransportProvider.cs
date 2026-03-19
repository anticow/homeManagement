using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Transport;

/// <summary>
/// Bridges <see cref="IRemoteExecutor"/> calls to <see cref="IAgentGateway"/>
/// for machines using <see cref="TransportProtocol.Agent"/>.
/// Resolves the target machine's hostname to a connected agent, then dispatches
/// the command through the gRPC bidirectional stream.
/// </summary>
internal sealed class AgentTransportProvider
{
    private readonly IAgentGateway _gateway;
    private readonly ILogger<AgentTransportProvider> _logger;

    public AgentTransportProvider(IAgentGateway gateway, ILogger<AgentTransportProvider> logger)
    {
        _gateway = gateway;
        _logger = logger;
    }

    public async Task<RemoteResult> ExecuteAsync(MachineTarget target, RemoteCommand command, CancellationToken ct)
    {
        var agentId = ResolveAgentId(target);

        _logger.LogInformation("Routing command to agent {AgentId} for host {Hostname}",
            agentId, target.Hostname);

        return await _gateway.SendCommandAsync(agentId, command, ct);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(MachineTarget target, CancellationToken ct)
    {
        try
        {
            var agentId = ResolveAgentId(target);
            var metadata = await _gateway.GetMetadataAsync(agentId, ct);

            return new ConnectionTestResult(
                Reachable: true,
                DetectedOs: metadata.OsType,
                OsVersion: metadata.OsVersion,
                Latency: TimeSpan.Zero,
                ProtocolVersion: metadata.Hardware.Architecture,
                ErrorMessage: null);
        }
        catch (InvalidOperationException ex)
        {
            return new ConnectionTestResult(
                Reachable: false,
                DetectedOs: null,
                OsVersion: null,
                Latency: TimeSpan.Zero,
                ProtocolVersion: null,
                ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Resolves a <see cref="MachineTarget"/> to the AgentId of the connected agent.
    /// Matches by hostname (case-insensitive). Falls back to checking AgentId directly
    /// in case the machine's hostname IS the AgentId.
    /// </summary>
    private string ResolveAgentId(MachineTarget target)
    {
        var hostname = target.Hostname.Value;
        var agents = _gateway.GetConnectedAgents();

        // Match by hostname first (agents report Environment.MachineName in Handshake)
        var match = agents.FirstOrDefault(a =>
            a.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase));

        // Fall back to matching AgentId (default AgentId = machinename.ToLower())
        match ??= agents.FirstOrDefault(a =>
            a.AgentId.Equals(hostname, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new InvalidOperationException(
                $"No connected agent found for host '{hostname}'. " +
                $"Connected agents: [{string.Join(", ", agents.Select(a => $"{a.AgentId}({a.Hostname})"))}]");
        }

        return match.AgentId;
    }
}
