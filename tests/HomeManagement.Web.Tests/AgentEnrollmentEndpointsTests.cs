using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Security.Cryptography;
using FluentAssertions;
using Grpc.Core;
using HomeManagement.Agent.Protocol;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Broker.Host.Endpoints;
using HomeManagement.AgentGateway.Host.Services;
using HomeManagement.Transport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Web.Tests;

public sealed class AgentEnrollmentEndpointsTests
{
    private static readonly Guid MachineId = Guid.NewGuid();
    private static readonly string Path = $"/api/machines/{MachineId}/agent-enrollment";
    private static AgentEnrollmentMetadata Metadata() =>
        new("explicit-node", "node.example.test", "node", MachineId, AgentEnrollmentState.Enabled, 5, 2, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("", "https", HttpStatusCode.Unauthorized)]
    [InlineData("Viewer", "https", HttpStatusCode.Forbidden)]
    [InlineData("Operator", "https", HttpStatusCode.Forbidden)]
    [InlineData("Admin", "http", HttpStatusCode.Forbidden)]
    [InlineData("Admin", "https", HttpStatusCode.OK)]
    public async Task ActualHttp_EnforcesAdminAndTlsForReadAndMutation(string role, string scheme, HttpStatusCode expected)
    {
        var enrollment = Control();
        await using var app = await App(enrollment);
        using var client = Client(app, role, scheme);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var read = await client.GetAsync(Path);
        using var change = await client.PostAsJsonAsync(Path + "/transition", new AgentEnrollmentChange(3, AgentEnrollmentTransition.Revoke));
        read.StatusCode.Should().Be(expected);
        change.StatusCode.Should().Be(expected);
        if (expected != HttpStatusCode.OK)
            await enrollment.DidNotReceiveWithAnyArgs().ChangeEnrollmentAsync(default, default!, default);
    }

    [Fact]
    public async Task RouteMachineAndDisplayedVersion_AreAuthoritative()
    {
        var enrollment = Control();
        var audit = Substitute.For<IAuditLogger>();
        await using var app = await App(enrollment, audit);
        using var client = Client(app);
        using var response = await client.PostAsJsonAsync(Path + "/transition",
            new { expectedVersion = 2, transition = 1, machineId = Guid.NewGuid(), agentId = "guessed-name" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        await enrollment.Received(1).ChangeEnrollmentAsync(MachineId, new(2, AgentEnrollmentTransition.Revoke), Arg.Any<CancellationToken>());
        await enrollment.DidNotReceiveWithAnyArgs().GetEnrollmentAsync(default, default);
        Received.InOrder(() =>
        {
            audit.RecordAsync(Arg.Is<AuditEvent>(e => e.Outcome == AuditOutcome.PartialSuccess && e.TargetMachineId == MachineId), Arg.Any<CancellationToken>());
            enrollment.ChangeEnrollmentAsync(MachineId, Arg.Any<AgentEnrollmentChange>(), Arg.Any<CancellationToken>());
            audit.RecordAsync(Arg.Is<AuditEvent>(e => e.Outcome == AuditOutcome.Success), Arg.Any<CancellationToken>());
        });
        (await response.Content.ReadAsStringAsync()).Should().NotContain("credential\":").And.NotContain("apiKey");
    }

    [Fact]
    public async Task AuditIntentFailure_PreventsMutation()
    {
        var enrollment = Control();
        var audit = Substitute.For<IAuditLogger>();
        audit.RecordAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("test audit failure")));
        await using var app = await App(enrollment, audit);
        using var client = Client(app);
        using var response = await client.PostAsJsonAsync(Path + "/transition", new AgentEnrollmentChange(1, AgentEnrollmentTransition.Rotate));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await enrollment.DidNotReceiveWithAnyArgs().ChangeEnrollmentAsync(default, default!, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MutationOrCompletionFailure_IsReportedWithoutReplay(bool mutationFails)
    {
        var enrollment = Control();
        var audit = Substitute.For<IAuditLogger>();
        if (mutationFails)
            enrollment.ChangeEnrollmentAsync(Arg.Any<Guid>(), Arg.Any<AgentEnrollmentChange>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<AgentEnrollmentMetadata>(new HttpRequestException("unsafe upstream details", null, HttpStatusCode.Conflict)));
        else
            audit.RecordAsync(Arg.Is<AuditEvent>(e => e.Outcome == AuditOutcome.Success), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new IOException("unsafe audit details")));
        await using var app = await App(enrollment, audit);
        using var client = Client(app);
        using var response = await client.PostAsJsonAsync(Path + "/transition", new AgentEnrollmentChange(3, AgentEnrollmentTransition.Revoke));
        response.StatusCode.Should().Be(mutationFails ? HttpStatusCode.Conflict : HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("unsafe").And.Contain("Refresh");
        await enrollment.Received(1).ChangeEnrollmentAsync(MachineId, Arg.Any<AgentEnrollmentChange>(), Arg.Any<CancellationToken>());
        if (mutationFails)
            await audit.Received(1).RecordAsync(Arg.Is<AuditEvent>(e => e.Outcome == AuditOutcome.Failure), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"expectedVersion":4}""")]
    [InlineData("""{"expectedVersion":0,"transition":0}""")]
    [InlineData("""{"expectedVersion":4,"transition":99}""")]
    [InlineData("null")]
    public async Task MissingOrInvalidConfirmation_CannotMutate(string body)
    {
        var enrollment = Control();
        await using var app = await App(enrollment);
        using var client = Client(app);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(Path + "/transition", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await enrollment.DidNotReceiveWithAnyArgs().ChangeEnrollmentAsync(default, default!, default);
    }

    [Theory]
    [InlineData(false, "node.example.test", HttpStatusCode.BadRequest)]
    [InlineData(true, "different.example.test", HttpStatusCode.BadRequest)]
    [InlineData(true, "NODE.EXAMPLE.TEST.", HttpStatusCode.OK)]
    public async Task AssignmentAndRebind_MustMatchAuthoritativeInventory(bool agentTransport, string hostname, HttpStatusCode status)
    {
        var enrollment = Control();
        await using var app = await App(enrollment, agentTransport: agentTransport);
        using var client = Client(app);
        using var assignment = await client.PostAsJsonAsync(Path + "/assign", new MachineAgentEnrollmentAssignment("explicit-node", hostname, "node"));
        using var rebind = await client.PostAsJsonAsync(Path + "/rebind", new AgentEnrollmentRebind(4, hostname, "node"));
        assignment.StatusCode.Should().Be(status);
        rebind.StatusCode.Should().Be(status);
        if (status != HttpStatusCode.OK)
        {
            await enrollment.DidNotReceiveWithAnyArgs().AssignEnrollmentAsync(default, default!, default);
            await enrollment.DidNotReceiveWithAnyArgs().RebindEnrollmentAsync(default, default!, default);
        }
    }

    [Theory]
    [InlineData("", "https", HttpStatusCode.Unauthorized)]
    [InlineData("Viewer", "http", HttpStatusCode.Forbidden)]
    [InlineData("Viewer", "https", HttpStatusCode.OK)]
    [InlineData("Operator", "https", HttpStatusCode.OK)]
    public async Task LiveObservation_IsMachineReadNotAdministration(string role, string scheme, HttpStatusCode expected)
    {
        var runtime = Substitute.For<IMachineAgentRuntime>();
        runtime.GetMachineObservationAsync(MachineId, Arg.Any<CancellationToken>()).Returns(
            new MachineAgentObservation(Guid.NewGuid(), MachineId, "explicit", 1, 1, null, false, DateTimeOffset.UtcNow));
        await using var app = await App(Control(), runtime: runtime);
        using var client = Client(app, role, scheme);
        using var response = await client.GetAsync($"/api/machines/{MachineId}/agent-runtime/observation");
        response.StatusCode.Should().Be(expected);
        if (expected != HttpStatusCode.OK)
            await runtime.DidNotReceiveWithAnyArgs().GetMachineObservationAsync(default, default);
    }

    [Theory]
    [InlineData(true, true, HttpStatusCode.NotFound)]
    [InlineData(false, false, HttpStatusCode.BadRequest)]
    public async Task LiveObservation_RejectsDeletedAndNonAgentInventoryBeforeGateway(bool deleted, bool agent, HttpStatusCode expected)
    {
        var runtime = Substitute.For<IMachineAgentRuntime>();
        await using var app = await App(Control(), agentTransport: agent, runtime: runtime, deleted: deleted);
        using var client = Client(app, "Viewer");
        using var response = await client.GetAsync($"/api/machines/{MachineId}/agent-runtime/observation");
        response.StatusCode.Should().Be(expected);
        await runtime.DidNotReceiveWithAnyArgs().GetMachineObservationAsync(default, default);
    }

    [Theory]
    [InlineData("rotate")]
    [InlineData("revoke")]
    [InlineData("rebind")]
    [InlineData("replace-session")]
    public async Task CoupledBrokerTransportGateway_LifecycleAndLiveObservationUseRealHttpSerialization(string interveningChange)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hm-coupled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var state = System.IO.Path.Combine(root, "state");
        var wrap = System.IO.Path.Combine(root, "wrap");
        var provision = System.IO.Path.Combine(root, "provision");
        Directory.CreateDirectory(state);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(wrap, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        File.WriteAllText(provision, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        try
        {
            EncryptedAgentEnrollmentStore.Initialize(state, wrap, [new("explicit-node", "node.example.test", "node", MachineId)]);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentGateway:ApiKey"] = key,
                ["AgentGateway:Enrollment:Enabled"] = "true",
                ["AgentGateway:Enrollment:Directory"] = state,
                ["AgentGateway:Enrollment:WrappingKeyPath"] = wrap,
                ["AgentGateway:Enrollment:ProvisioningKeyPath"] = provision
            });
            builder.Services.AddOptions<AgentGatewayHostOptions>().BindConfiguration("AgentGateway");
            builder.Services.AddGatewayAgentAuthority(builder.Configuration);
            await using var gateway = builder.Build();
            HomeManagement.AgentGateway.Host.Endpoints.AgentEnrollmentEndpoints.UseAgentEnrollmentBoundary(gateway);
            HomeManagement.AgentGateway.Host.Endpoints.ControlPlaneApiKeyMiddlewareExtensions.UseControlPlaneApiKeyAuthentication(gateway);
            HomeManagement.AgentGateway.Host.Endpoints.ControlPlaneEndpoints.MapControlPlaneEndpoints(gateway);
            HomeManagement.AgentGateway.Host.Endpoints.AgentEnrollmentEndpoints.MapAgentEnrollmentEndpoints(gateway);
            gateway.InitializeGatewayAgentAuthority();
            await gateway.StartAsync();
            using var loss = new LostCommandResponseHandler(gateway.GetTestServer().CreateHandler());
            using var transport = new RemoteAgentGatewayClient(Options.Create(new AgentGatewayClientOptions
            {
                BaseUrl = "https://gateway.test",
                ApiKey = key,
                EnrollmentEnabled = true
            }), NullLogger<RemoteAgentGatewayClient>.Instance, loss);
            var provider = new AgentTransportProvider(transport, NullLogger<AgentTransportProvider>.Instance, transport);
            var executor = new RemoteExecutorRouter(null!, null!, provider,
                new DefaultResiliencePipeline(NullLogger<DefaultResiliencePipeline>.Instance, Options.Create(new ResilienceOptions())),
                Substitute.For<ICorrelationContext>(), NullLogger<RemoteExecutorRouter>.Instance);
            await using var broker = await App(transport, runtime: transport, executor: executor);
            using var client = Client(broker);
            var registry = gateway.Services.GetRequiredService<AgentEnrollmentRegistry>();
            var service = gateway.Services.GetRequiredService<StandaloneAgentGatewayService>();
            var session = service.AuthenticateAndRegister(Substitute.For<IAgentApiKeyValidator>(),
                new EnrollmentCallContext(registry.Ensure("explicit-node", "node.example.test", "node").Credential),
                new Handshake { AgentId = "explicit-node", Hostname = "node" },
                Substitute.For<IServerStreamWriter<ControlMessage>>(), true, () => { });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var control = client.PostAsJsonAsync($"/api/machines/{MachineId}/managed-services/control",
                new ManagedServiceRequest("app", ManagedServiceAction.Start), timeout.Token);
            var outbound = await session.Outbound.Reader.ReadAsync(timeout.Token);
            outbound.Binding!.MachineId.Should().Be(MachineId);
            outbound.Message.CommandRequest.CommandType.Should().Be("ManagedServices");
            await service.WriteOutboundAsync(session, outbound, session.ResponseStream, timeout.Token);
            service.CompleteCommand(session, new() { RequestId = outbound.Message.CommandRequest.RequestId, Stdout = "[]" });
            using var dispatched = await control;
            dispatched.StatusCode.Should().Be(HttpStatusCode.OK);
            var target = new MachineTarget(MachineId, Hostname.Create("misleading"), OsType.Linux,
                MachineConnectionMode.Agent, TransportProtocol.Agent, 443, Guid.Empty);
            (await provider.TestConnectionAsync(target, timeout.Token)).Reachable.Should().BeTrue();
            await transport.RequestMachineUpdateAsync(target, new("2", [], "https://example.test/agent"), timeout.Token);
            var update = await session.Outbound.Reader.ReadAsync(timeout.Token);
            await service.WriteOutboundAsync(session, update, session.ResponseStream, timeout.Token);
            update.Message.UpdateDirective.TargetVersion.Should().Be("2");
            var metadata = await client.GetFromJsonAsync<AgentEnrollmentMetadata>(Path);
            metadata!.MachineId.Should().Be(MachineId);
            using var rotated = await client.PostAsJsonAsync(Path + "/transition", new AgentEnrollmentChange(1, AgentEnrollmentTransition.Rotate));
            rotated.StatusCode.Should().Be(HttpStatusCode.OK);
            (await rotated.Content.ReadFromJsonAsync<AgentEnrollmentMetadata>())!.Version.Should().Be(2);
            using var stale = await client.PostAsJsonAsync(Path + "/transition", new AgentEnrollmentChange(1, AgentEnrollmentTransition.Revoke));
            stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
            using var live = await client.GetAsync($"/api/machines/{MachineId}/agent-runtime/observation");
            live.StatusCode.Should().Be(HttpStatusCode.OK);
            (await live.Content.ReadFromJsonAsync<MachineAgentObservation>())!.Connected.Should().BeFalse();
            using var revoked = await client.PostAsJsonAsync(Path + "/transition", new AgentEnrollmentChange(2, AgentEnrollmentTransition.Revoke));
            revoked.StatusCode.Should().Be(HttpStatusCode.OK);
            using var unavailable = await client.GetAsync($"/api/machines/{MachineId}/agent-runtime/observation");
            unavailable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            using var rebound = await client.PostAsJsonAsync(Path + "/rebind", new AgentEnrollmentRebind(3, "node.example.test", "replacement"));
            rebound.StatusCode.Should().Be(HttpStatusCode.OK);
            (await rebound.Content.ReadFromJsonAsync<AgentEnrollmentMetadata>())!.State.Should().Be(AgentEnrollmentState.Revoked);
            using var reenabled = await client.PostAsJsonAsync(Path + "/transition", new AgentEnrollmentChange(4, AgentEnrollmentTransition.Reenable));
            reenabled.StatusCode.Should().Be(HttpStatusCode.OK);
            var final = await client.GetFromJsonAsync<AgentEnrollmentMetadata>(Path);
            final!.Version.Should().Be(5);
            final.ExpectedWireHostname.Should().Be("replacement");
            (await reenabled.Content.ReadAsStringAsync()).Should().NotContain("credential\":");

            var currentKey = registry.Ensure("explicit-node", "node.example.test", "replacement").Credential;
            var current = service.AuthenticateAndRegister(Substitute.For<IAgentApiKeyValidator>(),
                new EnrollmentCallContext(currentKey), new Handshake { AgentId = "explicit-node", Hostname = "replacement" },
                Substitute.For<IServerStreamWriter<ControlMessage>>(), true, () => { });
            AgentGatewaySession? replacement = null;
            loss.AfterHandoff = () =>
            {
                if (interveningChange == "replace-session") service.UnregisterAgent(current.AgentId, current);
                else registry.Transition("explicit-node", 5,
                    interveningChange == "rotate" ? AgentEnrollmentTransition.Rotate : AgentEnrollmentTransition.Revoke);
                if (interveningChange == "rebind")
                {
                    registry.Rebind("explicit-node", 6, "node.example.test", "replacement", MachineId);
                    registry.Transition("explicit-node", 7, AgentEnrollmentTransition.Reenable);
                }
                if (interveningChange != "revoke")
                    replacement = service.AuthenticateAndRegister(Substitute.For<IAgentApiKeyValidator>(),
                        new EnrollmentCallContext(registry.Ensure("explicit-node", "node.example.test", "replacement").Credential),
                        new Handshake { AgentId = "explicit-node", Hostname = "replacement" },
                        Substitute.For<IServerStreamWriter<ControlMessage>>(), true, () => { });
            };
            var before = loss.CommandPosts;
            using var lostBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var uncertain = executor.ExecuteAsync(target, new("synthetic", TimeSpan.FromSeconds(45), CommandType: "ManagedServices"), lostBudget.Token);
            var handedOff = await current.Outbound.Reader.ReadAsync(lostBudget.Token);
            await service.WriteOutboundAsync(current, handedOff, current.ResponseStream, lostBudget.Token);
            service.CompleteCommand(current, new() { RequestId = handedOff.Message.CommandRequest.RequestId, Stdout = "already-delivered" });
            await Assert.ThrowsAsync<HttpRequestException>(() => uncertain);
            loss.CommandPosts.Should().Be(before + 1);
            if (replacement is not null) replacement.Outbound.Reader.TryRead(out _).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class LostCommandResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public Action? AfterHandoff { get; set; }
        public int CommandPosts { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var command = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/commands", StringComparison.Ordinal);
            if (command) CommandPosts++;
            var response = await base.SendAsync(request, cancellationToken);
            if (command && AfterHandoff is { } change)
            {
                response.Dispose();
                change();
                throw new HttpRequestException("Synthetic response loss after actual Gateway handoff and lifecycle change.");
            }
            return response;
        }
    }

    private static IAgentEnrollmentControl Control()
    {
        var control = Substitute.For<IAgentEnrollmentControl>();
        control.GetEnrollmentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Metadata());
        control.ChangeEnrollmentAsync(Arg.Any<Guid>(), Arg.Any<AgentEnrollmentChange>(), Arg.Any<CancellationToken>()).Returns(Metadata());
        control.AssignEnrollmentAsync(Arg.Any<Guid>(), Arg.Any<MachineAgentEnrollmentAssignment>(), Arg.Any<CancellationToken>()).Returns(Metadata());
        control.RebindEnrollmentAsync(Arg.Any<Guid>(), Arg.Any<AgentEnrollmentRebind>(), Arg.Any<CancellationToken>()).Returns(Metadata());
        return control;
    }

    private static async Task<WebApplication> App(IAgentEnrollmentControl enrollment, IAuditLogger? audit = null, bool agentTransport = true,
        IMachineAgentRuntime? runtime = null, bool deleted = false, IRemoteExecutor? executor = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, RoleHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        var inventory = Substitute.For<IInventoryService>();
        inventory.GetAsync(MachineId, Arg.Any<CancellationToken>()).Returns(new Machine(
            MachineId, Hostname.Create("node"), "node.example.test", [], OsType.Linux, "test",
            MachineConnectionMode.Agent, agentTransport ? TransportProtocol.Agent : TransportProtocol.Ssh, 443, Guid.Empty,
            MachineState.Offline, new Dictionary<string, string>(), null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, IsDeleted: deleted));
        builder.Services.AddSingleton(inventory);
        builder.Services.AddSingleton(enrollment);
        builder.Services.AddSingleton(audit ?? Substitute.For<IAuditLogger>());
        builder.Services.AddSingleton(Substitute.For<ICorrelationContext>());
        builder.Services.AddSingleton(runtime ?? Substitute.For<IMachineAgentRuntime>());
        builder.Services.AddSingleton(executor ?? Substitute.For<IRemoteExecutor>());
        builder.Services.AddSingleton(Substitute.For<IAgentPeeringControl>());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAgentEnrollmentEndpoints();
        app.MapMachineAgentRuntimeEndpoints();
        app.MapManagedServiceEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class EnrollmentCallContext(string key) : ServerCallContext
    {
        protected override string MethodCore => "AgentHub/Connect";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddSeconds(10);
        protected override Metadata RequestHeadersCore => new() { { "x-agent-api-key", key } };
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new("", []);
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }

    private static HttpClient Client(WebApplication app, string role = "Admin", string scheme = "https")
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri($"{scheme}://localhost");
        if (role.Length > 0) client.DefaultRequestHeaders.Add("Test-Role", role);
        return client;
    }

    private sealed class RoleHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["Test-Role"].ToString();
            if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test")), "Test")));
        }
    }
}
