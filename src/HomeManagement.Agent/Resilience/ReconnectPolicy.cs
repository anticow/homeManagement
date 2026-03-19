using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Resilience;

/// <summary>
/// Calculates reconnection delays using exponential backoff with jitter.
/// </summary>
public sealed class ReconnectPolicy(ILogger<ReconnectPolicy> logger)
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    private const double JitterFactor = 0.2;

    private int _attempt;

    public TimeSpan NextDelay()
    {
        var attempt = Interlocked.Increment(ref _attempt) - 1;
        var power = Math.Min(attempt, 18); // Prevent TimeSpan overflow; 2^18 = 262144s >> MaxDelay
        var exponential = BaseDelay * Math.Pow(2, power);
        var capped = exponential > MaxDelay ? MaxDelay : exponential;

        // ±20% jitter
        var jitter = 1.0 + (Random.Shared.NextDouble() * 2 - 1) * JitterFactor;
        var delay = TimeSpan.FromMilliseconds(capped.TotalMilliseconds * jitter);

        logger.LogInformation("Reconnect attempt {Attempt}, delay {Delay:F1}s", attempt + 1, delay.TotalSeconds);
        return delay;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _attempt, 0);
    }
}
