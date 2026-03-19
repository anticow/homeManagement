using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Transport;

/// <summary>
/// Routes remote execution requests to the appropriate transport provider based on
/// the target machine's configured protocol. All calls flow through the resilience pipeline.
/// </summary>
internal sealed class RemoteExecutorRouter : IRemoteExecutor
{
    private readonly SshTransportProvider _sshProvider;
    private readonly WinRmTransportProvider _winRmProvider;
    private readonly AgentTransportProvider _agentProvider;
    private readonly IResiliencePipeline _resilience;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<RemoteExecutorRouter> _logger;

    public RemoteExecutorRouter(
        SshTransportProvider sshProvider,
        WinRmTransportProvider winRmProvider,
        AgentTransportProvider agentProvider,
        IResiliencePipeline resilience,
        ICorrelationContext correlation,
        ILogger<RemoteExecutorRouter> logger)
    {
        _sshProvider = sshProvider;
        _winRmProvider = winRmProvider;
        _agentProvider = agentProvider;
        _resilience = resilience;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task<RemoteResult> ExecuteAsync(MachineTarget target, RemoteCommand command, CancellationToken ct = default)
    {
        _logger.LogInformation("[{CorrelationId}] Executing command on {Host} via {Protocol}",
            _correlation.CorrelationId, target.Hostname, target.Protocol);

        return await _resilience.ExecuteAsync(
            target.Hostname.ToString(),
            async token => target.Protocol switch
            {
                TransportProtocol.Ssh => await _sshProvider.ExecuteAsync(target, command, token),
                TransportProtocol.WinRM or TransportProtocol.PSRemoting
                    => await _winRmProvider.ExecuteAsync(target, command, token),
                TransportProtocol.Agent => await _agentProvider.ExecuteAsync(target, command, token),
                _ => throw new ArgumentOutOfRangeException(nameof(target), $"Unknown protocol: {target.Protocol}")
            },
            ct);
    }

    public async Task TransferFileAsync(MachineTarget target, FileTransferRequest request,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        _logger.LogInformation("[{CorrelationId}] File transfer {Direction} on {Host} via {Protocol}",
            _correlation.CorrelationId, request.Direction, target.Hostname, target.Protocol);

        await _resilience.ExecuteAsync(
            target.Hostname.ToString(),
            async token =>
            {
                switch (target.Protocol)
                {
                    case TransportProtocol.Ssh:
                        await _sshProvider.TransferFileAsync(target, request, progress, token);
                        break;
                    default:
                        throw new NotSupportedException($"File transfer not supported via {target.Protocol}.");
                }
                return true; // Resilience pipeline requires a return value
            },
            ct);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(MachineTarget target, CancellationToken ct = default)
    {
        _logger.LogInformation("[{CorrelationId}] Testing connection to {Host} via {Protocol}",
            _correlation.CorrelationId, target.Hostname, target.Protocol);

        return target.Protocol switch
        {
            TransportProtocol.Ssh => await _sshProvider.TestConnectionAsync(target, ct),
            TransportProtocol.WinRM or TransportProtocol.PSRemoting
                => await _winRmProvider.TestConnectionAsync(target, ct),
            TransportProtocol.Agent => await _agentProvider.TestConnectionAsync(target, ct),
            _ => new ConnectionTestResult(
                Reachable: false, DetectedOs: null, OsVersion: null,
                Latency: TimeSpan.Zero, ProtocolVersion: null,
                ErrorMessage: $"Connection test not implemented for {target.Protocol}.")
        };
    }
}
