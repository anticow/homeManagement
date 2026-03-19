namespace HomeManagement.Abstractions.CrossCutting;

/// <summary>
/// Wraps retry + circuit-breaker + timeout into a single composable pipeline.
/// Transport providers and command executors use this to handle transient failures.
/// </summary>
public interface IResiliencePipeline
{
    /// <summary>Execute <paramref name="action"/> through the resilience pipeline for the given target.</summary>
    Task<T> ExecuteAsync<T>(string targetKey, Func<CancellationToken, Task<T>> action, CancellationToken ct = default);

    /// <summary>Query the current circuit-breaker state for a specific target.</summary>
    CircuitState GetCircuitState(string targetKey);
}

public enum CircuitState { Closed, Open, HalfOpen }

/// <summary>
/// Configuration for the resilience pipeline — specifies retry, circuit-breaker, and timeout behavior.
/// </summary>
public sealed record ResilienceOptions
{
    /// <summary>Maximum number of retry attempts before failing.</summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>Base delay between retries. Exponential backoff multiplies this by 2^attempt.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Number of consecutive failures to open the circuit breaker.</summary>
    public int CircuitBreakerFailureThreshold { get; init; } = 5;

    /// <summary>Duration the circuit stays open before allowing a test request.</summary>
    public TimeSpan CircuitBreakerDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for a single operation attempt.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Exception types considered transient (retryable). If empty, all exceptions are retried.</summary>
    public Type[] RetryableExceptions { get; init; } = [];
}
