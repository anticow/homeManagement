using FluentAssertions;
using HomeManagement.Auth;
using Microsoft.Extensions.Options;

namespace HomeManagement.Auth.Tests;

/// <summary>
/// Direct unit tests for <see cref="DefaultPasswordPolicy"/>.
/// These run without a database — pure logic tests.
/// </summary>
public sealed class DefaultPasswordPolicyTests
{
    private static DefaultPasswordPolicy CreatePolicy(int minLength = 12) =>
        new(Options.Create(new AuthOptions { JwtSigningKey = "x", PasswordMinLength = minLength }));

    [Theory]
    [InlineData("short")]                       // < 12 chars
    [InlineData("alllowercasepassword123")]     // no uppercase
    [InlineData("ALLUPPERCASEPASSWORD123")]     // no lowercase
    [InlineData("NoDigitsInThisPassword")]      // no digit
    [InlineData("")]                            // empty
    [InlineData(null!)]                         // null
    public void Validate_WeakPassword_Throws(string? password)
    {
        var policy = CreatePolicy();

        var act = () => policy.Validate(password!);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("ValidPassword1")]
    [InlineData("Abcdefghij123")]
    [InlineData("SuperSecure99!")]
    public void Validate_StrongPassword_DoesNotThrow(string password)
    {
        var policy = CreatePolicy();

        var act = () => policy.Validate(password);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ExactMinLength_Accepted()
    {
        var policy = CreatePolicy(minLength: 8);

        // Exactly 8 chars, meets all rules
        var act = () => policy.Validate("Abc1defg");

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_OneBelowMinLength_Throws()
    {
        var policy = CreatePolicy(minLength: 10);

        // 9 chars — one below minimum
        var act = () => policy.Validate("Abc1defgh");

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 10 characters*");
    }

    [Fact]
    public void Validate_CustomMinLength_IsRespected()
    {
        var policy = CreatePolicy(minLength: 16);

        // Valid 12-char password would pass default policy but fail 16-char policy
        var act = () => policy.Validate("ValidPass123");

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 16 characters*");
    }
}
