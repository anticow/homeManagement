using System.Diagnostics;

namespace HomeManagement.Automation;

/// <summary>
/// Default implementation of <see cref="IProcessRunner"/> using System.Diagnostics.Process.
/// </summary>
internal sealed class DefaultProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken ct)
    {
        CancellationTokenRegistration cancellationRegistration = default;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            cancellationRegistration = ct.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort kill on cancellation.
                }
            });

            var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            return new ProcessResult(process.ExitCode, stdOut, stdErr, WasCancelled: false);
        }
        catch (OperationCanceledException)
        {
            return new ProcessResult(ExitCode: null, StdOut: "", StdErr: "", WasCancelled: true);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }
}
