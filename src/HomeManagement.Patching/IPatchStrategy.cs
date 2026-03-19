using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Patching;

/// <summary>
/// OS-specific patch strategy contract used internally by <see cref="PatchService"/>.
/// </summary>
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
