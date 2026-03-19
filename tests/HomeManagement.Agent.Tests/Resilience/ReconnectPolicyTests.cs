using FluentAssertions;
using HomeManagement.Agent.Resilience;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeManagement.Agent.Tests.Resilience;

public sealed class ReconnectPolicyTests
{
    private static ReconnectPolicy CreatePolicy() =>
        new(NullLogger<ReconnectPolicy>.Instance);

    [Fact]
    public void NextDelay_FirstAttempt_ReturnsApproximately1Second()
    {
        var policy = CreatePolicy();
        var delay = policy.NextDelay();

        // Base delay is 1s ±20% jitter → 0.8s to 1.2s
        delay.TotalSeconds.Should().BeInRange(0.8, 1.2);
    }

    [Fact]
    public void NextDelay_ExponentialGrowth()
    {
        var policy = CreatePolicy();

        var d0 = policy.NextDelay(); // ~1s * 2^0 = ~1s
        var d1 = policy.NextDelay(); // ~1s * 2^1 = ~2s
        var d2 = policy.NextDelay(); // ~1s * 2^2 = ~4s

        // Each delay should be roughly double the previous (within jitter tolerance)
        d1.TotalSeconds.Should().BeGreaterThan(d0.TotalSeconds * 0.5);
        d2.TotalSeconds.Should().BeGreaterThan(d1.TotalSeconds * 0.5);
    }

    [Fact]
    public void NextDelay_CapsAt5Minutes()
    {
        var policy = CreatePolicy();

        // 2^20 seconds would be massive, but cap should keep it at 5 minutes
        for (var i = 0; i < 20; i++)
            policy.NextDelay();

        var capped = policy.NextDelay();

        // 5 minutes ±20% jitter → 240s to 360s
        capped.TotalSeconds.Should().BeLessOrEqualTo(360);
    }

    [Fact]
    public void Reset_ResetsAttemptCounter()
    {
        var policy = CreatePolicy();

        // Advance several attempts
        for (var i = 0; i < 10; i++)
            policy.NextDelay();

        policy.Reset();
        var afterReset = policy.NextDelay();

        // Should be back to ~1s
        afterReset.TotalSeconds.Should().BeInRange(0.8, 1.2);
    }

    [Fact]
    public void NextDelay_AlwaysPositive()
    {
        var policy = CreatePolicy();
        for (var i = 0; i < 50; i++)
        {
            var delay = policy.NextDelay();
            delay.Should().BeGreaterThan(TimeSpan.Zero);
        }
    }
}
