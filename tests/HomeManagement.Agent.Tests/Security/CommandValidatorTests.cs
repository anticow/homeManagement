using FluentAssertions;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeManagement.Agent.Tests.Security;

public sealed class CommandValidatorTests
{
    private static CommandValidator CreateValidator(
        int rateLimit = 100,
        bool allowElevation = false,
        string[]? deniedPatterns = null)
    {
        var config = new AgentConfiguration
        {
            CommandRateLimit = rateLimit,
            AllowElevation = allowElevation,
            DeniedCommandPatterns = deniedPatterns ?? []
        };
        return new CommandValidator(
            Options.Create(config),
            NullLogger<CommandValidator>.Instance);
    }

    // ── Command type allowlist ──

    [Theory]
    [InlineData("Shell")]
    [InlineData("PatchScan")]
    [InlineData("PatchApply")]
    [InlineData("ServiceControl")]
    [InlineData("SystemInfo")]
    public void Validate_AllowedCommandType_ReturnsAllowed(string commandType)
    {
        var validator = CreateValidator();
        var result = validator.Validate(commandType, null, "None");
        result.IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("SHELL")]
    [InlineData("patchscan")]
    public void Validate_AllowedCommandType_CaseInsensitive(string commandType)
    {
        var validator = CreateValidator();
        var result = validator.Validate(commandType, null, "None");
        result.IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Exploit")]
    [InlineData("")]
    public void Validate_UnknownCommandType_RejectsWithAuthorization(string commandType)
    {
        var validator = CreateValidator();
        var result = validator.Validate(commandType, null, "None");
        result.IsAllowed.Should().BeFalse();
        result.ErrorCategory.Should().Be("Authorization");
    }

    // ── Elevation guard ──

    [Theory]
    [InlineData("Sudo")]
    [InlineData("Admin")]
    public void Validate_ElevationRequested_WhenDisabled_Rejects(string elevationMode)
    {
        var validator = CreateValidator(allowElevation: false);
        var result = validator.Validate("Shell", "ls", elevationMode);
        result.IsAllowed.Should().BeFalse();
        result.ErrorCategory.Should().Be("Authorization");
        result.ErrorMessage.Should().Contain("Elevation");
    }

    [Fact]
    public void Validate_ElevationRequested_WhenEnabled_Allows()
    {
        var validator = CreateValidator(allowElevation: true);
        var result = validator.Validate("Shell", "ls", "Sudo");
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Validate_NoElevation_WhenDisabled_AllowsNormally()
    {
        var validator = CreateValidator(allowElevation: false);
        var result = validator.Validate("Shell", "ls", "None");
        result.IsAllowed.Should().BeTrue();
    }

    // ── Denied patterns ──

    [Fact]
    public void Validate_ShellCommand_MatchesDeniedPattern_Rejects()
    {
        var validator = CreateValidator(deniedPatterns: ["rm\\s+-rf", "format\\s+c:"]);
        var result = validator.Validate("Shell", "rm -rf /", "None");
        result.IsAllowed.Should().BeFalse();
        result.ErrorCategory.Should().Be("Authorization");
        result.ErrorMessage.Should().Contain("denied pattern");
    }

    [Fact]
    public void Validate_ShellCommand_NoDeniedPatternMatch_Allows()
    {
        var validator = CreateValidator(deniedPatterns: ["rm\\s+-rf"]);
        var result = validator.Validate("Shell", "ls -la /tmp", "None");
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Validate_NonShellCommand_DeniedPatternsIgnored()
    {
        var validator = CreateValidator(deniedPatterns: [".*"]);
        var result = validator.Validate("SystemInfo", null, "None");
        result.IsAllowed.Should().BeTrue();
    }

    // ── Rate limiting ──

    [Fact]
    public void Validate_ExceedingRateLimit_Rejects()
    {
        var validator = CreateValidator(rateLimit: 3);

        validator.Validate("Shell", "cmd1", "None").IsAllowed.Should().BeTrue();
        validator.Validate("Shell", "cmd2", "None").IsAllowed.Should().BeTrue();
        validator.Validate("Shell", "cmd3", "None").IsAllowed.Should().BeTrue();

        var result = validator.Validate("Shell", "cmd4", "None");
        result.IsAllowed.Should().BeFalse();
        result.ErrorCategory.Should().Be("Transient");
        result.ErrorMessage.Should().Contain("Rate limited");
    }

    [Fact]
    public async Task Validate_RateLimit_ResetsAfterWindow()
    {
        var validator = CreateValidator(rateLimit: 2);

        validator.Validate("Shell", "cmd1", "None").IsAllowed.Should().BeTrue();
        validator.Validate("Shell", "cmd2", "None").IsAllowed.Should().BeTrue();
        validator.Validate("Shell", "cmd3", "None").IsAllowed.Should().BeFalse();

        // Wait for the 1-second sliding window to expire
        await Task.Delay(1100);

        validator.Validate("Shell", "cmd4", "None").IsAllowed.Should().BeTrue();
    }

    // ── Input length guard (HIGH-07 fix) ──

    [Fact]
    public void Validate_ShellCommand_ExceedsMaxLength_Rejects()
    {
        var validator = CreateValidator(deniedPatterns: ["rm\\s+-rf"]);
        var longCommand = new string('A', 33_000); // > 32,768 limit

        var result = validator.Validate("Shell", longCommand, "None");

        result.IsAllowed.Should().BeFalse();
        result.ErrorCategory.Should().Be("Authorization");
        result.ErrorMessage.Should().Contain("maximum length");
    }

    [Fact]
    public void Validate_ShellCommand_AtMaxLength_StillChecksPatterns()
    {
        var validator = CreateValidator(deniedPatterns: ["rm\\s+-rf"]);
        var safeCommand = new string('A', 32_768); // exactly at limit

        var result = validator.Validate("Shell", safeCommand, "None");

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Validate_NonShellCommand_LengthNotChecked()
    {
        var validator = CreateValidator();
        var longParams = new string('A', 100_000); // way over limit

        // PatchScan doesn't check command text length (it's not shell)
        var result = validator.Validate("PatchScan", longParams, "None");
        result.IsAllowed.Should().BeTrue();
    }
}

public sealed class CommandValidationResultTests
{
    [Fact]
    public void Allowed_HasExpectedProperties()
    {
        var result = CommandValidationResult.Allowed;
        result.IsAllowed.Should().BeTrue();
        result.ErrorCategory.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Rejected_HasExpectedProperties()
    {
        var result = CommandValidationResult.Rejected("Security", "Not permitted");
        result.IsAllowed.Should().BeFalse();
        result.ErrorCategory.Should().Be("Security");
        result.ErrorMessage.Should().Be("Not permitted");
    }
}
