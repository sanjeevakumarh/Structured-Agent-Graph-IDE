namespace SAGIDE.Service.Resilience;

/// <summary>
/// Configuration for per-provider circuit breakers.
/// Bind from <c>SAGIDE:Resilience:CircuitBreaker</c>.
/// </summary>
public class CircuitBreakerConfig
{
    /// <summary>Whether circuit breakers are active. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Consecutive provider failures before the circuit opens.
    /// Resets to 0 on any successful call. Default 5.
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Seconds to hold the circuit open before allowing a single probe request.
    /// If the probe succeeds the circuit closes; if it fails the timer resets. Default 60.
    /// </summary>
    public int ResetTimeoutSec { get; set; } = 60;
}
