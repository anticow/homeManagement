using System.Collections.Concurrent;
using HomeManagement.Abstractions.CrossCutting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Transport;

/// <summary>
/// Retry + circuit-breaker + timeout resilience pipeline.
/// Per-target circuit breakers prevent cascading failures when a machine is unavailable.
/// Configuration sourced from <see cref="ResilienceOptions"/>.
/// </summary>
internal sealed class DefaultResiliencePipeline : IResiliencePipeline
{
    private readonly ILogger<DefaultResiliencePipeline> _logger;
    private readonly ResilienceOptions _options;
    private readonly ConcurrentDictionary<string, TargetCircuit> _circuits = new();

    public DefaultResiliencePipeline(ILogger<DefaultResiliencePipeline> logger, IOptions<ResilienceOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<T> ExecuteAsync<T>(string targetKey, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        var circuit = _circuits.GetOrAdd(targetKey, _ => new TargetCircuit());

        if (circuit.State == CircuitState.Open)
        {
            if (DateTime.UtcNow - circuit.OpenedAtUtc < _options.CircuitBreakerDuration)
                throw new InvalidOperationException($"Circuit breaker is open for target '{targetKey}'. Retry after {_options.CircuitBreakerDuration.TotalSeconds}s.");

            // Transition to half-open
            circuit.State = CircuitState.HalfOpen;
            _logger.LogInformation("Circuit breaker half-open for {Target}", targetKey);
        }

        var lastException = default(Exception);

        for (var attempt = 0; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                var delay = TimeSpan.FromMilliseconds(_options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                _logger.LogDebug("Retry {Attempt}/{Max} for {Target} after {Delay}ms",
                    attempt, _options.MaxRetryAttempts, targetKey, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_options.OperationTimeout);

                var result = await action(cts.Token);

                // Success — reset circuit
                circuit.FailureCount = 0;
                if (circuit.State != CircuitState.Closed)
                {
                    circuit.State = CircuitState.Closed;
                    _logger.LogInformation("Circuit breaker closed for {Target}", targetKey);
                }

                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Caller-initiated cancellation — don't retry
            }
            catch (Exception ex) when (_options.RetryableExceptions.Length == 0
                || Array.Exists(_options.RetryableExceptions, t => t.IsInstanceOfType(ex)))
            {
                lastException = ex;
                circuit.FailureCount++;

                _logger.LogWarning(ex, "Attempt {Attempt} failed for {Target}: {Message}",
                    attempt + 1, targetKey, ex.Message);

                if (circuit.FailureCount >= _options.CircuitBreakerFailureThreshold)
                {
                    circuit.State = CircuitState.Open;
                    circuit.OpenedAtUtc = DateTime.UtcNow;
                    _logger.LogWarning("Circuit breaker opened for {Target} after {Count} failures",
                        targetKey, circuit.FailureCount);
                    break;
                }
            }
        }

        throw new InvalidOperationException(
            $"All retry attempts exhausted for target '{targetKey}'.", lastException);
    }

    public CircuitState GetCircuitState(string targetKey)
    {
        return _circuits.TryGetValue(targetKey, out var circuit) ? circuit.State : CircuitState.Closed;
    }

    private sealed class TargetCircuit
    {
        public CircuitState State { get; set; } = CircuitState.Closed;
        public int FailureCount { get; set; }
        public DateTime OpenedAtUtc { get; set; }
    }
}
