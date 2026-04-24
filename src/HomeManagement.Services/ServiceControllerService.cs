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
///
/// State reads (GetStatusAsync, ListServicesAsync) use <see cref="IEndpointStateProvider"/>
/// (typically Prometheus) when available and fall back to direct remote execution when
/// the provider returns no data or is not registered.
/// Control operations (ControlAsync) always use direct remote execution.
/// </summary>
internal sealed class ServiceControllerService : IServiceController
{
    private readonly IRemoteExecutor _executor;
    private readonly IServiceSnapshotRepository _snapshotRepo;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<ServiceControllerService> _logger;
    private readonly LinuxServiceStrategy _linuxStrategy;
    private readonly WindowsServiceStrategy _windowsStrategy;
    private readonly IEndpointStateProvider? _stateProvider;

    public ServiceControllerService(
        IRemoteExecutor executor,
        IServiceSnapshotRepository snapshotRepo,
        ICorrelationContext correlation,
        ILogger<ServiceControllerService> logger,
        LinuxServiceStrategy linuxStrategy,
        WindowsServiceStrategy windowsStrategy,
        IEndpointStateProvider? stateProvider = null)
    {
        _executor = executor;
        _snapshotRepo = snapshotRepo;
        _correlation = correlation;
        _logger = logger;
        _linuxStrategy = linuxStrategy;
        _windowsStrategy = windowsStrategy;
        _stateProvider = stateProvider;
    }

    public async Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default)
    {
        // Try Prometheus first — avoids a remote round-trip when data is current.
        if (_stateProvider is not null)
        {
            var state = await _stateProvider.GetServiceStateAsync(
                target.Hostname.Value, serviceName.Value, target.OsType, ct);

            if (state != ServiceState.Unknown)
            {
                _logger.LogDebug("[{CorrelationId}] Service state for {Service}@{Host} from Prometheus: {State}",
                    _correlation.CorrelationId, serviceName, target.Hostname, state);

                var info = new ServiceInfo(
                    serviceName,
                    serviceName.Value,  // DisplayName: best effort from Prometheus (no display name available)
                    state,
                    ServiceStartupType.Automatic,  // Startup type not available from Prometheus
                    ProcessId: null,
                    Uptime: null,
                    Dependencies: []);

                await RecordSnapshotAsync(target.MachineId, info, ct);
                return info;
            }

            _logger.LogDebug("[{CorrelationId}] Prometheus returned Unknown for {Service}@{Host}; falling back to remote exec",
                _correlation.CorrelationId, serviceName, target.Hostname);
        }

        // Fallback: direct remote command
        var strategy = GetStrategy(target.OsType);
        var command = new RemoteCommand(strategy.BuildStatusCommand(serviceName), TimeSpan.FromSeconds(30));
        var result = await _executor.ExecuteAsync(target, command, ct);

        var remoteInfo = strategy.ParseStatusOutput(result.Stdout, serviceName);
        await RecordSnapshotAsync(target.MachineId, remoteInfo, ct);
        return remoteInfo;
    }

    public async Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(
        MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default)
    {
        // Prometheus can return all service states without SSH/WinRM round-trip.
        // Falls back to remote if Prometheus returns nothing (endpoint not scraped yet).
        if (_stateProvider is not null)
        {
            // GetEndpointOnlineAsync returns false when the endpoint has no metrics —
            // only use Prometheus path when we know the endpoint is being scraped.
            var online = await _stateProvider.GetEndpointOnlineAsync(target.Hostname.Value, ct);
            if (!online)
            {
                _logger.LogDebug("[{CorrelationId}] Endpoint {Host} not in Prometheus; falling back to remote exec",
                    _correlation.CorrelationId, target.Hostname);
            }
            else
            {
                // For known-online endpoints, state reads come from Prometheus via GetStatusAsync calls.
                // Full service listing without name falls back to remote (PromQL all-services query
                // is expensive and requires a known filter to be useful).
                if (filter?.NamePattern is not null)
                {
                    var state = await _stateProvider.GetServiceStateAsync(
                        target.Hostname.Value, filter.NamePattern, target.OsType, ct);
                    if (state != ServiceState.Unknown)
                    {
                        if (filter.State.HasValue && state != filter.State.Value)
                            return [];

                        return [new ServiceInfo(
                            ServiceName.Create(filter.NamePattern),
                            filter.NamePattern,
                            state,
                            ServiceStartupType.Automatic,
                            ProcessId: null,
                            Uptime: null,
                            Dependencies: [])];
                    }
                }
            }
        }

        // Fallback: remote listing
        var remoteStrategy = GetStrategy(target.OsType);
        var remoteCommand = new RemoteCommand(remoteStrategy.BuildListCommand(filter), TimeSpan.FromSeconds(60));
        var remoteResult = await _executor.ExecuteAsync(target, remoteCommand, ct);

        if (remoteResult.ExitCode != 0)
        {
            _logger.LogWarning("[{CorrelationId}] Service listing failed on {Host}: {Stderr}",
                _correlation.CorrelationId, target.Hostname, remoteResult.Stderr);
            return [];
        }

        var services = remoteStrategy.ParseListOutput(remoteResult.Stdout);

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

        // Get resulting state — prefer Prometheus for fast post-action status read
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
