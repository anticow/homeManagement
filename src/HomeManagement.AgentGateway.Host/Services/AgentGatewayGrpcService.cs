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
    private readonly IAgentApiKeyValidator _apiKeyValidator;
    private readonly StandaloneAgentGatewayService _gateway;
    private readonly ILogger<AgentGatewayGrpcService> _logger;

    public AgentGatewayGrpcService(
        IAgentApiKeyValidator apiKeyValidator,
        StandaloneAgentGatewayService gateway,
        ILogger<AgentGatewayGrpcService> logger)
    {
        _apiKeyValidator = apiKeyValidator;
        _gateway = gateway;
        _logger = logger;
    }

    public override async Task Connect(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControlMessage> responseStream,
        ServerCallContext context)
    {
        string? agentId = null;
        var isRegistered = false;
        AgentGatewaySession? registeredSession = null;
        Task? sendTask = null;

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

            _gateway.ValidateLegacyHandshake(_apiKeyValidator, context, handshake);
            var httpContext = context.GetHttpContext();
            var session = _gateway.AuthenticateAndRegister(_apiKeyValidator, context, handshake, responseStream,
                httpContext.Request.IsHttps, httpContext.Abort);
            registeredSession = session;
            isRegistered = true;

            _logger.LogInformation("Agent {AgentId} ({Hostname}) connected", agentId, handshake.Hostname);

            // Send ACK
            if (!_gateway.CanSend(session))
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Agent session no longer authorized."));
            await responseStream.WriteAsync(new ControlMessage
            {
                Ack = new Ack { InResponseTo = "handshake" }
            }, context.CancellationToken);

            sendTask = SendLoopAsync(session, session.Outbound.Reader, responseStream, httpContext.Abort, context.CancellationToken);

            // Process incoming messages
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                if (!_gateway.CanSend(session))
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "Agent session no longer authorized."));
                var msg = requestStream.Current;

                switch (msg.PayloadCase)
                {
                    case AgentMessage.PayloadOneofCase.Heartbeat:
                        _gateway.UpdateHeartbeat(session, msg.Heartbeat);
                        _logger.LogDebug("Heartbeat from {AgentId}", agentId);
                        break;

                    case AgentMessage.PayloadOneofCase.CommandResponse:
                        _gateway.CompleteCommand(session, msg.CommandResponse);
                        _logger.LogInformation("Command response from {AgentId}: request={RequestId}, exit={ExitCode}",
                            agentId, msg.CommandResponse.RequestId, msg.CommandResponse.ExitCode);
                        break;
                }
            }

            session.Outbound.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent {AgentId} stream cancelled", agentId ?? "unknown");
        }
        finally
        {
            if (isRegistered && agentId is not null)
            {
                _gateway.UnregisterAgent(agentId, registeredSession);
                _logger.LogInformation("Agent {AgentId} disconnected", agentId);
            }
            if (sendTask is not null)
            {
                try { await sendTask; }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
            }
        }
    }

    private async Task SendLoopAsync(
        AgentGatewaySession session,
        ChannelReader<AgentGatewayOutbound> reader,
        IServerStreamWriter<ControlMessage> responseStream,
        Action abort,
        CancellationToken ct)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(ct))
            {
                await _gateway.WriteOutboundAsync(session, message, responseStream, ct);
            }
        }
        catch (Exception ex) when (ex is RpcException or IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Agent {AgentId} send loop failed; aborting connection", session.AgentId);
            abort();
            throw;
        }
    }
}
