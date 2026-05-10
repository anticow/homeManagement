using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Services;

/// <summary>
/// Retrieves a process snapshot from a managed endpoint by dispatching a
/// "ProcessList" command to the HomeManagement agent via <see cref="IRemoteExecutor"/>.
/// Only works for machines connected via the Agent transport protocol.
/// Returns an empty list for SSH/WinRM targets (unsupported; use remote shell commands directly).
/// </summary>
internal sealed class RemoteProcessListService : IProcessListService
{
    private static readonly JsonSerializerOptions DeserializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IRemoteExecutor _executor;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<RemoteProcessListService> _logger;

    public RemoteProcessListService(
        IRemoteExecutor executor,
        ICorrelationContext correlation,
        ILogger<RemoteProcessListService> logger)
    {
        _executor = executor;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ProcessInfo>> ListAsync(MachineTarget target, CancellationToken ct = default)
    {
        if (target.Protocol != TransportProtocol.Agent)
        {
            _logger.LogDebug(
                "[{CorrelationId}] ProcessList skipped for {Host}: protocol {Protocol} is not Agent",
                _correlation.CorrelationId, target.Hostname, target.Protocol);
            return [];
        }

        _logger.LogInformation("[{CorrelationId}] ProcessList requested for {Host}",
            _correlation.CorrelationId, target.Hostname);

        var command = new RemoteCommand(
            CommandText: string.Empty,
            Timeout: TimeSpan.FromSeconds(30),
            CommandType: "ProcessList");
        var result = await _executor.ExecuteAsync(target, command, ct);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("[{CorrelationId}] ProcessList failed on {Host}: {Stderr}",
                _correlation.CorrelationId, target.Hostname, result.Stderr);
            return [];
        }

        try
        {
            var snapshots = JsonSerializer.Deserialize<List<ProcessSnapshot>>(result.Stdout, DeserializerOptions);
            if (snapshots is null) return [];

            return snapshots.Select(s => new ProcessInfo(
                ProcessId: s.ProcessId,
                ProcessName: s.ProcessName,
                WorkingSetBytes: s.WorkingSetBytes,
                Status: s.Status switch
                {
                    "Running" => ProcessStatus.Running,
                    "Sleeping" => ProcessStatus.Sleeping,
                    "Stopped" => ProcessStatus.Stopped,
                    _ => ProcessStatus.Unknown
                })).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[{CorrelationId}] Failed to parse ProcessList response from {Host}",
                _correlation.CorrelationId, target.Hostname);
            return [];
        }
    }

    private sealed record ProcessSnapshot(int ProcessId, string ProcessName, long WorkingSetBytes, string Status);
}
