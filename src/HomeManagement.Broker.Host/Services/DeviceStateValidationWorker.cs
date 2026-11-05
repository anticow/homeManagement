using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Options;

namespace HomeManagement.Broker.Host.Services;

public sealed partial class DeviceStateValidationWorker(IServiceScopeFactory scopes, IOptions<DeviceStateOptions> options,
    TimeProvider clock, ILogger<DeviceStateValidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.ValidationIntervalSeconds), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                budget.CancelAfter(TimeSpan.FromSeconds(240));
                await scope.ServiceProvider.GetRequiredService<DeviceStateCollector>().ValidateAsync(budget.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
#pragma warning disable CA1031 // A failed/budget-expired cycle must not terminate periodic validation.
            catch (Exception)
#pragma warning restore CA1031
            {
                LogCycleUnavailable(logger);
            }
            // Await the entire cycle before the next tick: no overlapping cycles.
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device-state validation cycle unavailable or exceeded its time budget.")]
    private static partial void LogCycleUnavailable(ILogger logger);
}
