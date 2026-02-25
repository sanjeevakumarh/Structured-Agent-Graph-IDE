using SAGIDE.Core.Models;

namespace SAGIDE.Service.Rag;

/// <summary>
/// Splits text into overlapping chunks for embedding.
/// Supports fixed-size (character), sentence-boundary, and code-aware modes.
/// </summary>
public sealed class TextChunker
{
    private readonly int _chunkSize;   // target chunk size in characters
    private readonly int _overlap;     // overlap in characters between consecutive chunks

    private static readonly char[] SentenceEnds = ['.', '!', '?', '\n'];
    private static readonly string[] CodeBoundaries = ["\npublic ", "\nprivate ", "\nprotected ",
        "\nclass ", "\nstruct ", "\ninterface ", "\ndef ", "\nasync def ", "\nfunction "];

    public TextChunker(int chunkSize = 1500, int overlap = 200)
    {
        _chunkSize = chunkSize;
        _overlap   = overlap;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Chunk a FetchedDocument into overlapping text pieces.</summary>
    public IReadOnlyList<TextChunk> Chunk(FetchedDocument doc, ChunkMode mode = ChunkMode.Sentence)
    {
        return mode switch
        {
            ChunkMode.Fixed    => ChunkFixed(doc.Body, doc.Url, doc.SourceType),
            ChunkMode.Sentence => ChunkBySentence(doc.Body, doc.Url, doc.SourceType),
            ChunkMode.Code     => ChunkByCodeBoundary(doc.Body, doc.Url, doc.SourceType),
            _ => ChunkBySentence(doc.Body, doc.Url, doc.SourceType),
        };
    }

    /// <summary>Chunk a list of documents. Uses code mode for source files.</summary>
    public IReadOnlyList<TextChunk> ChunkAll(IEnumerable<FetchedDocument> docs)
    {
        var all = new List<TextChunk>();
        foreach (var doc in docs)
        {
            var mode = IsSourceCode(doc.Url) ? ChunkMode.Code : ChunkMode.Sentence;
            all.AddRange(Chunk(doc, mode));
        }
        return all;
    }

    // ── Chunking strategies ───────────────────────────────────────────────────

    private IReadOnlyList<TextChunk> ChunkFixed(string text, string url, string? sourceType)
    {
        var chunks = new List<TextChunk>();
        var index  = 0;
        var i      = 0;
        while (i < text.Length)
        {
            var end    = Math.Min(i + _chunkSize, text.Length);
            var chunk  = text[i..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
                chunks.Add(new TextChunk(chunk, url, index++, sourceType));
            i += _chunkSize - _overlap;
            if (i >= text.Length) break;
        }
        return chunks;
    }

    private IReadOnlyList<TextChunk> ChunkBySentence(string text, string url, string? sourceType)
    {
        var chunks  = new List<TextChunk>();
        var buffer  = new System.Text.StringBuilder();
        var index   = 0;
        var overlap = new System.Text.StringBuilder();

        foreach (var sentence in SplitSentences(text))
        {
            if (buffer.Length + sentence.Length > _chunkSize && buffer.Length > 0)
            {
                var chunk = buffer.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(chunk))
                    chunks.Add(new TextChunk(chunk, url, index++, sourceType));

                // Carry overlap forward
                buffer.Clear();
                if (overlap.Length > 0)
                    buffer.Append(overlap);
                overlap.Clear();
            }

            buffer.Append(sentence).Append(' ');

            // Maintain trailing overlap window
            if (buffer.Length > _overlap)
                overlap = new System.Text.StringBuilder(buffer.ToString()[^_overlap..]);
        }

        if (buffer.Length > 0)
        {
            var chunk = buffer.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
                chunks.Add(new TextChunk(chunk, url, index, sourceType));
        }

        return chunks;
    }

    private IReadOnlyList<TextChunk> ChunkByCodeBoundary(string text, string url, string? sourceType)
    {
        // Split on top-level code boundaries (class/function definitions)
        var segments = new List<string> { text };
        foreach (var boundary in CodeBoundaries)
        {
            var expanded = new List<string>();
            foreach (var seg in segments)
            {
                var parts = seg.Split(boundary, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length; i++)
                    expanded.Add(i == 0 ? parts[i] : boundary.TrimStart('\n') + parts[i]);
            }
            segments = expanded;
        }

        var chunks = new List<TextChunk>();
        var index  = 0;
        foreach (var seg in segments)
        {
            if (seg.Length <= _chunkSize)
            {
                if (!string.IsNullOrWhiteSpace(seg))
                    chunks.Add(new TextChunk(seg.Trim(), url, index++, sourceType));
            }
            else
            {
                // Segment is still too large — fall back to sentence chunking
                var subDoc = new FetchedDocument(url, string.Empty, seg, DateTime.UtcNow, sourceType ?? "code");
                foreach (var sub in ChunkBySentence(seg, url, sourceType))
                    chunks.Add(sub with { ChunkIndex = index++ });
            }
        }
        return chunks;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(SentenceEnds, text[i]) >= 0 &&
                (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]) || text[i] == '\n'))
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    private static bool IsSourceCode(string url) =>
        url.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)  ||
        url.EndsWith(".py", StringComparison.OrdinalIgnoreCase)  ||
        url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)  ||
        url.EndsWith(".js", StringComparison.OrdinalIgnoreCase)  ||
        url.EndsWith(".go", StringComparison.OrdinalIgnoreCase)  ||
        url.EndsWith(".rs", StringComparison.OrdinalIgnoreCase);
}

public enum ChunkMode
{
    Fixed,
    Sentence,
    Code,
}
