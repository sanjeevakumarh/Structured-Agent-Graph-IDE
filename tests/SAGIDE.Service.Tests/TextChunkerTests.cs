using SAGIDE.Core.Models;
using SAGIDE.Service.Rag;

namespace SAGIDE.Service.Tests;

public class TextChunkerTests
{
    // ── Fixed chunking ────────────────────────────────────────────────────────

    [Fact]
    public void Fixed_ShortText_SingleChunk()
    {
        var chunker = new TextChunker(chunkSize: 200, overlap: 0);
        var doc     = Doc("hello world", "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Fixed);

        Assert.Single(chunks);
        Assert.Equal("hello world", chunks[0].Text);
        Assert.Equal(0, chunks[0].ChunkIndex);
    }

    [Fact]
    public void Fixed_LongText_SplitsOnChunkSize()
    {
        var chunker = new TextChunker(chunkSize: 10, overlap: 0);
        var doc     = Doc("0123456789ABCDEF", "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Fixed);

        // 16 chars / 10 per chunk = 2 chunks
        Assert.Equal(2, chunks.Count);
        Assert.Equal("0123456789", chunks[0].Text);
        Assert.Equal("ABCDEF",     chunks[1].Text);
    }

    [Fact]
    public void Fixed_Overlap_CarriesOverlap()
    {
        var chunker = new TextChunker(chunkSize: 10, overlap: 5);
        var doc     = Doc("ABCDEFGHIJKLMNOP", "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Fixed);

        // First chunk starts at 0, step = 10-5 = 5
        // chunk[0] = [0..10] = ABCDEFGHIJ
        // chunk[1] = [5..15] = FGHIJKLMNO
        // chunk[2] = [10..16] = KLMNOP
        Assert.True(chunks.Count >= 2);
        Assert.StartsWith("ABCDEFGHIJ", chunks[0].Text);
        Assert.StartsWith("FGHIJKLMNO", chunks[1].Text);
    }

    [Fact]
    public void Fixed_EmptyText_NoChunks()
    {
        var chunker = new TextChunker();
        var doc     = Doc(string.Empty, "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Fixed);

        Assert.Empty(chunks);
    }

    [Fact]
    public void Fixed_WhitespaceOnly_NoChunks()
    {
        var chunker = new TextChunker(chunkSize: 10, overlap: 0);
        var doc     = Doc("   \t\n   ", "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Fixed);

        Assert.Empty(chunks);
    }

    [Fact]
    public void Fixed_ChunkIndicesAreSequential()
    {
        var chunker = new TextChunker(chunkSize: 5, overlap: 0);
        var doc     = Doc("AAABBBCCCDDDEEE", "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Fixed);

        for (var i = 0; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].ChunkIndex);
    }

    [Fact]
    public void Fixed_SourceUrlPreserved()
    {
        var chunker = new TextChunker(chunkSize: 5, overlap: 0);
        var doc     = Doc("Hello World Again", "http://my-source.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Fixed);

        Assert.All(chunks, c => Assert.Equal("http://my-source.txt", c.SourceUrl));
    }

    // ── Sentence chunking ──────────────────────────────────────────────────────

    [Fact]
    public void Sentence_SingleShortSentence_OneChunk()
    {
        var chunker = new TextChunker(chunkSize: 200, overlap: 0);
        var doc     = Doc("Hello world.", "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Sentence);

        Assert.Single(chunks);
        Assert.Contains("Hello world", chunks[0].Text);
    }

    [Fact]
    public void Sentence_MultipleSentences_SplitsCorrectly()
    {
        // Each sentence is ~50 chars; chunk size is 60 so at most one per chunk
        var text = string.Join(" ", Enumerable.Repeat("Hello world, this is a test sentence.", 5));
        var chunker = new TextChunker(chunkSize: 60, overlap: 0);
        var doc     = Doc(text, "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Sentence);

        // Should have produced multiple chunks
        Assert.True(chunks.Count > 1);
    }

    [Fact]
    public void Sentence_TextSmallerThanChunkSize_OneChunk()
    {
        var chunker = new TextChunker(chunkSize: 10_000, overlap: 0);
        var doc     = Doc("Short sentence.", "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Sentence);

        Assert.Single(chunks);
    }

    [Fact]
    public void Sentence_ChunkIndicesAreSequential()
    {
        var text = string.Join(" ", Enumerable.Repeat("A sentence ends here.", 20));
        var chunker = new TextChunker(chunkSize: 50, overlap: 0);
        var doc     = Doc(text, "http://x.txt");
        var chunks  = chunker.Chunk(doc, ChunkMode.Sentence);

        for (var i = 0; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].ChunkIndex);
    }

    // ── Code chunking ─────────────────────────────────────────────────────────

    [Fact]
    public void Code_SplitsOnClassBoundary()
    {
        var code = "preamble\nclass Foo { int x; }\nclass Bar { int y; }";
        var chunker = new TextChunker(chunkSize: 5000, overlap: 0);
        var doc     = Doc(code, "http://file.cs");
        var chunks  = chunker.Chunk(doc, ChunkMode.Code);

        Assert.True(chunks.Count >= 2,
            $"Expected >= 2 chunks but got {chunks.Count}");
        Assert.Contains(chunks, c => c.Text.Contains("class Foo"));
        Assert.Contains(chunks, c => c.Text.Contains("class Bar"));
    }

    [Fact]
    public void Code_FallsBackToSentenceWhenSegmentTooLarge()
    {
        // One big class with no code boundaries inside — falls back to sentence chunking
        var bigBody = string.Concat(Enumerable.Repeat("This is a long comment line.\n", 200));
        var code    = $"class Big {{\n{bigBody}\n}}";
        var chunker = new TextChunker(chunkSize: 500, overlap: 0);
        var doc     = Doc(code, "http://file.cs");
        var chunks  = chunker.Chunk(doc, ChunkMode.Code);

        Assert.True(chunks.Count > 1, "Expected multiple chunks from sentence fallback");
    }

    // ── ChunkAll / source-type detection ──────────────────────────────────────

    [Fact]
    public void ChunkAll_CsFile_UsesCodeMode()
    {
        var code = "intro\nclass A { }\nclass B { }";
        var docs = new[]
        {
            new FetchedDocument("http://sample.cs", string.Empty, code, DateTime.UtcNow, "code"),
        };
        var chunker = new TextChunker(chunkSize: 5000, overlap: 0);
        var chunks  = chunker.ChunkAll(docs);

        // Code mode should split on class boundaries → ≥ 2 chunks
        Assert.True(chunks.Count >= 2);
    }

    [Fact]
    public void ChunkAll_TxtFile_UsesSentenceMode()
    {
        var text = string.Join(" ", Enumerable.Repeat("A sentence.", 40));
        var docs = new[]
        {
            new FetchedDocument("http://readme.txt", string.Empty, text, DateTime.UtcNow, "text"),
        };
        var chunker = new TextChunker(chunkSize: 60, overlap: 0);
        var chunks  = chunker.ChunkAll(docs);

        Assert.True(chunks.Count > 1);
    }

    [Fact]
    public void ChunkAll_MultipleDocuments_AllChunksIncluded()
    {
        var docs = new[]
        {
            Doc("Alpha sentence one. Alpha sentence two.",   "http://a.txt"),
            Doc("Beta sentence one. Beta sentence two.",     "http://b.txt"),
        };
        var chunker = new TextChunker(chunkSize: 5000, overlap: 0);
        var chunks  = chunker.ChunkAll(docs);

        Assert.Contains(chunks, c => c.SourceUrl == "http://a.txt");
        Assert.Contains(chunks, c => c.SourceUrl == "http://b.txt");
    }

    [Fact]
    public void ChunkAll_EmptyDocumentList_NoChunks()
    {
        var chunker = new TextChunker();
        var chunks  = chunker.ChunkAll([]);

        Assert.Empty(chunks);
    }

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_ZeroChunkSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(chunkSize: 0, overlap: 0));
    }

    [Fact]
    public void Constructor_NegativeChunkSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(chunkSize: -1, overlap: 0));
    }

    [Fact]
    public void Constructor_NegativeOverlap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(chunkSize: 100, overlap: -1));
    }

    [Fact]
    public void Constructor_OverlapEqualsChunkSize_Throws()
    {
        // overlap == chunkSize would cause ChunkFixed to never advance (i += 0)
        Assert.Throws<ArgumentException>(() => new TextChunker(chunkSize: 10, overlap: 10));
    }

    [Fact]
    public void Constructor_OverlapGreaterThanChunkSize_Throws()
    {
        // overlap > chunkSize would cause ChunkFixed to go backwards
        Assert.Throws<ArgumentException>(() => new TextChunker(chunkSize: 10, overlap: 11));
    }

    [Fact]
    public void Constructor_ValidArguments_DoesNotThrow()
    {
        var ex = Record.Exception(() => new TextChunker(chunkSize: 10, overlap: 9));
        Assert.Null(ex);
    }

    // ── Default constructor ───────────────────────────────────────────────────

    [Fact]
    public void DefaultConstructor_ChunksSingleSentence()
    {
        var chunker = new TextChunker();
        var doc     = Doc("Just one sentence.", "http://x.txt");
        var chunks  = chunker.Chunk(doc);

        Assert.Single(chunks);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FetchedDocument Doc(string body, string url) =>
        new(url, string.Empty, body, DateTime.UtcNow, "text");
}
