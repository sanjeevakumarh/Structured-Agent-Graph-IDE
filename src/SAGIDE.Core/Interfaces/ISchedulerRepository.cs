namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Persists per-prompt scheduler state so the service can survive restarts
/// without re-firing prompts that already ran within the current window.
/// </summary>
public interface ISchedulerRepository
{
    /// <summary>Returns the UTC timestamp of the last successful fire, or null if never fired.</summary>
    Task<DateTimeOffset?> GetLastFiredAtAsync(string promptKey);

    /// <summary>Persists the last-fired timestamp for a prompt key.</summary>
    Task SetLastFiredAtAsync(string promptKey, DateTimeOffset firedAt);

    /// <summary>Loads all stored last-fired timestamps into the supplied dictionary.</summary>
    Task LoadAllLastFiredAsync(IDictionary<string, DateTimeOffset> target);
}
