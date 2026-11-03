using System.Reactive.Subjects;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HomeManagement.Transport.Tests;

public sealed class ManagedAgentObserverTests
{
    [Fact]
    public async Task ManagedMode_NeverReadsHostnameSnapshotsOrWritesInventoryForAnyEvent()
    {
        var gateway = Substitute.For<IAgentGateway>();
        using var events = new Subject<AgentConnectionEvent>();
        gateway.ConnectionEvents.Returns(events);
        gateway.GetConnectedAgents().Returns([new("wire-id", "node", "1", DateTime.UtcNow, DateTime.UtcNow, TimeSpan.Zero)]);
        gateway.ClearReceivedCalls();
        var inventory = Substitute.For<IInventoryService>();
        var services = new ServiceCollection().AddSingleton(inventory);
        await using var provider = services.BuildServiceProvider();
        var runtime = Substitute.For<IMachineAgentRuntime>();
        var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.VerifyEnrollmentModeAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            verified.TrySetResult();
            return Task.FromResult(true);
        });
        using var observer = new AgentAutoRegistrationHostedService(gateway, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentAutoRegistrationHostedService>.Instance, runtime);
        await observer.StartAsync(CancellationToken.None);
        await verified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        events.OnNext(new("wire-id", "node", AgentConnectionEventType.Connected, DateTime.UtcNow));
        events.OnNext(new("wire-id", "node", AgentConnectionEventType.Disconnected, DateTime.UtcNow.AddSeconds(-1)));
        await observer.StopAsync(CancellationToken.None);
        inventory.ReceivedCalls().Should().BeEmpty();
        gateway.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task UnverifiableMode_FaultsHostedServiceBeforeLegacyAccess()
    {
        var gateway = Substitute.For<IAgentGateway>();
        var runtime = Substitute.For<IMachineAgentRuntime>();
        runtime.VerifyEnrollmentModeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("Mode unavailable.")));
        await using var provider = new ServiceCollection().BuildServiceProvider();
        using var observer = new AgentAutoRegistrationHostedService(gateway, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentAutoRegistrationHostedService>.Instance, runtime);
        await observer.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.ExecuteTask!);
        gateway.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectionTest_UsesMachineBoundMetadataInsteadOfConnectedHostname()
    {
        var gateway = Substitute.For<IAgentGateway>();
        var runtime = Substitute.For<IMachineAgentRuntime>();
        var target = new MachineTarget(Guid.NewGuid(), HomeManagement.Abstractions.Validation.Hostname.Create("node"),
            HomeManagement.Abstractions.OsType.Linux, HomeManagement.Abstractions.MachineConnectionMode.Agent,
            HomeManagement.Abstractions.TransportProtocol.Agent, 443, Guid.Empty);
        runtime.GetMachineMetadataAsync(target, Arg.Any<CancellationToken>())
            .Returns(new AgentMetadata("explicit", "node", HomeManagement.Abstractions.OsType.Linux, "test", new(1, 1, [], "x64")));
        var transport = new AgentTransportProvider(gateway, NullLogger<AgentTransportProvider>.Instance, runtime);
        (await transport.TestConnectionAsync(target, CancellationToken.None)).Reachable.Should().BeTrue();
        gateway.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ManagedObserver_ModeChangeTerminatesWithoutResumingLegacySynchronization()
    {
        var gateway = Substitute.For<IAgentGateway>();
        var runtime = Substitute.For<IMachineAgentRuntime>();
        runtime.VerifyEnrollmentModeAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true), Task.FromResult(false));
        await using var provider = new ServiceCollection().BuildServiceProvider();
        using var observer = new AgentAutoRegistrationHostedService(gateway, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentAutoRegistrationHostedService>.Instance, runtime);
        await observer.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
        gateway.ReceivedCalls().Should().BeEmpty();
    }
}
