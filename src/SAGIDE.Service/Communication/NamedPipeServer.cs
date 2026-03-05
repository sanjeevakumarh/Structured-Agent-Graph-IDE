using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SAGIDE.Service.Communication.Messages;

namespace SAGIDE.Service.Communication;

public class NamedPipeServer
{
    private readonly string _pipeName;
    private readonly ILogger<NamedPipeServer> _logger;
    private readonly MessageHandler _messageHandler;
    private readonly CommunicationConfig _config;
    // Per-client write lock prevents concurrent writes from HandleClientAsync and BroadcastAsync
    private record ClientEntry(NamedPipeServerStream Stream, SemaphoreSlim WriteLock);
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();
    // Maps taskId → clientId so streaming output is routed only to the submitting window
    private readonly ConcurrentDictionary<string, string> _taskOwners = new();
    // bounded channel — high-volume streaming messages (DropOldest prevents producer blockage)
    private readonly Channel<PipeMessage> _broadcastChannel;
    // unbounded channel — lifecycle/state events that must never be dropped
    private readonly Channel<PipeMessage> _lifecycleChannel;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Total number of broadcast messages silently dropped due to a full channel (DropOldest policy).
    /// Non-zero values indicate the service is under backpressure. Exposed via /api/health.
    /// </summary>
    public long DroppedMessageCount => Interlocked.Read(ref _droppedMessageCount);
    private long _droppedMessageCount;

    // Matches TypeScript client: camelCase properties, string enums, byte[] as base64 string
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public NamedPipeServer(
        string pipeName,
        MessageHandler messageHandler,
        ILogger<NamedPipeServer> logger,
        CommunicationConfig? config = null)
    {
        _pipeName       = pipeName;
        _messageHandler = messageHandler;
        _logger         = logger;
        _config         = config ?? new CommunicationConfig();

        // DropOldest ensures BroadcastAsync never blocks even under high-throughput streaming.
        _broadcastChannel = Channel.CreateBounded<PipeMessage>(new BoundedChannelOptions(_config.MaxBroadcastQueueSize)
        {
            FullMode          = BoundedChannelFullMode.DropOldest,
            SingleReader      = true,   // only DrainBroadcastChannelAsync reads
            SingleWriter      = false   // multiple threads may broadcast concurrently
        });

        // Unbounded — lifecycle events (TaskUpdate, WorkflowUpdate, ApprovalNeeded, etc.) must never
        // be dropped. These are low-volume compared to streaming_output tokens.
        _lifecycleChannel = Channel.CreateUnbounded<PipeMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.LogInformation("Named pipe server starting on: {PipeName}", _pipeName);

        // Run accept loop and both drain loops concurrently.
        await Task.WhenAll(
            RunAcceptLoopAsync(_cts.Token),
            DrainBroadcastChannelAsync(_cts.Token),
            DrainLifecycleChannelAsync(_cts.Token));
    }

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        var consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = CreatePipeServer();
                await server.WaitForConnectionAsync(ct);
                consecutiveErrors = 0; // reset on successful accept
                var clientId = Guid.NewGuid().ToString("N")[..8];
                var entry = new ClientEntry(server, new SemaphoreSlim(1, 1));
                _clients[clientId] = entry;
                _logger.LogInformation("Client {ClientId} connected", clientId);

                _ = HandleClientAsync(clientId, entry, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                var delayMs = (int)Math.Min(
                    _config.AcceptRetryInitialDelayMs *
                        Math.Pow(_config.AcceptRetryBackoffMultiplier, consecutiveErrors - 1),
                    _config.AcceptRetryMaxDelayMs);

                _logger.LogError(ex,
                    "Error accepting client connection (attempt {Count}), retrying in {DelayMs}ms",
                    consecutiveErrors, delayMs);
                await Task.Delay(delayMs, ct);
            }
        }
    }

    // ── Pipe creation ─────────────────────────────────────────────────────────

    private NamedPipeServerStream CreatePipeServer()
    {
        if (_config.EnablePipeSecurity && OperatingSystem.IsWindows())
            return CreateSecurePipeServer();

        return new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateSecurePipeServer()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        // Use .User (the actual user SID), not .Owner (which on elevated sessions is
        // the Administrators group SID and cannot create pipe instances).
        var sid = identity.User
            ?? throw new InvalidOperationException("Cannot determine current Windows user SID for pipe ACL");

        var security = new PipeSecurity();
        // FullControl is required for the pipe creator; ReadWrite alone is not sufficient
        // to create new instances and causes UnauthorizedAccessException.
        security.AddAccessRule(new PipeAccessRule(
            sid,
            PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 0, security);
    }

    // ── Shared-secret handshake ───────────────────────────────────────────────

    /// <summary>
    /// If a SharedSecret is configured, reads the first frame from the client and
    /// verifies it is a pipe_auth message whose payload matches the secret (constant-time
    /// comparison). Sends pipe_auth_ok on success; returns false (caller closes) on failure.
    /// If no secret is configured the method returns true immediately.
    /// </summary>
    private async Task<bool> PerformHandshakeAsync(string clientId, ClientEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.SharedSecret))
            return true;

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_config.HandshakeTimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var lengthBuffer = new byte[4];
            if (!await ReadExactAsync(entry.Stream, lengthBuffer, 4, linked.Token))
                return false;

            var len = BitConverter.ToInt32(lengthBuffer, 0);
            if (len <= 0 || len > _config.MaxMessageSizeBytes)
                return false;

            var msgBuffer = new byte[len];
            if (!await ReadExactAsync(entry.Stream, msgBuffer, len, linked.Token))
                return false;

            PipeMessage? msg;
            try { msg = JsonSerializer.Deserialize<PipeMessage>(msgBuffer, JsonOptions); }
            catch { return false; }

            if (msg?.Type != MessageTypes.PipeAuth)
                return false;

            var provided = msg.Payload != null ? Encoding.UTF8.GetString(msg.Payload) : string.Empty;
            var expected = _config.SharedSecret;

            // Constant-time comparison prevents timing side-channel attacks.
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(provided),
                    Encoding.UTF8.GetBytes(expected)))
            {
                _logger.LogWarning("Client {ClientId} supplied wrong pipe secret", clientId);
                return false;
            }

            await SendWithLockAsync(entry,
                new PipeMessage { Type = MessageTypes.PipeAuthOk, RequestId = msg.RequestId }, ct);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Client {ClientId} did not complete handshake within {Ms}ms",
                clientId, _config.HandshakeTimeoutMs);
            return false;
        }
    }

    /// <summary>
    /// Background drain loop — reads high-volume broadcast messages from the bounded channel and
    /// fans each one out to all connected clients in parallel with per-client timeouts.
    /// The producer (BroadcastAsync) is never blocked by a slow or stalled client.
    /// Only <see cref="MessageTypes.StreamingOutput"/> messages are routed here; all other
    /// message types go through the unbounded lifecycle channel.
    /// </summary>
    private async Task DrainBroadcastChannelAsync(CancellationToken ct)
    {
        await foreach (var message in _broadcastChannel.Reader.ReadAllAsync(ct))
        {
            var connected = _clients
                .Where(kvp => kvp.Value.Stream.IsConnected)
                .ToList();

            if (connected.Count == 0) continue;

            // Fire all per-client sends in parallel; don't await the aggregate — if one
            // client's timeout fires and disconnects it, the others are unaffected.
            _ = Task.WhenAll(connected.Select(
                kvp => SendWithTimeoutAsync(kvp.Key, kvp.Value, message, ct)));
        }
    }

    /// <summary>
    /// Background drain loop for lifecycle/state events (TaskUpdate, WorkflowUpdate,
    /// WorkflowApprovalNeeded, etc.). Uses an unbounded channel so these messages are
    /// never silently dropped under streaming backpressure.
    /// </summary>
    private async Task DrainLifecycleChannelAsync(CancellationToken ct)
    {
        await foreach (var message in _lifecycleChannel.Reader.ReadAllAsync(ct))
        {
            var connected = _clients
                .Where(kvp => kvp.Value.Stream.IsConnected)
                .ToList();

            if (connected.Count == 0) continue;

            _ = Task.WhenAll(connected.Select(
                kvp => SendWithTimeoutAsync(kvp.Key, kvp.Value, message, ct)));
        }
    }

    // Reads exactly 'count' bytes into buffer[0..count-1]; returns false on EOF.
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
            if (read == 0) return false;
            totalRead += read;
        }
        return true;
    }

    private async Task HandleClientAsync(string clientId, ClientEntry entry, CancellationToken ct)
    {
        var stream = entry.Stream;
        try
        {
            // Shared-secret handshake must succeed before any real messages are processed.
            if (!await PerformHandshakeAsync(clientId, entry, ct))
            {
                _logger.LogWarning("Client {ClientId} failed pipe authentication; closing", clientId);
                return;
            }

            var lengthBuffer = new byte[4];
            while (stream.IsConnected && !ct.IsCancellationRequested)
            {
                // Read the 4-byte little-endian length prefix fully before interpreting it.
                if (!await ReadExactAsync(stream, lengthBuffer, 4, ct)) break;

                var messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                // Guard against malformed frames: a negative, zero, or unreasonably large length
                // would cause an out-of-memory allocation. Limit is configurable via CommunicationConfig.
                if (messageLength <= 0 || messageLength > _config.MaxMessageSizeBytes)
                {
                    _logger.LogWarning(
                        "Client {ClientId} sent invalid frame length {Len}; closing connection",
                        clientId, messageLength);
                    break;
                }
                var messageBuffer = new byte[messageLength];
                if (!await ReadExactAsync(stream, messageBuffer, messageLength, ct)) break;

                var message = JsonSerializer.Deserialize<PipeMessage>(messageBuffer, JsonOptions)
                    ?? throw new InvalidOperationException("Failed to deserialize PipeMessage");
                _logger.LogDebug("Received message type: {Type} from {ClientId}", message.Type, clientId);

                var response = await _messageHandler.HandleAsync(message, ct);
                await SendWithLockAsync(entry, response, ct);

                // After a SubmitTask succeeds, register this client as the streaming-output owner
                if (message.Type == MessageTypes.SubmitTask
                    && response.Type == MessageTypes.TaskUpdate
                    && response.Payload is { Length: > 0 })
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(response.Payload);
                        if (doc.RootElement.TryGetProperty("taskId", out var idEl))
                        {
                            var taskId = idEl.GetString();
                            if (!string.IsNullOrEmpty(taskId))
                                _taskOwners[taskId] = clientId;
                        }
                    }
                    catch { /* silently ignore parse errors */ }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {ClientId}", clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            // Remove all task-owner entries for this client so we don't try to route to a dead pipe
            foreach (var kvp in _taskOwners.Where(k => k.Value == clientId).ToList())
                _taskOwners.TryRemove(kvp.Key, out _);
            await stream.DisposeAsync();
            _logger.LogInformation("Client {ClientId} disconnected", clientId);
        }
    }

    /// <summary>Returns the clientId that owns a task, or null if not tracked.</summary>
    public string? GetTaskOwner(string taskId) =>
        _taskOwners.TryGetValue(taskId, out var clientId) ? clientId : null;

    /// <summary>
    /// Send a message to one specific client (point-to-point, bypasses broadcast channel).
    /// Used for streaming output routed to the task owner.
    /// </summary>
    public async Task SendToClientAsync(string clientId, PipeMessage message, CancellationToken ct = default)
    {
        if (_clients.TryGetValue(clientId, out var entry) && entry.Stream.IsConnected)
            await SendWithTimeoutAsync(clientId, entry, message, ct);
    }

    /// <summary>
    /// Non-blocking broadcast — routes the message to the appropriate drain channel and returns immediately.
    /// <para>
    /// <see cref="MessageTypes.StreamingOutput"/> messages go to the bounded channel (DropOldest);
    /// all other message types (TaskUpdate, WorkflowUpdate, ApprovalNeeded, etc.) go to the
    /// unbounded lifecycle channel so they are never silently dropped.
    /// </para>
    /// </summary>
    public Task BroadcastAsync(PipeMessage message, CancellationToken ct = default)
    {
        if (message.Type == MessageTypes.StreamingOutput)
        {
            if (!_broadcastChannel.Writer.TryWrite(message))
            {
                Interlocked.Increment(ref _droppedMessageCount);
                _logger.LogWarning(
                    "Broadcast channel full; dropped oldest streaming message (total dropped={Count})",
                    DroppedMessageCount);
            }
        }
        else
        {
            // Lifecycle events are written to an unbounded channel — TryWrite always succeeds.
            _lifecycleChannel.Writer.TryWrite(message);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wraps SendWithLockAsync with a per-client timeout so a stalled write
    /// cannot block the drain loop for other clients.
    /// </summary>
    private async Task SendWithTimeoutAsync(string clientId, ClientEntry entry, PipeMessage message, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_config.PerClientBroadcastTimeoutSec));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await SendWithLockAsync(entry, message, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Broadcast to client {ClientId} timed out after {Sec}s; disconnecting stalled client",
                clientId, _config.PerClientBroadcastTimeoutSec);
            _clients.TryRemove(clientId, out _);
            await entry.Stream.DisposeAsync();
        }
    }

    // All writes go through here so the per-client SemaphoreSlim prevents interleaved frames.
    private static async Task SendWithLockAsync(ClientEntry entry, PipeMessage message, CancellationToken ct)
    {
        await entry.WriteLock.WaitAsync(ct);
        try
        {
            await SendMessageAsync(entry.Stream, message, ct);
        }
        finally
        {
            entry.WriteLock.Release();
        }
    }

    private static async Task SendMessageAsync(Stream stream, PipeMessage message, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var length = BitConverter.GetBytes(data.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public async Task StopAsync()
    {
        _broadcastChannel.Writer.Complete();
        _lifecycleChannel.Writer.Complete();
        _cts?.Cancel();
        foreach (var entry in _clients.Values)
        {
            await entry.Stream.DisposeAsync();
        }
        _clients.Clear();
        _logger.LogInformation("Named pipe server stopped");
    }
}
