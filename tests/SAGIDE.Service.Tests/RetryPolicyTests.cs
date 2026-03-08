using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void ExponentialBackoff_DoublesDelay()
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            InitialDelay = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(2));
    }

    [Fact]
    public void FixedBackoff_SameDelay()
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            InitialDelay = TimeSpan.FromSeconds(2)
        };

        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(2));
    }

    [Fact]
    public void IsRetryable_429_ReturnsTrue()
    {
        var policy = RetryPolicy.Default;
        Assert.True(policy.IsRetryable(429));
        Assert.True(policy.IsRetryable(500));
        Assert.True(policy.IsRetryable(503));
    }

    [Fact]
    public void IsRetryable_400_ReturnsFalse()
    {
        var policy = RetryPolicy.Default;
        Assert.False(policy.IsRetryable(400));
        Assert.False(policy.IsRetryable(401));
        Assert.False(policy.IsRetryable(404));
    }

    [Fact]
    public void ExponentialBackoff_MaxDelay_CapsDelay()
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(5)
        };

        // attempt 0 → 1s, attempt 1 → 2s, attempt 2 → 4s, attempt 3 → 8s (capped at 5s)
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(10));
    }

    [Fact]
    public void FixedBackoff_MaxDelay_HasNoEffect()
    {
        // MaxDelay cap only applies to exponential strategy
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            InitialDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromSeconds(5)
        };

        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetDelay(5));
    }

    [Fact]
    public void ExponentialBackoff_WithJitter_DelayWithinBounds()
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(10),
            UseJitter = true
        };

        // Jitter is random but must stay in [0, computed_delay]
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var delay = policy.GetDelay(attempt);
            Assert.True(delay >= TimeSpan.Zero, $"Jittered delay must be non-negative (attempt {attempt})");
            Assert.True(delay <= policy.MaxDelay, $"Jittered delay must not exceed MaxDelay (attempt {attempt})");
        }
    }

    [Fact]
    public void ExponentialBackoff_NoJitter_IsDeterministic()
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            InitialDelay = TimeSpan.FromSeconds(1),
            UseJitter = false
        };

        // Calling GetDelay twice with the same attempt must return the same value
        Assert.Equal(policy.GetDelay(2), policy.GetDelay(2));
        Assert.Equal(policy.GetDelay(3), policy.GetDelay(3));
    }
}
