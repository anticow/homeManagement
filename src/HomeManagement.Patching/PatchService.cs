using System.Runtime.CompilerServices;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Patching;

/// <summary>
/// Detects, applies, and tracks patches on remote machines.
/// Delegates OS-specific commands to <see cref="IPatchStrategy"/> implementations
/// and executes them via <see cref="IRemoteExecutor"/>.
/// </summary>
/// <remarks>
/// DEPRECATED: Use <c>HomeManagement.Integration.Action1.Action1PatchService</c> instead.
/// This class is retained for test backward-compatibility only and will be removed in a future release.
/// </remarks>
[System.Obsolete("PatchService is superseded by Action1PatchService and is no longer registered in DI. Use Action1PatchService via IPatchService.")]
internal sealed class PatchService : IPatchService
{
    private readonly IRemoteExecutor _executor;
    private readonly IPatchHistoryRepository _historyRepo;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<PatchService> _logger;
    private readonly LinuxPatchStrategy _linuxStrategy;
    private readonly WindowsPatchStrategy _windowsStrategy;

    public PatchService(
        IRemoteExecutor executor,
        IPatchHistoryRepository historyRepo,
        ICorrelationContext correlation,
        ILogger<PatchService> logger,
        LinuxPatchStrategy linuxStrategy,
        WindowsPatchStrategy windowsStrategy)
    {
        _executor = executor;
        _historyRepo = historyRepo;
        _correlation = correlation;
        _logger = logger;
        _linuxStrategy = linuxStrategy;
        _windowsStrategy = windowsStrategy;
    }

    public async Task<IReadOnlyList<PatchInfo>> DetectAsync(MachineTarget target, CancellationToken ct = default)
    {
        var strategy = GetStrategy(target.OsType);

        _logger.LogInformation("[{CorrelationId}] Patch scan starting for {Host}",
            _correlation.CorrelationId, target.Hostname);

        var command = new RemoteCommand(strategy.BuildDetectCommand(), TimeSpan.FromMinutes(5));
        var result = await _executor.ExecuteAsync(target, command, ct);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("[{CorrelationId}] Patch scan failed on {Host}: {Stderr}",
                _correlation.CorrelationId, target.Hostname, result.Stderr);
            return [];
        }

        var patches = strategy.ParseDetectOutput(result.Stdout);
        _logger.LogInformation("[{CorrelationId}] Patch scan found {Count} available patches on {Host}",
            _correlation.CorrelationId, patches.Count, target.Hostname);

        return patches;
    }

    public async IAsyncEnumerable<PatchInfo> DetectStreamAsync(
        MachineTarget target, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var patches = await DetectAsync(target, ct);
        foreach (var patch in patches)
        {
            ct.ThrowIfCancellationRequested();
            yield return patch;
        }
    }

    public async Task<PatchResult> ApplyAsync(MachineTarget target, IReadOnlyList<PatchInfo> patches,
        PatchOptions options, CancellationToken ct = default)
    {
        var strategy = GetStrategy(target.OsType);

        _logger.LogInformation("[{CorrelationId}] Applying {Count} patches to {Host} (DryRun={DryRun})",
            _correlation.CorrelationId, patches.Count, target.Hostname, options.DryRun);

        var command = new RemoteCommand(
            strategy.BuildApplyCommand(patches, options),
            TimeSpan.FromMinutes(30),
            ElevationMode.Sudo);
        var result = await _executor.ExecuteAsync(target, command, ct);
        var patchResult = strategy.ParseApplyOutput(target.MachineId, result.Stdout, result.Stderr, result.ExitCode, result.Duration);

        // Record history for each patch
        foreach (var patch in patches)
        {
            var state = result.ExitCode == 0 ? PatchInstallState.Installed : PatchInstallState.Failed;
            var entry = new PatchHistoryEntry(
                Id: Guid.NewGuid(),
                MachineId: target.MachineId,
                PatchId: patch.PatchId,
                Title: patch.Title,
                State: state,
                TimestampUtc: DateTime.UtcNow,
                ErrorMessage: result.ExitCode != 0 ? result.Stderr : null);

            await _historyRepo.AddAsync(entry, ct);
        }
        await _historyRepo.SaveChangesAsync(ct);

        _logger.LogInformation("[{CorrelationId}] Patch apply complete on {Host}: {Success} succeeded, {Failed} failed",
            _correlation.CorrelationId, target.Hostname, patchResult.Successful, patchResult.Failed);

        return patchResult;
    }

    public async Task<PatchResult> VerifyAsync(MachineTarget target, IReadOnlyList<string> patchIds, CancellationToken ct = default)
    {
        var strategy = GetStrategy(target.OsType);

        _logger.LogInformation("[{CorrelationId}] Verifying {Count} patches on {Host}",
            _correlation.CorrelationId, patchIds.Count, target.Hostname);

        var command = new RemoteCommand(strategy.BuildVerifyCommand(patchIds), TimeSpan.FromMinutes(2));
        var result = await _executor.ExecuteAsync(target, command, ct);

        return strategy.ParseApplyOutput(target.MachineId, result.Stdout, result.Stderr, result.ExitCode, result.Duration);
    }

    public async Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(Guid machineId, CancellationToken ct = default)
    {
        return await _historyRepo.GetByMachineAsync(machineId, ct);
    }

    public async Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(MachineTarget target, CancellationToken ct = default)
    {
        var strategy = GetStrategy(target.OsType);
        var command = new RemoteCommand(strategy.BuildListInstalledCommand(), TimeSpan.FromMinutes(2));
        var result = await _executor.ExecuteAsync(target, command, ct);

        return result.ExitCode == 0 ? strategy.ParseInstalledOutput(result.Stdout) : [];
    }

    private IPatchStrategy GetStrategy(OsType os) => os switch
    {
        OsType.Linux => _linuxStrategy,
        OsType.Windows => _windowsStrategy,
        _ => throw new NotSupportedException($"Unsupported OS type: {os}")
    };
}
