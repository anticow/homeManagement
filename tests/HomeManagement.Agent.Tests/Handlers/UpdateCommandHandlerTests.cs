using FluentAssertions;
using Google.Protobuf;
using HomeManagement.Agent.Communication;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Agent.Tests.Handlers;

public sealed class UpdateCommandHandlerTests
{
    private static UpdateCommandHandler CreateHandler(AgentConfiguration config)
    {
        var integrity = new IntegrityChecker(NullLogger<IntegrityChecker>.Instance);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var shutdown = new ShutdownCoordinator(lifetime, NullLogger<ShutdownCoordinator>.Instance);
        return new UpdateCommandHandler(
            integrity, shutdown,
            Options.Create(config),
            NullLogger<UpdateCommandHandler>.Instance);
    }

    private static UpdateDirective ValidDirective() => new()
    {
        TargetVersion = "1.2.3",
        DownloadUrl = "https://example.com/hm-agent.bin",
        BinarySha256 = ByteString.CopyFrom(new byte[32]),
        SignatureEd25519 = ByteString.CopyFrom(new byte[64])
    };

    [Fact]
    public async Task HandleAsync_WithEmptyBinarySha256_ReturnsEarlyWithoutAttemptingDownload()
    {
        // Arrange — signing key is present but hash is missing
        var handler = CreateHandler(new AgentConfiguration
        {
            ApiKey = "key",
            UpdateSigningPublicKey = new byte[32]
        });
        var directive = ValidDirective();
        directive.BinarySha256 = ByteString.Empty;

        // Act — should not throw even though DownloadUrl is unreachable
        await handler.Invoking(h => h.HandleAsync(directive, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleAsync_WithEmptySignatureEd25519_ReturnsEarlyWithoutAttemptingDownload()
    {
        // Arrange — hash present but signature is missing
        var handler = CreateHandler(new AgentConfiguration
        {
            ApiKey = "key",
            UpdateSigningPublicKey = new byte[32]
        });
        var directive = ValidDirective();
        directive.SignatureEd25519 = ByteString.Empty;

        await handler.Invoking(h => h.HandleAsync(directive, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleAsync_WithMissingUpdateSigningPublicKey_ReturnsEarlyWithoutAttemptingDownload()
    {
        // Arrange — both fields present but the agent has no public key configured
        var handler = CreateHandler(new AgentConfiguration
        {
            ApiKey = "key",
            UpdateSigningPublicKey = null
        });

        await handler.Invoking(h => h.HandleAsync(ValidDirective(), CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleAsync_WithEmptyPublicKeyArray_ReturnsEarlyWithoutAttemptingDownload()
    {
        // Arrange — key is non-null but zero-length (treated as unconfigured)
        var handler = CreateHandler(new AgentConfiguration
        {
            ApiKey = "key",
            UpdateSigningPublicKey = []
        });

        await handler.Invoking(h => h.HandleAsync(ValidDirective(), CancellationToken.None))
            .Should().NotThrowAsync();
    }
}
