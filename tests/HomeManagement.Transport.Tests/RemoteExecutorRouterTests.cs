using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HomeManagement.Transport.Tests;

public class RemoteExecutorRouterTests
{
    // RemoteExecutorRouter depends on sealed internal classes that can't be mocked.
    // These tests verify the routing switch logic via the public IRemoteExecutor interface
    // by testing the unsupported-protocol error paths which don't need real providers.

    private static readonly Hostname TestHost = Hostname.Create("test-host");

    [Fact]
    public async Task TestConnectionAsync_UnknownProtocol_ReturnsNotReachable()
    {
        var router = CreateRouterWithNullProviders();
        var target = new MachineTarget(Guid.NewGuid(), TestHost, OsType.Linux,
            MachineConnectionMode.Agentless, (TransportProtocol)99, 22, Guid.Empty);

        var result = await router.TestConnectionAsync(target);

        result.Reachable.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TransferFileAsync_NonSshProtocol_ThrowsNotSupported()
    {
        var router = CreateRouterWithNullProviders();
        var target = new MachineTarget(Guid.NewGuid(), TestHost, OsType.Windows,
            MachineConnectionMode.Agentless, TransportProtocol.WinRM, 5986, Guid.Empty);
        var request = new FileTransferRequest("/local", "/remote", FileTransferDirection.Upload);

        var act = () => router.TransferFileAsync(target, request);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownProtocol_ThrowsArgumentOutOfRange()
    {
        var router = CreateRouterWithNullProviders();
        var target = new MachineTarget(Guid.NewGuid(), TestHost, OsType.Linux,
            MachineConnectionMode.Agentless, (TransportProtocol)99, 22, Guid.Empty);
        var command = new RemoteCommand("echo test", TimeSpan.FromSeconds(5));

        var act = () => router.ExecuteAsync(target, command);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Creates a router where the resilience pipeline passes through to the delegate.
    /// Provider parameters are null since we only test error paths that don't invoke providers.
    /// </summary>
    private static RemoteExecutorRouter CreateRouterWithNullProviders()
    {
        var resilience = Substitute.For<IResiliencePipeline>();
        var correlation = Substitute.For<ICorrelationContext>();
        correlation.CorrelationId.Returns("test");

        // Resilience pipeline passes through
        resilience.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<RemoteResult>>>(),
            Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Func<CancellationToken, Task<RemoteResult>>>(1)(CancellationToken.None));

        resilience.ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Func<CancellationToken, Task<bool>>>(1)(CancellationToken.None));

        return new RemoteExecutorRouter(
            null!, null!, null!, resilience, correlation,
            NullLogger<RemoteExecutorRouter>.Instance);
    }
}
