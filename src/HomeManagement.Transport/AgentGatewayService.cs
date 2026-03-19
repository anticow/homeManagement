using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Transport.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Transport;

/// <summary>
/// Manages live agent connections via the gRPC bidirectional streams.
/// Called by <see cref="AgentHubService"/> on connect/disconnect/heartbeat/response.
/// Implements <see cref="IAgentGateway"/> for the GUI and orchestration layer.
/// </summary>
public sealed class AgentGatewayService : IAgentGateway, IDisposable
{
    private readonly ILogger<AgentGatewayService> _logger;
    private readonly ConcurrentDictionary<string, AgentConnection> _connections = new();
    private readonly Subject<AgentConnectionEvent> _connectionSubject = new();

    public IObservable<AgentConnectionEvent> ConnectionEvents =>
        _connectionSubject.AsObservable();

    public AgentGatewayService(ILogger<AgentGatewayService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Agent gateway started — listening for agent connections");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Agent gateway stopping");
        foreach (var connection in _connections.Values)
            connection.Outbound.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public IReadOnlyList<ConnectedAgent> GetConnectedAgents() =>
        _connections.Values.Select(c => new ConnectedAgent(
            c.AgentId,
            c.Handshake.Hostname,
            c.Handshake.AgentVersion,
            c.ConnectedUtc,
            c.LastHeartbeatUtc,
            DateTime.UtcNow - c.ConnectedUtc)).ToList().AsReadOnly();

    public async Task<RemoteResult> SendCommandAsync(
        string agentId,
        RemoteCommand command,
        CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(agentId, out var connection))
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.PendingCommands[requestId] = tcs;

        try
        {
            var request = new CommandRequest
            {
                RequestId = requestId,
                CommandType = "Shell",
                CommandText = command.CommandText,
                TimeoutSeconds = (int)command.Timeout.TotalSeconds,
                ElevationMode = command.Elevation.ToString(),
                RunAsUser = command.RunAsUser ?? string.Empty,
            };

            if (command.EnvironmentVariables is not null)
            {
                foreach (var (key, value) in command.EnvironmentVariables)
                    request.Env[key] = value;
            }

            await connection.Outbound.Writer.WriteAsync(
                new ControlMessage { CommandRequest = request }, ct);

            _logger.LogDebug("Command {RequestId} sent to {AgentId}", requestId, agentId);

            // Wait for the response with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(command.Timeout + TimeSpan.FromSeconds(5));
            await using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

            var response = await tcs.Task;
            return new RemoteResult(
                ExitCode: response.ExitCode,
                Stdout: response.Stdout,
                Stderr: response.Stderr,
                Duration: TimeSpan.FromMilliseconds(response.DurationMs),
                TimedOut: response.TimedOut);
        }
        finally
        {
            connection.PendingCommands.TryRemove(requestId, out _);
        }
    }

    public Task<AgentMetadata> GetMetadataAsync(string agentId, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(agentId, out var connection))
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");

        var hs = connection.Handshake;
        var osType = hs.OsType.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            ? OsType.Windows : OsType.Linux;

        return Task.FromResult(new AgentMetadata(
            AgentId: hs.AgentId,
            Hostname: hs.Hostname,
            OsType: osType,
            OsVersion: hs.OsVersion,
            Hardware: new HardwareInfo(0, 0, [], hs.Architecture)));
    }

    public async Task RequestUpdateAsync(
        string agentId,
        AgentUpdatePackage package,
        CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(agentId, out var connection))
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");

        var directive = new UpdateDirective
        {
            TargetVersion = package.Version,
            DownloadUrl = package.DownloadUrl,
        };
        if (package.BinarySha256.Length > 0)
            directive.BinarySha256 = Google.Protobuf.ByteString.CopyFrom(package.BinarySha256);

        await connection.Outbound.Writer.WriteAsync(
            new ControlMessage { UpdateDirective = directive }, ct);

        _logger.LogInformation("Update directive sent to {AgentId}: version {Version}",
            agentId, package.Version);
    }

    // ── Internal methods called by AgentHubService ──

    internal void RegisterAgent(AgentConnection connection)
    {
        _connections[connection.AgentId] = connection;
        _connectionSubject.OnNext(new AgentConnectionEvent(
            connection.AgentId, connection.Handshake.Hostname,
            AgentConnectionEventType.Connected, DateTime.UtcNow));
        _logger.LogInformation("Agent registered: {AgentId} ({Hostname})",
            connection.AgentId, connection.Handshake.Hostname);
    }

    internal void UnregisterAgent(string agentId)
    {
        if (_connections.TryRemove(agentId, out var connection))
        {
            foreach (var pending in connection.PendingCommands.Values)
                pending.TrySetCanceled();

            _connectionSubject.OnNext(new AgentConnectionEvent(
                agentId, connection.Handshake.Hostname,
                AgentConnectionEventType.Disconnected, DateTime.UtcNow));
        }
    }

    internal void UpdateHeartbeat(string agentId, Heartbeat heartbeat)
    {
        if (_connections.TryGetValue(agentId, out var connection))
        {
            connection.LastHeartbeatUtc = DateTime.UtcNow;
            _logger.LogDebug("Heartbeat from {AgentId}: uptime={Uptime}s cpu={Cpu:F1}%",
                agentId, heartbeat.UptimeSeconds, heartbeat.CpuPercent);
        }
    }

    internal void CompleteCommand(string agentId, CommandResponse response)
    {
        if (_connections.TryGetValue(agentId, out var connection)
            && connection.PendingCommands.TryRemove(response.RequestId, out var tcs))
        {
            tcs.TrySetResult(response);
            _logger.LogDebug("Command {RequestId} completed from {AgentId}: exit={ExitCode}",
                response.RequestId, agentId, response.ExitCode);
        }
        else
        {
            _logger.LogWarning("Received response for unknown command {RequestId} from {AgentId}",
                response.RequestId, agentId);
        }
    }

    public void Dispose()
    {
        _connectionSubject.OnCompleted();
        _connectionSubject.Dispose();
    }
}
