using System.Diagnostics;
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
    private readonly AgentCommandExecutionService _commandExecutionService;
    private readonly ILogger<AgentHostService> _logger;
    private readonly WindowsResourceLimits? _resourceLimits;

    // Thread-safe outbound message queue
    private readonly Channel<AgentMessage> _outbound = Channel.CreateBounded<AgentMessage>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    // Inbound command queue — decouples gRPC receive loop from command execution
    // so update directives, shutdowns, and acks are never blocked by slow commands.
    private readonly Channel<CommandRequest> _inboundCommands = Channel.CreateBounded<CommandRequest>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });

    public AgentHostService(
        IOptions<AgentConfiguration> config,
        GrpcChannelManager channelManager,
        CommandDispatcher dispatcher,
        ReconnectPolicy reconnectPolicy,
        ShutdownCoordinator shutdown,
        UpdateCommandHandler updateHandler,
        AgentCommandExecutionService commandExecutionService,
        ILogger<AgentHostService> logger,
        WindowsResourceLimits? resourceLimits = null)
    {
        _config = config.Value;
        _channelManager = channelManager;
        _dispatcher = dispatcher;
        _reconnectPolicy = reconnectPolicy;
        _shutdown = shutdown;
        _updateHandler = updateHandler;
        _commandExecutionService = commandExecutionService;
        _logger = logger;
        _resourceLimits = resourceLimits;
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
        await RunConnectionAsync(client, ct);
    }

    internal async Task RunConnectionAsync(AgentHub.AgentHubClient client, CancellationToken ct)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var headers = new Metadata();
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            headers.Add("x-agent-api-key", _config.ApiKey);
        }

        using var call = client.Connect(headers, cancellationToken: connectionCts.Token);

        // Send handshake
        var handshake = BuildHandshake();
        await call.RequestStream.WriteAsync(new AgentMessage { Handshake = handshake }, ct);
        _logger.LogInformation("Handshake sent: AgentId={AgentId}", _config.AgentId);

        Task[] tasks =
        [
            HeartbeatLoopAsync(connectionCts.Token),
            SendLoopAsync(call.RequestStream, connectionCts.Token),
            CommandProcessingLoopAsync(connectionCts.Token),
            ReceiveLoopAsync(call.ResponseStream, connectionCts.Token)
        ];
        try
        {
            await await Task.WhenAny(tasks);
        }
        finally
        {
            await connectionCts.CancelAsync();
            await Task.WhenAll(tasks.Select(task => SafeAwait(task, connectionCts.Token)));
        }
    }

    private async Task ReceiveLoopAsync(IAsyncStreamReader<ControlMessage> stream, CancellationToken ct)
    {
        await foreach (var message in stream.ReadAllAsync(ct))
        {
            _reconnectPolicy.Reset();
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
        await _commandExecutionService.ProcessAsync(_inboundCommands.Reader, _outbound.Writer, ct);
    }

    private async Task SendLoopAsync(IClientStreamWriter<AgentMessage> stream, CancellationToken ct)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(ct))
        {
            await stream.WriteAsync(message, ct);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds);
        using var process = Process.GetCurrentProcess();
        var started = process.StartTime.ToUniversalTime();
        var processorCount = OperatingSystem.IsWindows() && _resourceLimits is not null
            ? _resourceLimits.ProcessorCount
            : Environment.ProcessorCount;
        var previousCpu = GetCpuTime(process);
        var previousTimestamp = Stopwatch.GetTimestamp();

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);

            process.Refresh();
            var cpu = GetCpuTime(process);
            var timestamp = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, timestamp);
            var heartbeat = new Heartbeat
            {
                AgentId = _config.AgentId,
                UptimeSeconds = (long)(DateTime.UtcNow - started).TotalSeconds,
                CpuPercent = (cpu - previousCpu).TotalSeconds / elapsed.TotalSeconds / processorCount * 100,
                MemoryUsedBytes = process.WorkingSet64,
                MemoryTotalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                TimestampUtc = DateTime.UtcNow.ToString("O")
            };
            previousCpu = cpu;
            previousTimestamp = timestamp;

            try
            {
                // Route through outbound channel so SendLoopAsync is the sole writer to the gRPC stream.
                // IClientStreamWriter is not thread-safe; concurrent writes cause undefined behaviour.
                await _outbound.Writer.WriteAsync(new AgentMessage { Heartbeat = heartbeat }, ct);
                _logger.LogDebug("Heartbeat queued");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
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

    private TimeSpan GetCpuTime(Process process) =>
        OperatingSystem.IsWindows() && _resourceLimits is not null
            ? _resourceLimits.GetTotalProcessorTime()
            : process.TotalProcessorTime;

    private static async Task SafeAwait(Task task, CancellationToken ct)
    {
        try { await task; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (RpcException ex) when (ct.IsCancellationRequested && ex.StatusCode == StatusCode.Cancelled) { }
    }
}
