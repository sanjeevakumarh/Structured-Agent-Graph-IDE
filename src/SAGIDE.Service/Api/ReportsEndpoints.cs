using Microsoft.Extensions.Configuration;

namespace SAGIDE.Service.Api;

internal static class ReportsEndpoints
{
    internal static IEndpointRouteBuilder MapReportsEndpoints(
        this IEndpointRouteBuilder app,
        IConfiguration configuration)
    {
        var reportsRoot = ResolveReportsRoot(configuration);

        // GET /api/reports — list all domains (sub-directories of the reports root)
        app.MapGet("/api/reports", () =>
        {
            if (!Directory.Exists(reportsRoot))
                return Results.Ok(Array.Empty<object>());

            var domains = Directory
                .GetDirectories(reportsRoot)
                .Select(dir =>
                {
                    var name = Path.GetFileName(dir);
                    var count = Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly).Length;
                    return new { domain = name, fileCount = count };
                })
                .OrderBy(d => d.domain)
                .ToArray();

            return Results.Ok(domains);
        });

        // GET /api/reports/{domain} — list report files in a domain directory
        app.MapGet("/api/reports/{domain}", (string domain) =>
        {
            var domainDir = Path.Combine(reportsRoot, SanitizeSegment(domain));
            if (!Directory.Exists(domainDir))
                return Results.NotFound(new { error = $"Domain '{domain}' not found" });

            var files = Directory
                .GetFiles(domainDir, "*.md", SearchOption.TopDirectoryOnly)
                .Select(f =>
                {
                    var info = new FileInfo(f);
                    return new
                    {
                        filename     = info.Name,
                        sizeBytes    = info.Length,
                        lastModified = info.LastWriteTimeUtc,
                    };
                })
                .OrderByDescending(f => f.lastModified)
                .ToArray();

            return Results.Ok(files);
        });

        // GET /api/reports/{domain}/{filename}
        // Serves raw markdown as text/plain by default.
        // Pass ?format=html for a minimal HTML-wrapped rendering.
        app.MapGet("/api/reports/{domain}/{filename}", async (
            string domain, string filename,
            string? format) =>
        {
            // Reject path traversal attempts
            if (filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid filename" });

            var filePath = Path.Combine(reportsRoot, SanitizeSegment(domain), filename);
            if (!File.Exists(filePath))
                return Results.NotFound(new { error = "Report not found" });

            var content = await File.ReadAllTextAsync(filePath);

            if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
            {
                var html = WrapInHtml(filename, content);
                return Results.Content(html, "text/html; charset=utf-8");
            }

            return Results.Content(content, "text/plain; charset=utf-8");
        });

        return app;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ResolveReportsRoot(IConfiguration configuration)
    {
        var configured = configuration["SAGIDE:ReportsPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.StartsWith("~/") || configured == "~")
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    configured.Length > 2 ? configured[2..] : string.Empty);
            return configured;
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "reports");
    }

    /// <summary>
    /// Strips characters that could escape the reports directory.
    /// Only allows alphanumeric, dash, underscore, and dot.
    /// </summary>
    private static string SanitizeSegment(string segment) =>
        string.Concat(segment.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));

    /// <summary>
    /// Produces a minimal HTML page that renders the markdown file in a readable way.
    /// Wraps pre-formatted content; no external JS/CSS required.
    /// </summary>
    private static string WrapInHtml(string title, string markdownContent)
    {
        var escapedTitle   = System.Net.WebUtility.HtmlEncode(title);
        var escapedContent = System.Net.WebUtility.HtmlEncode(markdownContent);
        return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n"
             + "  <meta charset=\"utf-8\"/>\n"
             + $"  <title>{escapedTitle}</title>\n"
             + "  <style>\n"
             + "    body { font-family: system-ui, sans-serif; max-width: 860px; margin: 2rem auto; padding: 0 1rem; }\n"
             + "    pre  { white-space: pre-wrap; word-break: break-word; background: #f5f5f5; padding: 1rem; border-radius: 4px; }\n"
             + "  </style>\n"
             + $"</head>\n<body>\n  <pre>{escapedContent}</pre>\n</body>\n</html>";
    }
}
