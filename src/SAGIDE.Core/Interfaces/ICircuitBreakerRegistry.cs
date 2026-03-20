using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Thin abstraction over the circuit-breaker registry so the ModelRouter module
/// can query breaker state without depending on <c>SAGIDE.Service.Resilience</c>.
/// </summary>
public interface ICircuitBreakerRegistry
{
    /// <summary>Returns true when a call to <paramref name="provider"/> is allowed by the breaker.</summary>
    bool IsCallPermitted(ModelProvider provider);
}
