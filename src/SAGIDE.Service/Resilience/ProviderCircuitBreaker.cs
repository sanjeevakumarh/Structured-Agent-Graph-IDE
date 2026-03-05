using Microsoft.Extensions.Logging;

namespace SAGIDE.Service.Resilience;

/// <summary>
/// Per-provider circuit breaker: Closed → Open → HalfOpen.
///
/// Transitions:
///   Closed:   all calls permitted; consecutive failures are counted.
///   Open:     all calls rejected immediately; entered when failures ≥ threshold.
///             After <c>ResetTimeout</c> elapses, one probe is allowed (→ HalfOpen).
///   HalfOpen: exactly one probe is in flight; all other calls are rejected.
///             Probe success → Closed; probe failure → Open (timer resets).
///
/// Thread-safe via a single lock object.
/// </summary>
public sealed class ProviderCircuitBreaker
{
    private enum CircuitState { Closed, Open, HalfOpen }

    private CircuitState _state       = CircuitState.Closed;
    private int          _failures;
    private DateTimeOffset _openedAt;
    private readonly object _lock           = new();
    private readonly int    _failureThreshold;
    private readonly TimeSpan _resetTimeout;
    private readonly ILogger  _logger;

    public string ProviderName { get; }

    public ProviderCircuitBreaker(
        string providerName,
        int failureThreshold,
        TimeSpan resetTimeout,
        ILogger logger)
    {
        ProviderName      = providerName;
        _failureThreshold = failureThreshold;
        _resetTimeout     = resetTimeout;
        _logger           = logger;
    }

    // ── Snapshot for observability ────────────────────────────────────────────

    /// <summary>Current state as a display string: Closed | Open | HalfOpen.</summary>
    public string State
    {
        get
        {
            lock (_lock) return _state.ToString();
        }
    }

    /// <summary>How many more seconds the circuit will stay Open (-1 if Closed/HalfOpen).</summary>
    public double SecondsUntilReset
    {
        get
        {
            lock (_lock)
            {
                if (_state != CircuitState.Open) return -1;
                var remaining = _resetTimeout - (DateTimeOffset.UtcNow - _openedAt);
                return remaining > TimeSpan.Zero ? remaining.TotalSeconds : 0;
            }
        }
    }

    // ── State machine ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when a call should be allowed.
    /// Transitions <c>Open → HalfOpen</c> (and allows the probe) when the reset
    /// timeout elapses.  While in <c>HalfOpen</c>, all subsequent callers get
    /// <c>false</c> until the probe resolves via <see cref="RecordSuccess"/> or
    /// <see cref="RecordFailure"/>.
    /// </summary>
    public bool IsCallPermitted()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open:
                    if (DateTimeOffset.UtcNow - _openedAt >= _resetTimeout)
                    {
                        _state = CircuitState.HalfOpen;
                        _logger.LogInformation(
                            "Circuit breaker [{Provider}]: Open → HalfOpen — probe allowed after {Sec:F0}s",
                            ProviderName, _resetTimeout.TotalSeconds);
                        return true;   // permit the one probe
                    }
                    return false;

                case CircuitState.HalfOpen:
                    // Probe already in flight — block all others.
                    return false;

                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// Records a successful provider call.  Closes the circuit and resets the failure counter.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_state != CircuitState.Closed)
            {
                _logger.LogInformation(
                    "Circuit breaker [{Provider}]: {State} → Closed", ProviderName, _state);
            }
            _state    = CircuitState.Closed;
            _failures = 0;
        }
    }

    /// <summary>
    /// Records a failed provider call.
    /// Opens the circuit when consecutive failures reach the threshold,
    /// or immediately if a probe fails (HalfOpen).
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failures++;

            var shouldOpen = _state == CircuitState.HalfOpen || _failures >= _failureThreshold;
            if (shouldOpen)
            {
                var previous = _state;
                _state    = CircuitState.Open;
                _openedAt = DateTimeOffset.UtcNow;
                _logger.LogWarning(
                    "Circuit breaker [{Provider}]: {Previous} → Open " +
                    "(failures: {Count}, threshold: {Threshold}, resets in {Sec:F0}s)",
                    ProviderName, previous, _failures, _failureThreshold, _resetTimeout.TotalSeconds);
            }
        }
    }
}
