namespace SAGIDE.Service.Resilience;

public enum BackoffStrategy { Fixed, Exponential }

public class RetryPolicy
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public BackoffStrategy Strategy { get; init; } = BackoffStrategy.Exponential;
    public int[] RetryableStatusCodes { get; init; } = [429, 500, 502, 503, 529];

    /// <summary>
    /// Upper bound applied to the computed delay for exponential backoff.
    /// Prevents the delay from growing unboundedly over many retries.
    /// Has no effect when <see cref="Strategy"/> is <see cref="BackoffStrategy.Fixed"/>.
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// When <c>true</c>, applies full jitter to the computed delay
    /// (i.e. a random value in <c>[0, computedDelay]</c>).
    /// Enabling jitter staggers retries from concurrent clients that all hit
    /// the same failure simultaneously (thundering-herd mitigation).
    /// Default: <c>false</c> to preserve deterministic behaviour for existing callers.
    /// </summary>
    public bool UseJitter { get; init; } = false;

    /// <summary>
    /// Computes the delay before the next retry attempt.
    /// For <see cref="BackoffStrategy.Exponential"/>, the raw delay is capped at
    /// <see cref="MaxDelay"/> before any jitter is applied.
    /// </summary>
    public TimeSpan GetDelay(int attempt)
    {
        var baseDelay = Strategy switch
        {
            BackoffStrategy.Exponential => InitialDelay * Math.Pow(2, attempt),
            BackoffStrategy.Fixed => InitialDelay,
            _ => InitialDelay
        };

        // Cap exponential growth so delays never become unreasonably large.
        if (Strategy == BackoffStrategy.Exponential && baseDelay > MaxDelay)
            baseDelay = MaxDelay;

        if (UseJitter)
            return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * baseDelay.TotalMilliseconds);

        return baseDelay;
    }

    public bool IsRetryable(int statusCode) => RetryableStatusCodes.Contains(statusCode);

    public static RetryPolicy Default => new();

    public static RetryPolicy ForOllama => new()
    {
        MaxRetries = 2,
        InitialDelay = TimeSpan.FromSeconds(2),
        Strategy = BackoffStrategy.Fixed,
        RetryableStatusCodes = [500, 502, 503]
    };
}
