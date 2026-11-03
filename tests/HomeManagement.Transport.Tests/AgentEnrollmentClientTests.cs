using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeManagement.Transport.Tests;

public sealed class AgentEnrollmentClientTests
{
    private static readonly Guid MachineId = Guid.NewGuid();
    private static AgentEnrollmentMetadata Metadata(Guid? machineId = null) =>
        new("explicit-node", "node.example.test", "node", machineId ?? MachineId, AgentEnrollmentState.Enabled, 9, 3, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ModeMismatch_FailsWithoutLifecycleMutation(bool enabled, bool serverEnabled)
    {
        using var handler = new Handler(_ => Json(new AgentEnrollmentCapabilities(serverEnabled)));
        using var client = Client(handler, enabled);
        var action = () => client.ChangeEnrollmentAsync(MachineId, new(9, AgentEnrollmentTransition.Revoke));
        await action.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Plaintext_IsRejectedBeforeSendingCredentials()
    {
        using var handler = new Handler(_ => Json(Metadata()));
        var construct = () => Client(handler, true, "http://localhost");
        construct.Should().Throw<InvalidOperationException>();
        using var client = Client(handler, false, "http://localhost");
        await ((Func<Task>)(() => client.GetEnrollmentAsync(MachineId))).Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task MalformedCapabilities_NeverDowngradeToLegacy(string body)
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var client = Client(handler, false);
        await ((Func<Task>)(() => client.RevokeAsync("node"))).Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task FailedCapabilities_NeverRetryOrMutate(HttpStatusCode status)
    {
        using var handler = new Handler(_ => new(status) { Headers = { Location = new Uri("https://elsewhere.test") } });
        using var client = Client(handler);
        await ((Func<Task>)(() => client.ChangeEnrollmentAsync(MachineId, new(9, AgentEnrollmentTransition.Revoke))))
            .Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task OfflineBinding_UsesExplicitIdAndOriginalConfirmedVersion()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("capabilities", StringComparison.Ordinal)
            ? Json(new AgentEnrollmentCapabilities(true)) : Json(Metadata() with { Version = 4 }));
        using var client = Client(handler);
        var result = await client.ChangeEnrollmentAsync(MachineId, new(4, AgentEnrollmentTransition.Revoke));
        result.AgentId.Should().Be("explicit-node");
        handler.Requests.Select(r => r.Path).Should().Equal("/internal/agents/capabilities",
            $"/internal/agents/enrollments/machines/{MachineId:D}", "/internal/agents/enrollments/explicit-node/transition");
        using var body = JsonDocument.Parse(handler.Requests.Last().Body);
        body.RootElement.GetProperty("expectedVersion").GetInt64().Should().Be(4);
        handler.Requests.Should().OnlyContain(r => r.Key == "test-control-key");
    }

    [Fact]
    public async Task ChangedVersion_RequiresRefreshRatherThanSubstitutingCurrentVersion()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("capabilities", StringComparison.Ordinal)
            ? Json(new AgentEnrollmentCapabilities(true)) : Json(Metadata()));
        using var client = Client(handler);
        await ((Func<Task>)(() => client.ChangeEnrollmentAsync(MachineId, new(4, AgentEnrollmentTransition.Revoke))))
            .Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task WrongMachineBinding_CannotDispatchMutation()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("capabilities", StringComparison.Ordinal)
            ? Json(new AgentEnrollmentCapabilities(true)) : Json(Metadata(Guid.NewGuid())));
        using var client = Client(handler);
        await ((Func<Task>)(() => client.ChangeEnrollmentAsync(MachineId, new(9, AgentEnrollmentTransition.Rotate))))
            .Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task MutationFailure_IsNeverReplayed(HttpStatusCode failure)
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Post ? new(failure)
            : request.RequestUri!.AbsolutePath.EndsWith("capabilities", StringComparison.Ordinal)
                ? Json(new AgentEnrollmentCapabilities(true)) : Json(Metadata()));
        using var client = Client(handler);
        await ((Func<Task>)(() => client.ChangeEnrollmentAsync(MachineId, new(9, AgentEnrollmentTransition.Rotate))))
            .Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1);
    }

    [Fact]
    public async Task LegacyRevoke_RefusesManagedServerEvenWithClientFlagOff()
    {
        using var handler = new Handler(_ => Json(new AgentEnrollmentCapabilities(true)));
        using var client = Client(handler, false);
        await ((Func<Task>)(() => client.RevokeAsync("node"))).Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task OversizedMetadata_IsRejected()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 16 * 1024 + 1)) });
        using var client = Client(handler);
        await ((Func<Task>)(() => client.GetEnrollmentAsync(MachineId))).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutRetry()
    {
        using var handler = new Handler(_ => throw new OperationCanceledException());
        using var client = Client(handler);
        await ((Func<Task>)(() => client.GetEnrollmentAsync(MachineId))).Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().ContainSingle();
    }

    private static MachineTarget Target() => new(MachineId, Hostname.Create("misleading-hostname"),
        OsType.Linux, MachineConnectionMode.Agent, TransportProtocol.Agent, 443, Guid.Empty);

    [Theory]
    [InlineData("Shell")]
    [InlineData("ManagedServices")]
    public async Task ManagedRuntime_UsesGuidRouteForOrdinaryAndWorkloadCommands(string type)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("capabilities", StringComparison.Ordinal)
            ? Json(new AgentEnrollmentCapabilities(true))
            : Json(new RemoteResult(0, "bound", "", TimeSpan.Zero, false)));
        using var client = Client(handler);
        (await client.SendMachineCommandAsync(Target(), new("", TimeSpan.FromSeconds(1), CommandType: type))).Stdout.Should().Be("bound");
        handler.Requests.Select(r => r.Path).Should().Equal("/internal/agents/capabilities",
            $"/internal/agents/machines/{MachineId:D}/commands");
        handler.Requests.Should().NotContain(r => r.Path.Contains("misleading-hostname"));
    }

    [Fact]
    public async Task ManagedRuntime_MetadataUpdateAndObservationNeverUseLegacySnapshots()
    {
        using var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("capabilities", StringComparison.Ordinal)) return Json(new AgentEnrollmentCapabilities(true));
            if (path.EndsWith("metadata", StringComparison.Ordinal))
                return Json(new AgentMetadata("explicit", "node", OsType.Linux, "test", new(1, 1, [], "x64")));
            if (path.EndsWith("updates", StringComparison.Ordinal)) return new(HttpStatusCode.Accepted);
            return Json(new MachineAgentObservation(Guid.NewGuid(), MachineId, "explicit", 1, 1, null, false, DateTimeOffset.UtcNow));
        });
        using var client = Client(handler);
        (await client.GetMachineMetadataAsync(Target())).AgentId.Should().Be("explicit");
        await client.RequestMachineUpdateAsync(Target(), new("2", [], "https://example.test/agent"));
        (await client.GetMachineObservationAsync(MachineId)).Connected.Should().BeFalse();
        handler.Requests.Should().HaveCount(6);
        handler.Requests.Should().OnlyContain(r => r.Path.EndsWith("capabilities") || r.Path.StartsWith($"/internal/agents/machines/{MachineId:D}/"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RuntimeMismatch_PreventsAllRuntimeAndStartupFallback(bool enabled, bool server)
    {
        using var handler = new Handler(_ => Json(new AgentEnrollmentCapabilities(server)));
        using var client = Client(handler, enabled);
        Func<Task>[] actions =
        [
            () => client.StartAsync(), () => client.SendMachineCommandAsync(Target(), new("", TimeSpan.FromSeconds(1))),
            () => client.GetMachineMetadataAsync(Target()), () => client.RequestMachineUpdateAsync(Target(), new("2", [], "https://example.test")),
            () => client.GetMachineObservationAsync(MachineId), () => client.GetMetadataAsync("hostname"),
            () => client.SendCommandAsync("hostname", new("", TimeSpan.FromSeconds(1))), () => client.RequestUpdateAsync("hostname", new("2", [], "https://example.test"))
        ];
        foreach (var action in actions) await action.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().OnlyContain(r => r.Path == "/internal/agents/capabilities");
    }

    [Fact]
    public async Task ManagedStartup_DoesNotPollAndContradictoryServerModeFailsNextRequest()
    {
        var managed = true;
        using var handler = new Handler(_ => Json(new AgentEnrollmentCapabilities(managed)));
        using var client = Client(handler);
        await client.StartAsync();
        handler.Requests.Should().ContainSingle();
        ((Action)(() => client.GetConnectedAgents())).Should().Throw<InvalidOperationException>();
        managed = false;
        await ((Func<Task>)(() => client.GetMachineMetadataAsync(Target()))).Should().ThrowAsync<InvalidOperationException>();
        await client.StopAsync();
        handler.Requests.Should().OnlyContain(r => r.Path == "/internal/agents/capabilities");
    }

    [Theory]
    [InlineData("ManagedServices")]
    [InlineData("managedservices")]
    [InlineData("MANAGEDSERVICES")]
    public async Task LegacyWorkloadClassification_PreservesHostnameAsIdWithoutSnapshotLookup(string type)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/capabilities", StringComparison.Ordinal)
            ? Json(new AgentEnrollmentCapabilities(false))
            : request.Method == HttpMethod.Post
                ? Json(new RemoteResult(0, "", "", TimeSpan.Zero, false))
                : Json(new List<ConnectedAgent>()));
        using var client = Client(handler, false);
        await client.SendMachineCommandAsync(Target(), new("", TimeSpan.FromSeconds(1), CommandType: type));
        handler.Requests.Should().NotContain(r => r.Path.Contains("/machines/"));
        handler.Requests.Last().Path.Should().Be("/internal/agents/misleading-hostname/commands");
        await client.StopAsync();
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static RemoteAgentGatewayClient Client(HttpMessageHandler handler, bool enabled = true, string url = "https://gateway.test") =>
        new(Options.Create(new AgentGatewayClientOptions { BaseUrl = url, ApiKey = "test-control-key", EnrollmentEnabled = enabled }),
            NullLogger<RemoteAgentGatewayClient>.Instance, handler);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, string Key)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.GetValues("x-agent-gateway-api-key").Single()));
            return respond(request);
        }
    }
}
