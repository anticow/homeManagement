using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FluentAssertions;
using HomeManagement.Agent.Handlers;

namespace HomeManagement.Agent.Tests.Handlers;

public sealed class AgentProcessRunnerTests
{
    [Theory]
    [InlineData(3, 8, 3, false)]
    [InlineData(4, 8, 4, false)]
    [InlineData(9, 8, 4, true)]
    public async Task ReadBoundedAsync_RetainsPrefixAndDrainsRemainder(
        int characters, int maxBytes, int retained, bool truncated)
    {
        using var reader = new RepeatingReader(characters);

        var result = await AgentProcessRunner.ReadBoundedAsync(reader, maxBytes, CancellationToken.None);

        result.Text.Should().Be(new string('x', retained));
        result.Truncated.Should().Be(truncated);
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task ReadBoundedAsync_LargeOutputDoesNotAllocateInProportionToInput()
    {
        using var reader = new RepeatingReader(64 * 1024 * 1024);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var result = await AgentProcessRunner.ReadBoundedAsync(reader, 1_048_576, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        result.Text.Should().HaveLength(524_288);
        result.Truncated.Should().BeTrue();
        reader.Remaining.Should().Be(0);
        allocated.Should().BeLessThan(8 * 1024 * 1024);
    }

    [Fact]
    public async Task RunAsync_CancellationStopsTheChildProcess()
    {
        var pidPath = Path.Combine(Path.GetTempPath(), $"hm-agent-test-{Guid.NewGuid():N}.pid");
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add($"$PID | Set-Content -LiteralPath '{pidPath}'; Start-Sleep -Seconds 60");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"echo $$ > '{pidPath}'; sleep 60");
        }

        using var cancellation = new CancellationTokenSource();
        var execution = AgentProcessRunner.RunAsync(startInfo, cancellation.Token);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(pidPath) || new FileInfo(pidPath).Length == 0)
                await Task.Delay(20, timeout.Token);

            var pid = int.Parse((await File.ReadAllTextAsync(pidPath, timeout.Token)).Trim(), CultureInfo.InvariantCulture);
            using var child = Process.GetProcessById(pid);

            await cancellation.CancelAsync();
            var action = async () => await execution.WaitAsync(TimeSpan.FromSeconds(5));
            await action.Should().ThrowAsync<OperationCanceledException>();
            await child.WaitForExitAsync(timeout.Token);
            child.HasExited.Should().BeTrue();
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await execution; }
            catch (OperationCanceledException) { }
            File.Delete(pidPath);
        }
    }

    private sealed class RepeatingReader(int characters) : TextReader
    {
        public int Remaining { get; private set; } = characters;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, Remaining);
            buffer.Span[..count].Fill('x');
            Remaining -= count;
            return ValueTask.FromResult(count);
        }
    }
}
