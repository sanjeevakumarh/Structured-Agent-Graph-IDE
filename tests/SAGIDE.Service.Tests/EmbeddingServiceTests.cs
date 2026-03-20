using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Memory;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="EmbeddingService"/> covering:
/// - Server resolution: RagOrder servers preferred, first embed-named model wins
/// - <see cref="EmbeddingService.EmbedAsync"/>: happy path, correct URL/model in request, HTTP failure
/// - <see cref="EmbeddingService.EmbedChunksAsync"/>: parallel count, partial failures
/// </summary>
public class EmbeddingServiceTests
{
    // ── Fake HTTP infrastructure ───────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<Task<HttpResponseMessage>>> _queue = new();
        public List<(string Url, string Body)> Requests { get; } = [];

        public void EnqueueEmbedding(float[] vector)
        {
            var json = JsonSerializer.Serialize(new { embedding = vector });
            _queue.Enqueue(() => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }));
        }

        public void EnqueueStatus(HttpStatusCode status)
            => _queue.Enqueue(() => Task.FromResult(new HttpResponseMessage(status)));

        public void EnqueueException(Exception ex)
            => _queue.Enqueue(() => throw ex);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : string.Empty;
            Requests.Add((request.RequestUri!.ToString(), body));

            if (_queue.TryDequeue(out var factory))
                return await factory();

            // Default: return empty embedding
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"embedding\":[]}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (EmbeddingService Service, FakeHandler Handler) Make(
        string baseUrl  = "http://ollama:11434",
        string model    = "nomic-embed-text:latest",
        int    ragOrder = 0)
    {
        var handler = new FakeHandler();
        var config  = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"SAGIDE:Ollama:Servers:0:BaseUrl"]  = baseUrl,
                [$"SAGIDE:Ollama:Servers:0:RagOrder"] = ragOrder.ToString(),
                [$"SAGIDE:Ollama:Servers:0:Models:0"] = model,
            })
            .Build();
        var svc = new EmbeddingService(
            new HttpClient(handler), config, NullLogger<EmbeddingService>.Instance);
        return (svc, handler);
    }

    // ── Server resolution ─────────────────────────────────────────────────────

    [Fact]
    public async Task EmbedAsync_NoEmbedModel_ReturnsEmpty_NoHttpCall()
    {
        // Server has no model whose name contains "embed"
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:Ollama:Servers:0:BaseUrl"]  = "http://ollama:11434",
                ["SAGIDE:Ollama:Servers:0:RagOrder"] = "0",
                ["SAGIDE:Ollama:Servers:0:Models:0"] = "llama3:8b",
            })
            .Build();
        var handler = new FakeHandler();
        var svc     = new EmbeddingService(
            new HttpClient(handler), config, NullLogger<EmbeddingService>.Instance);

        // _baseUrl is empty → PostAsJsonAsync throws InvalidOperationException (no base address)
        // which is caught → returns []
        var result = await svc.EmbedAsync("test");

        Assert.Empty(result);
        // The exception is caught before any HTTP sends complete, so no requests tracked
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EmbedAsync_RagOrderServerPreferred_OverNonRagServer()
    {
        // Server 0: RagOrder=0, has embed model → should be used
        // Server 1: no RagOrder, also has embed model → should NOT be used
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:Ollama:Servers:0:BaseUrl"]  = "http://rag-server:11434",
                ["SAGIDE:Ollama:Servers:0:RagOrder"] = "0",
                ["SAGIDE:Ollama:Servers:0:Models:0"] = "nomic-embed-text:latest",

                ["SAGIDE:Ollama:Servers:1:BaseUrl"]  = "http://inference-only:11434",
                // No RagOrder
                ["SAGIDE:Ollama:Servers:1:Models:0"] = "mxbai-embed-large:latest",
            })
            .Build();
        var handler = new FakeHandler();
        handler.EnqueueEmbedding([1.0f, 2.0f, 3.0f]);
        var svc = new EmbeddingService(
            new HttpClient(handler), config, NullLogger<EmbeddingService>.Instance);

        await svc.EmbedAsync("test");

        Assert.Single(handler.Requests);
        Assert.StartsWith("http://rag-server:11434", handler.Requests[0].Url);
    }

    [Fact]
    public async Task EmbedAsync_RagOrder0BeforeOrder1()
    {
        // Server 1 (RagOrder=1) listed before Server 0 (RagOrder=0) in config —
        // but RagOrder=0 should still win because it's sorted ascending.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Server listed second but has lower RagOrder
                ["SAGIDE:Ollama:Servers:0:BaseUrl"]  = "http://secondary:11434",
                ["SAGIDE:Ollama:Servers:0:RagOrder"] = "1",
                ["SAGIDE:Ollama:Servers:0:Models:0"] = "nomic-embed-text:latest",

                // Server listed first but has higher RagOrder
                ["SAGIDE:Ollama:Servers:1:BaseUrl"]  = "http://primary:11434",
                ["SAGIDE:Ollama:Servers:1:RagOrder"] = "0",
                ["SAGIDE:Ollama:Servers:1:Models:0"] = "nomic-embed-text:latest",
            })
            .Build();
        var handler = new FakeHandler();
        handler.EnqueueEmbedding([0.5f]);
        var svc = new EmbeddingService(
            new HttpClient(handler), config, NullLogger<EmbeddingService>.Instance);

        await svc.EmbedAsync("test");

        // RagOrder=0 (primary) should be chosen
        Assert.StartsWith("http://primary:11434", handler.Requests[0].Url);
    }

    // ── EmbedAsync — happy path ───────────────────────────────────────────────

    [Fact]
    public async Task EmbedAsync_HappyPath_ReturnsFloatArray()
    {
        var (svc, handler) = Make();
        handler.EnqueueEmbedding([0.1f, 0.2f, 0.3f]);

        var result = await svc.EmbedAsync("hello world");

        Assert.Equal(3, result.Length);
        Assert.Equal(0.1f, result[0], precision: 5);
        Assert.Equal(0.2f, result[1], precision: 5);
        Assert.Equal(0.3f, result[2], precision: 5);
    }

    [Fact]
    public async Task EmbedAsync_PostsToCorrectUrl()
    {
        var (svc, handler) = Make(baseUrl: "http://myollama:11434");
        handler.EnqueueEmbedding([1.0f]);

        await svc.EmbedAsync("test text");

        Assert.Single(handler.Requests);
        Assert.Equal("http://myollama:11434/api/embeddings", handler.Requests[0].Url);
    }

    [Fact]
    public async Task EmbedAsync_RequestBodyContainsModelAndPrompt()
    {
        var (svc, handler) = Make(model: "nomic-embed-text:latest");
        handler.EnqueueEmbedding([1.0f]);

        await svc.EmbedAsync("the quick brown fox");

        var body = handler.Requests[0].Body;
        Assert.Contains("nomic-embed-text", body);
        Assert.Contains("the quick brown fox", body);
    }

    [Fact]
    public async Task EmbedAsync_TrailingSlashOnBaseUrl_Normalised()
    {
        // BaseUrl with trailing slash should still produce /api/embeddings (not //api/embeddings)
        var (svc, handler) = Make(baseUrl: "http://ollama:11434/");
        handler.EnqueueEmbedding([1.0f]);

        await svc.EmbedAsync("text");

        Assert.DoesNotContain("//api/embeddings", handler.Requests[0].Url);
        Assert.EndsWith("/api/embeddings", handler.Requests[0].Url);
    }

    // ── EmbedAsync — failures ─────────────────────────────────────────────────

    [Fact]
    public async Task EmbedAsync_HttpServerError_ReturnsEmptyArray()
    {
        var (svc, handler) = Make();
        handler.EnqueueStatus(HttpStatusCode.InternalServerError);

        // Must not throw; returns [] on any failure
        var result = await svc.EmbedAsync("failing text");

        Assert.Empty(result);
    }

    [Fact]
    public async Task EmbedAsync_HttpException_ReturnsEmptyArray()
    {
        var (svc, handler) = Make();
        handler.EnqueueException(new HttpRequestException("connection refused"));

        var result = await svc.EmbedAsync("text");

        Assert.Empty(result);
    }

    [Fact]
    public async Task EmbedAsync_NullEmbeddingInResponse_ReturnsEmptyArray()
    {
        var (svc, handler) = Make();
        // JSON with null embedding field
        handler.EnqueueEmbedding([]);

        var result = await svc.EmbedAsync("text");

        Assert.Empty(result);
    }

    // ── EmbedChunksAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task EmbedChunksAsync_EmptyList_ReturnsEmptyList()
    {
        var (svc, _) = Make();

        var results = await svc.EmbedChunksAsync([]);

        Assert.Empty(results);
    }

    [Fact]
    public async Task EmbedChunksAsync_ReturnsOneVectorPerChunk()
    {
        var (svc, handler) = Make();
        handler.EnqueueEmbedding([1.0f, 0.0f]);
        handler.EnqueueEmbedding([0.0f, 1.0f]);
        handler.EnqueueEmbedding([0.5f, 0.5f]);

        var chunks = new List<TextChunk>
        {
            new("chunk one",   "http://a.txt", 0),
            new("chunk two",   "http://b.txt", 0),
            new("chunk three", "http://c.txt", 0),
        };

        var results = await svc.EmbedChunksAsync(chunks);

        Assert.Equal(3, results.Count);
        Assert.Equal(1.0f, results[0][0], precision: 5);
        Assert.Equal(1.0f, results[1][1], precision: 5);
        Assert.Equal(0.5f, results[2][0], precision: 5);
    }

    [Fact]
    public async Task EmbedChunksAsync_PartialHttpFailure_FailedChunkReturnsEmpty()
    {
        var (svc, handler) = Make();
        handler.EnqueueEmbedding([1.0f, 2.0f]);
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);  // second fails

        var chunks = new List<TextChunk>
        {
            new("chunk one", "http://a.txt", 0),
            new("chunk two", "http://b.txt", 0),
        };

        var results = await svc.EmbedChunksAsync(chunks);

        Assert.Equal(2, results.Count);
        Assert.Equal(2,   results[0].Length);  // first succeeded
        Assert.Empty(results[1]);              // second failed → empty
    }

    [Fact]
    public async Task EmbedChunksAsync_RequestBodiesContainChunkText()
    {
        var (svc, handler) = Make();
        handler.EnqueueEmbedding([1.0f]);
        handler.EnqueueEmbedding([2.0f]);

        var chunks = new List<TextChunk>
        {
            new("unique text alpha",   "http://a.txt", 0),
            new("unique text beta",    "http://b.txt", 0),
        };

        await svc.EmbedChunksAsync(chunks);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("unique text alpha", handler.Requests[0].Body);
        Assert.Contains("unique text beta",  handler.Requests[1].Body);
    }
}
