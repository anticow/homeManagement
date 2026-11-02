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
        Channel<ControlMessage> outbound,
        IServerStreamWriter<ControlMessage> responseStream,
        bool isEncrypted,
        Action? abort,
        bool hasUniqueCredential)
    {
        AgentId = agentId;
        Handshake = handshake;
        Outbound = outbound;
        ResponseStream = responseStream;
        IsEncrypted = isEncrypted;
        Abort = abort;
        HasUniqueCredential = hasUniqueCredential;
    }

    public string AgentId { get; }

    public Handshake Handshake { get; }

    public Channel<ControlMessage> Outbound { get; }

    public IServerStreamWriter<ControlMessage> ResponseStream { get; }
    public bool IsEncrypted { get; }
    public Action? Abort { get; }
    public bool HasUniqueCredential { get; }

    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;

    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    public ConcurrentDictionary<string, TaskCompletionSource<CommandResponse>> PendingCommands { get; } = new();
}
