using System.Security.Cryptography;
using FluentAssertions;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeManagement.Agent.Tests.Security;

public sealed class IntegrityCheckerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IntegrityChecker _checker;

    public IntegrityCheckerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _checker = new IntegrityChecker(NullLogger<IntegrityChecker>.Instance);
    }

    [Fact]
    public async Task VerifySha256Async_MatchingHash_ReturnsTrue()
    {
        var filePath = Path.Combine(_tempDir, "valid.bin");
        var content = "Hello, integrity check!"u8.ToArray();
        await File.WriteAllBytesAsync(filePath, content);

        var expectedHash = SHA256.HashData(content);

        var result = await _checker.VerifySha256Async(filePath, expectedHash);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifySha256Async_MismatchedHash_ReturnsFalse()
    {
        var filePath = Path.Combine(_tempDir, "tampered.bin");
        await File.WriteAllBytesAsync(filePath, "original content"u8.ToArray());

        var wrongHash = SHA256.HashData("different content"u8.ToArray());

        var result = await _checker.VerifySha256Async(filePath, wrongHash);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifySha256Async_EmptyFile_VerifiesCorrectly()
    {
        var filePath = Path.Combine(_tempDir, "empty.bin");
        await File.WriteAllBytesAsync(filePath, []);

        var emptyHash = SHA256.HashData(ReadOnlySpan<byte>.Empty);

        var result = await _checker.VerifySha256Async(filePath, emptyHash);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifySha256Async_SupportsCancellation()
    {
        var filePath = Path.Combine(_tempDir, "cancel.bin");
        await File.WriteAllBytesAsync(filePath, new byte[1024]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _checker.VerifySha256Async(filePath, new byte[32], cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
