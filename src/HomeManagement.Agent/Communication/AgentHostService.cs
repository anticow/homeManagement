using System.Reflection;
using System.Threading.Channels;
using Grpc.Core;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using HomeManagement.Agent.Resilience;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Agent.Communication;

/// <summary>
/// Core hosted service that manages the gRPC bidirectional stream lifecycle:
/// connect → handshake → command loop / heartbeat → reconnect on failure.
/// </summary>
public sealed class AgentHostService : BackgroundService
{
    private readonly AgentConfiguration _config;
    private readonly GrpcChannelManager _channelManager;
    private readonly CommandDispatcher _dispatcher;
    private readonly ReconnectPolicy _reconnectPolicy;
    private readonly ShutdownCoordinator _shutdown;
    private readonly UpdateCommandHandler _updateHandler;
    private readonly ILogger<AgentHostService> _logger;

    // Thread-safe outbound message queue
    private readonly Channel<AgentMessage> _outbound = Channel.CreateBounded<AgentMessage>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    // Inbound command queue — decouples gRPC receive loop from command execution
    // so update directives, shutdowns, and acks are never blocked by slow commands.
    private readonly Channel<CommandRequest> _inboundCommands = Channel.CreateBounded<CommandRequest>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });

    private readonly SemaphoreSlim _commandSemaphore;

    public AgentHostService(
        IOptions<AgentConfiguration> config,
        GrpcChannelManager channelManager,
        CommandDispatcher dispatcher,
        ReconnectPolicy reconnectPolicy,
        ShutdownCoordinator shutdown,
        UpdateCommandHandler updateHandler,
        ILogger<AgentHostService> logger)
    {
        _config = config.Value;
        _channelManager = channelManager;
        _dispatcher = dispatcher;
        _reconnectPolicy = reconnectPolicy;
        _shutdown = shutdown;
        _updateHandler = updateHandler;
        _logger = logger;
        _commandSemaphore = new SemaphoreSlim(config.Value.MaxConcurrentCommands);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent {AgentId} starting, version {Version}",
            _config.AgentId, GetAgentVersion());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Must not crash on transient failures
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "Connection loop failed, will reconnect");
            }

            if (stoppingToken.IsCancellationRequested || _shutdown.IsShutdownRequested) break;

            var delay = _reconnectPolicy.NextDelay();
            _logger.LogInformation("Reconnecting in {Delay:F1}s", delay.TotalSeconds);
            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("Agent shutting down");
    }

    private async Task RunConnectionLoopAsync(CancellationToken ct)
    {
        var channel = _channelManager.CreateChannel();
        var client = new AgentHub.AgentHubClient(channel);

        using var call = client.Connect(cancellationToken: ct);

        // Send handshake
        var handshake = BuildHandshake();
        await call.RequestStream.WriteAsync(new AgentMessage { Handshake = handshake }, ct);
        _logger.LogInformation("Handshake sent: AgentId={AgentId}", _config.AgentId);

        _reconnectPolicy.Reset();

        // Start heartbeat timer
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = HeartbeatLoopAsync(call.RequestStream, heartbeatCts.Token);
        var sendTask = SendLoopAsync(call.RequestStream, heartbeatCts.Token);
        var commandTask = CommandProcessingLoopAsync(heartbeatCts.Token);
        var receiveTask = ReceiveLoopAsync(call.ResponseStream, ct);

        // Wait for receive loop to end (stream closed or error)
        await receiveTask;

        // Cancel heartbeat, send, and command processing loops
        await heartbeatCts.CancelAsync();

        await Task.WhenAll(
            SafeAwait(heartbeatTask),
            SafeAwait(sendTask),
            SafeAwait(commandTask));
    }

    private async Task ReceiveLoopAsync(IAsyncStreamReader<ControlMessage> stream, CancellationToken ct)
    {
        await foreach (var message in stream.ReadAllAsync(ct))
        {
            switch (message.PayloadCase)
            {
                case ControlMessage.PayloadOneofCase.CommandRequest:
                    // Enqueue to the inbound command channel — never blocks the receive loop
                    await _inboundCommands.Writer.WriteAsync(message.CommandRequest, ct);
                    _logger.LogDebug("Command {RequestId} queued (depth={Depth})",
                        message.CommandRequest.RequestId,
                        _inboundCommands.Reader.Count);
                    break;

                case ControlMessage.PayloadOneofCase.UpdateDirective:
                    _logger.LogInformation("Received update directive: version {Version}",
                        message.UpdateDirective.TargetVersion);
                    _ = _updateHandler.HandleAsync(message.UpdateDirective, ct);
                    break;

                case ControlMessage.PayloadOneofCase.Shutdown:
                    _logger.LogInformation("Received shutdown directive: {Reason}", message.Shutdown.Reason);
                    // Fire-and-forget the coordinator; it will signal IHostApplicationLifetime.StopApplication()
                    _ = _shutdown.RequestShutdownAsync(
                        message.Shutdown.Reason, message.Shutdown.DelayMs, ct);
                    return; // Exit receive loop

                case ControlMessage.PayloadOneofCase.Ack:
                    _logger.LogDebug("Received ack for {InResponseTo}", message.Ack.InResponseTo);
                    break;

                default:
                    _logger.LogWarning("Received unknown message type: {Case}", message.PayloadCase);
                    break;
            }
        }
    }

    /// <summary>
    /// Drains the inbound command queue with bounded concurrency.
    /// Commands execute in parallel up to <see cref="AgentConfiguration.MaxConcurrentCommands"/>.
    /// </summary>
    private async Task CommandProcessingLoopAsync(CancellationToken ct)
    {
        await foreach (var request in _inboundCommands.Reader.ReadAllAsync(ct))
        {
            await HandleCommandAsync(request, ct);
        }
    }

    private async Task HandleCommandAsync(CommandRequest request, CancellationToken ct)
    {
        await _commandSemaphore.WaitAsync(ct);
        try
        {
            var response = await _dispatcher.DispatchAsync(request, ct);
            await _outbound.Writer.WriteAsync(new AgentMessage { CommandResponse = response }, ct);
        }
        finally
        {
            _commandSemaphore.Release();
        }
    }

    private async Task SendLoopAsync(IClientStreamWriter<AgentMessage> stream, CancellationToken ct)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(ct))
        {
            await stream.WriteAsync(message, ct);
        }
    }

    private async Task HeartbeatLoopAsync(IClientStreamWriter<AgentMessage> stream, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);

            var heartbeat = new Heartbeat
            {
                AgentId = _config.AgentId,
                UptimeSeconds = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                CpuPercent = 0, // Placeholder — real impl would read perf counters
                MemoryUsedBytes = GC.GetTotalMemory(forceFullCollection: false),
                MemoryTotalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                TimestampUtc = DateTime.UtcNow.ToString("O")
            };

            try
            {
                await stream.WriteAsync(new AgentMessage { Heartbeat = heartbeat }, ct);
                _logger.LogDebug("Heartbeat sent");
            }
            catch (InvalidOperationException)
            {
                // Stream completed — exit loop
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                break;
            }
        }
    }

    private Handshake BuildHandshake()
    {
        var platform = AgentPlatformDetector.Detect();
        return new Handshake
        {
            AgentId = _config.AgentId,
            Hostname = Environment.MachineName,
            AgentVersion = GetAgentVersion(),
            OsType = platform.OsType.ToString(),
            OsVersion = platform.OsDescription,
            Architecture = platform.Architecture,
            ProtocolVersion = 1
        };
    }

    private static string GetAgentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static async Task SafeAwait(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
