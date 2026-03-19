namespace HomeManagement.Abstractions.Models;

// ── Patching ──

public record PatchInfo(
    string PatchId,
    string Title,
    PatchSeverity Severity,
    PatchCategory Category,
    string Description,
    long SizeBytes,
    bool RequiresReboot,
    DateTime PublishedUtc);

public record PatchResult(
    Guid MachineId,
    int Successful,
    int Failed,
    IReadOnlyList<PatchOutcome> Outcomes,
    bool RebootRequired,
    TimeSpan Duration);

public record PatchOutcome(
    string PatchId,
    PatchInstallState State,
    string? ErrorMessage);

public record PatchOptions(
    bool AllowReboot = false,
    TimeSpan RebootDelay = default,
    bool DryRun = false,
    int MaxConcurrentMachines = 5);

public record PatchHistoryEntry(
    Guid Id,
    Guid MachineId,
    string PatchId,
    string Title,
    PatchInstallState State,
    DateTime TimestampUtc,
    string? ErrorMessage);

public record InstalledPatch(
    string PatchId,
    string Title,
    DateTime InstalledUtc);
