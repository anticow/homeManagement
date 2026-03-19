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
    private const int MaxOutputBytes = 1_048_576; // 1 MB

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

        // Timeout is enforced by CommandDispatcher; use the already-linked token (NEW-04)
        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxOutputBytes);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaxOutputBytes);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Per-command timeout — kill and report, don't propagate
            timedOut = true;
            KillProcessTree(process);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown — kill and propagate
            KillProcessTree(process);
            throw;
        }

        // Ensure readers complete before reading results
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var outputTruncated = stdout.Length >= MaxOutputBytes / sizeof(char)
                           || stderr.Length >= MaxOutputBytes / sizeof(char);

        logger.LogInformation("Shell command {RequestId} exited with code {ExitCode}, timedOut={TimedOut}, truncated={Truncated}",
            request.RequestId, timedOut ? -1 : process.ExitCode, timedOut, outputTruncated);

        return new CommandResponse
        {
            RequestId = request.RequestId,
            ExitCode = timedOut ? -1 : process.ExitCode,
            Stdout = stdout,
            Stderr = timedOut ? $"{stderr}\n[TIMED OUT after {request.TimeoutSeconds}s]" : stderr,
            TimedOut = timedOut
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

    private static async Task<string> ReadBoundedAsync(System.IO.StreamReader reader, int maxBytes)
    {
        var buffer = new char[maxBytes / sizeof(char)];
        var read = await reader.ReadAsync(buffer);
        return new string(buffer, 0, read);
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
    }
}
