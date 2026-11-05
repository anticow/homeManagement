using System.Reflection;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Web.Components.Shared;
using HomeManagement.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace HomeManagement.Web.Tests;

public sealed class FleetMachineTableTests
{
    [Fact]
    public async Task InitialLoadAndExplicitRefreshReadOnlyStoredProjectionAndLabelIndependentSources()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Finish(await fixture.Begin());
        var html = await fixture.Html();
        html.Should().Contain("Refresh stored fleet view").And.Contain("Unknown").And.Contain("Last known: Online")
            .And.Contain("stale / unknown").And.Contain("2026-11-04 15:58:00 UTC")
            .And.Contain("Prometheus/exporter metrics").And.Contain("12.5% (last known)")
            .And.Contain("Prometheus/exporter metrics unavailable.").And.Contain("Historical inventory state");
        await fixture.Api.Received(2).GetFleetDeviceStatesAsync(1, 25, Arg.Any<CancellationToken>());
        fixture.Api.ReceivedCalls().Should().OnlyContain(call => call.GetMethodInfo().Name == nameof(IBrokerApi.GetFleetDeviceStatesAsync));
    }

    [Fact]
    public async Task MachinesPageDoesNotPreloadLiveAction1OrOtherEvidenceSources()
    {
        var api = Substitute.For<IBrokerApi>();
        api.GetFleetDeviceStatesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Data()));
        await using var services = new ServiceCollection().AddLogging().AddSingleton(api)
            .AddSingleton<NavigationManager>(new Navigation()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<HomeManagement.Web.Components.Pages.Machines>());
        api.ReceivedCalls().Should().ContainSingle()
            .Which.GetMethodInfo().Name.Should().Be(nameof(IBrokerApi.GetFleetDeviceStatesAsync));
    }

    [Fact]
    public async Task ReadFailureDoesNotInventOfflineOrExposeUpstreamDetails()
    {
        await using var fixture = await Fixture.Create();
        fixture.Api.GetFleetDeviceStatesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PagedResult<FleetDeviceState>>(new HttpRequestException("unsafe details")));
        await fixture.Finish(await fixture.Begin());
        (await fixture.Html()).Should().Contain("Stored fleet view unavailable").And.NotContain("unsafe details").And.NotContain("Offline");
    }

    [Fact]
    public async Task OlderReadCannotOverwriteNewRefreshAndDisposalCancelsInflightRead()
    {
        await using var fixture = await Fixture.Create();
        var older = new TaskCompletionSource<PagedResult<FleetDeviceState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new TaskCompletionSource<PagedResult<FleetDeviceState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken olderToken = default;
        CancellationToken latestToken = default;
        fixture.Api.GetFleetDeviceStatesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => { olderToken = call.ArgAt<CancellationToken>(2); return older.Task; },
                call => { latestToken = call.ArgAt<CancellationToken>(2); return latest.Task; });
        var first = await fixture.Begin();
        var second = await fixture.Begin();
        olderToken.IsCancellationRequested.Should().BeTrue();
        latest.SetResult(Data("latest-node"));
        await fixture.Finish(second);
        older.SetResult(Data("old-node"));
        await fixture.Finish(first);
        (await fixture.Html()).Should().Contain("latest-node").And.NotContain("old-node");

        var disposed = new TaskCompletionSource<PagedResult<FleetDeviceState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.GetFleetDeviceStatesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => { latestToken = call.ArgAt<CancellationToken>(2); return disposed.Task; });
        var last = await fixture.Begin();
        await fixture.DisposePanel();
        latestToken.IsCancellationRequested.Should().BeTrue();
        disposed.SetResult(Data("disposed-node"));
        await fixture.Finish(last);
        (await fixture.Html()).Should().NotContain("disposed-node");
    }

    [Fact]
    public async Task NonAgentDoesNotDisplayDisconnectedAndPaginationUsesDatabaseEndpoint()
    {
        await using var fixture = await Fixture.Create();
        fixture.Api.GetFleetDeviceStatesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Data("ssh-node", DeviceConnectivity.NotApplicable)));
        await fixture.Finish(await fixture.Begin());
        (await fixture.Html()).Should().Contain("Not applicable (non-agent transport)").And.NotContain("Disconnected");
        await fixture.Finish(await fixture.Begin("NextAsync"));
        await fixture.Api.Received(1).GetFleetDeviceStatesAsync(2, 25, Arg.Any<CancellationToken>());
    }

    private static PagedResult<FleetDeviceState> Data(string hostname = "node", DeviceConnectivity connectivity = DeviceConnectivity.Unknown)
    {
        var time = new DateTimeOffset(2026, 11, 4, 16, 0, 0, TimeSpan.Zero);
        var machine = new Machine(Guid.NewGuid(), Hostname.Create(hostname), null, [], OsType.Linux, "Linux",
            MachineConnectionMode.Agent, TransportProtocol.Agent, 0, Guid.Empty, MachineState.Maintenance,
            new Dictionary<string, string>(), null, default, default, default);
        var snapshot = new DeviceStateSnapshot
        {
            MachineId = machine.Id,
            LastKnownConnected = true,
            AgentSuccessUtc = time,
            LastHeartbeatUtc = time.AddMinutes(-2),
            Health = new() { Id = machine.Id, IsOnline = true, CpuUsagePercent = 12.5 },
            MetricsFailure = "Prometheus/exporter metrics unavailable."
        };
        return new([new(machine, snapshot, connectivity, 1500, true, 1500, true)], 30, 1, 25);
    }

    private sealed record Pending(Task Task);
    private sealed class Fixture(ServiceProvider services, HtmlRenderer renderer, Capture capture, HtmlRootComponent root,
        IBrokerApi api) : IAsyncDisposable
    {
        public IBrokerApi Api => api;
        public static async Task<Fixture> Create()
        {
            var api = Substitute.For<IBrokerApi>();
            api.GetFleetDeviceStatesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Data()));
            var capture = new Capture();
            var services = new ServiceCollection().AddLogging().AddSingleton(api)
                .AddSingleton<IComponentActivator>(capture).BuildServiceProvider();
            var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<FleetMachineTable>());
            return new(services, renderer, capture, root, api);
        }

        public Task<Pending> Begin(string method = "RefreshAsync") => renderer.Dispatcher.InvokeAsync(() =>
        {
            var task = Invoke(method) as Task ?? throw new InvalidOperationException("Expected an async method.");
            Invoke("StateHasChanged", typeof(ComponentBase));
            return new Pending(task);
        });

        public async Task Finish(Pending pending)
        {
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await renderer.Dispatcher.InvokeAsync(() => Invoke("StateHasChanged", typeof(ComponentBase)));
        }

        public Task<string> Html() => renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        public Task DisposePanel() => renderer.Dispatcher.InvokeAsync(capture.Panel.Dispose);
        private object? Invoke(string method, Type? type = null) =>
            (type ?? typeof(FleetMachineTable)).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(capture.Panel, null);

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class Capture : IComponentActivator
    {
        private FleetMachineTable? _panel;
        public FleetMachineTable Panel => _panel ?? throw new InvalidOperationException("Panel not created.");
        public IComponent CreateInstance(Type type)
        {
            var component = Activator.CreateInstance(type) as IComponent ?? throw new InvalidOperationException("Invalid component.");
            if (component is FleetMachineTable panel) _panel = panel;
            return component;
        }
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("https://console.test/", "https://console.test/machines");
    }
}
