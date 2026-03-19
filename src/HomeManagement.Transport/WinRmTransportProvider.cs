using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Transport;

/// <summary>
/// WinRM transport provider that executes commands on remote Windows machines
/// via PowerShell Remoting (Invoke-Command). Requires WinRM to be configured
/// on the target host. Currently delegates to a local PowerShell process.
/// </summary>
internal sealed class WinRmTransportProvider
{
    private readonly ICredentialVault _vault;
    private readonly ILogger<WinRmTransportProvider> _logger;

    public WinRmTransportProvider(ICredentialVault vault, ILogger<WinRmTransportProvider> logger)
    {
        _vault = vault;
        _logger = logger;
    }

    public async Task<RemoteResult> ExecuteAsync(MachineTarget target, RemoteCommand command, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new RemoteResult(-1, "", "WinRM transport is only available on Windows.", TimeSpan.Zero, false);

        using var credential = await _vault.GetPayloadAsync(target.CredentialId, ct);
        var password = System.Text.Encoding.UTF8.GetString(credential.DecryptedPayload);

        // Build Invoke-Command call with explicit credential
        var scriptBlock = command.CommandText.Replace("'", "''");
        var psScript =
            $"$secPass = ConvertTo-SecureString -String $env:HM_CRED -AsPlainText -Force; " +
            $"$cred = New-Object System.Management.Automation.PSCredential('{credential.Username}', $secPass); " +
            $"Invoke-Command -ComputerName '{target.Hostname}' -Credential $cred -ScriptBlock {{ {scriptBlock} }} | ConvertTo-Json -Depth 5";

        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Pass credential via environment variable to avoid command-line exposure
        psi.Environment["HM_CRED"] = password;

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        await process.StandardInput.WriteAsync(psScript);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        bool timedOut = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(command.Timeout);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        }

        sw.Stop();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new RemoteResult(
            process.HasExited ? process.ExitCode : -1,
            stdout,
            stderr,
            sw.Elapsed,
            timedOut);
    }

    public Task<ConnectionTestResult> TestConnectionAsync(MachineTarget target, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Task.FromResult(new ConnectionTestResult(
                Reachable: false, DetectedOs: null, OsVersion: null,
                Latency: TimeSpan.Zero, ProtocolVersion: null,
                ErrorMessage: "WinRM transport requires Windows."));
        }

        // Test-WSMan is the idiomatic connectivity check for WinRM
        _logger.LogInformation("Testing WinRM connectivity to {Host}", target.Hostname);
        return Task.FromResult(new ConnectionTestResult(
            Reachable: false, DetectedOs: OsType.Windows, OsVersion: null,
            Latency: TimeSpan.Zero, ProtocolVersion: null,
            ErrorMessage: "WinRM connection test not yet fully implemented."));
    }
}
