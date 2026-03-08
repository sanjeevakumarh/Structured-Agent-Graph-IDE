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
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, "chunkSize must be positive");
        if (overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(overlap), overlap, "overlap must be non-negative");
        if (overlap >= chunkSize)
            throw new ArgumentException(
                $"overlap ({overlap}) must be less than chunkSize ({chunkSize}); " +
                "otherwise the chunking loop would never advance.", nameof(overlap));

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
            ChunkMode.Markdown => ChunkByMarkdown(doc.Body, doc.Url, doc.SourceType),
            _ => ChunkBySentence(doc.Body, doc.Url, doc.SourceType),
        };
    }

    /// <summary>Chunk a list of documents. Uses code mode for source files.</summary>
    public IReadOnlyList<TextChunk> ChunkAll(IEnumerable<FetchedDocument> docs)
    {
        var all = new List<TextChunk>();
        foreach (var doc in docs)
        {
            var mode = IsSourceCode(doc.Url) ? ChunkMode.Code
                     : IsMarkdown(doc.Url)   ? ChunkMode.Markdown
                     : ChunkMode.Sentence;
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

    /// <summary>
    /// Markdown-aware chunking: splits on headers (## / ### / etc.), keeps each header
    /// attached to its body content, strips Logseq properties blocks, and respects
    /// chunk size limits by falling back to sentence splitting for large sections.
    /// </summary>
    private IReadOnlyList<TextChunk> ChunkByMarkdown(string text, string url, string? sourceType)
    {
        var chunks = new List<TextChunk>();
        var index = 0;

        // Split into sections by markdown headers
        var sections = SplitMarkdownSections(text);

        var buffer = new System.Text.StringBuilder();

        foreach (var (header, body) in sections)
        {
            // Strip Logseq :PROPERTIES: blocks
            var cleaned = StripPropertiesBlocks(body).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            var sectionText = header is not null ? $"{header}\n{cleaned}" : cleaned;

            // If adding this section would exceed chunk size, flush the buffer
            if (buffer.Length + sectionText.Length > _chunkSize && buffer.Length > 0)
            {
                var chunk = buffer.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(chunk))
                    chunks.Add(new TextChunk(chunk, url, index++, sourceType));
                buffer.Clear();
            }

            // Section itself is too large — split it with sentence chunking
            if (sectionText.Length > _chunkSize)
            {
                // Flush anything in the buffer first
                if (buffer.Length > 0)
                {
                    var chunk = buffer.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(chunk))
                        chunks.Add(new TextChunk(chunk, url, index++, sourceType));
                    buffer.Clear();
                }

                // Sub-chunk the large section, prepending the header to each sub-chunk
                var subChunks = ChunkBySentence(cleaned, url, sourceType);
                foreach (var sub in subChunks)
                {
                    var prefixed = header is not null ? $"{header}\n{sub.Text}" : sub.Text;
                    chunks.Add(new TextChunk(prefixed, url, index++, sourceType));
                }
                continue;
            }

            buffer.Append(sectionText).Append('\n');
        }

        // Flush remaining buffer
        if (buffer.Length > 0)
        {
            var chunk = buffer.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
                chunks.Add(new TextChunk(chunk, url, index, sourceType));
        }

        return chunks;
    }

    /// <summary>Splits markdown text into (header, body) sections on heading lines.</summary>
    private static List<(string? Header, string Body)> SplitMarkdownSections(string text)
    {
        var sections = new List<(string? Header, string Body)>();
        var lines = text.Split('\n');
        string? currentHeader = null;
        var body = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.Length > 0 && line[0] == '#' && line.Contains(' '))
            {
                // Flush previous section
                if (body.Length > 0 || currentHeader is not null)
                    sections.Add((currentHeader, body.ToString()));
                currentHeader = line.TrimEnd();
                body.Clear();
            }
            else
            {
                body.Append(line).Append('\n');
            }
        }

        // Flush last section
        if (body.Length > 0 || currentHeader is not null)
            sections.Add((currentHeader, body.ToString()));

        return sections;
    }

    /// <summary>Strips Logseq :PROPERTIES: ... :END: blocks from text.</summary>
    private static string StripPropertiesBlocks(string text)
    {
        const string propStart = ":PROPERTIES:";
        const string propEnd = ":END:";
        var result = text;
        while (true)
        {
            var start = result.IndexOf(propStart, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            var end = result.IndexOf(propEnd, start + propStart.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0) break;
            result = result[..start] + result[(end + propEnd.Length)..];
        }
        return result;
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

    private static bool IsMarkdown(string url) =>
        url.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

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
    Markdown,
}
