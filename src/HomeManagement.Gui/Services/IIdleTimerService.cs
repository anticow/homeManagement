namespace HomeManagement.Gui.Services;

/// <summary>
/// Tracks user inactivity and raises an event when the idle timeout expires.
/// Primarily used for auto-locking the vault after a period of inactivity.
/// </summary>
public interface IIdleTimerService
{
    /// <summary>Raised when the idle timeout expires.</summary>
    event EventHandler? IdleTimeoutReached;

    /// <summary>Gets or sets the idle timeout duration.</summary>
    TimeSpan Timeout { get; set; }

    /// <summary>Starts monitoring user activity.</summary>
    void Start();

    /// <summary>Stops the idle timer.</summary>
    void StopTimer();

    /// <summary>Resets the timer — call on user input events.</summary>
    void ResetTimer();
}
