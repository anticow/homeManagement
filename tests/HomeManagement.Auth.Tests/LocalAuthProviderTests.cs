using FluentAssertions;

namespace HomeManagement.Auth.Tests;

/// <summary>
/// Tests for <see cref="LocalAuthProvider"/> — Argon2id password hashing and verification.
/// </summary>
public sealed class LocalAuthProviderTests
{
    // ── HashPassword ──

    [Fact]
    public void HashPassword_ReturnsArgon2idPrefixedString()
    {
        var hash = LocalAuthProvider.HashPassword("P@ssw0rd!");

        hash.Should().StartWith("$argon2id$");
    }

    [Fact]
    public void HashPassword_ContainsThreeParts()
    {
        var hash = LocalAuthProvider.HashPassword("test123");

        var parts = hash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        parts.Should().HaveCount(3);
        parts[0].Should().Be("argon2id");
    }

    [Fact]
    public void HashPassword_SaltIsValidBase64()
    {
        var hash = LocalAuthProvider.HashPassword("test123");
        var parts = hash.Split('$', StringSplitOptions.RemoveEmptyEntries);

        var saltAction = () => Convert.FromBase64String(parts[1]);
        saltAction.Should().NotThrow();
        Convert.FromBase64String(parts[1]).Length.Should().Be(16);
    }

    [Fact]
    public void HashPassword_HashIsValidBase64()
    {
        var hash = LocalAuthProvider.HashPassword("test123");
        var parts = hash.Split('$', StringSplitOptions.RemoveEmptyEntries);

        var hashAction = () => Convert.FromBase64String(parts[2]);
        hashAction.Should().NotThrow();
        Convert.FromBase64String(parts[2]).Length.Should().Be(32);
    }

    [Fact]
    public void HashPassword_SameInputProducesDifferentHashes()
    {
        var hash1 = LocalAuthProvider.HashPassword("identical");
        var hash2 = LocalAuthProvider.HashPassword("identical");

        hash1.Should().NotBe(hash2, "because each hash uses a random salt");
    }

    // ── VerifyPassword ──

    [Fact]
    public void VerifyPassword_CorrectPassword_ReturnsTrue()
    {
        var hash = LocalAuthProvider.HashPassword("MySecret123!");

        LocalAuthProvider.VerifyPassword("MySecret123!", hash).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse()
    {
        var hash = LocalAuthProvider.HashPassword("CorrectPassword");

        LocalAuthProvider.VerifyPassword("WrongPassword", hash).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_EmptyPassword_ThrowsArgumentException()
    {
        var hash = LocalAuthProvider.HashPassword("SomePassword");

        var act = () => LocalAuthProvider.VerifyPassword("", hash);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("$wrong$format")]
    [InlineData("$argon2id$")]
    [InlineData("$argon2id$salt")]
    public void VerifyPassword_MalformedHash_ReturnsFalse(string malformedHash)
    {
        LocalAuthProvider.VerifyPassword("anything", malformedHash).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_CaseSensitive()
    {
        var hash = LocalAuthProvider.HashPassword("CaseSensitive");

        LocalAuthProvider.VerifyPassword("casesensitive", hash).Should().BeFalse();
        LocalAuthProvider.VerifyPassword("CASESENSITIVE", hash).Should().BeFalse();
        LocalAuthProvider.VerifyPassword("CaseSensitive", hash).Should().BeTrue();
    }

    // ── Special characters ──

    [Theory]
    [InlineData("p@$$w0rd!#%^&*()")]
    [InlineData("こんにちは世界")]
    [InlineData("🔐🔑🗝️")]
    [InlineData("   spaces   ")]
    [InlineData("line\nbreak")]
    public void HashAndVerify_SpecialCharacters_RoundTrips(string password)
    {
        var hash = LocalAuthProvider.HashPassword(password);
        LocalAuthProvider.VerifyPassword(password, hash).Should().BeTrue();
    }
}
