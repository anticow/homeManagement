using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.AgentGateway.Host.Services;

public sealed class StandaloneAgentGatewayService : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentGatewaySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<StandaloneAgentGatewayService> _logger;

    public StandaloneAgentGatewayService(ILogger<StandaloneAgentGatewayService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ConnectedAgent> GetConnectedAgents()
    {
        return _sessions.Values
            .Select(session => new ConnectedAgent(
                session.AgentId,
                session.Handshake.Hostname,
                session.Handshake.AgentVersion,
                session.ConnectedUtc,
                session.LastHeartbeatUtc,
                DateTime.UtcNow - session.ConnectedUtc))
            .OrderBy(agent => agent.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public AgentMetadata GetMetadata(string agentId)
    {
        if (!_sessions.TryGetValue(agentId, out var session))
        {
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");
        }

        return new AgentMetadata(
            session.Handshake.AgentId,
            session.Handshake.Hostname,
            ParseOsType(session.Handshake.OsType),
            session.Handshake.OsVersion,
            new HardwareInfo(0, 0, [], session.Handshake.Architecture));
    }

    public async Task<RemoteResult> SendCommandAsync(string agentId, RemoteCommand command, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(agentId, out var session))
        {
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingCommands[requestId] = pending;

        try
        {
            var request = new CommandRequest
            {
                RequestId = requestId,
                CommandType = "Shell",
                CommandText = command.CommandText,
                TimeoutSeconds = (int)command.Timeout.TotalSeconds,
                ElevationMode = command.Elevation.ToString(),
                RunAsUser = command.RunAsUser ?? string.Empty
            };

            if (command.EnvironmentVariables is not null)
            {
                foreach (var (key, value) in command.EnvironmentVariables)
                {
                    request.Env[key] = value;
                }
            }

            await session.Outbound.Writer.WriteAsync(new ControlMessage { CommandRequest = request }, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(command.Timeout + TimeSpan.FromSeconds(5));
            using var _ = timeoutCts.Token.Register(() => pending.TrySetCanceled(timeoutCts.Token));

            var response = await pending.Task.WaitAsync(timeoutCts.Token);
            return new RemoteResult(
                response.ExitCode,
                response.Stdout,
                response.Stderr,
                TimeSpan.FromMilliseconds(response.DurationMs),
                response.TimedOut);
        }
        finally
        {
            session.PendingCommands.TryRemove(requestId, out _);
        }
    }

    public async Task RequestUpdateAsync(string agentId, AgentUpdatePackage package, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(agentId, out var session))
        {
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");
        }

        var directive = new UpdateDirective
        {
            TargetVersion = package.Version,
            DownloadUrl = package.DownloadUrl
        };

        if (package.BinarySha256.Length > 0)
        {
            directive.BinarySha256 = Google.Protobuf.ByteString.CopyFrom(package.BinarySha256);
        }

        await session.Outbound.Writer.WriteAsync(new ControlMessage { UpdateDirective = directive }, ct);

        _logger.LogInformation("Update directive sent to {AgentId}: version {Version}", agentId, package.Version);
    }

    internal AgentGatewaySession RegisterAgent(Handshake handshake, IServerStreamWriter<ControlMessage> responseStream)
    {
        var outbound = Channel.CreateBounded<ControlMessage>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var session = new AgentGatewaySession(handshake.AgentId, handshake, outbound, responseStream);
        _sessions[session.AgentId] = session;

        _logger.LogInformation(
            "Agent registered: {AgentId} ({Hostname}) version {Version}",
            session.AgentId,
            session.Handshake.Hostname,
            session.Handshake.AgentVersion);

        return session;
    }

    internal void UnregisterAgent(string agentId)
    {
        if (_sessions.TryRemove(agentId, out var session))
        {
            session.Outbound.Writer.TryComplete();

            foreach (var pending in session.PendingCommands.Values)
            {
                pending.TrySetCanceled();
            }

            _logger.LogInformation("Agent unregistered: {AgentId} ({Hostname})", agentId, session.Handshake.Hostname);
        }
    }

    internal void UpdateHeartbeat(string agentId, Heartbeat heartbeat)
    {
        if (_sessions.TryGetValue(agentId, out var session))
        {
            session.LastHeartbeatUtc = DateTime.UtcNow;
            _logger.LogDebug(
                "Heartbeat from {AgentId}: uptime={UptimeSeconds}s cpu={CpuPercent:F1}%",
                agentId,
                heartbeat.UptimeSeconds,
                heartbeat.CpuPercent);
        }
    }

    internal void CompleteCommand(string agentId, CommandResponse response)
    {
        if (_sessions.TryGetValue(agentId, out var session)
            && session.PendingCommands.TryRemove(response.RequestId, out var pending))
        {
            pending.TrySetResult(response);
            _logger.LogDebug(
                "Command {RequestId} completed from {AgentId}: exit={ExitCode}",
                response.RequestId,
                agentId,
                response.ExitCode);
            return;
        }

        _logger.LogWarning(
            "Received response for unknown command {RequestId} from {AgentId}",
            response.RequestId,
            agentId);
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Outbound.Writer.TryComplete();
            foreach (var pending in session.PendingCommands.Values)
            {
                pending.TrySetCanceled();
            }
        }

        _sessions.Clear();
    }

    private static OsType ParseOsType(string osType)
    {
        return osType.Equals(nameof(OsType.Windows), StringComparison.OrdinalIgnoreCase)
            ? OsType.Windows
            : OsType.Linux;
    }
}
