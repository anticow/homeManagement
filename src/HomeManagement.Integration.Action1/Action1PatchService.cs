using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Integration.Action1.Models;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Implements <see cref="IPatchService"/> by delegating all patching operations to
/// the Action1 RMM platform via <see cref="Action1Client"/>.
///
/// Machine identity mapping: homeManagement uses <see cref="MachineTarget.MachineId"/> (Guid).
/// Action1 uses a string endpoint ID. The mapping is stored in the machine record's
/// ExternalId field (see InventoryService / Machine model).
///
/// Workflow:
///   DetectAsync  → ListEndpoints + GetAvailablePatches
///   ApplyAsync   → CreateDeployment + PollUntilComplete
///   VerifyAsync  → GetDeployment results check
///   GetHistoryAsync → local DB (PatchHistoryRepository)
///   GetInstalledAsync → Action1 software inventory
/// </summary>
internal sealed class Action1PatchService : IPatchService
{
    private readonly Action1Client _client;
    private readonly IPatchHistoryRepository _historyRepo;
    private readonly IInventoryService _inventory;
    private readonly ILogger<Action1PatchService> _logger;

    // Timeout waiting for a deployment to complete (30 minutes max).
    private static readonly TimeSpan DeploymentTimeout = TimeSpan.FromMinutes(30);

    public Action1PatchService(
        Action1Client client,
        IPatchHistoryRepository historyRepo,
        IInventoryService inventory,
        ILogger<Action1PatchService> logger)
    {
        _client = client;
        _historyRepo = historyRepo;
        _inventory = inventory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PatchInfo>> DetectAsync(
        MachineTarget target, CancellationToken ct = default)
    {
        var endpointId = await ResolveAction1EndpointIdAsync(target, ct);
        if (endpointId is null) return [];

        var patches = await _client.GetAvailablePatchesAsync(endpointId, ct);
        _logger.LogInformation("Action1: detected {Count} available patches on {Host}",
            patches.Count, target.Hostname);

        return patches.Select(MapPatchInfo).ToList().AsReadOnly();
    }

    public async IAsyncEnumerable<PatchInfo> DetectStreamAsync(
        MachineTarget target,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var patches = await DetectAsync(target, ct);
        foreach (var patch in patches)
        {
            ct.ThrowIfCancellationRequested();
            yield return patch;
        }
    }

    public async Task<PatchResult> ApplyAsync(
        MachineTarget target,
        IReadOnlyList<PatchInfo> patches,
        PatchOptions options,
        CancellationToken ct = default)
    {
        if (options.DryRun)
        {
            _logger.LogInformation("Action1: dry-run requested for {Host} — returning detected patches without deploying",
                target.Hostname);
            // Action1 does not support dry-run; simulate by returning all as "Staged".
            return new PatchResult(
                MachineId: target.MachineId,
                Successful: 0,
                Failed: 0,
                Outcomes: patches.Select(p => new PatchOutcome(p.PatchId, PatchInstallState.Staged, null)).ToList(),
                RebootRequired: patches.Any(p => p.RequiresReboot),
                Duration: TimeSpan.Zero);
        }

        var endpointId = await ResolveAction1EndpointIdAsync(target, ct);
        if (endpointId is null)
            return EmptyFailedResult(target.MachineId, patches);

        var started = DateTime.UtcNow;
        var deploymentId = await _client.CreateDeploymentAsync(
            endpointId, patches.Select(p => p.PatchId).ToList(), options.AllowReboot, ct);

        if (deploymentId is null)
        {
            _logger.LogError("Action1: failed to create deployment for {Host}", target.Hostname);
            return EmptyFailedResult(target.MachineId, patches);
        }

        _logger.LogInformation("Action1: deployment {DeploymentId} created for {Host}, polling for completion",
            deploymentId, target.Hostname);

        var deployment = await _client.PollDeploymentUntilCompleteAsync(deploymentId, DeploymentTimeout, ct);
        var duration = DateTime.UtcNow - started;

        if (deployment is null)
            return EmptyFailedResult(target.MachineId, patches);

        var outcomes = MapOutcomes(deployment.Results);
        var result = new PatchResult(
            MachineId: target.MachineId,
            Successful: outcomes.Count(o => o.State == PatchInstallState.Installed),
            Failed: outcomes.Count(o => o.State == PatchInstallState.Failed),
            Outcomes: outcomes,
            RebootRequired: deployment.Results.Any(r => r.RebootRequired),
            Duration: duration);

        await PersistHistoryAsync(target.MachineId, deployment.Results, ct);

        return result;
    }

    public async Task<PatchResult> VerifyAsync(
        MachineTarget target,
        IReadOnlyList<string> patchIds,
        CancellationToken ct = default)
    {
        // Verification via Action1: check if the patchIds are no longer in available patches.
        var available = await DetectAsync(target, ct);
        var stillAvailable = available.Select(p => p.PatchId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outcomes = patchIds
            .Select(id => new PatchOutcome(
                id,
                stillAvailable.Contains(id) ? PatchInstallState.Failed : PatchInstallState.Installed,
                null))
            .ToList();

        return new PatchResult(
            MachineId: target.MachineId,
            Successful: outcomes.Count(o => o.State == PatchInstallState.Installed),
            Failed: outcomes.Count(o => o.State == PatchInstallState.Failed),
            Outcomes: outcomes,
            RebootRequired: false,
            Duration: TimeSpan.Zero);
    }

    public Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(
        Guid machineId, CancellationToken ct = default) =>
        _historyRepo.GetByMachineAsync(machineId, ct);

    public async Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(
        MachineTarget target, CancellationToken ct = default)
    {
        var endpointId = await ResolveAction1EndpointIdAsync(target, ct);
        if (endpointId is null) return [];

        var software = await _client.GetSoftwareInventoryAsync(endpointId, ct);
        return software
            .Select(s => new InstalledPatch(s.Name, s.Name, s.InstalledUtc ?? DateTime.MinValue))
            .ToList()
            .AsReadOnly();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the Action1 endpoint ID for a homeManagement machine.
    /// Uses the machine's ExternalId field if set; falls back to hostname match.
    /// </summary>
    private async Task<string?> ResolveAction1EndpointIdAsync(MachineTarget target, CancellationToken ct)
    {
        var machine = await _inventory.GetAsync(target.MachineId, ct);
        if (machine is null)
        {
            _logger.LogWarning("Action1: machine {MachineId} not found in inventory", target.MachineId);
            return null;
        }

        // Action1 endpoint ID is stored as a machine tag for zero-schema-change mapping.
        if (machine.Tags.TryGetValue("action1:endpoint_id", out var taggedId) &&
            !string.IsNullOrEmpty(taggedId))
            return taggedId;

        // Fallback: match by hostname in Action1 endpoint list
        var endpoints = await _client.ListEndpointsAsync(ct);
        var match = endpoints.FirstOrDefault(e =>
            e.Name.Equals(target.Hostname.Value, StringComparison.OrdinalIgnoreCase) ||
            e.IpAddress == target.Hostname.Value);

        if (match is null)
            _logger.LogWarning("Action1: no endpoint found matching hostname {Hostname}", target.Hostname);

        return match?.Id;
    }

    private static PatchInfo MapPatchInfo(Action1Patch p) => new(
        PatchId: p.Id,
        Title: p.Title,
        Severity: MapSeverity(p.Severity),
        Category: MapCategory(p.Category),
        Description: p.Description,
        SizeBytes: p.SizeBytes,
        RequiresReboot: p.RequiresReboot,
        PublishedUtc: p.PublishedUtc);

    private static System.Collections.ObjectModel.ReadOnlyCollection<PatchOutcome> MapOutcomes(IReadOnlyList<Action1DeploymentResult> results) =>
        results.Select(r => new PatchOutcome(
            r.PatchId,
            r.Status switch
            {
                "Installed" => PatchInstallState.Installed,
                "Skipped" => PatchInstallState.Deferred,
                _ => PatchInstallState.Failed
            },
            r.ErrorMessage)).ToList().AsReadOnly();

    private async Task PersistHistoryAsync(
        Guid machineId,
        IReadOnlyList<Action1DeploymentResult> results,
        CancellationToken ct)
    {
        foreach (var r in results)
        {
            var state = r.Status == "Installed" ? PatchInstallState.Installed : PatchInstallState.Failed;
            await _historyRepo.AddAsync(new PatchHistoryEntry(
                Id: Guid.NewGuid(),
                MachineId: machineId,
                PatchId: r.PatchId,
                Title: r.Title,
                State: state,
                TimestampUtc: DateTime.UtcNow,
                ErrorMessage: r.ErrorMessage), ct);
        }
        await _historyRepo.SaveChangesAsync(ct);
    }

    private static PatchResult EmptyFailedResult(Guid machineId, IReadOnlyList<PatchInfo> patches) =>
        new(machineId, 0, patches.Count,
            patches.Select(p => new PatchOutcome(p.PatchId, PatchInstallState.Failed, "Action1 deployment failed"))
                   .ToList().AsReadOnly(),
            false, TimeSpan.Zero);

    private static PatchSeverity MapSeverity(string s) => s switch
    {
        "Critical" => PatchSeverity.Critical,
        "Important" => PatchSeverity.Important,
        "Moderate" => PatchSeverity.Moderate,
        "Low" => PatchSeverity.Low,
        _ => PatchSeverity.Unclassified
    };

    private static PatchCategory MapCategory(string c) => c switch
    {
        "Security" => PatchCategory.Security,
        "Driver" => PatchCategory.Driver,
        "FeaturePack" => PatchCategory.Feature,
        _ => PatchCategory.Other
    };
}
