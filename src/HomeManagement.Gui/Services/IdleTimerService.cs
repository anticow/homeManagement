namespace HomeManagement.Gui.Services;

/// <summary>
/// Timer-based idle detection. Resets on <see cref="ResetTimer"/> calls.
/// When the timeout elapses, fires <see cref="IdleTimeoutReached"/>.
/// </summary>
public sealed class IdleTimerService : IIdleTimerService, IDisposable
{
    private Timer? _timer;
    private readonly object _lock = new();
    private bool _running;

    public event EventHandler? IdleTimeoutReached;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _timer = new Timer(OnTimerElapsed, null, Timeout, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    public void StopTimer()
    {
        lock (_lock)
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void ResetTimer()
    {
        lock (_lock)
        {
            if (!_running || _timer is null) return;
            _timer.Change(Timeout, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerElapsed(object? state)
    {
        IdleTimeoutReached?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        StopTimer();
    }
}
