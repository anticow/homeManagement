using System.Threading.Channels;
using Grpc.Core;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.AgentGateway.Host.Services;

/// <summary>
/// gRPC service implementation for agent bidirectional streaming.
/// Agents call Connect() and maintain a persistent stream.
/// </summary>
public sealed class AgentGatewayGrpcService : AgentHub.AgentHubBase
{
    private readonly StandaloneAgentGatewayService _gateway;
    private readonly ILogger<AgentGatewayGrpcService> _logger;

    public AgentGatewayGrpcService(StandaloneAgentGatewayService gateway, ILogger<AgentGatewayGrpcService> logger)
    {
        _gateway = gateway;
        _logger = logger;
    }

    public override async Task Connect(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControlMessage> responseStream,
        ServerCallContext context)
    {
        string? agentId = null;

        try
        {
            // First message must be a handshake
            if (!await requestStream.MoveNext(context.CancellationToken))
                return;

            var firstMsg = requestStream.Current;
            if (firstMsg.PayloadCase != AgentMessage.PayloadOneofCase.Handshake)
            {
                _logger.LogWarning("First message was not a handshake — disconnecting");
                return;
            }

            var handshake = firstMsg.Handshake;
            agentId = handshake.AgentId;

            var session = _gateway.RegisterAgent(handshake, responseStream);

            _logger.LogInformation("Agent {AgentId} ({Hostname}) connected", agentId, handshake.Hostname);

            // Send ACK
            await responseStream.WriteAsync(new ControlMessage
            {
                Ack = new Ack { InResponseTo = "handshake" }
            }, context.CancellationToken);

            var sendTask = SendLoopAsync(session.Outbound.Reader, responseStream, context.CancellationToken);

            // Process incoming messages
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                var msg = requestStream.Current;

                switch (msg.PayloadCase)
                {
                    case AgentMessage.PayloadOneofCase.Heartbeat:
                        _gateway.UpdateHeartbeat(agentId, msg.Heartbeat);
                        _logger.LogDebug("Heartbeat from {AgentId}", agentId);
                        break;

                    case AgentMessage.PayloadOneofCase.CommandResponse:
                        _gateway.CompleteCommand(agentId, msg.CommandResponse);
                        _logger.LogInformation("Command response from {AgentId}: request={RequestId}, exit={ExitCode}",
                            agentId, msg.CommandResponse.RequestId, msg.CommandResponse.ExitCode);
                        break;
                }
            }

            session.Outbound.Writer.TryComplete();
            await sendTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent {AgentId} stream cancelled", agentId ?? "unknown");
        }
        finally
        {
            if (agentId is not null)
            {
                _gateway.UnregisterAgent(agentId);
                _logger.LogInformation("Agent {AgentId} disconnected", agentId);
            }
        }
    }

    private static async Task SendLoopAsync(
        ChannelReader<ControlMessage> reader,
        IServerStreamWriter<ControlMessage> responseStream,
        CancellationToken ct)
    {
        await foreach (var message in reader.ReadAllAsync(ct))
        {
            await responseStream.WriteAsync(message, ct);
        }
    }
}
