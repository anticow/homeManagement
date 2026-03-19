using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Handlers;

/// <summary>
/// Handles patch scan and patch apply commands by delegating to OS package managers.
/// Linux: apt/yum/dnf. Windows: Windows Update Agent (via PowerShell).
/// Uses ProcessStartInfo.ArgumentList to prevent shell metacharacter injection.
/// </summary>
public sealed partial class PatchCommandHandler(ILogger<PatchCommandHandler> logger) : ICommandHandler
{
    // Strict allowlist: alphanumeric, hyphens, dots, underscores, colons, tildes (covers dpkg/rpm/KB naming)
    [GeneratedRegex(@"^[\w.\-:~]+$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex SafePatchIdPattern();

    public string CommandType => "PatchScan";

    public async Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct)
    {
        // The command_type can be "PatchScan" or "PatchApply" — dispatch from parameters
        var isPatchApply = request.ParametersJson.Contains("\"patchIds\"", StringComparison.OrdinalIgnoreCase);

        logger.LogInformation("{Operation} for {RequestId}",
            isPatchApply ? "PatchApply" : "PatchScan", request.RequestId);

        var (exitCode, stdout, stderr) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? await RunWindowsPatchCommandAsync(isPatchApply, request.ParametersJson, ct)
            : await RunLinuxPatchCommandAsync(isPatchApply, request.ParametersJson, ct);

        return new CommandResponse
        {
            RequestId = request.RequestId,
            ExitCode = exitCode,
            Stdout = stdout,
            Stderr = stderr,
            ResultJson = stdout // Parseable structured output from the script
        };
    }

    private static string[] SanitizePatchIds(string[]? patchIds)
    {
        if (patchIds is null || patchIds.Length == 0) return [];

        var pattern = SafePatchIdPattern();
        var safe = new List<string>(patchIds.Length);
        foreach (var id in patchIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && pattern.IsMatch(id))
                safe.Add(id);
        }
        return safe.ToArray();
    }

    private async Task<(int, string, string)> RunLinuxPatchCommandAsync(
        bool isApply, string parametersJson, CancellationToken ct)
    {
        // Detect package manager
        var packageManager = File.Exists("/usr/bin/apt")
            ? "apt"
            : File.Exists("/usr/bin/dnf") ? "dnf" : "yum";

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isApply)
        {
            var parameters = JsonSerializer.Deserialize<PatchApplyParameters>(parametersJson);
            var safeIds = SanitizePatchIds(parameters?.PatchIds);
            if (safeIds.Length == 0)
                return (-1, "", "No valid patch IDs provided.");

            if (packageManager == "apt")
            {
                psi.FileName = "/usr/bin/apt-get";
                psi.ArgumentList.Add("install");
                psi.ArgumentList.Add("-y");
            }
            else
            {
                psi.FileName = $"/usr/bin/{packageManager}";
                psi.ArgumentList.Add("update");
                psi.ArgumentList.Add("-y");
            }

            foreach (var id in safeIds)
                psi.ArgumentList.Add(id);
        }
        else
        {
            switch (packageManager)
            {
                case "apt":
                    psi.FileName = "/usr/bin/apt";
                    psi.ArgumentList.Add("list");
                    psi.ArgumentList.Add("--upgradable");
                    break;
                default:
                    psi.FileName = $"/usr/bin/{packageManager}";
                    psi.ArgumentList.Add("check-update");
                    break;
            }
        }

        return await RunProcessAsync(psi, ct);
    }

    private async Task<(int, string, string)> RunWindowsPatchCommandAsync(
        bool isApply, string parametersJson, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isApply)
        {
            var parameters = JsonSerializer.Deserialize<PatchApplyParameters>(parametersJson);
            var safeIds = SanitizePatchIds(parameters?.PatchIds);
            if (safeIds.Length == 0)
                return (-1, "", "No valid patch IDs provided.");

            // Build PowerShell script with sanitized IDs
            var idFilter = string.Join("','", safeIds);
            var script = $"Install-WindowsUpdate -KBArticleID '{idFilter}' -AcceptAll -IgnoreReboot | ConvertTo-Json";
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
        }
        else
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("Get-WindowsUpdate -MicrosoftUpdate | ConvertTo-Json");
        }

        return await RunProcessAsync(psi, ct);
    }

    private async Task<(int, string, string)> RunProcessAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        logger.LogDebug("Launching {FileName} with {ArgCount} arguments", psi.FileName, psi.ArgumentList.Count);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record PatchApplyParameters(string[]? PatchIds, bool AllowReboot, bool DryRun);
}
