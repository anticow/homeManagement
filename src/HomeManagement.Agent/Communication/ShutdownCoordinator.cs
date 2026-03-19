using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Communication;

/// <summary>
/// Coordinates graceful shutdown of the agent.
/// When a <c>Shutdown</c> directive is received from the controller,
/// this class manages the drain/delay sequence and triggers IHostApplicationLifetime.StopApplication().
/// </summary>
public sealed class ShutdownCoordinator
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ShutdownCoordinator> _logger;
    private int _shutdownRequested;

    public ShutdownCoordinator(IHostApplicationLifetime lifetime, ILogger<ShutdownCoordinator> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>True once a shutdown has been requested (either from directive or host stop).</summary>
    public bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) == 1;

    /// <summary>
    /// Initiate graceful shutdown: honor the delay, then signal the host to stop.
    /// Safe to call multiple times; only the first call takes effect.
    /// </summary>
    public async Task RequestShutdownAsync(string reason, int delayMs, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _shutdownRequested, 1, 0) != 0)
            return; // Already shutting down

        _logger.LogInformation("Graceful shutdown requested: {Reason}, delay={DelayMs}ms", reason, delayMs);

        if (delayMs > 0)
        {
            _logger.LogInformation("Draining in-flight work for {DelayMs}ms before stopping", delayMs);
            try
            {
                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Shutdown drain period interrupted by cancellation");
            }
        }

        _logger.LogInformation("Signaling application stop");
        _lifetime.StopApplication();
    }
}
