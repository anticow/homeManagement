using System.Diagnostics;
using System.Runtime.CompilerServices;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Services;

/// <summary>
/// Manages system services on remote machines. Delegates OS-specific control commands
/// to strategy implementations and executes them via <see cref="IRemoteExecutor"/>.
/// </summary>
internal sealed class ServiceControllerService : IServiceController
{
    private readonly IRemoteExecutor _executor;
    private readonly IServiceSnapshotRepository _snapshotRepo;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<ServiceControllerService> _logger;
    private readonly LinuxServiceStrategy _linuxStrategy;
    private readonly WindowsServiceStrategy _windowsStrategy;

    public ServiceControllerService(
        IRemoteExecutor executor,
        IServiceSnapshotRepository snapshotRepo,
        ICorrelationContext correlation,
        ILogger<ServiceControllerService> logger,
        LinuxServiceStrategy linuxStrategy,
        WindowsServiceStrategy windowsStrategy)
    {
        _executor = executor;
        _snapshotRepo = snapshotRepo;
        _correlation = correlation;
        _logger = logger;
        _linuxStrategy = linuxStrategy;
        _windowsStrategy = windowsStrategy;
    }

    public async Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default)
    {
        var strategy = GetStrategy(target.OsType);
        var command = new RemoteCommand(strategy.BuildStatusCommand(serviceName), TimeSpan.FromSeconds(30));
        var result = await _executor.ExecuteAsync(target, command, ct);

        var info = strategy.ParseStatusOutput(result.Stdout, serviceName);

        // Snapshot for history
        await RecordSnapshotAsync(target.MachineId, info, ct);

        return info;
    }

    public async Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(
        MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default)
    {
        var strategy = GetStrategy(target.OsType);
        var command = new RemoteCommand(strategy.BuildListCommand(filter), TimeSpan.FromSeconds(60));
        var result = await _executor.ExecuteAsync(target, command, ct);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("[{CorrelationId}] Service listing failed on {Host}: {Stderr}",
                _correlation.CorrelationId, target.Hostname, result.Stderr);
            return [];
        }

        var services = strategy.ParseListOutput(result.Stdout);

        // Apply name pattern filter if specified
        if (filter?.NamePattern is not null)
        {
            services = services
                .Where(s => s.Name.ToString().Contains(filter.NamePattern, StringComparison.OrdinalIgnoreCase)
                         || s.DisplayName.Contains(filter.NamePattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return services;
    }

    public async IAsyncEnumerable<ServiceInfo> ListServicesStreamAsync(
        MachineTarget target, ServiceFilter? filter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var services = await ListServicesAsync(target, filter, ct);
        foreach (var svc in services)
        {
            ct.ThrowIfCancellationRequested();
            yield return svc;
        }
    }

    public async Task<ServiceActionResult> ControlAsync(
        MachineTarget target, ServiceName serviceName, ServiceAction action, CancellationToken ct = default)
    {
        var strategy = GetStrategy(target.OsType);

        _logger.LogInformation("[{CorrelationId}] {Action} service '{Service}' on {Host}",
            _correlation.CorrelationId, action, serviceName, target.Hostname);

        var sw = Stopwatch.StartNew();
        var command = new RemoteCommand(
            strategy.BuildControlCommand(serviceName, action),
            TimeSpan.FromSeconds(60),
            ElevationMode.Sudo);
        var result = await _executor.ExecuteAsync(target, command, ct);
        sw.Stop();

        // Get resulting state
        var postStatus = await GetStatusAsync(target, serviceName, ct);

        var actionResult = new ServiceActionResult(
            target.MachineId, serviceName, action,
            Success: result.ExitCode == 0,
            ResultingState: postStatus.State,
            ErrorMessage: result.ExitCode != 0 ? result.Stderr : null,
            Duration: sw.Elapsed);

        _logger.LogInformation("[{CorrelationId}] Service {Action} on {Host}/{Service}: {Success} → {State}",
            _correlation.CorrelationId, action, target.Hostname, serviceName,
            actionResult.Success, actionResult.ResultingState);

        return actionResult;
    }

    public async Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(
        IReadOnlyList<MachineTarget> targets, ServiceName serviceName, ServiceAction action, CancellationToken ct = default)
    {
        _logger.LogInformation("[{CorrelationId}] Bulk {Action} service '{Service}' across {Count} machines",
            _correlation.CorrelationId, action, serviceName, targets.Count);

        var tasks = targets.Select(target => ControlAsync(target, serviceName, action, ct));
        var results = await Task.WhenAll(tasks);
        return results;
    }

    private async Task RecordSnapshotAsync(Guid machineId, ServiceInfo info, CancellationToken ct)
    {
        var snapshot = new ServiceSnapshot(
            Id: Guid.NewGuid(),
            MachineId: machineId,
            ServiceName: info.Name.ToString(),
            DisplayName: info.DisplayName,
            State: info.State,
            StartupType: info.StartupType,
            ProcessId: info.ProcessId,
            CapturedUtc: DateTime.UtcNow);

        await _snapshotRepo.AddAsync(snapshot, ct);
        await _snapshotRepo.SaveChangesAsync(ct);
    }

    private IServiceStrategy GetStrategy(OsType os) => os switch
    {
        OsType.Linux => _linuxStrategy,
        OsType.Windows => _windowsStrategy,
        _ => throw new NotSupportedException($"Unsupported OS type: {os}")
    };
}
