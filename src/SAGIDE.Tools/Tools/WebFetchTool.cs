using SAGIDE.Core.Interfaces;

namespace SAGIDE.Tools.Tools;

/// <summary>
/// Tool wrapper for HTTP URL fetching.
///
/// Parameters:
///   url  (required) — the URL to fetch
///
/// Returns the page body as plain text, trimmed to a reasonable length.
/// Backed by the existing <c>WebFetcher</c> in <c>SAGIDE.Service.Rag</c> via a
/// delegate so this assembly has no direct reference to that class.
/// </summary>
public sealed class WebFetchTool : ITool
{
    public string Name        => "web_fetch";
    public string Description => "Fetches the content of an HTTP URL and returns it as plain text.";

    private readonly Func<string, CancellationToken, Task<string>> _fetchDelegate;

    /// <param name="fetchDelegate">
    /// Delegate that performs the actual fetch — typically
    /// <c>async (url, ct) => (await webFetcher.FetchUrlAsync(url, ct)).Body</c>.
    /// Using a delegate keeps this class free of a reference to WebFetcher.
    /// </param>
    public WebFetchTool(Func<string, CancellationToken, Task<string>> fetchDelegate)
    {
        _fetchDelegate = fetchDelegate;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Parameter 'url' is required for web_fetch.");

        return await _fetchDelegate(url, ct);
    }
}
