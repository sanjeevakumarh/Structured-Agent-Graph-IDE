namespace SAGIDE.Service.Communication;

/// <summary>
/// IPC / named-pipe configuration bound from SAGIDE:Communication in appsettings.json.
/// </summary>
public class CommunicationConfig
{
    /// <summary>
    /// Maximum allowed size of a single IPC message frame in bytes.
    /// Default 10 MB — raise if large workflow payloads exceed this limit.
    /// </summary>
    public int MaxMessageSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Initial delay (ms) after a pipe-accept error before retrying.</summary>
    public int AcceptRetryInitialDelayMs { get; set; } = 100;

    /// <summary>Maximum delay (ms) for exponential back-off on repeated accept errors.</summary>
    public int AcceptRetryMaxDelayMs { get; set; } = 5_000;

    /// <summary>Back-off multiplier applied to the delay after each consecutive error.</summary>
    public double AcceptRetryBackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Per-client write timeout in seconds during BroadcastAsync / SendToClientAsync.
    /// A stalled client that holds the write lock longer than this is disconnected.
    /// Default 5 s — raise only if clients legitimately need more time to drain the pipe.
    /// </summary>
    public int PerClientBroadcastTimeoutSec { get; set; } = 5;

    /// <summary>
    /// Capacity of the bounded broadcast channel.
    /// When full, the oldest undelivered broadcast is dropped so the producer never blocks.
    /// Default 10 000 — covers ~20 s of 500-token/s streaming at typical message sizes.
    /// </summary>
    public int MaxBroadcastQueueSize { get; set; } = 10_000;

    // ── Authentication ────────────────────────────────────────────────────────

    /// <summary>
    /// Apply Windows PipeSecurity ACL so only the current user's SID may connect.
    /// Effective only on Windows; ignored on other platforms. Default true.
    /// </summary>
    public bool EnablePipeSecurity { get; set; } = true;

    /// <summary>
    /// Optional shared secret for a challenge-free handshake (defense-in-depth).
    /// When set, the very first message from each client must be a pipe_auth frame
    /// whose payload matches this value (UTF-8). Empty/null disables the check.
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>
    /// Maximum time (ms) the server waits for the client to complete the shared-secret
    /// handshake before closing the connection. Default 5 000 ms.
    /// </summary>
    public int HandshakeTimeoutMs { get; set; } = 5_000;
}
