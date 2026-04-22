using FluentAssertions;
using HomeManagement.Abstractions.CrossCutting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeManagement.Transport.Tests;

/// <summary>
/// Tests for <see cref="DefaultResiliencePipeline"/> — verifies HIGH-11 resilience rework.
/// </summary>
public sealed class DefaultResiliencePipelineTests
{
    private static DefaultResiliencePipeline CreatePipeline(ResilienceOptions? options = null)
    {
        var opts = Options.Create(options ?? new ResilienceOptions
        {
            MaxRetryAttempts = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerDuration = TimeSpan.FromMilliseconds(200),
            OperationTimeout = TimeSpan.FromSeconds(5),
            RetryableExceptions = []
        });
        return new DefaultResiliencePipeline(NullLogger<DefaultResiliencePipeline>.Instance, opts);
    }

    // ── Success path ──

    [Fact]
    public async Task ExecuteAsync_SuccessOnFirstAttempt_ReturnsResult()
    {
        var pipeline = CreatePipeline();

        var result = await pipeline.ExecuteAsync("host1", _ => Task.FromResult(42));

        result.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteAsync_Success_CircuitStateRemainsClosed()
    {
        var pipeline = CreatePipeline();

        await pipeline.ExecuteAsync("host1", _ => Task.FromResult(1));

        pipeline.GetCircuitState("host1").Should().Be(CircuitState.Closed);
    }

    // ── Retry behavior ──

    [Fact]
    public async Task ExecuteAsync_TransientFailureThenSuccess_ReturnsResult()
    {
        var pipeline = CreatePipeline();
        var callCount = 0;

        var result = await pipeline.ExecuteAsync("host1", _ =>
        {
            callCount++;
            if (callCount < 2)
                throw new TimeoutException("transient");
            return Task.FromResult(99);
        });

        result.Should().Be(99);
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_AllRetriesExhausted_ThrowsWithInnerException()
    {
        var pipeline = CreatePipeline(new ResilienceOptions
        {
            MaxRetryAttempts = 1,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            CircuitBreakerFailureThreshold = 10, // high so breaker doesn't trip
            OperationTimeout = TimeSpan.FromSeconds(5),
            RetryableExceptions = []
        });

        var act = () => pipeline.ExecuteAsync("host1", _ =>
            Task.FromException<int>(new InvalidOperationException("persistent failure")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*retry attempts exhausted*");
    }

    // ── Circuit breaker ──

    [Fact]
    public async Task ExecuteAsync_ExceedsFailureThreshold_OpensCircuit()
    {
        var pipeline = CreatePipeline(new ResilienceOptions
        {
            MaxRetryAttempts = 0, // no retries — 1 failure per call
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerDuration = TimeSpan.FromSeconds(30),
            OperationTimeout = TimeSpan.FromSeconds(5),
            RetryableExceptions = []
        });

        // Fail twice to trip the breaker
        for (var i = 0; i < 2; i++)
        {
            try { await pipeline.ExecuteAsync("host1", _ => Task.FromException<int>(new InvalidOperationException("fail"))); }
            catch { /* expected */ }
        }

        pipeline.GetCircuitState("host1").Should().Be(CircuitState.Open);
    }

    [Fact]
    public async Task ExecuteAsync_CircuitOpen_ThrowsImmediately()
    {
        var pipeline = CreatePipeline(new ResilienceOptions
        {
            MaxRetryAttempts = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerDuration = TimeSpan.FromSeconds(60),
            OperationTimeout = TimeSpan.FromSeconds(5),
            RetryableExceptions = []
        });

        // Trip the breaker
        try { await pipeline.ExecuteAsync("host1", _ => Task.FromException<int>(new InvalidOperationException("fail"))); }
        catch { /* expected */ }

        // Next call should fail without calling the action
        var act = () => pipeline.ExecuteAsync("host1", _ => Task.FromResult(0));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Circuit breaker is open*");
    }

    // ── Per-target isolation ──

    [Fact]
    public async Task ExecuteAsync_DifferentTargets_HaveIndependentCircuits()
    {
        var pipeline = CreatePipeline(new ResilienceOptions
        {
            MaxRetryAttempts = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            CircuitBreakerFailureThreshold = 1,
            CircuitBreakerDuration = TimeSpan.FromSeconds(60),
            OperationTimeout = TimeSpan.FromSeconds(5),
            RetryableExceptions = []
        });

        // Trip breaker for host1
        try { await pipeline.ExecuteAsync("host1", _ => Task.FromException<int>(new InvalidOperationException("fail"))); }
        catch { /* expected */ }

        pipeline.GetCircuitState("host1").Should().Be(CircuitState.Open);
        pipeline.GetCircuitState("host2").Should().Be(CircuitState.Closed);

        // host2 should still work
        var result = await pipeline.ExecuteAsync("host2", _ => Task.FromResult(42));
        result.Should().Be(42);
    }

    // ── RetryableExceptions filter ──

    [Fact]
    public async Task ExecuteAsync_NonRetryableException_DoesNotRetry()
    {
        var pipeline = CreatePipeline(new ResilienceOptions
        {
            MaxRetryAttempts = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            CircuitBreakerFailureThreshold = 10,
            OperationTimeout = TimeSpan.FromSeconds(5),
            RetryableExceptions = [typeof(TimeoutException)] // only retry timeouts
        });

        var callCount = 0;
        var act = () => pipeline.ExecuteAsync<int>("host1", _ =>
        {
            callCount++;
            throw new ArgumentException("not retryable");
        });

        // ArgumentException should propagate immediately via retry-exhaustion
        // since the catch filter won't match, it propagates directly
        await act.Should().ThrowAsync<ArgumentException>();
        callCount.Should().Be(1);
    }

    // ── Cancellation ──

    [Fact]
    public async Task ExecuteAsync_CallerCancellation_PropagatesImmediately()
    {
        var pipeline = CreatePipeline();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => pipeline.ExecuteAsync("host1", _ => Task.FromResult(0), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── GetCircuitState ──

    [Fact]
    public void GetCircuitState_UnknownTarget_ReturnsClosed()
    {
        var pipeline = CreatePipeline();
        pipeline.GetCircuitState("never-seen").Should().Be(CircuitState.Closed);
    }

    // ── Timeout ──

    [Fact]
    public async Task ExecuteAsync_OperationTimeout_RetriesOnTimeout()
    {
        var pipeline = CreatePipeline(new ResilienceOptions
        {
            MaxRetryAttempts = 1,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            CircuitBreakerFailureThreshold = 10,
            OperationTimeout = TimeSpan.FromMilliseconds(50),
            RetryableExceptions = []
        });

        var callCount = 0;
        var act = () => pipeline.ExecuteAsync<int>("host1", async ct =>
        {
            callCount++;
            await Task.Delay(TimeSpan.FromSeconds(10), ct); // will timeout
            return 0;
        });

        // Should retry then exhaust
        await act.Should().ThrowAsync<InvalidOperationException>();
        callCount.Should().BeGreaterThanOrEqualTo(2);
    }
}
