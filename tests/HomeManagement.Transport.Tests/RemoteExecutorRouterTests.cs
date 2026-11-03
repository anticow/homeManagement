using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

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

    [Theory]
    [InlineData("Shell", false)]
    [InlineData("ManagedServices", false)]
    [InlineData("Shell", true)]
    [InlineData("ManagedServices", true)]
    public async Task ManagedProductionRouter_NeverReplaysHandoffFailureOrCancellation(string type, bool canceled)
    {
        using var handler = new ManagedCommandHandler(async ct =>
        {
            if (canceled) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new HttpRequestException("Synthetic lost response after handoff and identity replacement.");
        });
        using var client = ManagedClient(handler);
        var router = ManagedRouter(client, TimeSpan.FromMilliseconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Func<Task> action = () => router.ExecuteAsync(ManagedTarget(),
            new("test", TimeSpan.FromSeconds(45), CommandType: type), cancellation.Token);
        if (canceled) await action.Should().ThrowAsync<OperationCanceledException>();
        else await action.Should().ThrowAsync<HttpRequestException>();
        handler.CommandPosts.Should().Be(1);
    }

    [Fact]
    public async Task ManagedProductionRouter_UsesCommandBudgetNotGenericAttemptTimeout()
    {
        using var handler = new ManagedCommandHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new RemoteResult(0, "once", "", TimeSpan.Zero, false)) };
        });
        using var client = ManagedClient(handler);
        var router = ManagedRouter(client, TimeSpan.FromMilliseconds(5));
        (await router.ExecuteAsync(ManagedTarget(), new("test", TimeSpan.FromSeconds(45), CommandType: "ManagedServices")))
            .Stdout.Should().Be("once");
        handler.CommandPosts.Should().Be(1);
    }

    private static MachineTarget ManagedTarget() => new(Guid.NewGuid(), TestHost, OsType.Linux,
        MachineConnectionMode.Agent, TransportProtocol.Agent, 443, Guid.Empty);

    private static RemoteAgentGatewayClient ManagedClient(HttpMessageHandler handler) =>
        new(Options.Create(new AgentGatewayClientOptions
        {
            BaseUrl = "https://gateway.test",
            ApiKey = "synthetic-key",
            EnrollmentEnabled = true
        }), NullLogger<RemoteAgentGatewayClient>.Instance, handler);

    private static RemoteExecutorRouter ManagedRouter(RemoteAgentGatewayClient client, TimeSpan attemptTimeout) =>
        new(null!, null!, new AgentTransportProvider(client, NullLogger<AgentTransportProvider>.Instance, client),
            new DefaultResiliencePipeline(NullLogger<DefaultResiliencePipeline>.Instance,
                Options.Create(new ResilienceOptions { OperationTimeout = attemptTimeout, RetryBaseDelay = TimeSpan.Zero })),
            Substitute.For<ICorrelationContext>(), NullLogger<RemoteExecutorRouter>.Instance);

    private sealed class ManagedCommandHandler(Func<CancellationToken, Task<HttpResponseMessage>> handoff) : HttpMessageHandler
    {
        public int CommandPosts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/capabilities", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentEnrollmentCapabilities(true)) });
            if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/commands", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected runtime request.");
            CommandPosts++;
            return handoff(cancellationToken);
        }
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
