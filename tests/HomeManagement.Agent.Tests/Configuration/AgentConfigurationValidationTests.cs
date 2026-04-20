using FluentAssertions;
using HomeManagement.Agent.Configuration;

namespace HomeManagement.Agent.Tests.Configuration;

public sealed class AgentConfigurationValidationTests
{
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
