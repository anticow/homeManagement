using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using HomeManagement.Agent.Protocol;

namespace HomeManagement.AgentGateway.Host.Services;

internal sealed class AgentGatewaySession
{
    public AgentGatewaySession(
        string agentId,
        Handshake handshake,
        Channel<AgentGatewayOutbound> outbound,
        IServerStreamWriter<ControlMessage> responseStream,
        bool isEncrypted,
        Action? abort,
        bool hasUniqueCredential,
        long credentialGeneration = 0)
    {
        AgentId = agentId;
        Handshake = handshake;
        Outbound = outbound;
        ResponseStream = responseStream;
        IsEncrypted = isEncrypted;
        Abort = abort;
        HasUniqueCredential = hasUniqueCredential;
        CredentialGeneration = credentialGeneration;
    }

    public string AgentId { get; }

    public Handshake Handshake { get; }

    public Channel<AgentGatewayOutbound> Outbound { get; }
    public Guid SessionId { get; } = Guid.NewGuid();

    public IServerStreamWriter<ControlMessage> ResponseStream { get; }
    public bool IsEncrypted { get; }
    public Action? Abort { get; }
    public bool HasUniqueCredential { get; }
    public long CredentialGeneration { get; }

    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;

    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    public ConcurrentDictionary<string, PendingAgentCommand> PendingCommands { get; } = new();
}

internal sealed record MachineAgentBinding(Guid RegistryId, Guid MachineId, string AgentId,
    long Version, long Generation, AgentGatewaySession Session);
internal sealed record AgentGatewayOutbound(ControlMessage Message, MachineAgentBinding? Binding = null);
internal sealed record PendingAgentCommand(TaskCompletionSource<CommandResponse> Completion, MachineAgentBinding? Binding);
