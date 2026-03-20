using AngleSharp;
using AngleSharp.Dom;

namespace SAGIDE.Memory;

/// <summary>
/// Extracts readable text from HTML pages by stripping scripts, styles, navigation,
/// and other non-content elements. Used after fetching search result URLs to provide
/// actual page content to LLMs instead of meta-description snippets.
/// </summary>
public static class HtmlTextExtractor
{
    /// <summary>Tags that never contain useful content — removed before text extraction.</summary>
    private static readonly string[] RemoveTags =
    [
        "script", "style", "nav", "header", "footer", "aside",
        "form", "noscript", "svg", "iframe", "button", "input",
        "select", "textarea", "menu", "dialog",
    ];

    /// <summary>
    /// Extracts readable text from raw HTML, prioritizing article/main content.
    /// Returns empty string on any failure — never throws.
    /// </summary>
    public static async Task<string> ExtractAsync(string html, int maxChars = 3000)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        try
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var doc = await context.OpenAsync(req => req.Content(html));

            // Remove non-content elements
            foreach (var tag in RemoveTags)
                foreach (var el in doc.QuerySelectorAll(tag).ToArray())
                    el.Remove();

            // Prefer structured content regions; fall back to body
            var contentEl = doc.QuerySelector("article")
                        ?? doc.QuerySelector("main")
                        ?? doc.QuerySelector("[role='main']")
                        ?? (IElement?)doc.Body;

            if (contentEl is null) return string.Empty;

            // Insert separators between table cells/rows so data doesn't run together.
            // TextContent strips all tags leaving "Revenue305,453281,724" instead of
            // "Revenue | 305,453 | 281,724".
            foreach (var td in contentEl.QuerySelectorAll("td, th").ToArray())
                td.TextContent = td.TextContent + " | ";
            foreach (var tr in contentEl.QuerySelectorAll("tr").ToArray())
                tr.InsertBefore(doc.CreateTextNode("\n"), tr.FirstChild);

            var text = contentEl.TextContent;

            // Collapse whitespace: multiple blank lines → double-newline, runs of spaces → single
            text = CollapseWhitespace(text);

            // Truncate on a word boundary
            if (text.Length > maxChars)
            {
                var cutoff = text.LastIndexOf(' ', maxChars);
                if (cutoff < maxChars / 2) cutoff = maxChars; // no good word boundary
                text = text[..cutoff] + "\n[…truncated]";
            }

            return text.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length / 2);
        var blankLineCount = 0;
        var spaceRun = false;

        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                blankLineCount++;
                spaceRun = false;
                if (blankLineCount <= 2) sb.Append('\n');
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (!spaceRun && blankLineCount == 0)
                {
                    sb.Append(' ');
                    spaceRun = true;
                }
            }
            else
            {
                blankLineCount = 0;
                spaceRun = false;
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
