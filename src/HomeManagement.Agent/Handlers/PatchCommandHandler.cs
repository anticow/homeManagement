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

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(BuildWindowsApplyScript(safeIds));
        }
        else
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(BuildWindowsScanScript());
        }

        return await RunProcessAsync(psi, ct);
    }

    private static string BuildWindowsScanScript() =>
        """
        $ErrorActionPreference = 'Stop'
        function Ensure-WinGet {
            if (Get-Command winget -ErrorAction SilentlyContinue) { return }

            $bundlePath = Join-Path $env:TEMP 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle'
            Invoke-WebRequest -Uri 'https://aka.ms/getwinget' -OutFile $bundlePath -UseBasicParsing
            Add-AppxPackage -Path $bundlePath

            if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
                throw 'winget is not available after installation attempt.'
            }
        }

        Ensure-WinGet
        $wingetVersion = (& winget --version).Trim()
        $windowsUpdates = @(Get-WindowsUpdate -MicrosoftUpdate)

        [pscustomobject]@{
            WingetVersion = $wingetVersion
            WindowsUpdates = $windowsUpdates
        } | ConvertTo-Json -Compress -Depth 8
        """;

    private static string BuildWindowsApplyScript(string[] safeIds)
    {
        var packageIds = safeIds.Where(id => !IsKbPatchId(id)).ToArray();
        var kbIds = safeIds.Where(IsKbPatchId).ToArray();

        var packageIdsLiteral = ToPowerShellStringArrayLiteral(packageIds);
        var kbIdsLiteral = ToPowerShellStringArrayLiteral(kbIds);
        const string scriptTemplate =
            """
            $ErrorActionPreference = 'Stop'
            function Ensure-WinGet {
                if (Get-Command winget -ErrorAction SilentlyContinue) { return }

                $bundlePath = Join-Path $env:TEMP 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle'
                Invoke-WebRequest -Uri 'https://aka.ms/getwinget' -OutFile $bundlePath -UseBasicParsing
                Add-AppxPackage -Path $bundlePath

                if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
                    throw 'winget is not available after installation attempt.'
                }
            }

            $packageIds = __PACKAGE_IDS__
            $kbIds = __KB_IDS__
            $results = @()

            if ($packageIds.Count -gt 0) {
                Ensure-WinGet
                foreach ($packageId in $packageIds) {
                    $exitCode = 0
                    try {
                        winget upgrade --id $packageId --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Null
                        $exitCode = $LASTEXITCODE
                    } catch {
                        $exitCode = -1
                    }

                    $results += [pscustomobject]@{
                        PatchId = $packageId
                        Installed = ($exitCode -eq 0)
                        ExitCode = $exitCode
                        ErrorMessage = if ($exitCode -eq 0) { $null } else { "winget exit code $exitCode" }
                    }
                }
            }

            if ($kbIds.Count -gt 0) {
                try {
                    $kbUpdates = Install-WindowsUpdate -KBArticleID $kbIds -AcceptAll -IgnoreReboot -Confirm:$false
                    foreach ($update in $kbUpdates) {
                        $status = [string]$update.Status
                        $installed = $status -like '*Installed*'
                        $results += [pscustomobject]@{
                            PatchId = [string]$update.KB
                            Installed = $installed
                            ExitCode = if ($installed) { 0 } else { 1 }
                            ErrorMessage = if ($installed) { $null } else { $status }
                        }
                    }
                } catch {
                    foreach ($kbId in $kbIds) {
                        $results += [pscustomobject]@{
                            PatchId = $kbId
                            Installed = $false
                            ExitCode = -1
                            ErrorMessage = $_.Exception.Message
                        }
                    }
                }
            }

            $results | ConvertTo-Json -Compress
            """;

        return scriptTemplate
            .Replace("__PACKAGE_IDS__", packageIdsLiteral, StringComparison.Ordinal)
            .Replace("__KB_IDS__", kbIdsLiteral, StringComparison.Ordinal);
    }

    private static bool IsKbPatchId(string patchId)
    {
        if (!patchId.StartsWith("KB", StringComparison.OrdinalIgnoreCase) || patchId.Length <= 2)
            return false;

        for (var i = 2; i < patchId.Length; i++)
        {
            if (!char.IsDigit(patchId[i]))
                return false;
        }

        return true;
    }

    private static string ToPowerShellStringArrayLiteral(string[] values)
    {
        if (values.Length == 0)
            return "@()";

        return $"@('{string.Join("','", values.Select(EscapePowerShellSingleQuotedString))}')";
    }

    private static string EscapePowerShellSingleQuotedString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

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
