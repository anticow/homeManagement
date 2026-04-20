using FluentAssertions;
using Grpc.Core;
using HomeManagement.Agent.Protocol;
using HomeManagement.AgentGateway.Host.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeManagement.AgentGateway.Host.Tests;

public sealed class AgentApiKeyValidatorTests
{
    [Fact]
    public void ValidateOrThrow_WithConfiguredAgentKey_AllowsMatch()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["AgentGateway:AgentApiKeys:agent-01"] = "key-01"
        });

        var act = () => validator.ValidateOrThrow(
            new TestServerCallContext(new Metadata { { "x-agent-api-key", "key-01" } }),
            new Handshake { AgentId = "agent-01" });

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateOrThrow_WithJsonConfiguredAgentKey_AllowsMatch()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["AgentGateway:AgentApiKeysJson"] = "{\"agent-01\":\"json-key\"}"
        });

        var act = () => validator.ValidateOrThrow(
            new TestServerCallContext(new Metadata { { "x-agent-api-key", "json-key" } }),
            new Handshake { AgentId = "agent-01" });

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateOrThrow_WithUnknownAgent_ThrowsUnauthenticated()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["AgentGateway:AgentApiKeys:agent-01"] = "key-01"
        });

        var act = () => validator.ValidateOrThrow(
            new TestServerCallContext(new Metadata { { "x-agent-api-key", "key-01" } }),
            new Handshake { AgentId = "agent-02" });

        act.Should().Throw<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.Unauthenticated)
            .WithMessage("*Agent API key not configured.*");
    }

    [Fact]
    public void ValidateOrThrow_WithInvalidKey_ThrowsUnauthenticated()
    {
        var validator = CreateValidator(new Dictionary<string, string?>
        {
            ["AgentGateway:AgentApiKeys:agent-01"] = "key-01"
        });

        var act = () => validator.ValidateOrThrow(
            new TestServerCallContext(new Metadata { { "x-agent-api-key", "wrong-key" } }),
            new Handshake { AgentId = "agent-01" });

        act.Should().Throw<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.Unauthenticated)
            .WithMessage("*Invalid API key.*");
    }

    [Fact]
    public void LoadConfiguredKeys_WithPlaceholderValue_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentGateway:AgentApiKeys:agent-01"] = "CHANGE-ME-placeholder"
            })
            .Build();

        var act = () => AgentApiKeyValidator.LoadConfiguredKeys(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*agent-01*");
    }

    private static AgentApiKeyValidator CreateValidator(IDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new AgentApiKeyValidator(configuration, NullLogger<AgentApiKeyValidator>.Instance);
    }
}

internal sealed class TestServerCallContext : ServerCallContext
{
    private readonly Metadata _requestHeaders;

    public TestServerCallContext(Metadata requestHeaders)
    {
        _requestHeaders = requestHeaders;
    }

    protected override string MethodCore => "AgentHub/Connect";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "ipv4:127.0.0.1:12345";
    protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore { get; } = [];
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new(string.Empty, []);

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        throw new NotSupportedException();
    }

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        return Task.CompletedTask;
    }
}
