using System.Reflection;
using FluentAssertions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Web.Components.Shared;
using HomeManagement.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace HomeManagement.Web.Tests;

public sealed class MachineAgentStatusPanelTests
{
    [Fact]
    public async Task NewRefreshInvalidatesOldSuccessAndOlderCompletionCannotOverwriteLatest()
    {
        await using var fixture = await Fixture.Create();
        var old = new TaskCompletionSource<MachineAgentObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<MachineAgentObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.GetMachineAgentObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(old.Task, current.Task);
        var first = await fixture.Begin();
        var second = await fixture.Begin();
        fixture.Html.Should().Contain("Refreshing").And.NotContain("connected observed");
        current.SetResult(fixture.Observation("new-authority"));
        await fixture.Finish(second);
        fixture.Html.Should().Contain("new-authority");
        old.SetResult(fixture.Observation("old-authority"));
        await fixture.Finish(first);
        fixture.Html.Should().Contain("new-authority").And.NotContain("old-authority");
    }

    [Fact]
    public async Task FailedRefreshClearsPriorSuccessAndDoesNotInventOffline()
    {
        await using var fixture = await Fixture.Create();
        fixture.Api.GetMachineAgentObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fixture.Observation("previous")), Task.FromException<MachineAgentObservation>(new HttpRequestException("unsafe")));
        await fixture.Finish(await fixture.Begin());
        fixture.Html.Should().Contain("previous");
        await fixture.Finish(await fixture.Begin());
        fixture.Html.Should().Contain("unavailable").And.NotContain("previous").And.NotContain("unsafe");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationAndDisposalInvalidateInflightObservation(bool dispose)
    {
        await using var fixture = await Fixture.Create();
        var pending = new TaskCompletionSource<MachineAgentObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.GetMachineAgentObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        var refresh = await fixture.Begin();
        await fixture.Invalidate(dispose);
        pending.SetResult(fixture.Observation("stale-machine"));
        await fixture.Finish(refresh);
        fixture.Html.Should().NotContain("stale-machine");
    }

    private sealed record Pending(Task Task);
    private sealed class Fixture(ServiceProvider services, HtmlRenderer renderer, Capture activator, HtmlRootComponent root,
        IBrokerApi api) : IAsyncDisposable
    {
        private readonly Guid _machine = activator.Panel!.MachineId;
        public IBrokerApi Api => api;
        public string Html { get; private set; } = "";
        public MachineAgentObservation Observation(string id) =>
            new(Guid.NewGuid(), _machine, id, 1, 1, Guid.NewGuid(), true, DateTimeOffset.UtcNow);

        public static async Task<Fixture> Create()
        {
            var collection = new ServiceCollection().AddLogging();
            var api = Substitute.For<IBrokerApi>();
            collection.AddSingleton(api);
            collection.AddSingleton<NavigationManager>(new Navigation());
            var capture = new Capture();
            collection.AddSingleton<IComponentActivator>(capture);
            var services = collection.BuildServiceProvider();
            var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MachineAgentStatusPanel>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(MachineAgentStatusPanel.MachineId)] = Guid.NewGuid() })));
            return new(services, renderer, capture, root, api);
        }

        public Task<Pending> Begin() => renderer.Dispatcher.InvokeAsync(() =>
        {
            var task = (Task)typeof(MachineAgentStatusPanel).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Panel, null)!;
            Render();
            return new Pending(task);
        });

        public async Task Finish(Pending pending)
        {
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await renderer.Dispatcher.InvokeAsync(Render);
        }

        public Task Invalidate(bool dispose) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (dispose) activator.Panel!.Dispose();
            else
            {
                await activator.Panel!.SetParametersAsync(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(MachineAgentStatusPanel.MachineId)] = Guid.NewGuid() }));
            }
            Render();
        });

        private void Render()
        {
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activator.Panel, null);
            Html = root.ToHtmlString();
        }

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class Capture : IComponentActivator
    {
        public MachineAgentStatusPanel? Panel { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is MachineAgentStatusPanel panel) Panel = panel;
            return component;
        }
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("https://console.test/", "https://console.test/machines");
    }
}
