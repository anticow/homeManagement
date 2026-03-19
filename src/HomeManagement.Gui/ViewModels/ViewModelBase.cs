using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

/// <summary>
/// Base class for all ViewModels. Provides IsBusy, ErrorMessage, CanRetry, and
/// disposable tracking via <see cref="CompositeDisposable"/>.
/// </summary>
public abstract class ViewModelBase : ReactiveObject, IDisposable
{
    private bool _isBusy;
    private string? _errorMessage;
    private bool _canRetry;

    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public bool CanRetry
    {
        get => _canRetry;
        set => this.RaiseAndSetIfChanged(ref _canRetry, value);
    }

    protected CompositeDisposable Disposables { get; } = [];

    /// <summary>
    /// Runs an async operation with automatic IsBusy/ErrorMessage management.
    /// Catches operational exceptions for display; re-throws critical CLR errors.
    /// </summary>
    protected async Task RunSafe(Func<Task> action)
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            CanRetry = false;
            await action();
        }
        catch (OperationCanceledException)
        {
            // User cancelled — not an error
        }
        catch (OutOfMemoryException)
        {
            throw; // Critical CLR error — do not swallow
        }
        catch (StackOverflowException)
        {
            throw; // Critical CLR error — do not swallow
        }
#pragma warning disable CA1031 // Intentional catch-all for UI error display
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = ex.Message;
            CanRetry = IsTransient(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is TimeoutException or System.Net.Sockets.SocketException or TaskCanceledException;

    /// Subscribe to <see cref="ReactiveCommand{TParam,TResult}.ThrownExceptions"/>
    /// and route errors to <see cref="ErrorMessage"/> instead of breaking the Rx pipeline.
    /// </summary>
    protected void TrackErrors(IHandleObservableErrors command)
    {
        command.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex => ErrorMessage = ex.Message)
            .DisposeWith(Disposables);
    }

    public virtual void Dispose()
    {
        Disposables.Dispose();
        GC.SuppressFinalize(this);
    }
}
