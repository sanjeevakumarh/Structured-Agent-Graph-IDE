using System.IO.Pipes;
using System.Collections.Concurrent;
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
    // bounded channel — BroadcastAsync enqueues here; drain loop fans out to all clients
    private readonly Channel<PipeMessage> _broadcastChannel;
    private CancellationTokenSource? _cts;

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
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.LogInformation("Named pipe server starting on: {PipeName}", _pipeName);

        // Run accept loop and broadcast drain loop concurrently.
        await Task.WhenAll(
            RunAcceptLoopAsync(_cts.Token),
            DrainBroadcastChannelAsync(_cts.Token));
    }

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        var consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

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

    /// <summary>
    /// Background drain loop — reads broadcast messages from the bounded channel and
    /// fans each one out to all connected clients in parallel with per-client timeouts.
    /// The producer (BroadcastAsync) is never blocked by a slow or stalled client.
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
    /// Non-blocking broadcast — enqueues into the bounded channel and returns immediately.
    /// The drain loop fans the message out to all connected clients asynchronously.
    /// If the channel is full, the oldest undelivered message is dropped (DropOldest policy).
    /// </summary>
    public Task BroadcastAsync(PipeMessage message, CancellationToken ct = default)
    {
        if (!_broadcastChannel.Writer.TryWrite(message))
            _logger.LogWarning("Broadcast channel full; dropped oldest message (type={Type})", message.Type);
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
        _cts?.Cancel();
        foreach (var entry in _clients.Values)
        {
            await entry.Stream.DisposeAsync();
        }
        _clients.Clear();
        _logger.LogInformation("Named pipe server stopped");
    }
}
