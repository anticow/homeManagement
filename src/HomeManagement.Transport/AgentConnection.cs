using System.Threading.Channels;
using Grpc.Core;
using HomeManagement.Transport.Protocol;

namespace HomeManagement.Transport;

/// <summary>
/// Represents a single connected agent's bidirectional stream state.
/// </summary>
internal sealed class AgentConnection
{
    public string AgentId { get; }
    public Handshake Handshake { get; }
    public Channel<ControlMessage> Outbound { get; }
    public IServerStreamWriter<ControlMessage> ResponseStream { get; }
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Pending command completions keyed by request_id.
    /// When a command is sent to the agent, a TCS is stored here; when the
    /// CommandResponse arrives, the TCS is completed.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<CommandResponse>> PendingCommands { get; } = new();

    public AgentConnection(
        string agentId,
        Handshake handshake,
        Channel<ControlMessage> outbound,
        IServerStreamWriter<ControlMessage> responseStream)
    {
        AgentId = agentId;
        Handshake = handshake;
        Outbound = outbound;
        ResponseStream = responseStream;
    }
}
