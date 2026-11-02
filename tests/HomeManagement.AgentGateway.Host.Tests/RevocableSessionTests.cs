using FluentAssertions;
using Grpc.Core;
using HomeManagement.Abstractions.Models;
using HomeManagement.Agent.Protocol;
using HomeManagement.AgentGateway.Host.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HomeManagement.AgentGateway.Host.Tests;

public sealed class RevocableSessionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hm-revoked-{Guid.NewGuid():N}.json");
    private RevokedAgentStore CreateStore() => new(
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AgentGateway:RevocationListPath"] = _path }).Build(),
        NullLogger<RevokedAgentStore>.Instance);

    [Fact]
    public async Task Revoke_AbortsLiveSession_CancelsPendingAndPersists()
    {
        var store = CreateStore();
        using var gateway = new StandaloneAgentGatewayService(NullLogger<StandaloneAgentGatewayService>.Instance, store);
        var aborted = false;
        gateway.RegisterAgent(new Handshake { AgentId = "appnode" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true, () => aborted = true, true);
        var pending = gateway.SendCommandAsync("appnode", new RemoteCommand("", TimeSpan.FromSeconds(10), CommandType: "ManagedServices"));
        gateway.RevokeAgent("appnode", "test");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        aborted.Should().BeTrue();
        gateway.GetConnectedAgents().Should().BeEmpty();
        CreateStore().IsRevoked("APPNODE").Should().BeTrue();
        var register = () => gateway.RegisterAgent(new Handshake { AgentId = "appnode" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true);
        register.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task PlaintextAgentSession_CannotControlManagedServices()
    {
        using var gateway = new StandaloneAgentGatewayService(NullLogger<StandaloneAgentGatewayService>.Instance, CreateStore());
        gateway.RegisterAgent(new Handshake { AgentId = "appnode" },
            Substitute.For<IServerStreamWriter<ControlMessage>>());
        var send = () => gateway.SendCommandAsync("appnode",
            new RemoteCommand("", TimeSpan.FromSeconds(1), CommandType: "ManagedServices"));
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*encrypted*");
    }

    [Fact]
    public async Task NondurableRevocation_CannotControlManagedServices()
    {
        using var gateway = new StandaloneAgentGatewayService(NullLogger<StandaloneAgentGatewayService>.Instance, new NullRevokedAgentStore());
        gateway.RegisterAgent(new Handshake { AgentId = "appnode" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true, hasUniqueCredential: true);
        var send = () => gateway.SendCommandAsync("appnode",
            new RemoteCommand("", TimeSpan.FromSeconds(1), CommandType: "ManagedServices"));
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*durable*");
    }

    [Fact]
    public void CorruptedRevocationFile_DoesNotStartEmpty()
    {
        File.WriteAllText(_path, "{");
        var load = () => CreateStore();
        load.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task SharedCredentialSession_CannotControlManagedServices()
    {
        using var gateway = new StandaloneAgentGatewayService(NullLogger<StandaloneAgentGatewayService>.Instance, CreateStore());
        gateway.RegisterAgent(new Handshake { AgentId = "appnode" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true, hasUniqueCredential: false);
        var send = () => gateway.SendCommandAsync("appnode",
            new RemoteCommand("", TimeSpan.FromSeconds(1), CommandType: "ManagedServices"));
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*credential*");
    }

    [Fact]
    public void FailedPersistence_DisablesControlAndDoesNotReportSuccess()
    {
        var store = CreateStore();
        Directory.CreateDirectory(_path);
        try
        {
            var revoke = () => store.Revoke("appnode", "test");
            revoke.Should().Throw<Exception>().Where(ex => ex is IOException || ex is UnauthorizedAccessException);
            store.HasDurableStorage.Should().BeFalse();
            store.IsRevoked("any-node").Should().BeTrue();
        }
        finally
        {
            Directory.Delete(_path);
        }
    }

    [Fact]
    public void DuplicateIdentity_DoesNotReplaceExistingSession()
    {
        using var gateway = new StandaloneAgentGatewayService(NullLogger<StandaloneAgentGatewayService>.Instance, CreateStore());
        gateway.RegisterAgent(new Handshake { AgentId = "appnode" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true);
        var register = () => gateway.RegisterAgent(new Handshake { AgentId = "APPNODE" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true);
        register.Should().Throw<InvalidOperationException>().WithMessage("*already connected*");
        gateway.GetConnectedAgents().Should().ContainSingle();
    }

    [Fact]
    public void NormalDisconnect_DoesNotResetGracefulGrpcStream()
    {
        using var gateway = new StandaloneAgentGatewayService(NullLogger<StandaloneAgentGatewayService>.Instance, CreateStore());
        var aborted = false;
        gateway.RegisterAgent(new Handshake { AgentId = "appnode" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true, () => aborted = true, true);
        gateway.UnregisterAgent("appnode");
        aborted.Should().BeFalse();
        gateway.GetConnectedAgents().Should().BeEmpty();
    }

    public void Dispose() => File.Delete(_path);
}
