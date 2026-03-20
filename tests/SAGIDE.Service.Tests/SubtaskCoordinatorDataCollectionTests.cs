using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Memory;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for <see cref="SubtaskCoordinator"/> data-collection steps.
///
/// Strategy: construct a <see cref="PromptDefinition"/> with zero subtasks and a
/// synthesis template that echoes the collected variable.  This lets us call
/// <c>RunAsync</c> (which returns the synthesis output) without needing a real
/// <see cref="AgentOrchestrator"/> — when <c>Subtasks.Count == 0</c> the
/// orchestrator is never touched.
/// </summary>
public class SubtaskCoordinatorDataCollectionTests
{
    // ── Fake HTTP infrastructure ───────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue = new();

        public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (_queue.TryDequeue(out var resp))
                return Task.FromResult(resp);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }

    private static HttpResponseMessage Ok(string body, string ct = "text/plain")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, ct) };

    private static HttpResponseMessage Redirect(string location)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(location, UriKind.Absolute);
        return r;
    }

    private static HttpResponseMessage ServerError()
        => new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"model not found\"}")
        };

    // ── SubtaskCoordinator factory ─────────────────────────────────────────────

    private static SubtaskCoordinator MakeCoordinator(FakeHandler handler)
    {
        var http         = new HttpClient(handler);
        var fetcher      = new WebFetcher(http, NullLogger<WebFetcher>.Instance,
                               rateLimitDelay: TimeSpan.Zero,
                               cacheTtl: TimeSpan.FromHours(1));
        var config       = new ConfigurationBuilder().Build();
        var searchAdapter = new WebSearchAdapter(http, config,
                               NullLogger<WebSearchAdapter>.Instance);

        // Pass null! for orchestrator — safe because all test prompts have 0 subtasks
        // so DispatchSubtasksAsync returns [] immediately and orchestrator is never called.
        return new SubtaskCoordinator(
            null!,
            fetcher,
            searchAdapter,
            config,
            NullLogger<SubtaskCoordinator>.Instance);
    }

    /// <summary>
    /// Builds a minimal prompt with one data-collection step and a synthesis template
    /// that echoes the step's output variable so it appears in RunAsync's return value.
    /// </summary>
    private static PromptDefinition Prompt(PromptDataCollectionStep step, string outputVar)
        => new()
        {
            Name   = "test",
            Domain = "test",
            DataCollection = new PromptDataCollection { Steps = [step] },
            Synthesis = new PromptSynthesis
            {
                // Scriban syntax — echoes the collected variable
                PromptTemplate = $"{{{{ {outputVar} }}}}"
            },
        };

    private const string SampleRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Test Feed</title>
            <item>
              <title>AI Paper One</title>
              <link>https://arxiv.org/abs/2401.0001</link>
              <description>Great abstract.</description>
            </item>
            <item>
              <title>AI Paper Two</title>
              <link>https://arxiv.org/abs/2401.0002</link>
              <description>Another abstract.</description>
            </item>
          </channel>
        </rss>
        """;

    // ── web_api step ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WebApiStep_FetchesBodyAndExposesInSynthesis()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok("fetched page content"));

        var step = new PromptDataCollectionStep
        {
            Name = "fetch_page", Type = "web_api",
            Source = "https://example.com/page", OutputVar = "page_content"
        };

        var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "page_content"));

        Assert.Contains("fetched page content", result.SynthesizedOutput);
    }

    [Fact]
    public async Task WebApiStep_302Redirect_FollowedSuccessfully()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Redirect("http://export.example.com/data"));
        handler.Enqueue(Ok("redirected content"));

        var step = new PromptDataCollectionStep
        {
            Name = "fetch_redirect", Type = "web_api",
            Source = "https://original.example.com/data", OutputVar = "data"
        };

        var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "data"));

        Assert.Contains("redirected content", result.SynthesizedOutput);
    }

    [Fact]
    public async Task WebApiStep_HttpError_ContinuesWithEmpty_NoCrash()
    {
        // This matches the "[WRN] Data collection step 'X' failed, continuing with empty result"
        // log line from the original bug report.
        var handler = new FakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("down")
        });

        var step = new PromptDataCollectionStep
        {
            Name = "fetch_down", Type = "web_api",
            Source = "https://down.example.com/", OutputVar = "result"
        };

        // Should not throw — step failure is swallowed, var is set to empty string
        var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "result"));

        Assert.NotNull(result); // pipeline completed
    }

    // ── rss step ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RssStep_ParsesFeed_FormattedEntriesInSynthesis()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok(SampleRss, "application/rss+xml"));

        var step = new PromptDataCollectionStep
        {
            Name = "fetch_arxiv", Type = "rss",
            Source = "https://arxiv.org/rss/cs.RO", OutputVar = "papers"
        };

        var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "papers"));

        Assert.Contains("AI Paper One",    result.SynthesizedOutput);
        Assert.Contains("AI Paper Two",    result.SynthesizedOutput);
        Assert.Contains("Great abstract.", result.SynthesizedOutput);
        Assert.Contains("arxiv.org",       result.SynthesizedOutput);
    }

    [Fact]
    public async Task AtomStep_TypeAlias_SameAsRss()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok(SampleRss, "application/atom+xml"));

        var step = new PromptDataCollectionStep
        {
            Name = "fetch_atom", Type = "atom",
            Source = "https://example.com/atom.xml", OutputVar = "entries"
        };

        var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "entries"));

        Assert.Contains("AI Paper One", result.SynthesizedOutput);
    }

    [Fact]
    public async Task RssStep_302Redirect_FollowedSuccessfully()
    {
        // arXiv redirects https://arxiv.org/rss/cs.RO → http://export.arxiv.org/rss/cs.RO
        var handler = new FakeHandler();
        handler.Enqueue(Redirect("http://export.example.com/feed.rss"));
        handler.Enqueue(Ok(SampleRss, "application/rss+xml"));

        var step = new PromptDataCollectionStep
        {
            Name = "fetch_arxiv", Type = "rss",
            Source = "https://original.example.com/feed.rss", OutputVar = "papers"
        };

        var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "papers"));

        Assert.Contains("AI Paper One", result.SynthesizedOutput);
    }

    [Fact]
    public async Task RssStep_MalformedXml_ContinuesWithEmpty_NoCrash()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok("this is not xml", "application/rss+xml"));

        var step = new PromptDataCollectionStep
        {
            Name = "fetch_bad", Type = "rss",
            Source = "https://example.com/bad.rss", OutputVar = "papers"
        };

        var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "papers"));

        Assert.NotNull(result);
    }

    // ── read_file step ────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadFileStep_ExistingFile_ContentInSynthesis()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "file content here");
        try
        {
            var handler = new FakeHandler();
            var step = new PromptDataCollectionStep
            {
                Name = "load_file", Type = "read_file",
                Source = path, OutputVar = "file_data"
            };

            var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "file_data"));

            Assert.Contains("file content here", result.SynthesizedOutput);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadFileStep_MissingFile_EmptyNoCrash()
    {
        var handler = new FakeHandler();
        var step = new PromptDataCollectionStep
        {
            Name = "load_missing", Type = "read_file",
            Source = "/no/such/file.md", OutputVar = "data"
        };

        // Should complete without exception; missing file → empty string
        var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "data"));

        Assert.NotNull(result);
    }

    // ── unknown step type ─────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownStepType_LogsWarning_NoCrash()
    {
        var handler = new FakeHandler();
        var step = new PromptDataCollectionStep
        {
            Name = "mystery", Type = "totally_unknown_type",
            Source = "https://example.com/", OutputVar = "out"
        };

        // Unknown type hits the default case → logs WRN, returns empty string
        var result = await MakeCoordinator(handler).RunAsync(Prompt(step, "out"));

        Assert.NotNull(result);
    }

    // ── multiple steps ────────────────────────────────────────────────────────

    [Fact]
    public async Task MultipleSteps_AllVarsAvailableInSynthesis()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "project context");
        try
        {
            var handler = new FakeHandler();
            handler.Enqueue(Ok(SampleRss, "application/rss+xml"));

            var steps = new[]
            {
                new PromptDataCollectionStep
                {
                    Name = "fetch_papers", Type = "rss",
                    Source = "https://arxiv.example.com/rss", OutputVar = "papers"
                },
                new PromptDataCollectionStep
                {
                    Name = "load_ctx", Type = "read_file",
                    Source = path, OutputVar = "context"
                },
            };

            var prompt = new PromptDefinition
            {
                Name   = "multi",
                Domain = "test",
                DataCollection = new PromptDataCollection { Steps = [.. steps] },
                Synthesis = new PromptSynthesis
                {
                    PromptTemplate = "papers={{ papers }} ctx={{ context }}"
                },
            };

            var result = await MakeCoordinator(handler).RunAsync(prompt);

            Assert.Contains("AI Paper One",    result.SynthesizedOutput);
            Assert.Contains("project context", result.SynthesizedOutput);
        }
        finally { File.Delete(path); }
    }

    // ── type: llm step ────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal fake that returns a deterministic task ID and immediately marks every
    /// task Completed with a configurable Output string.
    /// </summary>
    private sealed class InstantTaskSubmitter(string output) : ITaskSubmissionService
    {
        private int _counter;
        private readonly Dictionary<string, string> _results = new();

        public Task<string> SubmitTaskAsync(AgentTask task, CancellationToken ct)
        {
            var id = $"llm-{Interlocked.Increment(ref _counter):D3}";
            _results[id] = output;
            return Task.FromResult(id);
        }

        public Task CancelTaskAsync(string taskId, CancellationToken ct) => Task.CompletedTask;

        public TaskStatusResponse? GetTaskStatus(string taskId)
        {
            if (!_results.TryGetValue(taskId, out var text)) return null;
            return new TaskStatusResponse
            {
                TaskId  = taskId,
                Status  = AgentTaskStatus.Completed,
                Result  = new AgentResult { TaskId = taskId, Success = true, Output = text },
            };
        }
    }

    private static SubtaskCoordinator MakeCoordinatorWithSubmitter(ITaskSubmissionService submitter)
    {
        var http    = new HttpClient(new FakeHandler());
        var fetcher = new WebFetcher(http, NullLogger<WebFetcher>.Instance,
                          rateLimitDelay: TimeSpan.Zero, cacheTtl: TimeSpan.FromHours(1));
        var config  = new ConfigurationBuilder().Build();
        var search  = new WebSearchAdapter(http, config, NullLogger<WebSearchAdapter>.Instance);

        return new SubtaskCoordinator(submitter, fetcher, search, config,
                   NullLogger<SubtaskCoordinator>.Instance);
    }

    [Fact]
    public async Task LlmStep_NoPromptTemplate_ReturnsEmpty()
    {
        var step = new PromptDataCollectionStep
        {
            Name = "summarise", Type = "llm",
            PromptTemplate = null, OutputVar = "summary"
        };

        // null! submitter is safe — SubmitTaskAsync is never called when template is missing
        var result = await MakeCoordinator(new FakeHandler()).RunAsync(Prompt(step, "summary"));

        Assert.Equal(string.Empty, result.SynthesizedOutput.Trim());
    }

    [Fact]
    public async Task LlmStep_RendersTemplateAndReturnsTaskOutput()
    {
        const string LlmOutput = "entity inventory JSON here";
        var submitter = new InstantTaskSubmitter(LlmOutput);

        var step = new PromptDataCollectionStep
        {
            Name           = "entity_inventory",
            Type           = "llm",
            PromptTemplate = "Extract entities from: {{market_data}}",
            OutputVar      = "entities",
        };
        var prompt = new PromptDefinition
        {
            Name           = "test",
            Domain         = "research",
            Variables      = new() { ["market_data"] = "ACME Corp revenue $5M [Source: FT 2024]" },
            DataCollection = new PromptDataCollection { Steps = [step] },
            Synthesis      = new PromptSynthesis { PromptTemplate = "{{ entities }}" },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MakeCoordinatorWithSubmitter(submitter).RunAsync(prompt, ct: cts.Token);

        Assert.Contains(LlmOutput, result.SynthesizedOutput);
    }
}
