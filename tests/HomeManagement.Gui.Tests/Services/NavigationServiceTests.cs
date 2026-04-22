using FluentAssertions;
using HomeManagement.Gui.Services;
using HomeManagement.Gui.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Gui.Tests.Services;

public sealed class NavigationServiceTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly NavigationService _nav;

    public NavigationServiceTests()
    {
        var services = new ServiceCollection();
        services.AddTransient<StubViewModelA>();
        services.AddTransient<StubViewModelB>();
        _provider = services.BuildServiceProvider();
        _nav = new NavigationService(_provider);
    }

    [Fact]
    public void Initial_CurrentPage_IsNull()
    {
        _nav.CurrentPage.Should().BeNull();
        _nav.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void NavigateTo_Generic_SetsCurrentPage()
    {
        _nav.NavigateTo<StubViewModelA>();
        _nav.CurrentPage.Should().BeOfType<StubViewModelA>();
    }

    [Fact]
    public void NavigateTo_Instance_SetsCurrentPage()
    {
        var vm = new StubViewModelA();
        _nav.NavigateTo(vm);
        _nav.CurrentPage.Should().BeSameAs(vm);
    }

    [Fact]
    public void NavigateTo_PushesPreviousToBackStack()
    {
        _nav.NavigateTo<StubViewModelA>();
        _nav.NavigateTo<StubViewModelB>();

        _nav.CurrentPage.Should().BeOfType<StubViewModelB>();
        _nav.CanGoBack.Should().BeTrue();
    }

    [Fact]
    public void GoBack_RestoresPreviousPage()
    {
        _nav.NavigateTo<StubViewModelA>();
        var firstPage = _nav.CurrentPage;

        _nav.NavigateTo<StubViewModelB>();
        _nav.GoBack();

        _nav.CurrentPage.Should().BeSameAs(firstPage);
    }

    [Fact]
    public void GoBack_OnEmptyStack_DoesNothing()
    {
        _nav.NavigateTo<StubViewModelA>();
        var page = _nav.CurrentPage;

        _nav.GoBack(); // back stack empty — should be no-op
        _nav.CurrentPage.Should().BeSameAs(page);
    }

    [Fact]
    public void GoBack_DisposesCurrentPage()
    {
        _nav.NavigateTo<StubViewModelA>();
        var disposable = new DisposableViewModel();
        _nav.NavigateTo(disposable);

        _nav.GoBack();

        disposable.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public void BackStack_BoundedAt20()
    {
        // Navigate 25 times — back stack should cap at 20
        for (var i = 0; i < 25; i++)
            _nav.NavigateTo<StubViewModelA>();

        // Navigate back 20 times (max depth)
        var backCount = 0;
        while (_nav.CanGoBack)
        {
            _nav.GoBack();
            backCount++;
        }

        backCount.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public async Task NavigateTo_ThreadSafe_NoCrash()
    {
        // Concurrent navigation should not throw
        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            _nav.NavigateTo<StubViewModelA>();
            _nav.NavigateTo<StubViewModelB>();
            if (_nav.CanGoBack) _nav.GoBack();
        }));

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }

    public void Dispose() => _provider.Dispose();

    // ── Stub ViewModels ──

    public sealed class StubViewModelA : ViewModelBase;
    public sealed class StubViewModelB : ViewModelBase;

    public sealed class DisposableViewModel : ViewModelBase
    {
        public bool WasDisposed { get; private set; }
        public override void Dispose()
        {
            WasDisposed = true;
            base.Dispose();
        }
    }
}
