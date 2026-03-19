using System.Globalization;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Patching;

/// <summary>
/// Linux patch detection and application via apt/dnf package managers.
/// Uses apt on Debian-based systems (default). Parses machine-readable output.
/// </summary>
internal sealed class LinuxPatchStrategy : IPatchStrategy
{
    public OsType TargetOs => OsType.Linux;

    public string BuildDetectCommand() =>
        "apt list --upgradable 2>/dev/null | tail -n +2";

    public IReadOnlyList<PatchInfo> ParseDetectOutput(string stdout)
    {
        var patches = new List<PatchInfo>();
        if (string.IsNullOrWhiteSpace(stdout)) return patches;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: "package/suite version arch [upgradable from: old-version]"
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var nameAndSuite = parts[0].Split('/');
            var patchId = nameAndSuite[0];
            var version = parts[1];

            patches.Add(new PatchInfo(
                PatchId: patchId,
                Title: $"{patchId} {version}",
                Severity: ClassifySeverity(nameAndSuite.Length > 1 ? nameAndSuite[1] : ""),
                Category: PatchCategory.Security,
                Description: line.Trim(),
                SizeBytes: 0,
                RequiresReboot: patchId.Contains("linux-image", StringComparison.OrdinalIgnoreCase),
                PublishedUtc: DateTime.UtcNow));
        }

        return patches;
    }

    public string BuildApplyCommand(IReadOnlyList<PatchInfo> patches, PatchOptions options)
    {
        var patchList = string.Join(' ', patches.Select(p => SanitizePackageName(p.PatchId)));
        var dryRun = options.DryRun ? " --dry-run" : "";
        return $"DEBIAN_FRONTEND=noninteractive apt-get install -y{dryRun} {patchList}";
    }

    public PatchResult ParseApplyOutput(Guid machineId, string stdout, string stderr, int exitCode, TimeSpan duration)
    {
        // Simple parsing: if exit code is 0, all succeeded
        var outcomes = new List<PatchOutcome>();
        var success = exitCode == 0;

        return new PatchResult(
            MachineId: machineId,
            Successful: success ? 1 : 0,
            Failed: success ? 0 : 1,
            Outcomes: outcomes,
            RebootRequired: stdout.Contains("reboot", StringComparison.OrdinalIgnoreCase),
            Duration: duration);
    }

    public string BuildVerifyCommand(IReadOnlyList<string> patchIds)
    {
        var names = string.Join(' ', patchIds.Select(SanitizePackageName));
        return $"dpkg -l {names}";
    }

    public string BuildListInstalledCommand() =>
        "dpkg-query -W -f='${Package} ${Version} ${db:Status-Abbrev}\\n' | grep '^.i '";

    public IReadOnlyList<InstalledPatch> ParseInstalledOutput(string stdout)
    {
        var patches = new List<InstalledPatch>();
        if (string.IsNullOrWhiteSpace(stdout)) return patches;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                patches.Add(new InstalledPatch(parts[0], parts[0], DateTime.UtcNow));
            }
        }
        return patches;
    }

    private static PatchSeverity ClassifySeverity(string suite) =>
        suite.Contains("security", StringComparison.OrdinalIgnoreCase)
            ? PatchSeverity.Critical
            : PatchSeverity.Unclassified;

    /// <summary>
    /// Sanitize package name to prevent command injection — alphanumeric, hyphens, dots, colons, plus only.
    /// </summary>
    private static string SanitizePackageName(string name)
    {
        return new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.' or ':' or '+').ToArray());
    }
}
