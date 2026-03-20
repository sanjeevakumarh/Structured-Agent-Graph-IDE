using SAGIDE.Core.Interfaces;

namespace SAGIDE.Tools.Tools;

/// <summary>
/// Tool wrapper for web search via SearXNG.
///
/// Parameters:
///   query  (required) — the search query string
///   limit  (optional) — max result count hint, default "10"
///
/// Returns formatted search results as plain text.
/// Backed by a delegate so this assembly has no direct reference to WebSearchAdapter.
/// </summary>
public sealed class WebSearchTool : ITool
{
    public string Name        => "web_search";
    public string Description => "Searches the web via SearXNG and returns formatted results.";

    private readonly Func<string, CancellationToken, Task<string>> _searchDelegate;

    public WebSearchTool(Func<string, CancellationToken, Task<string>> searchDelegate)
    {
        _searchDelegate = searchDelegate;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Parameter 'query' is required for web_search.");

        return await _searchDelegate(query, ct);
    }
}
