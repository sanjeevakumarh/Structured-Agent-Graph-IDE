using SAGIDE.Contracts;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Memory;
using SAGIDE.Service.Providers;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Text.Json;

namespace SAGIDE.Service.Api;

internal static class PreflightEndpoints
{
    internal static IEndpointRouteBuilder MapPreflightEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/preflight — smoke-test all servers (2nd model), all SearXNG, and RAG
        app.MapGet("/api/preflight", async (
            IConfiguration config,
            ProviderFactory providerFactory,
            EmbeddingService embeddingService,
            RagPipeline ragPipeline,
            WebSearchAdapter searchAdapter,
            ILogger<PreflightChecker> logger,
            CancellationToken ct) =>
        {
            var checker = new PreflightChecker(config, providerFactory, embeddingService, ragPipeline, searchAdapter, logger);
            var result = await checker.RunAllAsync(ct);
            return Results.Ok(result);
        });

        // GET /api/preflight/{domain}/{name} — preflight for a specific prompt's dependencies
        app.MapGet("/api/preflight/{domain}/{name}", async (
            string domain, string name,
            IPromptRegistry promptRegistry,
            ISkillRegistry skillRegistry,
            IConfiguration config,
            ProviderFactory providerFactory,
            EmbeddingService embeddingService,
            RagPipeline ragPipeline,
            WebSearchAdapter searchAdapter,
            ILogger<PreflightChecker> logger,
            CancellationToken ct) =>
        {
            var prompt = promptRegistry.GetByKey(domain, name);
            if (prompt is null)
                return Results.NotFound(new { error = $"Prompt '{domain}/{name}' not found" });

            var checker = new PreflightChecker(config, providerFactory, embeddingService, ragPipeline, searchAdapter, logger);
            var result = await checker.RunForPromptAsync(prompt, skillRegistry, ct);
            return Results.Ok(result);
        });

        return app;
    }
}

public sealed class PreflightChecker(
    IConfiguration config,
    ProviderFactory providerFactory,
    EmbeddingService embeddingService,
#pragma warning disable CS9113 // Parameters injected for future use / API forward-compatibility
    RagPipeline ragPipeline,
    WebSearchAdapter searchAdapter,
#pragma warning restore CS9113
    ILogger<PreflightChecker> logger)
{
    private const string SmokePrompt = "Say hello in one word.";
    private const int SmokeMaxTokens = 20;
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(30);

    // ── Full system preflight ────────────────────────────────────────────────

    public async Task<PreflightResult> RunAllAsync(CancellationToken ct)
    {
        var result = new PreflightResult();
        var tasks = new List<Task>();

        // Test all Ollama servers (2nd model = general purpose)
        foreach (var server in config.GetSection("SAGIDE:Ollama:Servers").GetChildren())
        {
            var name = server["Name"] ?? "unknown";
            var baseUrl = server["BaseUrl"] ?? "";
            var models = server.GetSection("Models").GetChildren().Select(m => m.Value ?? "").Where(m => m.Length > 0).ToList();
            var testModel = models.Count >= 2 ? models[1] : models.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(testModel) || string.IsNullOrEmpty(baseUrl)) continue;

            tasks.Add(TestOllamaModelAsync(name, baseUrl, testModel, result, ct));
        }

        // Test all OpenAI-compatible servers (2nd model, or 1st if only one)
        foreach (var server in config.GetSection("SAGIDE:OpenAICompatible:Servers").GetChildren())
        {
            var name = server["Name"] ?? "unknown";
            var baseUrl = server["BaseUrl"] ?? "";
            var models = server.GetSection("Models").GetChildren().Select(m => m.Value ?? "").Where(m => m.Length > 0).ToList();
            var testModel = models.Count >= 2 ? models[1] : models.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(testModel) || string.IsNullOrEmpty(baseUrl)) continue;

            tasks.Add(TestOpenAiModelAsync(name, baseUrl, testModel, result, ct));
        }

        // Test all SearXNG instances
        tasks.Add(TestAllSearchEnginesAsync(result, ct));

        // Test RAG embed + query
        tasks.Add(TestRagAsync(result, ct));

        await Task.WhenAll(tasks);
        result.AllPassed = result.Checks.All(c => c.Passed);
        return result;
    }

    // ── Prompt-specific preflight ────────────────────────────────────────────

    public async Task<PreflightResult> RunForPromptAsync(
        PromptDefinition prompt, ISkillRegistry skillRegistry, CancellationToken ct)
    {
        var result = new PreflightResult { Prompt = $"{prompt.Domain}/{prompt.Name}" };
        var tasks = new List<Task>();

        // Collect all model specs referenced by this prompt
        var modelSpecs = ExtractModelSpecs(prompt, skillRegistry);

        // Deduplicate by (server, model)
        var tested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in modelSpecs)
        {
            var parsed = ParseModelSpec(spec);
            if (parsed is null) continue;
            var key = $"{parsed.Value.ServerName}/{parsed.Value.ModelId}";
            if (!tested.Add(key)) continue;

            if (parsed.Value.Provider == ModelProvider.Ollama)
                tasks.Add(TestOllamaModelAsync(parsed.Value.ServerName, parsed.Value.BaseUrl, parsed.Value.ModelId, result, ct));
            else if (parsed.Value.Provider == ModelProvider.Codex)
                tasks.Add(TestOpenAiModelAsync(parsed.Value.ServerName, parsed.Value.BaseUrl, parsed.Value.ModelId, result, ct));
        }

        // Check if prompt needs web search
        var needsSearch = prompt.DataCollection?.Steps?.Any(s =>
            s.Type is "web_search_batch" or "web_fetch" or "web_api" or "web_api_batch") == true;
        // Also check skills used in data collection
        if (prompt.DataCollection?.Steps?.Any(s => s.Type == "skill") == true)
        {
            foreach (var step in prompt.DataCollection.Steps.Where(s => s.Type == "skill" && !string.IsNullOrEmpty(s.Skill)))
            {
                var parts = step.Skill!.Split('/');
                if (parts.Length == 2)
                {
                    var skill = skillRegistry.GetByKey(parts[0], parts[1]);
                    if (skill?.Implementation?.Any(i =>
                        i.Type is "web_search_batch" or "web_fetch" or "web_api" or "web_api_batch") == true)
                        needsSearch = true;
                }
            }
        }
        if (needsSearch)
            tasks.Add(TestAllSearchEnginesAsync(result, ct));

        // Check if prompt needs vector search (RAG)
        var needsRag = prompt.DataCollection?.Steps?.Any(s => s.Type == "vector_search") == true;
        if (needsRag)
            tasks.Add(TestRagAsync(result, ct));

        await Task.WhenAll(tasks);
        result.AllPassed = result.Checks.All(c => c.Passed);
        return result;
    }

    // ── Test methods ─────────────────────────────────────────────────────────

    private async Task TestOllamaModelAsync(
        string serverName, string baseUrl, string modelId, PreflightResult result, CancellationToken ct)
    {
        var check = new PreflightCheck
        {
            Category = "model",
            Target = $"{serverName}/{modelId}",
            Provider = "ollama"
        };
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SmokeTimeout);
            var provider = providerFactory.GetProvider(ModelProvider.Ollama);
            if (provider is null) { check.Error = "Ollama provider not registered"; result.AddCheck(check); return; }

            var model = new ModelConfig(ModelProvider.Ollama, modelId, Endpoint: baseUrl);
            var response = await provider.CompleteAsync(SmokePrompt, model, cts.Token);
            sw.Stop();
            check.Passed = !string.IsNullOrWhiteSpace(response);
            check.LatencyMs = sw.ElapsedMilliseconds;
            check.ResponsePreview = Truncate(response, 100);
            if (!check.Passed) check.Error = "Empty response";
        }
        catch (Exception ex)
        {
            sw.Stop();
            check.LatencyMs = sw.ElapsedMilliseconds;
            check.Error = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning(ex, "Preflight model check failed for {Target}", check.Target);
        }
        result.AddCheck(check);
    }

    private async Task TestOpenAiModelAsync(
        string serverName, string baseUrl, string modelId, PreflightResult result, CancellationToken ct)
    {
        var check = new PreflightCheck
        {
            Category = "model",
            Target = $"{serverName}/{modelId}",
            Provider = "openai-compatible"
        };
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SmokeTimeout);
            var provider = providerFactory.GetProvider(ModelProvider.Codex);
            if (provider is null) { check.Error = "Codex provider not registered"; result.AddCheck(check); return; }

            var model = new ModelConfig(ModelProvider.Codex, modelId, Endpoint: baseUrl);
            var response = await provider.CompleteAsync(SmokePrompt, model, cts.Token);
            sw.Stop();
            check.Passed = !string.IsNullOrWhiteSpace(response);
            check.LatencyMs = sw.ElapsedMilliseconds;
            check.ResponsePreview = Truncate(response, 100);
            if (!check.Passed) check.Error = "Empty response";
        }
        catch (Exception ex)
        {
            sw.Stop();
            check.LatencyMs = sw.ElapsedMilliseconds;
            check.Error = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning(ex, "Preflight model check failed for {Target}", check.Target);
        }
        result.AddCheck(check);
    }

    private async Task TestAllSearchEnginesAsync(PreflightResult result, CancellationToken ct)
    {
        // Collect all search URLs from both server sections
        var searchUrls = new List<(string Name, string Url)>();
        foreach (var section in new[] { "SAGIDE:Ollama:Servers", "SAGIDE:OpenAICompatible:Servers" })
        foreach (var server in config.GetSection(section).GetChildren())
        {
            var name = server["Name"] ?? "unknown";
            var searchUrl = server["SearchUrl"]?.TrimEnd('/');
            if (!string.IsNullOrEmpty(searchUrl))
                searchUrls.Add((name, searchUrl));
        }

        var tasks = searchUrls.Select(s => TestSearxngAsync(s.Name, s.Url, result, ct));
        await Task.WhenAll(tasks);
    }

    private async Task TestSearxngAsync(
        string serverName, string searchUrl, PreflightResult result, CancellationToken ct)
    {
        var check = new PreflightCheck
        {
            Category = "search",
            Target = $"{serverName} ({searchUrl})",
            Provider = "searxng"
        };
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"{searchUrl}/search?q=hello+world&format=json&pageno=1&categories=general";
            var response = await http.GetAsync(url, cts.Token);
            sw.Stop();
            check.LatencyMs = sw.ElapsedMilliseconds;
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                // Verify we got actual search results
                check.Passed = body.Contains("\"results\"");
                check.ResponsePreview = $"HTTP {(int)response.StatusCode}, {body.Length} bytes";
                if (!check.Passed) check.Error = $"HTTP {(int)response.StatusCode} but no results in response";
            }
            else
            {
                check.Error = $"HTTP {(int)response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            check.LatencyMs = sw.ElapsedMilliseconds;
            check.Error = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning(ex, "Preflight search check failed for {Target}", check.Target);
        }
        result.AddCheck(check);
    }

    private async Task TestRagAsync(PreflightResult result, CancellationToken ct)
    {
        var check = new PreflightCheck
        {
            Category = "rag",
            Target = "embed+query",
            Provider = embeddingService.IsConfigured ? "configured" : "not configured"
        };
        var sw = Stopwatch.StartNew();
        try
        {
            if (!embeddingService.IsConfigured)
            {
                check.Error = "No embedding model configured";
                result.AddCheck(check);
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            // Test: embed a simple string and check we get a non-empty vector
            var vector = await embeddingService.EmbedAsync("hello world preflight test", cts.Token);
            sw.Stop();
            check.LatencyMs = sw.ElapsedMilliseconds;
            check.Passed = vector.Length > 0;
            check.ResponsePreview = check.Passed
                ? $"Embedding OK: {vector.Length} dimensions"
                : "Empty embedding vector returned";
            if (!check.Passed) check.Error = "Embedding returned empty vector";
        }
        catch (Exception ex)
        {
            sw.Stop();
            check.LatencyMs = sw.ElapsedMilliseconds;
            check.Error = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning(ex, "Preflight RAG check failed for {Target}", check.Target);
        }
        result.AddCheck(check);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private HashSet<string> ExtractModelSpecs(PromptDefinition prompt, ISkillRegistry skillRegistry)
    {
        var specs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ModelPreference fields
        AddIfNotEmpty(specs, prompt.ModelPreference?.Primary);
        AddIfNotEmpty(specs, prompt.ModelPreference?.Orchestrator);
        AddIfNotEmpty(specs, prompt.ModelPreference?.Fallback);
        if (prompt.ModelPreference?.Subtasks is not null)
            foreach (var kv in prompt.ModelPreference.Subtasks)
                AddIfNotEmpty(specs, kv.Value);

        // Subtask models
        foreach (var sub in prompt.Subtasks)
            AddIfNotEmpty(specs, sub.Model);

        // Data collection step models
        if (prompt.DataCollection?.Steps is not null)
            foreach (var step in prompt.DataCollection.Steps)
            {
                AddIfNotEmpty(specs, step.Model);
                // If step references a skill, extract its capability models
                if (step.Type == "skill" && !string.IsNullOrEmpty(step.Skill))
                {
                    var parts = step.Skill.Split('/');
                    if (parts.Length == 2)
                    {
                        var skill = skillRegistry.GetByKey(parts[0], parts[1]);
                        if (skill?.Implementation is not null)
                            foreach (var impl in skill.Implementation)
                                AddIfNotEmpty(specs, impl.Model);
                        if (skill?.CapabilityRequirements is not null)
                            foreach (var cap in skill.CapabilityRequirements.Keys)
                            {
                                var resolved = config[$"SAGIDE:Routing:Capabilities:{cap}"];
                                AddIfNotEmpty(specs, resolved);
                            }
                    }
                }
            }

        // Object/workflow skill models
        foreach (var obj in prompt.Objects)
        {
            if (string.IsNullOrEmpty(obj.Skill)) continue;
            var objParts = obj.Skill.Split('/');
            if (objParts.Length == 2)
            {
                var skill = skillRegistry.GetByKey(objParts[0], objParts[1]);
                if (skill?.CapabilityRequirements is not null)
                    foreach (var cap in skill.CapabilityRequirements.Keys)
                    {
                        var resolved = config[$"SAGIDE:Routing:Capabilities:{cap}"];
                        AddIfNotEmpty(specs, resolved);
                    }
            }
        }

        // Resolve template references like {{model_preference.subtasks.analyst}}
        var resolved2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            if (spec.StartsWith("{{") && spec.Contains("model_preference"))
            {
                // Try to resolve from the prompt's ModelPreference
                var inner = spec.Trim('{', '}', ' ');
                if (inner.StartsWith("model_preference.subtasks."))
                {
                    var key = inner["model_preference.subtasks.".Length..];
                    if (prompt.ModelPreference?.Subtasks?.TryGetValue(key, out var val) == true)
                        AddIfNotEmpty(resolved2, val);
                }
                else if (inner == "model_preference.orchestrator")
                    AddIfNotEmpty(resolved2, prompt.ModelPreference?.Orchestrator);
                else if (inner == "model_preference.primary")
                    AddIfNotEmpty(resolved2, prompt.ModelPreference?.Primary);
                else if (inner == "model_preference.fallback")
                    AddIfNotEmpty(resolved2, prompt.ModelPreference?.Fallback);
            }
            else if (spec.StartsWith("{{") && spec.Contains("capability."))
            {
                var inner = spec.Trim('{', '}', ' ');
                var capName = inner["capability.".Length..];
                var capResolved = config[$"SAGIDE:Routing:Capabilities:{capName}"];
                AddIfNotEmpty(resolved2, capResolved);
            }
            else
            {
                resolved2.Add(spec);
            }
        }
        return resolved2;
    }

    private static void AddIfNotEmpty(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) set.Add(value);
    }

    private record struct ParsedModelSpec(ModelProvider Provider, string ModelId, string ServerName, string BaseUrl);

    private ParsedModelSpec? ParseModelSpec(string spec)
    {
        ModelProvider provider;
        if (spec.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
        { provider = ModelProvider.Ollama; spec = spec[7..]; }
        else if (spec.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        { provider = ModelProvider.Codex; spec = spec[7..]; }
        else if (spec.StartsWith("codex/", StringComparison.OrdinalIgnoreCase))
        { provider = ModelProvider.Codex; spec = spec[6..]; }
        else if (spec.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return null; // Skip cloud provider tests
        else if (spec.StartsWith("gemini/", StringComparison.OrdinalIgnoreCase))
            return null; // Skip cloud provider tests
        else
            provider = ModelProvider.Ollama;

        string serverName = "default";
        string baseUrl = "";
        var atIdx = spec.LastIndexOf('@');
        if (atIdx > 0)
        {
            serverName = spec[(atIdx + 1)..].Trim();
            spec = spec[..atIdx].Trim();
            baseUrl = ResolveServerUrl(serverName) ?? "";
        }

        if (string.IsNullOrEmpty(baseUrl)) return null;
        return new ParsedModelSpec(provider, spec, serverName, baseUrl);
    }

    private string? ResolveServerUrl(string machineName)
    {
        foreach (var server in config.GetSection("SAGIDE:Ollama:Servers").GetChildren())
            if (string.Equals(server["Name"], machineName, StringComparison.OrdinalIgnoreCase))
                return server["BaseUrl"];
        foreach (var server in config.GetSection("SAGIDE:OpenAICompatible:Servers").GetChildren())
            if (string.Equals(server["Name"], machineName, StringComparison.OrdinalIgnoreCase))
                return server["BaseUrl"];
        return null;
    }

    private static string Truncate(string? s, int maxLen)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= maxLen ? s : s[..maxLen] + "...";
}

// ── Result DTOs ──────────────────────────────────────────────────────────

public sealed class PreflightResult
{
    public string? Prompt { get; set; }
    public bool AllPassed { get; set; }
    public List<PreflightCheck> Checks { get; } = [];
    public string Summary => AllPassed
        ? $"All {Checks.Count} checks passed"
        : $"{Checks.Count(c => c.Passed)}/{Checks.Count} passed, {Checks.Count(c => !c.Passed)} failed";

    private readonly object _lock = new();
    internal void AddCheck(PreflightCheck check) { lock (_lock) Checks.Add(check); }
}

public sealed class PreflightCheck
{
    public string Category { get; set; } = "";    // model, search, rag
    public string Target { get; set; } = "";       // e.g. "workstation/qwen3:30b-a3b"
    public string Provider { get; set; } = "";     // ollama, openai-compatible, searxng
    public bool Passed { get; set; }
    public long LatencyMs { get; set; }
    public string? ResponsePreview { get; set; }
    public string? Error { get; set; }
}
