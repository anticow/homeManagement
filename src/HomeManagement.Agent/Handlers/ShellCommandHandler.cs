using System.Diagnostics;
using System.Runtime.InteropServices;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Handlers;

/// <summary>
/// Executes shell commands via the OS shell using ArgumentList (no shell interpolation).
/// </summary>
public sealed class ShellCommandHandler(ILogger<ShellCommandHandler> logger) : ICommandHandler
{
    public string CommandType => "Shell";

    public async Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct)
    {
        logger.LogInformation("Executing shell command {RequestId}", request.RequestId);

        var psi = BuildProcessStartInfo(request.CommandText, request.ElevationMode, request.RunAsUser);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        // Apply environment overrides
        foreach (var kvp in request.Env)
            psi.Environment[kvp.Key] = kvp.Value;

        var result = await AgentProcessRunner.RunAsync(psi, ct);
        logger.LogInformation("Shell command {RequestId} exited with code {ExitCode}, truncated={Truncated}",
            request.RequestId, result.ExitCode, result.OutputTruncated);

        return new CommandResponse
        {
            RequestId = request.RequestId,
            ExitCode = result.ExitCode,
            Stdout = result.Stdout,
            Stderr = result.Stderr
        };
    }

    /// <summary>
    /// Builds ProcessStartInfo using ArgumentList to prevent shell metacharacter injection.
    /// ArgumentList passes arguments directly to the process without shell interpretation.
    /// </summary>
    private static ProcessStartInfo BuildProcessStartInfo(
        string commandText, string elevationMode, string runAsUser)
    {
        var psi = new ProcessStartInfo();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use PowerShell — all Windows strategies emit PowerShell cmdlets
            // (Get-Service, Get-WindowsUpdate, etc.) which require a PS host.
            psi.FileName = "powershell.exe";
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(commandText);
        }
        else
        {
            // On Linux, use bash -c with the command as a single argument.
            // ArgumentList prevents the outer shell from interpreting metacharacters.
            switch (elevationMode)
            {
                case "Sudo":
                    psi.FileName = "/usr/bin/sudo";
                    psi.ArgumentList.Add("/bin/bash");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(commandText);
                    break;

                case "SudoAsUser" when !string.IsNullOrEmpty(runAsUser):
                    psi.FileName = "/usr/bin/sudo";
                    psi.ArgumentList.Add("-u");
                    psi.ArgumentList.Add(runAsUser);
                    psi.ArgumentList.Add("/bin/bash");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(commandText);
                    break;

                default:
                    psi.FileName = "/bin/bash";
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(commandText);
                    break;
            }
        }

        return psi;
    }

}
