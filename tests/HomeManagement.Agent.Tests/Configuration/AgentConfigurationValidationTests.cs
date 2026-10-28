using FluentAssertions;
using HomeManagement.Agent.Configuration;

namespace HomeManagement.Agent.Tests.Configuration;

public sealed class AgentConfigurationValidationTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(30, 0)]
    [InlineData(30, -1)]
    public void Validate_WithInvalidLoopOrConcurrencySettings_Throws(int heartbeatSeconds, int maxConcurrentCommands)
    {
        var config = new AgentConfiguration
        {
            ApiKey = "configured-api-key",
            HeartbeatIntervalSeconds = heartbeatSeconds,
            MaxConcurrentCommands = maxConcurrentCommands
        };

        var act = () => config.Validate();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_WithMissingApiKey_Throws()
    {
        var config = new AgentConfiguration
        {
            ControlServer = "localhost:9444",
            ApiKey = string.Empty,
            UseTls = true
        };

        var act = () => config.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Agent:ApiKey*");
    }

    [Fact]
    public void Validate_AllowsPlaintextOnlyForLoopbackTargets()
    {
        var config = new AgentConfiguration
        {
            ControlServer = "localhost:9444",
            ApiKey = "configured-api-key",
            UseTls = false
        };

        var act = () => config.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithPlaintextNonLoopbackTarget_Throws()
    {
        var config = new AgentConfiguration
        {
            ControlServer = "agentgw.cowgomu.net:9444",
            ApiKey = "configured-api-key",
            UseTls = false
        };

        var act = () => config.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*UseTls may be false only*");
    }
}
