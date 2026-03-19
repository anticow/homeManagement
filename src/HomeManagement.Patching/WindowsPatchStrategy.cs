using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Patching;

/// <summary>
/// Windows patch detection and application via PowerShell/Windows Update APIs.
/// Uses Get-WindowsUpdate commands (PSWindowsUpdate module) parsed in machine-readable format.
/// </summary>
internal sealed class WindowsPatchStrategy : IPatchStrategy
{
    public OsType TargetOs => OsType.Windows;

    public string BuildDetectCommand() =>
        "Get-WindowsUpdate -MicrosoftUpdate | Select-Object KB,Title,Size,MsrcSeverity,IsDownloaded | ConvertTo-Json -Compress";

    public IReadOnlyList<PatchInfo> ParseDetectOutput(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            // PowerShell ConvertTo-Json returns a single object if only one result, array otherwise
            if (root.ValueKind == JsonValueKind.Object)
                return [ParsePatchElement(root)];

            if (root.ValueKind != JsonValueKind.Array)
                return [];

            var patches = new List<PatchInfo>(root.GetArrayLength());
            foreach (var element in root.EnumerateArray())
                patches.Add(ParsePatchElement(element));
            return patches;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public string BuildApplyCommand(IReadOnlyList<PatchInfo> patches, PatchOptions options)
    {
        var kbList = string.Join("','", patches.Select(p => SanitizeKbId(p.PatchId)));
        var dryRun = options.DryRun ? " -Simulate" : "";
        var reboot = options.AllowReboot ? " -AutoReboot" : " -IgnoreReboot";
        return $"Install-WindowsUpdate -KBArticleID '{kbList}'{dryRun}{reboot} -AcceptAll -Confirm:$false | ConvertTo-Json -Compress";
    }

    public PatchResult ParseApplyOutput(Guid machineId, string stdout, string stderr, int exitCode, TimeSpan duration)
    {
        var outcomes = new List<PatchOutcome>();
        var rebootRequired = false;

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                var elements = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : new[] { root }.AsEnumerable().Select(e => e);

                foreach (var el in elements)
                {
                    var kb = el.TryGetProperty("KB", out var kbProp) ? kbProp.GetString() ?? "" : "";
                    var status = el.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                    var reboot = el.TryGetProperty("RebootRequired", out var rebootProp) && rebootProp.GetBoolean();

                    if (reboot) rebootRequired = true;

                    var state = status.Contains("Installed", StringComparison.OrdinalIgnoreCase)
                        ? PatchInstallState.Installed
                        : PatchInstallState.Failed;

                    outcomes.Add(new PatchOutcome(kb, state, state == PatchInstallState.Failed ? status : null));
                }
            }
            catch (JsonException)
            {
                // Fall through to simple detection
                rebootRequired = stdout.Contains("reboot", StringComparison.OrdinalIgnoreCase);
            }
        }

        var successful = outcomes.Count(o => o.State == PatchInstallState.Installed);
        var failed = outcomes.Count(o => o.State == PatchInstallState.Failed);
        if (outcomes.Count == 0)
        {
            // Could not parse structured output, fall back to exit code
            if (exitCode == 0) successful = 1; else failed = 1;
        }

        return new PatchResult(machineId, successful, failed, outcomes, rebootRequired, duration);
    }

    public string BuildVerifyCommand(IReadOnlyList<string> patchIds)
    {
        var kbList = string.Join("','", patchIds.Select(SanitizeKbId));
        return $"Get-HotFix -Id '{kbList}' | ConvertTo-Json -Compress";
    }

    public string BuildListInstalledCommand() =>
        "Get-HotFix | Select-Object HotFixID,Description,InstalledOn | ConvertTo-Json -Compress";

    public IReadOnlyList<InstalledPatch> ParseInstalledOutput(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
                return [ParseInstalledElement(root)];

            if (root.ValueKind != JsonValueKind.Array)
                return [];

            var patches = new List<InstalledPatch>(root.GetArrayLength());
            foreach (var element in root.EnumerateArray())
                patches.Add(ParseInstalledElement(element));
            return patches;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static PatchInfo ParsePatchElement(JsonElement el)
    {
        var kb = el.TryGetProperty("KB", out var kbProp) ? kbProp.GetString() ?? "Unknown" : "Unknown";
        var title = el.TryGetProperty("Title", out var titleProp) ? titleProp.GetString() ?? kb : kb;
        var size = el.TryGetProperty("Size", out var sizeProp) && sizeProp.TryGetInt64(out var sizeVal) ? sizeVal : 0L;
        var severity = el.TryGetProperty("MsrcSeverity", out var sevProp)
            ? MapSeverity(sevProp.GetString())
            : PatchSeverity.Unclassified;

        return new PatchInfo(kb, title, severity, PatchCategory.Security, title, size, false, DateTime.UtcNow);
    }

    private static InstalledPatch ParseInstalledElement(JsonElement el)
    {
        var id = el.TryGetProperty("HotFixID", out var idProp) ? idProp.GetString() ?? "" : "";
        var desc = el.TryGetProperty("Description", out var descProp) ? descProp.GetString() ?? "" : "";
        var installed = el.TryGetProperty("InstalledOn", out var dateProp)
            && DateTime.TryParse(dateProp.GetString(), out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;

        return new InstalledPatch(id, desc, installed);
    }

    private static PatchSeverity MapSeverity(string? severity) => severity?.ToUpperInvariant() switch
    {
        "CRITICAL" => PatchSeverity.Critical,
        "IMPORTANT" => PatchSeverity.Important,
        "MODERATE" => PatchSeverity.Moderate,
        "LOW" => PatchSeverity.Low,
        _ => PatchSeverity.Unclassified
    };

    private static string SanitizeKbId(string id) =>
        new(id.Where(c => char.IsLetterOrDigit(c)).ToArray());
}
