using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Web.Components.Shared;
using HomeManagement.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace HomeManagement.Web.Tests;

public sealed class AgentEnrollmentPanelTests
{
    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Operator", false)]
    public async Task InitialRender_IsMetadataOnlyAdminSurfaceWithoutAutomaticEnrollment(string role, bool visible)
    {
        await using var fixture = await Fixture.Create(role);
        if (visible) fixture.Html.Should().Contain("Refresh enrollment");
        else fixture.Html.Should().NotContain("Refresh enrollment");
        fixture.Api.ReceivedCalls().Should().BeEmpty();
        fixture.Html.Should().NotContain("test-secret");
    }

    [Fact]
    public async Task Rotation_RequiresConfirmationAndUsesFrozenDisplayedVersion()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Act("RefreshAsync");
        await fixture.Act("Select", AgentEnrollmentTransition.Rotate);
        fixture.Html.Should().Contain("version 7").And.Contain("Confirm").And.Contain("AWX");
        await fixture.Api.DidNotReceiveWithAnyArgs().ChangeAgentEnrollmentAsync(default, default!, default);
        await fixture.Act("ConfirmAsync");
        await fixture.Api.Received(1).ChangeAgentEnrollmentAsync(Fixture.MachineId, new(7, AgentEnrollmentTransition.Rotate), Arg.Any<CancellationToken>());
        await fixture.Act("ConfirmAsync");
        await fixture.Api.Received(1).ChangeAgentEnrollmentAsync(Fixture.MachineId, Arg.Any<AgentEnrollmentChange>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UncertainMutation_RefreshesMetadataAndClearsConfirmationWithoutReplay()
    {
        await using var fixture = await Fixture.Create();
        fixture.Api.ChangeAgentEnrollmentAsync(Arg.Any<Guid>(), Arg.Any<AgentEnrollmentChange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AgentEnrollmentMetadata>(new HttpRequestException("private upstream error")));
        await fixture.Act("RefreshAsync");
        await fixture.Act("Select", AgentEnrollmentTransition.Revoke);
        await fixture.Act("ConfirmAsync");
        fixture.Html.Should().Contain("outcome is uncertain").And.NotContain("private upstream error");
        await fixture.Api.Received(2).GetAgentEnrollmentAsync(Fixture.MachineId, Arg.Any<CancellationToken>());
        await fixture.Act("ConfirmAsync");
        await fixture.Api.Received(1).ChangeAgentEnrollmentAsync(Fixture.MachineId, Arg.Any<AgentEnrollmentChange>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlaintextBrowser_CannotReadOrMutateEnrollment()
    {
        await using var fixture = await Fixture.Create(scheme: "http");
        await fixture.Act("RefreshAsync");
        fixture.Api.ReceivedCalls().Should().BeEmpty();
        fixture.Html.Should().Contain("failed");
    }

    [Fact]
    public async Task RevokedRebind_FreezesExplicitHostnamesAndVersionForConfirmation()
    {
        await using var fixture = await Fixture.Create(state: AgentEnrollmentState.Revoked);
        await fixture.Act("RefreshAsync");
        fixture.Html.Should().Contain("Re-enable").And.Contain("Rebind").And.NotContain("Rotate credential");
        fixture.Set("_wire", "replacement-node");
        await fixture.Act("SelectRebind");
        fixture.Set("_wire", "changed-after-selection");
        await fixture.Act("ConfirmAsync");
        await fixture.Api.Received(1).RebindAgentEnrollmentAsync(Fixture.MachineId,
            new(7, "node.example.test", "replacement-node"), Arg.Any<CancellationToken>());
    }

    private sealed class Fixture(ServiceProvider provider, HtmlRenderer renderer, CaptureActivator activator,
        HtmlRootComponent root, IBrokerApi api) : IAsyncDisposable
    {
        public static readonly Guid MachineId = Guid.NewGuid();
        public IBrokerApi Api => api;
        public string Html { get; private set; } = "";

        public static async Task<Fixture> Create(string role = "Admin", string scheme = "https",
            AgentEnrollmentState state = AgentEnrollmentState.Enabled)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthorizationCore();
            services.AddCascadingAuthenticationState();
            services.AddSingleton<AuthenticationStateProvider>(new State(role));
            services.AddSingleton<NavigationManager>(new Navigation(scheme));
            var api = Substitute.For<IBrokerApi>();
            var metadata = new AgentEnrollmentMetadata("explicit-node", "node.example.test", "node", MachineId,
                state, 7, 3, DateTimeOffset.UtcNow);
            api.GetAgentEnrollmentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(metadata);
            api.ChangeAgentEnrollmentAsync(Arg.Any<Guid>(), Arg.Any<AgentEnrollmentChange>(), Arg.Any<CancellationToken>()).Returns(metadata);
            api.RebindAgentEnrollmentAsync(Arg.Any<Guid>(), Arg.Any<AgentEnrollmentRebind>(), Arg.Any<CancellationToken>()).Returns(metadata);
            services.AddSingleton(api);
            var activator = new CaptureActivator();
            services.AddSingleton<IComponentActivator>(activator);
            var provider = services.BuildServiceProvider();
            var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<AgentEnrollmentPanel>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(AgentEnrollmentPanel.MachineId)] = MachineId,
                    [nameof(AgentEnrollmentPanel.InventoryHostname)] = "node.example.test"
                })));
            var fixture = new Fixture(provider, renderer, activator, root, api);
            fixture.Html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            return fixture;
        }

        public void Set(string field, object value) =>
            typeof(AgentEnrollmentPanel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activator.Panel, value);

        public Task Act(string name, params object[] args) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var method = typeof(AgentEnrollmentPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (method.Invoke(activator.Panel, args) is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activator.Panel, null);
            Html = root.ToHtmlString();
        });

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class CaptureActivator : IComponentActivator
    {
        public AgentEnrollmentPanel? Panel { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is AgentEnrollmentPanel panel) Panel = panel;
            return component;
        }
    }

    private sealed class State(string role) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"))));
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation(string scheme) => Initialize($"{scheme}://console.test/", $"{scheme}://console.test/machines");
    }
}
