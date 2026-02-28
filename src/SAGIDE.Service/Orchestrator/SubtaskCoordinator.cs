using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Prompts;
using SAGIDE.Service.Providers;
using SAGIDE.Service.Rag;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Orchestrates multi-model prompt execution:
///   1. Runs data_collection steps (file reads, HTTP fetches) to gather context.
///   2. Dispatches subtasks in parallel to the models specified in the prompt YAML.
///   3. Waits for all subtask results, then renders the synthesis prompt.
///   4. Writes the synthesized output to the configured destination.
///
/// Supports "@machine" notation in model specs
/// (e.g. "ollama/deepseek-r1:14b@mini") — resolved to BaseUrl via SAGIDE:Ollama:Servers config.
///
/// Load balancing: OllamaHostHealthMonitor is used to route around unhealthy servers before
/// dispatch, and to retry failed subtasks on an alternative healthy server (one retry per subtask).
/// </summary>
public sealed class SubtaskCoordinator
{
    private readonly ITaskSubmissionService _taskSubmission;
    private readonly WebFetcher _fetcher;
    private readonly WebSearchAdapter _search;
    private readonly IConfiguration _config;
    private readonly ILogger<SubtaskCoordinator> _logger;
    private readonly OllamaHostHealthMonitor? _healthMonitor;
    private readonly IReadOnlyList<string> _allOllamaUrls;

    // Tracks a submitted subtask together with the actual endpoint used, for retry purposes.
    private sealed record SubtaskSubmission(string Name, string TaskId, string ModelId, string Endpoint);

    // Common English stop words excluded when building topic_slug.
    private static readonly HashSet<string> _slugStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "in", "on", "of", "for", "to", "and", "or",
        "with", "is", "are", "at", "by", "as", "from", "its", "it",
        "this", "that", "be", "was", "were", "about", "into", "over",
    };

    public SubtaskCoordinator(
        ITaskSubmissionService taskSubmission,
        WebFetcher fetcher,
        WebSearchAdapter search,
        IConfiguration config,
        ILogger<SubtaskCoordinator> logger,
        OllamaHostHealthMonitor? healthMonitor = null)
    {
        _taskSubmission = taskSubmission;
        _fetcher        = fetcher;
        _search        = search;
        _config        = config;
        _logger        = logger;
        _healthMonitor = healthMonitor;
        _allOllamaUrls = config.GetSection("SAGIDE:Ollama:Servers")
            .GetChildren()
            .Select(s => s["BaseUrl"]?.TrimEnd('/') ?? string.Empty)
            .Where(u => u.Length > 0)
            .ToList();
    }

    // ── Main entry point ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full subtask pipeline for the given prompt definition.
    /// </summary>
    public async Task<SubtaskRunResult> RunAsync(
        PromptDefinition prompt,
        Dictionary<string, string>? variableOverrides = null,
        CancellationToken ct = default)
    {
        var instanceId = Guid.NewGuid().ToString("N")[..12];
        _logger.LogInformation(
            "SubtaskCoordinator [{Id}] starting for {Domain}/{Name} ({SubtaskCount} subtasks)",
            instanceId, prompt.Domain, prompt.Name, prompt.Subtasks.Count);

        // 1. Build variable context (YAML vars + caller overrides + auto date/time)
        var vars = BuildVarContext(prompt, variableOverrides);

        // 2. Execute data_collection steps sequentially (each step can reference prior outputs)
        await ExecuteDataCollectionAsync(prompt, vars, ct);

        // 3. Dispatch all subtasks in parallel
        var subtaskResults = await DispatchSubtasksAsync(prompt, vars, instanceId, ct);

        // 4. Merge subtask results into vars so synthesis template can reference them
        foreach (var (name, output) in subtaskResults)
            vars[$"{name}_result"] = output;

        // 5. Run synthesis (or aggregate if no synthesis template)
        var synthesized = RenderSynthesisOrAggregate(prompt, vars, subtaskResults);

        // 6. Write to output destination if configured
        if (!string.IsNullOrEmpty(prompt.Output?.Destination))
            await WriteOutputAsync(prompt.Output.Destination, synthesized, vars);

        _logger.LogInformation("SubtaskCoordinator [{Id}] complete", instanceId);
        return new SubtaskRunResult(instanceId, synthesized, subtaskResults);
    }

    // ── Variable context ────────────────────────────────────────────────────────

    private static Dictionary<string, object> BuildVarContext(
        PromptDefinition prompt,
        Dictionary<string, string>? overrides)
    {
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"]     = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["datetime"] = DateTime.UtcNow.ToString("O"),
        };

        foreach (var kv in prompt.Variables)
            vars[kv.Key] = kv.Value;

        if (overrides is not null)
            foreach (var kv in overrides)
                vars[kv.Key] = kv.Value;

        // Derive topic_slug from the final topic value so prompts can use it in output paths.
        // e.g. "Microsoft stock outlook" → "Microsoft_stock_outlook"
        if (vars.TryGetValue("topic", out var topicVal))
            vars["topic_slug"] = BuildTopicSlug(topicVal?.ToString() ?? string.Empty);

        // Derive ticker_upper so finance prompts can use {{ticker_upper}} for display.
        // e.g. "ko" → "KO"
        if (vars.TryGetValue("ticker", out var tickerVal))
            vars["ticker_upper"] = tickerVal?.ToString()?.ToUpperInvariant() ?? string.Empty;

        // Make model_preference available so subtask model fields like
        // "{{model_preference.subtasks.fundamental}}" resolve correctly.
        if (prompt.ModelPreference is not null)
        {
            var mp = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (prompt.ModelPreference.Orchestrator is not null)
                mp["orchestrator"] = prompt.ModelPreference.Orchestrator;
            if (prompt.ModelPreference.Subtasks.Count > 0)
                mp["subtasks"] = prompt.ModelPreference.Subtasks
                    .ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.OrdinalIgnoreCase);
            vars["model_preference"] = mp;
        }

        return vars;
    }

    // ── Data collection steps ───────────────────────────────────────────────────

    private async Task ExecuteDataCollectionAsync(
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        CancellationToken ct)
    {
        if (prompt.DataCollection is null) return;

        foreach (var step in prompt.DataCollection.Steps)
        {
            _logger.LogDebug("Data collection: {Name} ({Type})", step.Name, step.Type);
            try
            {
                var result = await ExecuteStepAsync(step, vars, prompt, ct);
                if (!string.IsNullOrEmpty(step.OutputVar))
                    vars[step.OutputVar] = result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Data collection step '{Name}' failed, continuing with empty result", step.Name);
                if (!string.IsNullOrEmpty(step.OutputVar))
                    vars[step.OutputVar] = string.Empty;
            }
        }
    }

    private async Task<object> ExecuteStepAsync(
        PromptDataCollectionStep step,
        Dictionary<string, object> vars,
        PromptDefinition prompt,
        CancellationToken ct)
    {
        switch (step.Type.Trim().ToLowerInvariant())
        {
            case "read_file":
            {
                var path = ExpandPath(ResolveSimpleTemplate(step.Source ?? string.Empty, vars));
                return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : string.Empty;
            }

            case "web_api":
            {
                var url = ResolveSimpleTemplate(step.Source ?? string.Empty, vars);
                if (string.IsNullOrWhiteSpace(url)) return string.Empty;
                var doc = await _fetcher.FetchUrlAsync(url, ct);
                return doc.Body;
            }

            case "rss":
            case "atom":
            {
                var feedUrl = ResolveSimpleTemplate(step.Source ?? string.Empty, vars);
                if (string.IsNullOrWhiteSpace(feedUrl)) return string.Empty;
                var entries = await _fetcher.FetchRssAsync(feedUrl, ct);
                // Format each entry as a compact block so the LLM receives structured text
                return string.Join("\n\n", entries.Select(e =>
                    $"### {e.Title}\nURL: {e.Url}\n{e.Body}".Trim()));
            }

            case "web_api_batch":
            {
                var urlTemplate = ResolveSimpleTemplate(step.Source ?? string.Empty, vars);
                var items       = ResolveCollection(step.IterateOver ?? string.Empty, vars);
                var bodies      = new List<string>();

                foreach (var item in items.Take(_config.GetValue("SAGIDE:Orchestration:WebApiBatchMaxItems", 50))) // safety cap — configurable
                {
                    var url = urlTemplate
                        .Replace("{symbol}", item)
                        .Replace("{item}",   item);
                    try
                    {
                        var doc = await _fetcher.FetchUrlAsync(url, ct);
                        bodies.Add(doc.Body);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Batch fetch failed for item '{Item}'", item);
                    }
                }

                return string.Join("\n---\n", bodies);
            }

            case "filter":
            {
                // Resolve the input data
                var inputStr  = ResolveSimpleTemplate(step.Input ?? string.Empty, vars);

                // Resolve condition (may contain {{template}} expressions like {{drop_threshold}})
                var condition = ResolveSimpleTemplate(step.Condition ?? string.Empty, vars);

                // Resolve limit (template or plain integer); 0 / missing → no limit
                var limitStr  = ResolveSimpleTemplate(step.Limit ?? string.Empty, vars);
                var limit     = int.TryParse(limitStr, out var l) && l > 0 ? l : int.MaxValue;

                _logger.LogDebug("Filter step '{Name}': condition='{Condition}' limit={Limit}",
                    step.Name, condition, limit == int.MaxValue ? "none" : limitStr);

                return FilterConditionEvaluator.Filter(inputStr, condition, limit);
            }

            case "web_search_batch":
            {
                if (!_search.IsConfigured)
                {
                    _logger.LogWarning(
                        "web_search_batch step '{Name}': SAGIDE:Rag:SearchUrl not configured — skipped", step.Name);
                    return string.Empty;
                }

                var queryTemplate = ResolveSimpleTemplate(step.Query ?? step.Source ?? string.Empty, vars);
                var items         = string.IsNullOrWhiteSpace(step.IterateOver)
                    ? (IEnumerable<string>)[queryTemplate]           // single query
                    : ResolveCollection(step.IterateOver, vars);    // one query per item

                var limitStr   = ResolveSimpleTemplate(step.Limit ?? string.Empty, vars);
                var maxResults = int.TryParse(limitStr, out var l) && l > 0 ? l : 5;

                var sections = new List<string>();
                foreach (var item in items.Take(_config.GetValue("SAGIDE:Orchestration:WebSearchBatchMaxItems", 20))) // safety cap — configurable
                {
                    // Render the query with the current item substituted for {{symbol}}, {{item}}, etc.
                    var query = queryTemplate
                        .Replace("{{symbol}}", item)
                        .Replace("{{item}}",   item)
                        .Replace("{symbol}",   item)
                        .Replace("{item}",     item);

                    _logger.LogDebug("web_search_batch '{Name}': querying '{Query}'", step.Name, query);
                    var result = await _search.SearchAsync(query, maxResults, ct);
                    if (!string.IsNullOrWhiteSpace(result))
                        sections.Add($"## Search: {query}\n{result}");
                }

                return string.Join("\n\n---\n\n", sections);
            }

            case "llm_queries":
            {
                if (!_search.IsConfigured)
                {
                    _logger.LogWarning(
                        "llm_queries step '{Name}': search not configured — skipped", step.Name);
                    return string.Empty;
                }

                if (string.IsNullOrWhiteSpace(step.PlanningPrompt))
                {
                    _logger.LogWarning(
                        "llm_queries step '{Name}': no planning_prompt defined — skipped", step.Name);
                    return string.Empty;
                }

                // Render planning prompt with current vars
                var planningPromptText = ResolveSimpleTemplate(step.PlanningPrompt, vars);

                // Determine model: step.Model → orchestrator model → empty (orchestrator default)
                var modelSpecRaw = string.IsNullOrWhiteSpace(step.Model)
                    ? (prompt.ModelPreference?.Orchestrator ?? string.Empty)
                    : step.Model;
                var modelSpec = ResolveSimpleTemplate(modelSpecRaw, vars);
                var (planProvider, planModelId, planEndpoint) = ParseModelSpec(modelSpec);

                // Submit planning task to the LLM
                var planTask = new AgentTask
                {
                    AgentType     = AgentType.Generic,
                    ModelProvider = planProvider,
                    ModelId       = planModelId,
                    Description   = planningPromptText,
                    SourceTag     = prompt.SourceTag ?? $"{prompt.Domain}_planning",
                    Priority      = 1,
                    Metadata      = new Dictionary<string, string>
                    {
                        ["step_name"]     = step.Name,
                        ["step_type"]     = "llm_queries",
                        ["prompt_domain"] = prompt.Domain,
                        ["prompt_name"]   = prompt.Name,
                    },
                };
                if (!string.IsNullOrEmpty(planEndpoint))
                    planTask.Metadata["modelEndpoint"] = planEndpoint;

                var planTaskId = await _taskSubmission.SubmitTaskAsync(planTask, ct);
                _logger.LogInformation(
                    "llm_queries '{Name}': planning task {TaskId} submitted (model: {Model}@{Host})",
                    step.Name, planTaskId, planModelId, planEndpoint ?? "auto");

                var planResult = await WaitForTaskAsync(planTaskId, ct);
                var queries    = ParseJsonStringArray(planResult);

                if (queries.Count == 0)
                {
                    _logger.LogWarning(
                        "llm_queries '{Name}': LLM returned no parseable queries. Raw: {Raw}",
                        step.Name, planResult.Length > 300 ? planResult[..300] + "..." : planResult);
                    return string.Empty;
                }

                _logger.LogInformation(
                    "llm_queries '{Name}': {Count} queries generated — {List}",
                    step.Name, queries.Count, string.Join("; ", queries));

                // Execute each query via web search
                var limitStr   = ResolveSimpleTemplate(step.Limit ?? string.Empty, vars);
                var maxResults = int.TryParse(limitStr, out var lq) && lq > 0 ? lq : 5;
                var maxQueries = _config.GetValue("SAGIDE:Orchestration:LlmQueriesMaxQueries", 10);

                var sections = new List<string>();
                foreach (var query in queries.Take(maxQueries))
                {
                    _logger.LogDebug("llm_queries '{Name}': searching '{Query}'", step.Name, query);
                    var searchResult = await _search.SearchAsync(query, maxResults, ct);
                    if (!string.IsNullOrWhiteSpace(searchResult))
                        sections.Add($"## Search: {query}\n{searchResult}");
                }

                return string.Join("\n\n---\n\n", sections);
            }

            case "llm_per_section":
            {
                if (string.IsNullOrWhiteSpace(step.PlanningPrompt))
                {
                    _logger.LogWarning("llm_per_section step '{Name}': no planning_prompt — skipped", step.Name);
                    return string.Empty;
                }

                // ── Phase 1: ask LLM which sections to write ──────────────────
                var planText  = ResolveSimpleTemplate(step.PlanningPrompt, vars);
                var modelSpecRaw = string.IsNullOrWhiteSpace(step.Model)
                    ? (prompt.ModelPreference?.Orchestrator ?? string.Empty)
                    : step.Model;
                var modelSpec = ResolveSimpleTemplate(modelSpecRaw, vars);
                var (secProvider, secModelId, secEndpoint) = ParseModelSpec(modelSpec);

                var planTask = new AgentTask
                {
                    AgentType     = AgentType.Generic,
                    ModelProvider = secProvider,
                    ModelId       = secModelId,
                    Description   = planText,
                    SourceTag     = prompt.SourceTag ?? $"{prompt.Domain}_section_plan",
                    Priority      = 1,
                    Metadata      = new Dictionary<string, string>
                    {
                        ["step_name"] = step.Name, ["step_type"] = "llm_per_section_plan",
                        ["prompt_domain"] = prompt.Domain, ["prompt_name"] = prompt.Name,
                    },
                };
                if (!string.IsNullOrEmpty(secEndpoint)) planTask.Metadata["modelEndpoint"] = secEndpoint;

                var planTaskId = await _taskSubmission.SubmitTaskAsync(planTask, ct);
                var planResult = await WaitForTaskAsync(planTaskId, ct);
                var sectionNames = ParseJsonStringArray(planResult);

                var maxSectionsStr = ResolveSimpleTemplate(step.MaxSections ?? "5", vars);
                var maxSections = int.TryParse(maxSectionsStr, out var ms) && ms > 0 ? ms : 5;
                sectionNames = sectionNames.Take(maxSections).ToList();

                if (sectionNames.Count == 0)
                {
                    _logger.LogWarning("llm_per_section '{Name}': LLM returned no section names. Raw: {Raw}",
                        step.Name, planResult.Length > 300 ? planResult[..300] + "..." : planResult);
                    return string.Empty;
                }

                _logger.LogInformation("llm_per_section '{Name}': {Count} sections — {List}",
                    step.Name, sectionNames.Count, string.Join(" | ", sectionNames));

                if (string.IsNullOrWhiteSpace(step.SectionAnalysisPrompt))
                {
                    _logger.LogWarning("llm_per_section '{Name}': no section_analysis_prompt — returning headings only", step.Name);
                    return string.Join("\n\n", sectionNames.Select(s => $"## {s}"));
                }

                // ── Phase 2: dispatch all section analysis tasks in parallel ──
                var searchData = vars.TryGetValue(step.SearchResultsVar ?? "all_search_results", out var sr)
                    ? sr?.ToString() ?? string.Empty : string.Empty;

                var sectionTasks = sectionNames.Select(async sectionName =>
                {
                    var sectionVars = new Dictionary<string, object>(vars, StringComparer.OrdinalIgnoreCase)
                    {
                        ["section_name"]   = sectionName,
                        ["search_results"] = searchData,
                    };
                    var sectionPromptText = ResolveSimpleTemplate(step.SectionAnalysisPrompt, sectionVars);

                    var sTask = new AgentTask
                    {
                        AgentType     = AgentType.Generic,
                        ModelProvider = secProvider,
                        ModelId       = secModelId,
                        Description   = sectionPromptText,
                        SourceTag     = prompt.SourceTag ?? $"{prompt.Domain}_section_analysis",
                        Priority      = 1,
                        Metadata      = new Dictionary<string, string>
                        {
                            ["step_name"]    = step.Name, ["step_type"] = "llm_per_section_analysis",
                            ["section_name"] = sectionName,
                            ["prompt_domain"] = prompt.Domain, ["prompt_name"] = prompt.Name,
                        },
                    };
                    if (!string.IsNullOrEmpty(secEndpoint)) sTask.Metadata["modelEndpoint"] = secEndpoint;

                    var sTaskId = await _taskSubmission.SubmitTaskAsync(sTask, ct);
                    _logger.LogDebug("llm_per_section '{Name}': section '{Section}' → task {TaskId}",
                        step.Name, sectionName, sTaskId);
                    var result = await WaitForTaskAsync(sTaskId, ct);
                    return (sectionName, result);
                });

                var sectionResults = await Task.WhenAll(sectionTasks);

                // Reassemble in planning order; skip empty results
                return string.Join("\n\n", sectionNames
                    .Select(name => sectionResults.FirstOrDefault(r => r.sectionName == name))
                    .Where(r => !string.IsNullOrWhiteSpace(r.result))
                    .Select(r => $"## {r.sectionName}\n\n{r.result.Trim()}"));
            }

            default:
                _logger.LogWarning(
                    "Unknown data_collection step type '{Type}' in step '{Name}'", step.Type, step.Name);
                return string.Empty;
        }
    }

    /// <summary>
    /// Extracts and parses a JSON string array from LLM output.
    /// Handles output that wraps the array in prose or code fences.
    /// </summary>
    private static List<string> ParseJsonStringArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        // Strip markdown code fences if present
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var fence = text.IndexOf('\n');
            var closing = text.LastIndexOf("```");
            if (fence > 0 && closing > fence)
                text = text[(fence + 1)..closing].Trim();
        }

        // Find the first '[' ... last ']' in the text (handles prose around the array)
        var start = text.IndexOf('[');
        var end   = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        var json = text[start..(end + 1)];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    // ── Subtask dispatch ────────────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> DispatchSubtasksAsync(
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        string instanceId,
        CancellationToken ct)
    {
        if (prompt.Subtasks.Count == 0)
            return [];

        // Submit all subtasks concurrently, capturing the resolved (modelId, endpoint) for retry
        var submissionTasks = prompt.Subtasks
            .Select(st => SubmitSubtaskAsync(st, prompt, vars, instanceId, ct))
            .ToList();

        var submitted = (await Task.WhenAll(submissionTasks))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        // Wait for all submitted tasks to reach a terminal state
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        var waitTasks = submitted.Select(async s =>
        {
            var output = await WaitForTaskAsync(s.TaskId, ct);
            lock (results) results[s.Name] = output;
        });
        await Task.WhenAll(waitTasks);

        // One retry pass: for subtasks that returned empty due to a server-side error,
        // find a healthy alternative server and re-submit once.
        if (_healthMonitor is not null)
        {
            var retryTasks = submitted
                .Where(s => string.IsNullOrEmpty(results.GetValueOrDefault(s.Name)))
                .Select(async s =>
                {
                    var status = _taskSubmission.GetTaskStatus(s.TaskId);
                    if (status?.Status != SAGIDE.Core.Models.AgentTaskStatus.Failed) return;
                    if (!IsRetryableServerError(status.StatusMessage ?? string.Empty)) return;

                    // Find a healthy alternative, explicitly excluding the endpoint that failed
                    var alternatives = _allOllamaUrls
                        .Where(u => !string.Equals(u, s.Endpoint, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var fallback = _healthMonitor.TryGetBestHost(s.ModelId, string.Empty, alternatives);

                    if (fallback is null)
                    {
                        _logger.LogWarning(
                            "No healthy fallback for subtask '{Name}' (model: {Model}, failed: {Endpoint}): {Error}",
                            s.Name, s.ModelId, s.Endpoint, status.StatusMessage);
                        return;
                    }

                    _logger.LogInformation(
                        "Retrying subtask '{Name}' on {Fallback} — original {Endpoint} failed: {Error}",
                        s.Name, fallback, s.Endpoint, status.StatusMessage);

                    var subtask = prompt.Subtasks.First(st => st.Name == s.Name);
                    var retry = await SubmitSubtaskAsync(subtask, prompt, vars, instanceId, ct, endpointOverride: fallback);
                    if (retry is null) return;

                    var retryOutput = await WaitForTaskAsync(retry.TaskId, ct);
                    if (!string.IsNullOrEmpty(retryOutput))
                        lock (results) results[s.Name] = retryOutput;
                })
                .ToList();

            if (retryTasks.Count > 0)
                await Task.WhenAll(retryTasks);
        }

        return results;
    }

    private async Task<SubtaskSubmission?> SubmitSubtaskAsync(
        PromptSubtask subtask,
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        string instanceId,
        CancellationToken ct,
        string? endpointOverride = null)
    {
        try
        {
            // Resolve model spec — may be a Scriban template
            var modelSpec = ResolveSimpleTemplate(subtask.Model ?? string.Empty, vars);
            var (provider, modelId, parsedEndpoint) = ParseModelSpec(modelSpec);

            // Use override if provided (retry path); otherwise start with the parsed endpoint
            var endpoint = endpointOverride ?? parsedEndpoint;

            // Pre-dispatch health routing (only on initial dispatch, not on explicit retry)
            if (endpointOverride is null && _healthMonitor is not null
                && provider == ModelProvider.Ollama && !string.IsNullOrEmpty(endpoint))
            {
                var bestHost = _healthMonitor.TryGetBestHost(modelId, endpoint, _allOllamaUrls);
                if (bestHost is null)
                {
                    _logger.LogWarning(
                        "No healthy Ollama server for subtask '{Name}' (model: {Model}) — submitting to {Endpoint} anyway",
                        subtask.Name, modelId, endpoint);
                }
                else if (!string.Equals(bestHost, endpoint, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Health routing: subtask '{Name}' rerouted from {Old} to {New}",
                        subtask.Name, endpoint, bestHost);
                    endpoint = bestHost;
                }
            }

            // Build subtask variable context
            var subtaskVars = BuildSubtaskVars(subtask, prompt, vars);

            // Render the subtask prompt template
            var renderedPrompt = PromptTemplate.RenderSubtask(subtask, prompt, subtaskVars);
            if (string.IsNullOrWhiteSpace(renderedPrompt))
            {
                _logger.LogWarning("Subtask '{Name}' produced an empty prompt — skipping", subtask.Name);
                return null;
            }

            var task = new AgentTask
            {
                AgentType     = AgentType.Generic,
                ModelProvider = provider,
                ModelId       = modelId,
                Description   = renderedPrompt,
                SourceTag     = prompt.SourceTag ?? $"{prompt.Domain}_subtask",
                Priority      = 1,
                Metadata      = new Dictionary<string, string>
                {
                    ["subtask_name"]   = subtask.Name,
                    ["coordinator_id"] = instanceId,
                    ["prompt_domain"]  = prompt.Domain,
                    ["prompt_name"]    = prompt.Name,
                    ["triggered_by"]   = "subtask_coordinator",
                },
            };

            if (!string.IsNullOrEmpty(endpoint))
                task.Metadata["modelEndpoint"] = endpoint;

            var taskId = await _taskSubmission.SubmitTaskAsync(task, ct);
            _logger.LogInformation(
                "Subtask '{Name}' submitted as task {TaskId} (model: {Model}@{Host})",
                subtask.Name, taskId, modelId, endpoint ?? "auto");
            return new SubtaskSubmission(subtask.Name, taskId, modelId, endpoint ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit subtask '{Name}'", subtask.Name);
            return null;
        }
    }

    private static bool IsRetryableServerError(string errorMessage)
        => errorMessage.Contains("cudaMalloc", StringComparison.OrdinalIgnoreCase)
        || errorMessage.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        || errorMessage.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
        || errorMessage.Contains("No such host", StringComparison.OrdinalIgnoreCase)
        || errorMessage.Contains("Ollama 500", StringComparison.OrdinalIgnoreCase)
        || errorMessage.Contains("llama runner", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object> BuildSubtaskVars(
        PromptSubtask subtask,
        PromptDefinition prompt,
        Dictionary<string, object> allVars)
    {
        if (subtask.InputVars.Count == 0)
            return allVars; // No filter — pass everything

        // Seed with auto-computed date vars.
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"]     = allVars.GetValueOrDefault("date", string.Empty),
            ["datetime"] = allVars.GetValueOrDefault("datetime", string.Empty),
        };

        // Copy all YAML prompt-level variables (topic, context, output_subdir, …)
        // from allVars — not from prompt.Variables — so caller overrides (POST body)
        // are applied instead of the YAML defaults.
        foreach (var kv in prompt.Variables)
            if (allVars.TryGetValue(kv.Key, out var overriddenVal))
                result[kv.Key] = overriddenVal;

        // Propagate auto-derived keys that are not in prompt.Variables.
        foreach (var key in (ReadOnlySpan<string>)["topic_slug", "ticker_upper", "model_preference"])
            if (allVars.TryGetValue(key, out var computedVal))
                result[key] = computedVal;

        // Add data-collection outputs explicitly declared by this subtask.
        foreach (var key in subtask.InputVars)
            if (allVars.TryGetValue(key, out var val))
                result[key] = val;

        return result;
    }

    private async Task<string> WaitForTaskAsync(string taskId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(taskId)) return string.Empty;

        // Poll every 2 seconds; give up after 2 hours (matches TaskExecutionTimeout)
        var deadline = DateTime.UtcNow.AddHours(2);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(2_000, ct);

            var status = _taskSubmission.GetTaskStatus(taskId);
            if (status is null) break;

            switch (status.Status)
            {
                case AgentTaskStatus.Completed:
                    return status.Result?.Output ?? string.Empty;
                case AgentTaskStatus.Failed:
                    _logger.LogWarning(
                        "Subtask {TaskId} failed: {Err}", taskId, status.Result?.ErrorMessage);
                    return string.Empty;
                case AgentTaskStatus.Cancelled:
                    return string.Empty;
            }
        }

        _logger.LogWarning("Timed out waiting for subtask task {TaskId}", taskId);
        return string.Empty;
    }

    // ── Synthesis ───────────────────────────────────────────────────────────────

    private static string RenderSynthesisOrAggregate(
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        Dictionary<string, string> subtaskResults)
    {
        if (prompt.Synthesis?.PromptTemplate is not null)
        {
            try { return PromptTemplate.RenderSynthesis(prompt, vars); }
            catch (Exception) { /* fall through to aggregation */ }
        }

        // Fallback: concatenate subtask results under headings
        var sb = new StringBuilder();
        foreach (var (name, output) in subtaskResults)
        {
            sb.AppendLine($"## {name}");
            sb.AppendLine(output);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── Output writing ──────────────────────────────────────────────────────────

    private async Task WriteOutputAsync(
        string destinationTemplate,
        string content,
        Dictionary<string, object> vars)
    {
        try
        {
            var path = ExpandPath(ResolveSimpleTemplate(destinationTemplate, vars));
            var dir  = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, content);
            _logger.LogInformation("Output written to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write output to '{Dest}'", destinationTemplate);
        }
    }

    // ── Model spec parsing ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses a model spec string such as "ollama/deepseek-r1:14b@mini" into its components.
    /// The optional @machine suffix is resolved to a BaseUrl via SAGIDE:Ollama:Servers config.
    /// </summary>
    private (ModelProvider Provider, string ModelId, string? Endpoint) ParseModelSpec(string spec)
    {
        string? endpoint = null;

        // Extract @machine suffix for cross-machine routing
        var atIdx = spec.LastIndexOf('@');
        if (atIdx > 0)
        {
            var machine = spec[(atIdx + 1)..].Trim();
            spec     = spec[..atIdx].Trim();
            endpoint = ResolveServerUrl(machine);

            if (endpoint is null)
                _logger.LogWarning(
                    "Machine '{Machine}' not found in SAGIDE:Ollama:Servers — request will use health-monitor routing",
                    machine);
        }

        if (spec.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            return (ModelProvider.Ollama, spec[7..], endpoint);

        if (spec.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return (ModelProvider.Claude, spec, endpoint);

        if (spec.StartsWith("codex/", StringComparison.OrdinalIgnoreCase) ||
            spec.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            var slash = spec.IndexOf('/');
            return (ModelProvider.Codex, spec[(slash + 1)..], endpoint);
        }

        if (spec.StartsWith("gemini/", StringComparison.OrdinalIgnoreCase))
            return (ModelProvider.Gemini, spec[7..], endpoint);

        // Default: treat as Ollama model id
        return (ModelProvider.Ollama, spec, endpoint);
    }

    /// <summary>
    /// Looks up a machine name in SAGIDE:Ollama:Servers and SAGIDE:OpenAICompatible:Servers.
    /// Returns the BaseUrl if found, or null.
    /// </summary>
    private string? ResolveServerUrl(string machineName)
    {
        foreach (var server in _config.GetSection("SAGIDE:Ollama:Servers").GetChildren())
        {
            if (string.Equals(server["Name"], machineName, StringComparison.OrdinalIgnoreCase))
                return server["BaseUrl"];
        }
        foreach (var server in _config.GetSection("SAGIDE:OpenAICompatible:Servers").GetChildren())
        {
            if (string.Equals(server["Name"], machineName, StringComparison.OrdinalIgnoreCase))
                return server["BaseUrl"];
        }
        return null;
    }

    // ── Template helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a model template string that may contain Scriban expressions such as
    /// <c>{{model_preference.subtasks.fundamental}}</c>.
    /// Falls back to the original string on render failure.
    /// </summary>
    /// <summary>
    /// Renders a template string with Scriban. Handles both simple {{key}} substitutions and
    /// nested paths like {{model_preference.subtasks.planning}}.
    /// On parse or render error the original template is returned unchanged.
    /// </summary>
    private static string ResolveSimpleTemplate(string template, Dictionary<string, object> vars) =>
        PromptTemplate.RenderRaw(template, vars);

    /// <summary>
    /// Derives a file-safe slug from a topic string: up to 4 meaningful keywords joined by underscores.
    /// Stop words are filtered first; if fewer than 2 words remain after filtering, the first 4 raw
    /// words are used instead.
    /// Examples:
    ///   "Microsoft stock outlook"          → "Microsoft_stock_outlook"
    ///   "The future of edge AI hardware"   → "future_edge_AI_hardware"
    ///   "Learn AI In C#"                   → "Learn_AI_C"
    /// </summary>
    private static string BuildTopicSlug(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return "digest";

        // Strip non-alphanumeric characters from each word (keeps letters and digits only).
        var words = topic.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();

        var keywords = words.Where(w => !_slugStopWords.Contains(w)).Take(4).ToList();

        // Fall back to first 4 raw words if stop-word filtering leaves fewer than 2.
        if (keywords.Count < 2)
            keywords = words.Take(4).ToList();

        return keywords.Count > 0 ? string.Join("_", keywords) : "digest";
    }

    /// <summary>
    /// Resolves an expression like "{{watchlist.symbols}}" into its var name ("watchlist"),
    /// then extracts the value from vars as a collection of strings.
    /// Falls back to treating the resolved string as a single item.
    /// </summary>
    private static IEnumerable<string> ResolveCollection(string expression, Dictionary<string, object> vars)
    {
        var varName = ExtractLeadingVarName(expression);
        if (!string.IsNullOrEmpty(varName) && vars.TryGetValue(varName, out var val))
        {
            if (val is IEnumerable<string> list) return list;
            var str = val?.ToString() ?? string.Empty;
            return str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // Not a var reference: resolve template and return as single item
        var resolved = ResolveSimpleTemplate(expression, vars);
        return string.IsNullOrWhiteSpace(resolved) ? [] : [resolved];
    }

    /// <summary>
    /// Extracts the leading var name from a template expression like "{{watchlist.symbols}}".
    /// Returns the segment before the first dot.
    /// </summary>
    private static string ExtractLeadingVarName(string template)
    {
        var start = template.IndexOf("{{", StringComparison.Ordinal);
        var end   = template.IndexOf("}}", StringComparison.Ordinal);
        if (start < 0 || end < 0) return template.Trim();
        var inner = template[(start + 2)..end].Trim();
        var dot   = inner.IndexOf('.');
        return dot > 0 ? inner[..dot] : inner;
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~/") || path == "~")
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.Length > 2 ? path[2..] : string.Empty);
        return path;
    }
}

public record SubtaskRunResult(
    string InstanceId,
    string SynthesizedOutput,
    Dictionary<string, string> SubtaskResults);
