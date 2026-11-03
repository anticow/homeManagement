using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using HomeManagement.Agent.Protocol;
using HomeManagement.AgentGateway.Host.Endpoints;
using HomeManagement.AgentGateway.Host.Services;
using HomeManagement.Abstractions.Models;
using HomeManagement.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using NSubstitute;

namespace HomeManagement.AgentGateway.Host.Tests;

public sealed class AgentEnrollmentHttpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hm-enrollment-http-{Guid.NewGuid():N}");
    private readonly string _state;
    private readonly string _wrap;
    private readonly string _provision;
    private readonly string _controlKey = NewKey();
    private readonly string _provisionKey = NewKey();
    private readonly Guid _machine = Guid.NewGuid();

    public AgentEnrollmentHttpTests()
    {
        _state = Path.Combine(_root, "state");
        _wrap = Path.Combine(_root, "wrapping-key");
        _provision = Path.Combine(_root, "provisioning-key");
        Directory.CreateDirectory(_state);
        File.WriteAllText(_wrap, NewKey());
        File.WriteAllText(_provision, _provisionKey);
        EncryptedAgentEnrollmentStore.Initialize(_state, _wrap,
            [new("assigned-id", "inventory.example", "wire", _machine), new("rejected", "rejected.example", "rejected", Revoked: true)]);
    }

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private WebApplication Build(bool enabled = true, Dictionary<string, string?>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var config = new Dictionary<string, string?>
        {
            ["AgentGateway:ApiKey"] = _controlKey,
            ["AgentGateway:Enrollment:Enabled"] = enabled.ToString(),
            ["AgentGateway:Enrollment:Directory"] = _state,
            ["AgentGateway:Enrollment:WrappingKeyPath"] = _wrap,
            ["AgentGateway:Enrollment:ProvisioningKeyPath"] = _provision
        };
        if (overrides is not null) foreach (var (key, value) in overrides) config[key] = value;
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddOptions<AgentGatewayHostOptions>().BindConfiguration("AgentGateway")
            .Validate(AgentGatewayHostOptions.HasValidControlPlaneApiKey).ValidateOnStart();
        builder.Services.AddGatewayAgentAuthority(builder.Configuration);
        builder.Services.AddHealthChecks();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddPrometheusExporter());
        var app = builder.Build();
        app.UseAgentEnrollmentBoundary();
        app.UseControlPlaneApiKeyAuthentication();
        app.UseHomeManagementHealthEndpoints(taggedReadiness: enabled);
        app.MapControlPlaneEndpoints();
        app.MapAgentEnrollmentEndpoints();
        return app;
    }

    private async Task<WebApplication> Start(bool enabled = true)
    {
        var app = Build(enabled);
        app.InitializeGatewayAgentAuthority();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, string scheme = "https")
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri($"{scheme}://localhost");
        return client;
    }

    private static async Task<HttpResponseMessage> Ensure(HttpClient client, string? key = null,
        string id = "assigned-id", string inventory = "inventory.example", string wire = "wire")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentEnrollmentEndpoints.ProvisioningPath)
        {
            Content = JsonContent.Create(new { agentId = id, inventoryHostname = inventory, expectedWireHostname = wire })
        };
        if (key is not null) request.Headers.Add(AgentEnrollmentEndpoints.ProvisioningHeader, key);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task EnsureIsIdempotentAndMetadataDoesNotExposeCredentials()
    {
        await using var app = await Start();
        using var client = Client(app);
        using var first = await Ensure(client, _provisionKey);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.CacheControl!.NoStore.Should().BeTrue();
        var enrollment = await first.Content.ReadFromJsonAsync<AgentProvisioningResponse>();
        enrollment!.Metadata.AgentId.Should().Be("assigned-id");
        enrollment.Metadata.MachineId.Should().Be(_machine);
        using var second = await Ensure(client, _provisionKey);
        (await second.Content.ReadFromJsonAsync<AgentProvisioningResponse>()).Should().Be(enrollment);
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        using var metadata = await client.GetAsync($"{AgentEnrollmentEndpoints.EnrollmentPath}/machines/{_machine}");
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await metadata.Content.ReadAsStringAsync();
        text.Should().NotContain(enrollment.Credential).And.NotContain("credential\":");
        using var capabilities = await client.GetAsync("/internal/agents/capabilities");
        (await capabilities.Content.ReadAsStringAsync()).Should().Contain("\"enrollmentEnabled\":true");
    }

    [Theory]
    [InlineData("control", "https", HttpStatusCode.OK)]
    [InlineData("provision", "https", HttpStatusCode.Unauthorized)]
    [InlineData("none", "https", HttpStatusCode.Unauthorized)]
    [InlineData("control", "http", HttpStatusCode.Forbidden)]
    public async Task BoundRuntime_RequiresControlAuthorityAndRealRequestScheme(string authority, string scheme, HttpStatusCode expected)
    {
        await using var app = await Start();
        using var client = Client(app, scheme);
        if (authority == "control") client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        if (authority == "provision") client.DefaultRequestHeaders.Add(AgentEnrollmentEndpoints.ProvisioningHeader, _provisionKey);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.GetAsync($"/internal/agents/machines/{_machine}/observation");
        response.StatusCode.Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Theory]
    [InlineData("Shell")]
    [InlineData("ManagedServices")]
    public async Task RuntimeHttp_UsesBoundSessionForCommandsMetadataUpdatesAndObservation(string type)
    {
        await using var app = await Start();
        var registry = app.Services.GetRequiredService<AgentEnrollmentRegistry>();
        var gateway = app.Services.GetRequiredService<StandaloneAgentGatewayService>();
        var session = gateway.AuthenticateAndRegister(Substitute.For<IAgentApiKeyValidator>(),
            new TestServerCallContext(new Metadata { { "x-agent-api-key", registry.Ensure("assigned-id", "inventory.example", "wire").Credential } }),
            new Handshake { AgentId = "assigned-id", Hostname = "wire" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true, () => { });
        using var client = Client(app);
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        var path = $"/internal/agents/machines/{_machine}";
        using var metadata = await client.GetAsync(path + "/metadata");
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        (await metadata.Content.ReadFromJsonAsync<AgentMetadata>())!.AgentId.Should().Be("assigned-id");
        using var observation = await client.GetAsync(path + "/observation");
        (await observation.Content.ReadFromJsonAsync<MachineAgentObservation>())!.SessionId.Should().Be(session.SessionId);
        var command = client.PostAsJsonAsync(path + "/commands", new RemoteCommand("", TimeSpan.FromSeconds(5), CommandType: type));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outbound = await session.Outbound.Reader.ReadAsync(timeout.Token);
        await gateway.WriteOutboundAsync(session, outbound, session.ResponseStream, timeout.Token);
        gateway.CompleteCommand(session, new() { RequestId = outbound.Message.CommandRequest.RequestId, Stdout = "bound-response" });
        using var response = await command;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RemoteResult>())!.Stdout.Should().Be("bound-response");
        using var updated = await client.PostAsJsonAsync(path + "/updates", new AgentUpdatePackage("2", [], "https://example.test/agent"));
        updated.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var update = await session.Outbound.Reader.ReadAsync(timeout.Token);
        await gateway.WriteOutboundAsync(session, update, session.ResponseStream, timeout.Token);
        update.Message.UpdateDirective.TargetVersion.Should().Be("2");
        using var unknown = await client.GetAsync($"/internal/agents/machines/{Guid.NewGuid()}/metadata");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        registry.Transition("assigned-id", 1, AgentEnrollmentTransition.Revoke);
        using var revoked = await client.GetAsync(path + "/observation");
        revoked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        app.Services.GetService<RevokedAgentStore>().Should().BeNull();
        app.Services.GetService<AgentApiKeyValidator>().Should().BeNull();
    }

    [Theory]
    [InlineData("""{"commandText":"test","timeout":"-00:00:10"}""")]
    [InlineData("""{"commandText":"test","timeout":"00:00:00"}""")]
    [InlineData("""{"commandText":"test","timeout":"00:00:00.500"}""")]
    [InlineData("""{"commandText":"test","timeout":"1.00:00:01"}""")]
    [InlineData("""{"commandText":"test","timeout":"10675199.02:48:05.4775807"}""")]
    [InlineData("""{"commandText":"test","timeout":"999999999999.00:00:00"}""")]
    [InlineData("""{"commandText":"test","timeout":"00:00:10","commandType":null}""")]
    [InlineData("""{"commandText":null,"timeout":"00:00:10"}""")]
    [InlineData("""{"timeout":"00:00:10"}""")]
    [InlineData("""{"commandText":"test"}""")]
    [InlineData("""{"commandText":"test","timeout":"00:00:10","elevation":99}""")]
    [InlineData("""{"commandText":"test","timeout":"00:00:10","environmentVariables":{"TEST":null}}""")]
    public async Task InvalidRuntimeCommand_IsRejectedBeforeAnyPendingRegistrationOrQueueAdmission(string json)
    {
        await AssertInvalidRuntimeRequest("commands", json);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"version":null,"binarySha256":"","downloadUrl":"https://example.test/agent"}""")]
    [InlineData("""{"version":"2","binarySha256":null,"downloadUrl":"https://example.test/agent"}""")]
    [InlineData("""{"version":"2","binarySha256":"","downloadUrl":null}""")]
    [InlineData("""{"version":"2","binarySha256":"AA==","downloadUrl":"https://example.test/agent"}""")]
    [InlineData("""{"version":"2","binarySha256":"","downloadUrl":"file:///agent"}""")]
    public async Task InvalidUpdate_IsRejectedWithoutQueueAdmission(string json) =>
        await AssertInvalidRuntimeRequest("updates", json);

    private async Task AssertInvalidRuntimeRequest(string operation, string json)
    {
        await using var app = await Start();
        var registry = app.Services.GetRequiredService<AgentEnrollmentRegistry>();
        var gateway = app.Services.GetRequiredService<StandaloneAgentGatewayService>();
        var session = gateway.AuthenticateAndRegister(Substitute.For<IAgentApiKeyValidator>(),
            new TestServerCallContext(new Metadata { { "x-agent-api-key", registry.Ensure("assigned-id", "inventory.example", "wire").Credential } }),
            new Handshake { AgentId = "assigned-id", Hostname = "wire" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true, () => { });
        using var client = Client(app);
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/internal/agents/machines/{_machine}/{operation}", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("operation failed").And.NotContain("NullReference");
        session.Outbound.Reader.TryRead(out _).Should().BeFalse();
        session.PendingCommands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("00:00:01", 1)]
    [InlineData("00:00:01.001", 2)]
    [InlineData("1.00:00:00", 86400)]
    public async Task RuntimeTimeoutBounds_AreNormalizedBeforeDispatch(string timeout, int seconds)
    {
        await using var app = await Start();
        var registry = app.Services.GetRequiredService<AgentEnrollmentRegistry>();
        var gateway = app.Services.GetRequiredService<StandaloneAgentGatewayService>();
        var session = gateway.AuthenticateAndRegister(Substitute.For<IAgentApiKeyValidator>(),
            new TestServerCallContext(new Metadata { { "x-agent-api-key", registry.Ensure("assigned-id", "inventory.example", "wire").Credential } }),
            new Handshake { AgentId = "assigned-id", Hostname = "wire" },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), true, () => { });
        using var client = Client(app);
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        using var content = new StringContent($$"""{"commandText":"","timeout":"{{timeout}}"}""", Encoding.UTF8, "application/json");
        var responseTask = client.PostAsync($"/internal/agents/machines/{_machine}/commands", content);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outbound = await session.Outbound.Reader.ReadAsync(budget.Token);
        outbound.Message.CommandRequest.TimeoutSeconds.Should().Be(seconds);
        gateway.CompleteCommand(session, new() { RequestId = outbound.Message.CommandRequest.RequestId });
        using var response = await responseTask;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("control", 401)]
    [InlineData("invalid", 401)]
    [InlineData("provision", 200)]
    public async Task EnsureRequiresOnlyDedicatedProvisioningAuthority(string? authority, int status)
    {
        await using var app = await Start();
        using var client = Client(app);
        using var response = await Ensure(client, authority switch { "control" => _controlKey, "provision" => _provisionKey, _ => authority });
        ((int)response.StatusCode).Should().Be(status);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Theory]
    [InlineData("/internal/agents/enrollments")]
    [InlineData("/internal/agents/capabilities")]
    [InlineData("/internal/agents/assigned-id/revoke")]
    [InlineData("/internal/agents/assigned-id/commands")]
    public async Task ProvisioningKeyCannotActAsControlKey(string path)
    {
        await using var app = await Start();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _provisionKey);
        using var response = await client.PostAsJsonAsync(path, new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task DuplicateHeadersHttpAndForwardedSchemeCannotAuthorize()
    {
        await using var app = await Start();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add(AgentEnrollmentEndpoints.ProvisioningHeader, new[] { _provisionKey, _provisionKey });
        using var duplicate = await Ensure(client);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, new[] { _controlKey, _controlKey });
        using var control = await client.GetAsync("/internal/agents/enrollments");
        control.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var http = Client(app, "http");
        http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var plaintext = await Ensure(http, _provisionKey);
        plaintext.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        plaintext.Headers.CacheControl!.NoStore.Should().BeTrue();
        http.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        using var plaintextAdmin = await http.GetAsync("/internal/agents/enrollments");
        plaintextAdmin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("GET", "/internal/agent-provisioning/ensure")]
    [InlineData("DELETE", "/internal/agent-provisioning/ensure")]
    [InlineData("POST", "/internal/agent-provisioning/ensure/")]
    [InlineData("POST", "/internal/agent-provisioning/ensure/extra")]
    [InlineData("POST", "/internal/agent-provisioning-evil/ensure")]
    public async Task ProvisioningRequiresExactMethodAndPath(string method, string path)
    {
        await using var app = await Start();
        using var client = Client(app);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(AgentEnrollmentEndpoints.ProvisioningHeader, _provisionKey);
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureRejectsBindingUnknownRevokedMalformedAndOversizedInput()
    {
        await using var app = await Start();
        using var client = Client(app);
        using var wrong = await Ensure(client, _provisionKey, inventory: "other.example");
        wrong.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var unknown = await Ensure(client, _provisionKey, id: "unknown");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var revoked = await Ensure(client, _provisionKey, id: "rejected", inventory: "rejected.example", wire: "rejected");
        revoked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        client.DefaultRequestHeaders.Add(AgentEnrollmentEndpoints.ProvisioningHeader, _provisionKey);
        foreach (var body in new[] { "{", "{}", "{\"agentId\":\"secret-value\"}", new string('x', 8193) })
        {
            using var response = await client.PostAsync(AgentEnrollmentEndpoints.ProvisioningPath, new StringContent(body, Encoding.UTF8, "application/json"));
            response.StatusCode.Should().Be(body.Length > 8192 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            (await response.Content.ReadAsStringAsync()).Should().NotContain("secret-value");
        }
        using var streamed = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 8193))));
        streamed.Headers.ContentType = new("application/json");
        using var chunked = await client.PostAsync(AgentEnrollmentEndpoints.ProvisioningPath, streamed);
        chunked.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        chunked.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task ManagedLifecycleRequiresCasAndLegacyReinstateCannotBypassFreshKey()
    {
        await using var app = await Start();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        using var noVersion = await client.PostAsJsonAsync("/internal/agents/assigned-id/revoke", new { reason = "operator" });
        noVersion.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var missingAction = await client.PostAsJsonAsync("/internal/agents/enrollments/assigned-id/transition", new { expectedVersion = 1 });
        missingAction.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var rotated = await client.PostAsJsonAsync("/internal/agents/enrollments/assigned-id/transition", new { expectedVersion = 1, transition = 0 });
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        using var stale = await client.PostAsJsonAsync("/internal/agents/assigned-id/revoke", new { expectedVersion = 1 });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var rejected = await client.PostAsJsonAsync("/internal/agents/assigned-id/revoke", new { expectedVersion = 2 });
        rejected.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reinstate = await client.DeleteAsync("/internal/agents/assigned-id/revoke");
        reinstate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var denied = await Ensure(client, _provisionKey);
        denied.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var reenabled = await client.PostAsJsonAsync("/internal/agents/enrollments/assigned-id/transition", new { expectedVersion = 3, transition = 2 });
        reenabled.StatusCode.Should().Be(HttpStatusCode.OK);
        using var provision = await Ensure(client, _provisionKey);
        provision.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdministrativeAssignmentRebindAndConflictsUseMetadataOnly()
    {
        await using var app = await Start();
        using var client = Client(app);
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        var assignment = new { agentId = "second", inventoryHostname = "second.example", expectedWireHostname = "second" };
        using var created = await client.PostAsJsonAsync("/internal/agents/enrollments", assignment);
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        (await created.Content.ReadAsStringAsync()).Should().NotContain("\"credential\":");
        using var duplicate = await client.PostAsJsonAsync("/internal/agents/enrollments", assignment);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var rebind = new { expectedVersion = 1, inventoryHostname = "rebound.example", expectedWireHostname = "rebound", machineId = (Guid?)null };
        using var activeRebind = await client.PostAsJsonAsync("/internal/agents/enrollments/second/rebind", rebind);
        activeRebind.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var revoked = await client.PostAsJsonAsync("/internal/agents/enrollments/second/transition", new { expectedVersion = 1, transition = 1 });
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rebound = await client.PostAsJsonAsync("/internal/agents/enrollments/second/rebind", rebind with { expectedVersion = 2 });
        rebound.StatusCode.Should().Be(HttpStatusCode.OK);
        using var denied = await Ensure(client, _provisionKey, id: "second", inventory: "rebound.example", wire: "rebound");
        denied.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DisabledModeIsExplicitAndPreservesLegacyReadiness()
    {
        File.Delete(_provision); // Default-off server must not require enrollment files.
        await using var app = await Start(enabled: false);
        using var client = Client(app);
        using var ensure = await Ensure(client, _provisionKey);
        ensure.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ensure.Headers.CacheControl!.NoStore.Should().BeTrue();
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        using var registry = await client.GetAsync("/internal/agents/enrollments");
        registry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var capability = await client.GetAsync("/internal/agents/capabilities");
        (await capability.Content.ReadAsStringAsync()).Should().Contain("\"enrollmentEnabled\":false");
        using var ready = await client.GetAsync("/readyz");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ActualReadinessIsUnhealthyAfterStoreFaultAndDisposalButLivenessIsNot()
    {
        await using var app = await Start();
        using var client = Client(app);
        using var ready = await client.GetAsync("/readyz");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        File.Delete(Path.Combine(_state, "registry.json"));
        client.DefaultRequestHeaders.Add(ControlPlaneApiKeyMiddleware.HeaderName, _controlKey);
        using var failed = await client.PostAsJsonAsync("/internal/agents/enrollments/assigned-id/transition",
            new { expectedVersion = 1, transition = 0 });
        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        failed.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var faulted = await client.GetAsync("/readyz");
        faulted.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var live = await client.GetAsync("/healthz");
        live.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Services.GetRequiredService<AgentEnrollmentRegistry>().Dispose();
        using var disposed = await client.GetAsync("/readyz");
        disposed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData("missing-state")]
    [InlineData("missing-marker")]
    [InlineData("bad-state")]
    [InlineData("missing-provision")]
    [InlineData("same-control")]
    [InlineData("same-wrap")]
    [InlineData("control-wrap")]
    [InlineData("node-provision")]
    public async Task EagerInitializationRejectsMissingCorruptOrConflictingKeysBeforeServing(string issue)
    {
        if (issue == "missing-state") File.Delete(Path.Combine(_state, "registry.json"));
        if (issue == "missing-marker") File.Delete(Path.Combine(_state, "initialized.json"));
        if (issue == "bad-state") File.WriteAllText(Path.Combine(_state, "registry.json"), "{");
        if (issue == "missing-provision") File.Delete(_provision);
        if (issue == "same-control") File.WriteAllText(_provision, _controlKey);
        if (issue == "same-wrap") File.WriteAllText(_provision, File.ReadAllText(_wrap));
        if (issue == "node-provision")
        {
            using var registry = new AgentEnrollmentRegistry(new EncryptedAgentEnrollmentStore(_state, _wrap));
            File.WriteAllText(_provision, registry.Ensure("assigned-id", "inventory.example", "wire").Credential);
        }
        await using var app = Build(overrides: issue == "control-wrap"
            ? new() { ["AgentGateway:ApiKey"] = File.ReadAllText(_wrap) } : null);
        var initialize = () => app.InitializeGatewayAgentAuthority();
        initialize.Should().Throw<Exception>();
        app.Lifetime.ApplicationStarted.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task RealServiceStartupCannotServeWithMissingInitializedState()
    {
        File.Delete(Path.Combine(_state, "registry.json"));
        var result = await RunExecutable(["--urls", "http://127.0.0.1:0"], new()
        {
            ["AgentGateway__ApiKey"] = _controlKey,
            ["AgentGateway__Enrollment__Enabled"] = "true",
            ["AgentGateway__Enrollment__Directory"] = _state,
            ["AgentGateway__Enrollment__WrappingKeyPath"] = _wrap,
            ["AgentGateway__Enrollment__ProvisioningKeyPath"] = _provision
        });
        result.Code.Should().NotBe(0);
        result.Output.Should().NotContain("Now listening").And.NotContain(_controlKey).And.NotContain(_provisionKey);
    }

    [Fact]
    public async Task ExplicitBootstrapRunsInRealExecutableAndCannotInitializeTwice()
    {
        var empty = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(empty);
        var assignment = Path.Combine(_root, "assignments.json");
        File.WriteAllText(assignment, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            assignments = new[] { new { agentId = "explicit", inventoryHostname = "explicit.example", expectedWireHostname = "wire" } }
        }));
        var arguments = new[] { "--initialize-agent-enrollment", "--directory", empty,
            "--wrapping-key-file", _wrap, "--assignment-file", assignment };
        var first = await RunExecutable(arguments);
        first.Code.Should().Be(0);
        first.Output.Should().Contain("Initialized 1").And.NotContain(File.ReadAllText(_wrap));
        using (var loaded = new AgentEnrollmentRegistry(new EncryptedAgentEnrollmentStore(empty, _wrap)))
            loaded.Ensure("explicit", "explicit.example", "wire").Metadata.Version.Should().Be(1);
        (await RunExecutable(arguments)).Code.Should().Be(1);
        (await RunExecutable(["--initialize-agent-enrollment", "--directory", empty])).Code.Should().Be(1);
        (await RunExecutable([.. arguments, "--urls", "http://localhost:0"])).Code.Should().Be(1);
    }

    private static async Task<(int Code, string Output)> RunExecutable(string[] arguments,
        Dictionary<string, string>? environment = null)
    {
        var dll = typeof(AgentGatewayHostOptions).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dll)!
        };
        start.ArgumentList.Add(dll);
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Gateway fixture.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
