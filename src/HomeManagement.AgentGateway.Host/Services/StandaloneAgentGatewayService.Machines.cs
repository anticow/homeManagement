using Grpc.Core;
using HomeManagement.Abstractions.Models;
using HomeManagement.Agent.Protocol;

namespace HomeManagement.AgentGateway.Host.Services;

public sealed partial class StandaloneAgentGatewayService
{
    public Task<RemoteResult> SendMachineCommandAsync(Guid machineId, RemoteCommand command, CancellationToken ct = default) =>
        SendCommandCoreAsync(null, machineId, command, ct);

    public Task RequestMachineUpdateAsync(Guid machineId, AgentUpdatePackage package, CancellationToken ct = default) =>
        RequestUpdateCoreAsync(null, machineId, package, ct);

    public AgentMetadata GetMachineMetadata(Guid machineId)
    {
        lock (_sessionLock)
        {
            var binding = CaptureBinding(machineId);
            return GetMetadata(binding.AgentId);
        }
    }

    public MachineAgentObservation GetMachineObservation(Guid machineId)
    {
        lock (_sessionLock)
        {
            var registry = RequireRegistry();
            var metadata = registry.ForMachine(machineId);
            if (metadata.State == AgentEnrollmentState.Revoked)
                throw new InvalidOperationException("Machine enrollment is revoked.");
            var session = _sessions.GetValueOrDefault(metadata.AgentId);
            var connected = session is not null && IsCurrent(session);
            var currentSession = connected ? session : null;
            return new(registry.RegistryId, machineId, metadata.AgentId, metadata.Version, metadata.CredentialGeneration,
                currentSession?.SessionId, connected, DateTimeOffset.UtcNow, currentSession?.LastReceivedHeartbeatUtc);
        }
    }

    private AgentEnrollmentRegistry RequireRegistry() =>
        _enrollment ?? throw new InvalidOperationException("Machine-bound runtime requires managed enrollment.");

    private MachineAgentBinding CaptureBinding(Guid machineId)
    {
        if (machineId == Guid.Empty) throw new ArgumentException("A bound machine ID is required.");
        var registry = RequireRegistry();
        var metadata = registry.ForMachine(machineId);
        var session = RequireSession(metadata.AgentId);
        return new(registry.RegistryId, machineId, metadata.AgentId, metadata.Version, metadata.CredentialGeneration, session);
    }

    private bool IsCurrent(MachineAgentBinding binding)
    {
        if (_enrollment is null || !_enrollment.IsHealthy || !IsCurrent(binding.Session)) return false;
        var current = _enrollment.ForMachine(binding.MachineId);
        return _enrollment.RegistryId == binding.RegistryId && current.AgentId == binding.AgentId
            && current.Version == binding.Version && current.CredentialGeneration == binding.Generation;
    }

    private bool IsCurrent(AgentGatewaySession session, AgentGatewayOutbound outbound) =>
        IsCurrent(session) && (outbound.Binding is null || ReferenceEquals(outbound.Binding.Session, session) && IsCurrent(outbound.Binding));

    private async Task EnqueueAsync(AgentGatewaySession session, AgentGatewayOutbound outbound, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sessionLock)
            {
                if (!IsCurrent(session, outbound)) throw new InvalidOperationException("Stale machine binding or agent session.");
                if (session.Outbound.Writer.TryWrite(outbound)) return;
            }
            if (!await session.Outbound.Writer.WaitToWriteAsync(ct))
                throw new InvalidOperationException("Agent session closed before queue admission.");
        }
    }

    internal async Task WriteOutboundAsync(AgentGatewaySession session, AgentGatewayOutbound outbound,
        IServerStreamWriter<ControlMessage> writer, CancellationToken ct)
    {
        lock (_sessionLock)
            if (!IsCurrent(session, outbound))
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Machine binding or agent session changed."));
        // A revocation after this last check cannot recall a write already handed to the transport.
        await writer.WriteAsync(outbound.Message, ct);
    }
}
