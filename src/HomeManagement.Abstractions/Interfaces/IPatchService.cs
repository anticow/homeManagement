using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Detects available patches on remote machines and applies them in a controlled, auditable manner.
/// </summary>
public interface IPatchService
{
    /// <summary>Detect all available patches (loaded in memory). Suitable for small-to-medium results.</summary>
    Task<IReadOnlyList<PatchInfo>> DetectAsync(MachineTarget target, CancellationToken ct = default);

    /// <summary>Stream detected patches for large result sets without loading all into memory.</summary>
    IAsyncEnumerable<PatchInfo> DetectStreamAsync(MachineTarget target, CancellationToken ct = default);

    Task<PatchResult> ApplyAsync(MachineTarget target, IReadOnlyList<PatchInfo> patches, PatchOptions options, CancellationToken ct = default);
    Task<PatchResult> VerifyAsync(MachineTarget target, IReadOnlyList<string> patchIds, CancellationToken ct = default);
    Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(Guid machineId, CancellationToken ct = default);
    Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(MachineTarget target, CancellationToken ct = default);
}
