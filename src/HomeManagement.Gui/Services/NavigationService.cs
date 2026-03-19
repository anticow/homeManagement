using HomeManagement.Gui.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace HomeManagement.Gui.Services;

/// <summary>
/// Manages page navigation by resolving ViewModels from the DI container
/// and exposing the current page as an observable property.
/// Thread-safe with bounded back stack.
/// </summary>
public sealed class NavigationService : ReactiveObject
{
    private readonly IServiceProvider _services;
    private readonly Stack<ViewModelBase> _backStack = new();
    private readonly object _navigationLock = new();
    private const int MaxBackStackDepth = 20;
    private ViewModelBase? _currentPage;

    public NavigationService(IServiceProvider services)
    {
        _services = services;
    }

    public ViewModelBase? CurrentPage
    {
        get => _currentPage;
        private set => this.RaiseAndSetIfChanged(ref _currentPage, value);
    }

    public bool CanGoBack
    {
        get { lock (_navigationLock) { return _backStack.Count > 0; } }
    }

    /// <summary>
    /// Navigate to a new page. The previous page is pushed onto the back stack.
    /// </summary>
    public void NavigateTo<T>() where T : ViewModelBase
    {
        lock (_navigationLock)
        {
            PushCurrentToBackStack();
            CurrentPage = _services.GetRequiredService<T>();
        }
    }

    /// <summary>
    /// Navigate to a specific ViewModel instance (for parameterized pages).
    /// </summary>
    public void NavigateTo(ViewModelBase viewModel)
    {
        lock (_navigationLock)
        {
            PushCurrentToBackStack();
            CurrentPage = viewModel;
        }
    }

    /// <summary>
    /// Go back to the previous page, disposing the current one.
    /// </summary>
    public void GoBack()
    {
        lock (_navigationLock)
        {
            if (_backStack.Count == 0) return;

            var old = CurrentPage;
            CurrentPage = _backStack.Pop();
            (old as IDisposable)?.Dispose();
        }
    }

    private void PushCurrentToBackStack()
    {
        if (CurrentPage is not null)
        {
            _backStack.Push(CurrentPage);

            // Evict oldest entry to prevent unbounded memory growth (one push = at most one eviction)
            if (_backStack.Count > MaxBackStackDepth)
            {
                var items = _backStack.ToArray();
                _backStack.Clear();
                // items[0] is top (most recent), items[^1] is bottom (oldest)
                (items[^1] as IDisposable)?.Dispose();
                for (var i = items.Length - 2; i >= 0; i--)
                    _backStack.Push(items[i]);
            }
        }
    }
}
