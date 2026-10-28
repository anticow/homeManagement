using System.Diagnostics;
using System.Text;

namespace HomeManagement.Agent.Handlers;

internal static class AgentProcessRunner
{
    private const int MaxOutputBytes = 1_048_576;

    internal static async Task<(int ExitCode, string Stdout, string Stderr, bool OutputTruncated)> RunAsync(
        ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxOutputBytes, ct);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaxOutputBytes, ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process may exit between the check and the kill.
                }
            }

            await Task.WhenAll(stdoutTask, stderrTask);
        }

        var (stdout, stdoutTruncated) = await stdoutTask;
        var (stderr, stderrTruncated) = await stderrTask;
        var truncated = stdoutTruncated || stderrTruncated;
        if (truncated)
            stderr += "\n[Output truncated: maximum retained output is 1 MiB per stream.]";

        return (process.ExitCode, stdout, stderr, truncated);
    }

    internal static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        TextReader reader, int maxBytes, CancellationToken ct)
    {
        var maxChars = maxBytes / sizeof(char);
        var content = new StringBuilder(Math.Min(maxChars, 4096));
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            var retained = Math.Min(read, maxChars - content.Length);
            content.Append(buffer, 0, retained);
            truncated |= retained < read;
        }

        return (content.ToString(), truncated);
    }
}
