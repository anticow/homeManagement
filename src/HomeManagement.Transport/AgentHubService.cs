using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Transport.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Transport;

/// <summary>
/// Server-side gRPC implementation of <see cref="AgentHub.AgentHubBase"/>.
/// Each agent opens a bidirectional stream via <see cref="Connect"/>. The server
/// processes handshakes/heartbeats, dispatches commands, and tracks connected agents
/// through the <see cref="IAgentGateway"/> interface.
/// </summary>
public sealed class AgentHubService : AgentHub.AgentHubBase
{
    private readonly AgentGatewayService _gateway;
    private readonly ILogger<AgentHubService> _logger;

    public AgentHubService(AgentGatewayService gateway, ILogger<AgentHubService> logger)
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
            // Wait for the first message — must be a handshake
            if (!await requestStream.MoveNext(context.CancellationToken))
                return;

            if (requestStream.Current.PayloadCase != AgentMessage.PayloadOneofCase.Handshake)
            {
                _logger.LogWarning("Expected handshake as first message, got {Case}", requestStream.Current.PayloadCase);
                return;
            }

            var handshake = requestStream.Current.Handshake;
            agentId = handshake.AgentId;

            _logger.LogInformation(
                "Agent connected: Id={AgentId} Host={Hostname} Version={Version} OS={Os}/{Arch}",
                agentId, handshake.Hostname, handshake.AgentVersion,
                handshake.OsType, handshake.Architecture);

            // Register the agent connection
            var outbound = Channel.CreateBounded<ControlMessage>(
                new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

            var connection = new AgentConnection(agentId, handshake, outbound, responseStream);
            _gateway.RegisterAgent(connection);

            // Send handshake ACK
            var ack = new ControlMessage
            {
                Ack = new Ack
                {
                    InResponseTo = "handshake",
                    ControllerVersion = GetControllerVersion()
                }
            };
            await responseStream.WriteAsync(ack);

            // Start outbound send loop (pumps queued commands to the agent)
            var sendTask = SendLoopAsync(outbound.Reader, responseStream, context.CancellationToken);

            // Process inbound messages (heartbeats, command responses)
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                switch (message.PayloadCase)
                {
                    case AgentMessage.PayloadOneofCase.Heartbeat:
                        _gateway.UpdateHeartbeat(agentId, message.Heartbeat);
                        await responseStream.WriteAsync(new ControlMessage
                        {
                            Ack = new Ack { InResponseTo = "heartbeat", ControllerVersion = GetControllerVersion() }
                        });
                        break;

                    case AgentMessage.PayloadOneofCase.CommandResponse:
                        _gateway.CompleteCommand(agentId, message.CommandResponse);
                        break;

                    default:
                        _logger.LogWarning("Unexpected message type from {AgentId}: {Case}",
                            agentId, message.PayloadCase);
                        break;
                }
            }

            // Wait for send loop to drain
            outbound.Writer.TryComplete();
            await sendTask;
        }
        catch (OperationCanceledException)
        {
            // Normal disconnection
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // Client disconnected
        }
        finally
        {
            if (agentId is not null)
            {
                _gateway.UnregisterAgent(agentId);
                _logger.LogInformation("Agent disconnected: {AgentId}", agentId);
            }
        }
    }

    private static async Task SendLoopAsync(
        ChannelReader<ControlMessage> reader,
        IServerStreamWriter<ControlMessage> stream,
        CancellationToken ct)
    {
        await foreach (var message in reader.ReadAllAsync(ct))
        {
            await stream.WriteAsync(message, ct);
        }
    }

    private static string GetControllerVersion() =>
        typeof(AgentHubService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
