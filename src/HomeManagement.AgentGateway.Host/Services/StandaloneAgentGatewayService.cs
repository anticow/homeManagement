using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.AgentGateway.Host.Services;

public sealed partial class StandaloneAgentGatewayService : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentGatewaySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<StandaloneAgentGatewayService> _logger;
    private readonly IRevokedAgentStore _revoked;
    private readonly object _sessionLock;
    private readonly AgentEnrollmentRegistry? _enrollment;
    private bool _disposed;

    public StandaloneAgentGatewayService(ILogger<StandaloneAgentGatewayService> logger, IRevokedAgentStore revoked)
        : this(logger, revoked, null)
    {
    }

    internal StandaloneAgentGatewayService(ILogger<StandaloneAgentGatewayService> logger,
        IRevokedAgentStore revoked, AgentEnrollmentRegistry? enrollment)
    {
        _logger = logger;
        _revoked = revoked;
        _enrollment = enrollment;
        _sessionLock = enrollment?.SyncRoot ?? new object();
        if (enrollment is not null) enrollment.Changed += FenceInvalidSessions;
    }

    public IReadOnlyList<ConnectedAgent> GetConnectedAgents()
    {
        lock (_sessionLock)
        {
            return _sessions.Values.Where(IsCurrent)
                .Select(session => new ConnectedAgent(
                    session.AgentId,
                    session.Handshake.Hostname,
                    session.Handshake.AgentVersion,
                    session.ConnectedUtc,
                    session.LastHeartbeatUtc,
                    DateTime.UtcNow - session.ConnectedUtc,
                    session.LastReceivedHeartbeatUtc))
                .OrderBy(agent => agent.Hostname, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public AgentMetadata GetMetadata(string agentId)
    {
        lock (_sessionLock)
        {
            var session = RequireSession(agentId);

            return new AgentMetadata(
                session.Handshake.AgentId,
                session.Handshake.Hostname,
                ParseOsType(session.Handshake.OsType),
                session.Handshake.OsVersion,
                new HardwareInfo(0, 0, [], session.Handshake.Architecture));
        }
    }

    public Task<RemoteResult> SendCommandAsync(string agentId, RemoteCommand command, CancellationToken ct = default) =>
        SendCommandCoreAsync(agentId, null, command, ct);

    private async Task<RemoteResult> SendCommandCoreAsync(string? agentId, Guid? machineId, RemoteCommand command, CancellationToken ct)
    {
        var timeoutSeconds = AgentRuntimeRequestValidation.ValidateCommand(command);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 5));
        AgentGatewaySession session;
        MachineAgentBinding? binding;
        var requestId = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sessionLock)
        {
            binding = machineId is { } id ? CaptureBinding(id) : null;
            session = binding?.Session ?? RequireSession(agentId!);
            if (command.CommandType.Equals("ManagedServices", StringComparison.OrdinalIgnoreCase)
                && (!session.IsEncrypted || !session.HasUniqueCredential
                    || (_enrollment is null ? !_revoked.HasDurableStorage : !_enrollment.IsHealthy)))
                throw new InvalidOperationException("Managed services require an encrypted agent session, a unique 32-byte credential and durable revocation storage.");
            session.PendingCommands[requestId] = new(pending, binding);
        }

        try
        {
            var request = new CommandRequest
            {
                RequestId = requestId,
                CommandType = command.CommandType,
                CommandText = command.CommandText,
                TimeoutSeconds = timeoutSeconds,
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

            await EnqueueAsync(session, new(new ControlMessage { CommandRequest = request }, binding), timeoutCts.Token);
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

    public Task RequestUpdateAsync(string agentId, AgentUpdatePackage package, CancellationToken ct = default) =>
        RequestUpdateCoreAsync(agentId, null, package, ct);

    private async Task RequestUpdateCoreAsync(string? agentId, Guid? machineId, AgentUpdatePackage package, CancellationToken ct)
    {
        AgentRuntimeRequestValidation.ValidateUpdate(package);
        AgentGatewaySession session;
        MachineAgentBinding? binding;
        lock (_sessionLock)
        {
            binding = machineId is { } id ? CaptureBinding(id) : null;
            session = binding?.Session ?? RequireSession(agentId!);
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

        await EnqueueAsync(session, new(new ControlMessage { UpdateDirective = directive }, binding), ct);

        _logger.LogInformation("Update directive queued for {AgentId}: version {Version}", session.AgentId, package.Version);
    }

    internal AgentGatewaySession RegisterAgent(Handshake handshake, IServerStreamWriter<ControlMessage> responseStream,
        bool isEncrypted = false, Action? abort = null, bool hasUniqueCredential = false)
    {
        if (_enrollment is not null)
            throw new InvalidOperationException("Managed enrollment requires authenticated session admission.");
        lock (_sessionLock)
            return RegisterAgentCore(handshake, responseStream, isEncrypted, abort, hasUniqueCredential, 0);
    }

    internal AgentGatewaySession AuthenticateAndRegister(IAgentApiKeyValidator validator, ServerCallContext context,
        Handshake handshake, IServerStreamWriter<ControlMessage> responseStream, bool encrypted, Action abort)
    {
        AgentGatewaySession? detached = null;
        try
        {
            lock (_sessionLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var generation = 0L;
                if (_enrollment is null)
                    validator.ValidateOrThrow(context, handshake);
                else
                {
                    try
                    {
                        generation = _enrollment.Authenticate(handshake.AgentId, handshake.Hostname,
                            context.RequestHeaders.GetValue("x-agent-api-key"), encrypted);
                    }
                    catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
                    {
                        _logger.LogWarning("Agent enrollment handshake rejected.");
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "Agent enrollment rejected."));
                    }
                }
                if (_sessions.TryGetValue(handshake.AgentId, out var existing) && !IsCurrent(existing))
                    detached = Detach(existing.AgentId, existing);
                return RegisterAgentCore(handshake, responseStream, encrypted, abort,
                    _enrollment is not null || validator.SupportsManagedServiceControl(handshake.AgentId), generation);
            }
        }
        finally
        {
            // The old session is already fenced. Its transport cannot revoke ownership
            // of the new admission by throwing after publication.
            if (detached is not null) AbortDetached([detached], throwOnFailure: false);
        }
    }

    internal void ValidateLegacyHandshake(IAgentApiKeyValidator validator, ServerCallContext context, Handshake handshake)
    {
        if (_enrollment is null) validator.ValidateOrThrow(context, handshake);
    }

    private AgentGatewaySession RegisterAgentCore(Handshake handshake, IServerStreamWriter<ControlMessage> responseStream,
        bool isEncrypted, Action? abort, bool hasUniqueCredential, long generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var outbound = Channel.CreateBounded<AgentGatewayOutbound>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        var id = _enrollment is null ? handshake.AgentId : AgentEnrollmentIdentity.AgentId(handshake.AgentId);
        var session = new AgentGatewaySession(id, handshake.Clone(), outbound, responseStream,
            isEncrypted, abort, hasUniqueCredential, generation);
        lock (_sessionLock)
        {
            if (_enrollment is null && _revoked.IsRevoked(session.AgentId))
                throw new InvalidOperationException("Agent peering has been revoked.");
            if (!_sessions.TryAdd(session.AgentId, session))
                throw new InvalidOperationException("An agent session with this identity is already connected.");
        }

        _logger.LogInformation(
            "Agent registered: {AgentId} ({Hostname}) version {Version}",
            session.AgentId,
            session.Handshake.Hostname,
            session.Handshake.AgentVersion);

        return session;
    }

    public void RevokeAgent(string agentId, string reason)
    {
        if (_enrollment is not null)
            throw new InvalidOperationException("Managed enrollment revocation requires an expected registry version.");
        AgentGatewaySession? detached = null;
        try
        {
            lock (_sessionLock)
            {
                try { _revoked.Revoke(agentId, reason); }
                finally { detached = Detach(agentId); }
            }
        }
        finally { if (detached is not null) AbortDetached([detached]); }
    }

    internal void UnregisterAgent(string agentId, AgentGatewaySession? expectedSession = null, bool abort = false)
    {
        AgentGatewaySession? detached;
        lock (_sessionLock) detached = Detach(agentId, expectedSession);
        if (abort && detached is not null) AbortDetached([detached]);
    }

    private AgentGatewaySession? Detach(string agentId, AgentGatewaySession? expectedSession = null)
    {
        if (_sessions.TryGetValue(agentId, out var session)
            && (expectedSession is null || ReferenceEquals(session, expectedSession))
            && _sessions.TryRemove(agentId, out _))
        {
            session.Outbound.Writer.TryComplete();
            foreach (var pending in session.PendingCommands.Values) pending.Completion.TrySetCanceled();
            _logger.LogInformation("Agent unregistered: {AgentId} ({Hostname})", agentId, session.Handshake.Hostname);
            return session;
        }
        return null;
    }

    internal void UpdateHeartbeat(AgentGatewaySession session, Heartbeat heartbeat)
    {
        lock (_sessionLock)
        {
            if (!IsCurrent(session)) throw new InvalidOperationException("Stale agent session.");
            session.LastHeartbeatUtc = DateTime.UtcNow;
            session.LastReceivedHeartbeatUtc = session.LastHeartbeatUtc;
            _logger.LogDebug(
                "Heartbeat from {AgentId}: uptime={UptimeSeconds}s cpu={CpuPercent:F1}%",
                session.AgentId,
                heartbeat.UptimeSeconds,
                heartbeat.CpuPercent);
        }

    }

    internal bool CanSend(AgentGatewaySession session)
    {
        lock (_sessionLock) return IsCurrent(session);
    }

    private AgentGatewaySession RequireSession(string agentId)
    {
        if (_sessions.TryGetValue(agentId, out var session) && IsCurrent(session)) return session;
        throw new InvalidOperationException("Agent is not connected or its peering is revoked.");
    }

    private bool IsCurrent(AgentGatewaySession session) =>
        !_disposed && _sessions.TryGetValue(session.AgentId, out var current) && ReferenceEquals(current, session)
        && (_enrollment is null ? !_revoked.IsRevoked(session.AgentId)
            : _enrollment.IsCurrent(session.AgentId, session.CredentialGeneration));

    private void FenceInvalidSessions()
    {
        List<AgentGatewaySession> detached = [];
        lock (_sessionLock)
        {
            foreach (var session in _sessions.Values.Where(s => !IsCurrent(s)))
                if (Detach(session.AgentId, session) is { } removed) detached.Add(removed);
        }
        AbortDetached(detached);
    }

    internal void CompleteCommand(AgentGatewaySession session, CommandResponse response)
    {
        lock (_sessionLock)
        {
            if (!IsCurrent(session)) throw new InvalidOperationException("Stale agent session.");
            if (session.PendingCommands.TryRemove(response.RequestId, out var pending))
            {
                if (pending.Binding is { } binding && !IsCurrent(binding))
                {
                    pending.Completion.TrySetCanceled();
                    throw new InvalidOperationException("Machine binding changed before command completion.");
                }
                pending.Completion.TrySetResult(response);
                _logger.LogDebug("Command {RequestId} completed from {AgentId}: exit={ExitCode}",
                    response.RequestId, session.AgentId, response.ExitCode);
                return;
            }
        }
        _logger.LogWarning("Received response for unknown command {RequestId} from {AgentId}",
            response.RequestId, session.AgentId);
    }

    public void Dispose()
    {
        List<AgentGatewaySession> detached = [];
        lock (_sessionLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _sessions.Values)
                if (Detach(session.AgentId, session) is { } removed) detached.Add(removed);
        }
        if (_enrollment is not null) _enrollment.Changed -= FenceInvalidSessions;
        AbortDetached(detached);
    }

    private void AbortDetached(IEnumerable<AgentGatewaySession> detached, bool throwOnFailure = true)
    {
        List<Exception> failures = [];
        foreach (var session in detached)
        {
            try { session.Abort?.Invoke(); }
            catch (Exception ex)
            {
                _logger.LogError("Failed to abort a detached agent transport; the session remains fenced.");
                failures.Add(ex);
            }
        }
        if (throwOnFailure && failures.Count > 0)
            throw new AggregateException("Agent transports could not all be aborted.", failures);
    }

    private static OsType ParseOsType(string osType)
    {
        return osType.Equals(nameof(OsType.Windows), StringComparison.OrdinalIgnoreCase)
            ? OsType.Windows
            : OsType.Linux;
    }
}
