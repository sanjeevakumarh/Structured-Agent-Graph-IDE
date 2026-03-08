using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Rag;

namespace SAGIDE.Service.Tests;

public class VectorStoreTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private VectorStore _store = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sagide-vs-test-{Guid.NewGuid():N}.db");
        _store  = new VectorStore(_dbPath, NullLogger<VectorStore>.Instance);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        // Clear the SQLite connection pool for this database so the file is fully
        // released before we attempt to delete it (Windows keeps the file locked
        // until all pooled connections are closed).
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ── Upsert ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_ThenSearch_ReturnsInsertedChunk()
    {
        var chunks     = Chunks(("hello world", "http://a.txt", 0));
        var embeddings = Vecs(new[] { 1f, 0f, 0f });

        await _store.UpsertAsync(chunks, embeddings);

        var results = await _store.SearchAsync(new[] { 1f, 0f, 0f }, topK: 5);

        Assert.Single(results);
        Assert.Equal("hello world", results[0].Chunk.Text);
    }

    [Fact]
    public async Task Upsert_Idempotent_UpdatesExistingChunk()
    {
        var chunks1 = Chunks(("original text", "http://a.txt", 0));
        var chunks2 = Chunks(("updated text",  "http://a.txt", 0));
        var vec     = Vecs(new[] { 1f, 0f, 0f });

        await _store.UpsertAsync(chunks1, vec);
        await _store.UpsertAsync(chunks2, vec);

        var results = await _store.SearchAsync(new[] { 1f, 0f, 0f }, topK: 5);

        Assert.Single(results);
        Assert.Equal("updated text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task Upsert_MismatchedCountsThrows()
    {
        var chunks = Chunks(("text1", "http://a.txt", 0), ("text2", "http://b.txt", 0));
        var vecs   = Vecs(new[] { 1f, 0f });  // only one embedding

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.UpsertAsync(chunks, vecs));
    }

    // ── Search — ranking ──────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ReturnsClosestChunkFirst()
    {
        // Chunk A is close to query, Chunk B is far
        var chunks = Chunks(
            ("close", "http://close.txt", 0),
            ("far",   "http://far.txt",   0));
        var embeddings = new List<float[]>
        {
            new[] { 1f, 0f, 0f },   // A — same direction as query
            new[] { 0f, 1f, 0f },   // B — perpendicular
        };

        await _store.UpsertAsync(chunks, embeddings);

        var results = await _store.SearchAsync(new[] { 1f, 0f, 0f }, topK: 2);

        Assert.Equal("close", results[0].Chunk.Text);
        Assert.Equal("far",   results[1].Chunk.Text);
    }

    [Fact]
    public async Task Search_TopKLimitsResults()
    {
        var chunks = Chunks(
            ("a", "http://a.txt", 0),
            ("b", "http://b.txt", 0),
            ("c", "http://c.txt", 0));
        var vecs = new List<float[]>
        {
            new[] { 1f, 0f, 0f },
            new[] { 0f, 1f, 0f },
            new[] { 0f, 0f, 1f },
        };

        await _store.UpsertAsync(chunks, vecs);

        var results = await _store.SearchAsync(new[] { 1f, 0f, 0f }, topK: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Search_EmptyStore_ReturnsEmpty()
    {
        var results = await _store.SearchAsync(new[] { 1f, 0f, 0f }, topK: 5);
        Assert.Empty(results);
    }

    // ── Source-tag filtering ──────────────────────────────────────────────────

    [Fact]
    public async Task Search_SourceTagFilter_ReturnsOnlyTaggedChunks()
    {
        var tagged   = Chunks(("tagged",   "http://t.txt", 0));
        var untagged = Chunks(("untagged", "http://u.txt", 0));
        var vec      = Vecs(new[] { 1f, 0f, 0f });

        await _store.UpsertAsync(tagged,   vec, sourceTag: "mytag");
        await _store.UpsertAsync(untagged, vec, sourceTag: null);

        var results = await _store.SearchAsync(new[] { 1f, 0f, 0f }, topK: 5, sourceTag: "mytag");

        Assert.Single(results);
        Assert.Equal("tagged", results[0].Chunk.Text);
    }

    [Fact]
    public async Task Search_NoTagFilter_ReturnsAllChunks()
    {
        var a = Chunks(("a", "http://a.txt", 0));
        var b = Chunks(("b", "http://b.txt", 0));
        var v = Vecs(new[] { 1f, 0f, 0f });

        await _store.UpsertAsync(a, v, sourceTag: "tag1");
        await _store.UpsertAsync(b, v, sourceTag: "tag2");

        var results = await _store.SearchAsync(new[] { 1f, 0f, 0f }, topK: 10, sourceTag: null);

        Assert.Equal(2, results.Count);
    }

    // ── DeleteBySourceUrlPrefix — LIKE wildcard escaping ─────────────────────

    [Fact]
    public async Task DeleteBySourceUrlPrefix_PlainPrefix_DeletesMatchingChunks()
    {
        var chunks = Chunks(
            ("keep",   "http://other/doc.txt", 0),
            ("delete", "http://project/a.txt", 0),
            ("delete", "http://project/b.txt", 0));
        var v = Vecs(new[] { 1f, 0f }, new[] { 1f, 0f }, new[] { 1f, 0f });

        await _store.UpsertAsync(chunks, v);
        await _store.DeleteBySourceUrlPrefixAsync("http://project/");

        var results = await _store.SearchAsync(new[] { 1f, 0f }, topK: 10);

        Assert.Single(results);
        Assert.Equal("keep", results[0].Chunk.Text);
    }

    [Fact]
    public async Task DeleteBySourceUrlPrefix_PrefixContainsPercent_OnlyDeletesExactPrefix()
    {
        // A prefix that literally contains '%' should be treated as a literal character,
        // not a LIKE wildcard, so only URLs that actually start with "http://host%"
        // should be deleted — all others must survive.
        var chunks = Chunks(
            ("should delete", "http://host%2Fpath/file.txt", 0),
            ("should keep",   "http://host/anything/file.txt", 0));
        var v = Vecs(new[] { 1f, 0f }, new[] { 1f, 0f });

        await _store.UpsertAsync(chunks, v);
        await _store.DeleteBySourceUrlPrefixAsync("http://host%2Fpath/");

        var results = await _store.SearchAsync(new[] { 1f, 0f }, topK: 10);

        Assert.Single(results);
        Assert.Equal("should keep", results[0].Chunk.Text);
    }

    [Fact]
    public async Task DeleteBySourceUrlPrefix_PrefixContainsUnderscore_OnlyDeletesExactPrefix()
    {
        // '_' is a single-character wildcard in LIKE; must be escaped to be literal.
        var chunks = Chunks(
            ("should delete", "http://my_bucket/file.txt", 0),
            ("should keep",   "http://myXbucket/file.txt", 0));
        var v = Vecs(new[] { 1f, 0f }, new[] { 1f, 0f });

        await _store.UpsertAsync(chunks, v);
        await _store.DeleteBySourceUrlPrefixAsync("http://my_bucket/");

        var results = await _store.SearchAsync(new[] { 1f, 0f }, topK: 10);

        Assert.Single(results);
        Assert.Equal("should keep", results[0].Chunk.Text);
    }

    // ── Scores are in [0, 1] ──────────────────────────────────────────────────

    [Fact]
    public async Task Search_ScoresAreBetweenZeroAndOne()
    {
        var chunks = Chunks(
            ("a", "http://a.txt", 0),
            ("b", "http://b.txt", 0));
        var vecs = new List<float[]>
        {
            new[] { 1f, 0f },
            new[] { 0f, 1f },
        };

        await _store.UpsertAsync(chunks, vecs);

        var results = await _store.SearchAsync(new[] { 0.7f, 0.3f }, topK: 5);

        Assert.All(results, r =>
        {
            Assert.True(r.Score >= 0f, $"Score {r.Score} is negative");
            Assert.True(r.Score <= 1f + 1e-5f, $"Score {r.Score} exceeds 1.0");
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<TextChunk> Chunks(params (string text, string url, int idx)[] items) =>
        items.Select(i => new TextChunk(i.text, i.url, i.idx)).ToList();

    private static IReadOnlyList<float[]> Vecs(params float[][] vecs) =>
        vecs.ToList();
}
