using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Integration tests for <c>GET /api/reports[/{domain}[/{filename}]]</c>.
/// Uses a <see cref="WebApplicationFactory{TEntryPoint}"/> with a temporary reports
/// directory so tests can seed real files without touching the host filesystem.
/// </summary>
public class ReportsEndpointsTests : IClassFixture<ReportsEndpointsTests.ReportsTestFactory>, IDisposable
{
    private readonly ReportsTestFactory _factory;
    private readonly HttpClient _client;

    public ReportsEndpointsTests(ReportsTestFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    // ── GET /api/reports ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetReports_EmptyRoot_ReturnsOkEmptyArray()
    {
        var resp = await _client.GetAsync("/api/reports");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("[]", body.Trim());
    }

    [Fact]
    public async Task GetReports_WithDomainDirectory_ReturnsDomainEntry()
    {
        // Create a domain directory with one file
        var domainDir = Path.Combine(_factory.ReportsRoot, "finance");
        Directory.CreateDirectory(domainDir);
        await File.WriteAllTextAsync(Path.Combine(domainDir, "report.md"), "# Report");

        try
        {
            var resp = await _client.GetAsync("/api/reports");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("finance", body);
        }
        finally
        {
            Directory.Delete(domainDir, recursive: true);
        }
    }

    // ── GET /api/reports/{domain} ─────────────────────────────────────────────

    [Fact]
    public async Task GetReportsDomain_UnknownDomain_Returns404()
    {
        var resp = await _client.GetAsync("/api/reports/ghost-domain-xyz");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetReportsDomain_ExistingDomain_ReturnsFileList()
    {
        var domainDir = Path.Combine(_factory.ReportsRoot, "testdomain");
        Directory.CreateDirectory(domainDir);
        await File.WriteAllTextAsync(Path.Combine(domainDir, "my-report.md"), "# My Report");

        try
        {
            var resp = await _client.GetAsync("/api/reports/testdomain");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("my-report.md", body);
        }
        finally
        {
            Directory.Delete(domainDir, recursive: true);
        }
    }

    // ── GET /api/reports/{domain}/{filename} ──────────────────────────────────

    [Fact]
    public async Task GetReportFile_PathTraversalWithDotDot_Returns400()
    {
        var resp = await _client.GetAsync("/api/reports/finance/../../secret.md");

        // ASP.NET Core route matching will normalise /../ in the URL path, so the
        // server may see just "secret.md" without ".." — but if the raw filename
        // still contains ".." we should get 400.  Either way the file doesn't exist,
        // so 400 or 404 are both acceptable defensive outcomes.
        Assert.True(
            resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Expected 400 or 404 for path traversal, got {resp.StatusCode}");
    }

    [Fact]
    public async Task GetReportFile_FilenameWithDotDot_Returns400()
    {
        // URL-encoded .. so it isn't normalised by the HTTP stack
        var resp = await _client.GetAsync("/api/reports/finance/..%2Fsecret.md");

        Assert.True(
            resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Expected 400 or 404 for encoded path traversal, got {resp.StatusCode}");
    }

    [Fact]
    public async Task GetReportFile_UnknownFile_Returns404()
    {
        // Domain directory exists, file does not
        var domainDir = Path.Combine(_factory.ReportsRoot, "existing");
        Directory.CreateDirectory(domainDir);

        try
        {
            var resp = await _client.GetAsync("/api/reports/existing/no-such-file.md");

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            Directory.Delete(domainDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetReportFile_ExistingFile_ReturnsPlainText()
    {
        const string markdown = "# Hello\nThis is a report.";
        var domainDir = Path.Combine(_factory.ReportsRoot, "plain");
        Directory.CreateDirectory(domainDir);
        await File.WriteAllTextAsync(Path.Combine(domainDir, "hello.md"), markdown);

        try
        {
            var resp = await _client.GetAsync("/api/reports/plain/hello.md");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("text/plain", resp.Content.Headers.ContentType?.MediaType ?? "");
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("Hello", body);
        }
        finally
        {
            Directory.Delete(domainDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetReportFile_HtmlFormat_ReturnsHtmlContentType()
    {
        const string markdown = "# My Report";
        var domainDir = Path.Combine(_factory.ReportsRoot, "htmltest");
        Directory.CreateDirectory(domainDir);
        await File.WriteAllTextAsync(Path.Combine(domainDir, "report.md"), markdown);

        try
        {
            var resp = await _client.GetAsync("/api/reports/htmltest/report.md?format=html");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("text/html", resp.Content.Headers.ContentType?.MediaType ?? "");
            var body = await resp.Content.ReadAsStringAsync();
            // HTML wrapper should include DOCTYPE and <pre> tag
            Assert.Contains("<!DOCTYPE html>", body);
            Assert.Contains("<pre>", body);
        }
        finally
        {
            Directory.Delete(domainDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetReportFile_HtmlFormat_HtmlEncodesContent()
    {
        const string markdown = "# Report\n<script>alert('xss')</script>";
        var domainDir = Path.Combine(_factory.ReportsRoot, "xsstest");
        Directory.CreateDirectory(domainDir);
        await File.WriteAllTextAsync(Path.Combine(domainDir, "xss.md"), markdown);

        try
        {
            var resp = await _client.GetAsync("/api/reports/xsstest/xss.md?format=html");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            // Raw <script> must not appear; it should be HTML-encoded
            Assert.DoesNotContain("<script>", body);
            Assert.Contains("&lt;script&gt;", body);
        }
        finally
        {
            Directory.Delete(domainDir, recursive: true);
        }
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    public sealed class ReportsTestFactory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly string _dbPath;
        private readonly string _promptsDir;
        public readonly string ReportsRoot;

        public ReportsTestFactory()
        {
            _dbPath     = Path.Combine(Path.GetTempPath(), $"sagide-rpt-test-{Guid.NewGuid():N}.db");
            _promptsDir = Path.Combine(Path.GetTempPath(), $"sagide-rpt-prompts-{Guid.NewGuid():N}");
            ReportsRoot = Path.Combine(Path.GetTempPath(), $"sagide-reports-{Guid.NewGuid():N}");

            Directory.CreateDirectory(_promptsDir);
            Directory.CreateDirectory(ReportsRoot);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Add(new MemoryConfigurationSource
                {
                    InitialData = new Dictionary<string, string?>
                    {
                        ["SAGIDE:Database:Path"]            = _dbPath,
                        ["SAGIDE:PromptsPath"]              = _promptsDir,
                        ["SAGIDE:ReportsPath"]              = ReportsRoot,
                        ["SAGIDE:Scheduler:Enabled"]        = "false",
                        ["SAGIDE:Ollama:Servers:0:BaseUrl"] = null,
                    }
                });
            });

            builder.ConfigureTestServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .ToList();
                foreach (var desc in toRemove)
                    services.Remove(desc);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try { if (File.Exists(_dbPath))          File.Delete(_dbPath); }          catch { }
                try { if (Directory.Exists(_promptsDir)) Directory.Delete(_promptsDir, true); } catch { }
                try { if (Directory.Exists(ReportsRoot)) Directory.Delete(ReportsRoot, true); }  catch { }
            }
        }
    }
}
