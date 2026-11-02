using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Broker.Host.Endpoints;
using HomeManagement.Broker.Host.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Web.Tests;

public sealed class ManagedServiceEndpointsTests
{
    [Theory]
    [InlineData("Viewer", "https", HttpStatusCode.Forbidden)]
    [InlineData("Operator", "http", HttpStatusCode.Forbidden)]
    [InlineData("Operator", "https", HttpStatusCode.OK)]
    [InlineData("Admin", "https", HttpStatusCode.OK)]
    public async Task Control_RequiresTlsAndControlPermission(string role, string scheme, HttpStatusCode expected)
    {
        var executor = Substitute.For<IRemoteExecutor>();
        executor.ExecuteAsync(Arg.Any<MachineTarget>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, "[]", "", TimeSpan.Zero, false));
        await using var app = await CreateAppAsync(executor);
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri($"{scheme}://localhost");
        client.DefaultRequestHeaders.Add("Test-Role", role);
        using var response = await client.PostAsJsonAsync(
            $"/api/machines/{MachineId}/managed-services/control", new ManagedServiceRequest("app", ManagedServiceAction.Start));
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
        {
            await executor.Received(1).ExecuteAsync(Arg.Any<MachineTarget>(),
                Arg.Is<RemoteCommand>(command => command.CommandType == "ManagedServices"
                    && command.Elevation == ElevationMode.None && !command.CommandText.Contains("ServiceName")),
                Arg.Any<CancellationToken>());
        }
        else
            await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task InvalidActionAndAnonymousRequests_DoNotDispatch()
    {
        var executor = Substitute.For<IRemoteExecutor>();
        await using var app = await CreateAppAsync(executor);
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        using var anonymous = await client.GetAsync($"/api/machines/{MachineId}/managed-services");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("Test-Role", "Operator");
        using var invalid = await client.PostAsJsonAsync($"/api/machines/{MachineId}/managed-services/control",
            new { id = "app", action = 99 });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("Operator", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task BreakPeering_RequiresAdmin(string role, HttpStatusCode expected)
    {
        var peering = Substitute.For<IAgentPeeringControl>();
        await using var app = await CreateAppAsync(Substitute.For<IRemoteExecutor>(), peering);
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add("Test-Role", role);
        using var response = await client.PostAsync($"/api/machines/{MachineId}/managed-services/break-peering", null);
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
            await peering.Received(1).RevokeAsync("appnode", Arg.Any<CancellationToken>());
        else
            await peering.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default);
    }

    private static readonly Guid MachineId = Guid.NewGuid();

    [Fact]
    public async Task GenericAgentEndpoint_CannotBypassManagedServiceAuthorization()
    {
        var executor = Substitute.For<IRemoteExecutor>();
        await using var app = await CreateAppAsync(executor);
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add("Test-Role", "Viewer");
        using var response = await client.PostAsJsonAsync("/api/agents/appnode/commands",
            new RemoteCommand("""{"id":"app","action":0}""", TimeSpan.FromSeconds(10), CommandType: "ManagedServices"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [InlineData(false, HttpStatusCode.OK)]
    public async Task AwxEnrollmentTemplates_AreHiddenAndCannotBeLaunched(bool enrollment, HttpStatusCode expected)
    {
        using var handler = new AwxHandler(enrollment);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://awx.example.invalid") };
        var awx = new AwxClient(http, NullLogger<AwxClient>.Instance);
        await using var app = await CreateAppAsync(Substitute.For<IRemoteExecutor>(), awx: awx);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Test-Role", "Admin");
        var templates = await client.GetFromJsonAsync<List<AwxJobTemplate>>("/api/awx/templates");
        templates.Should().HaveCount(enrollment ? 0 : 1);
        using var response = await client.PostAsync("/api/awx/templates/1/launch", null);
        response.StatusCode.Should().Be(expected);
        handler.LaunchCount.Should().Be(enrollment ? 0 : 1);
    }

    private sealed class AwxHandler(bool enrollment) : HttpMessageHandler
    {
        public int LaunchCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var playbook = enrollment ? "configure-homemanagement-workloads.yml" : "pingtest.yml";
            var template = $$"""{"id":1,"name":"Template","playbook":"{{playbook}}"}""";
            string body;
            if (request.Method == HttpMethod.Post)
            {
                LaunchCount++;
                body = """{"id":42}""";
            }
            else
                body = request.RequestUri!.Query.Length > 0 ? $$"""{"results":[{{template}}]}""" : template;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static async Task<WebApplication> CreateAppAsync(IRemoteExecutor executor, IAgentPeeringControl? peering = null,
        AwxClient? awx = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, RoleHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        var inventory = Substitute.For<IInventoryService>();
        inventory.GetAsync(MachineId, Arg.Any<CancellationToken>()).Returns(new Machine(
            MachineId, Hostname.Create("appnode"), null, [], OsType.Windows, "test",
            MachineConnectionMode.Agent, TransportProtocol.Agent, 443, Guid.Empty, MachineState.Online,
            new Dictionary<string, string>(), null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow));
        builder.Services.AddSingleton(inventory);
        builder.Services.AddSingleton(executor);
        builder.Services.AddSingleton(peering ?? Substitute.For<IAgentPeeringControl>());
        builder.Services.AddSingleton(Substitute.For<IAuditLogger>());
        builder.Services.AddSingleton(Substitute.For<ICorrelationContext>());
        builder.Services.AddSingleton(Substitute.For<IAgentGateway>());
        if (awx is not null)
            builder.Services.AddSingleton(awx);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapManagedServiceEndpoints();
        app.MapAgentEndpoints();
        app.MapAwxEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class RoleHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["Test-Role"].ToString();
            if (string.IsNullOrEmpty(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.Name, "test")], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
