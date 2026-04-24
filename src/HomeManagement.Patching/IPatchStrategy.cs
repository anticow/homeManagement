using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Patching;

/// <summary>
/// OS-specific patch strategy contract used internally by <see cref="PatchService"/>.
/// </summary>
/// <remarks>
/// DEPRECATED: This interface and its implementations are superseded by
/// <c>HomeManagement.Integration.Action1.Action1PatchService</c>.
/// Will be removed in a future release.
/// </remarks>
[Obsolete("IPatchStrategy is superseded by Action1PatchService. Do not create new implementations.")]
internal interface IPatchStrategy
{
    OsType TargetOs { get; }
    string BuildDetectCommand();
    IReadOnlyList<PatchInfo> ParseDetectOutput(string stdout);
    string BuildApplyCommand(IReadOnlyList<PatchInfo> patches, PatchOptions options);
    PatchResult ParseApplyOutput(Guid machineId, string stdout, string stderr, int exitCode, TimeSpan duration);
    string BuildVerifyCommand(IReadOnlyList<string> patchIds);
    string BuildListInstalledCommand();
    IReadOnlyList<InstalledPatch> ParseInstalledOutput(string stdout);
}
