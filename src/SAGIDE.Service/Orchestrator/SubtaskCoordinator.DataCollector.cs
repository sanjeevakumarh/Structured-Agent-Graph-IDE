using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SAGIDE.Contracts;
using SAGIDE.Core.Models;
using SAGIDE.Service.Prompts;
using SAGIDE.Memory;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Data collection phase of <see cref="SubtaskCoordinator"/>:
/// file reads, HTTP fetches, web searches, LLM-planned queries,
/// per-section LLM analysis, vector search, JSON extraction, and filtering.
/// </summary>
public sealed partial class SubtaskCoordinator
{
    // ── Data collection steps ─────────────────────────────────────────────────

    private async Task ExecuteDataCollectionAsync(
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        RunTracer tracer,
        CancellationToken ct)
    {
        if (prompt.DataCollection is null) return;

        var stepQueue = new Queue<PromptDataCollectionStep>(prompt.DataCollection.Steps);
        var executionPlan = new List<object>();

        while (stepQueue.Count > 0)
        {
            var raw = stepQueue.Dequeue();

            if (!string.IsNullOrWhiteSpace(raw.Skill) && _skillRegistry is not null)
            {
                var expanded = ExpandSkillRefs([raw], vars);
                var remaining = new List<PromptDataCollectionStep>(expanded);
                while (stepQueue.Count > 0) remaining.Add(stepQueue.Dequeue());
                stepQueue = new Queue<PromptDataCollectionStep>(remaining);
                continue;
            }

            var step = raw;
            executionPlan.Add(new { step.Name, step.Type, skill = step.Skill ?? "(none)", output_var = step.OutputVar ?? "(none)" });

            _logger.LogDebug("Data collection: {Name} ({Type})", step.Name, step.Type);
            using var stepActivity = _activitySource.StartActivity($"step.{step.Name}");
            stepActivity?.SetTag("step.type", step.Type);
            stepActivity?.SetTag("step.skill", step.Skill ?? string.Empty);
            stepActivity?.SetTag("prompt.domain", prompt.Domain);
            stepActivity?.SetTag("prompt.name", prompt.Name);
            try
            {
                var result = await ExecuteStepAsync(step, vars, prompt, tracer, ct);
                if (!string.IsNullOrEmpty(step.OutputVar))
                    vars[step.OutputVar] = result;

                if (_parentOutputVarAliases.TryGetValue(step.Name, out var parentVar))
                    vars[parentVar] = result;

                if (tracer.IsEnabled
                    && step.Type is not "llm_queries" and not "llm_per_section")
                {
                    tracer.WriteText($"step-{step.Name}-output",
                        result?.ToString() ?? "(empty)");
                }

                if (_expandedSkillMap.TryGetValue(step.Name, out var sourceSkill))
                    ValidateSkillOutput(step.Name, result?.ToString() ?? string.Empty, sourceSkill);

                stepActivity?.SetTag("step.output.length", result?.ToString()?.Length ?? 0);
            }
            catch (Exception ex)
            {
                stepActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogWarning(ex, "Data collection step '{Name}' failed, continuing with empty result", step.Name);
                if (!string.IsNullOrEmpty(step.OutputVar))
                    vars[step.OutputVar] = string.Empty;
            }
        }

        tracer.Write("data-collection-plan", new
        {
            step_count = executionPlan.Count,
            steps = executionPlan,
        });
    }

    // ── Data quality guard ───────────────────────────────────────────────────────

    private static (List<(string StepName, string VarName)> Required,
                    List<(string StepName, string VarName)> Optional)
        ClassifyEmptyVars(PromptDefinition prompt, Dictionary<string, object> vars)
    {
        if (prompt.DataCollection is null) return ([], []);

        static bool IsCriticalType(string? t) =>
            t is "skill" or "llm_queries" or "web_search_batch" or "llm_per_section" or "llm";

        var required = new List<(string StepName, string VarName)>();
        var optional = new List<(string StepName, string VarName)>();

        foreach (var step in prompt.DataCollection.Steps)
        {
            if (!IsCriticalType(step.Type)) continue;
            if (string.IsNullOrEmpty(step.OutputVar)) continue;

            var isEmpty = !vars.TryGetValue(step.OutputVar, out var v)
                          || string.IsNullOrEmpty(v?.ToString());
            if (!isEmpty) continue;

            if (step.OptionalOutput)
                optional.Add((step.Name, step.OutputVar));
            else
                required.Add((step.Name, step.OutputVar));
        }

        foreach (var step in prompt.DataCollection.Steps)
        {
            if (step.Type is not "llm_per_section") continue;
            var searchVar = step.SearchResultsVar;
            if (string.IsNullOrEmpty(searchVar)) continue;
            if (required.Any(m => m.VarName == searchVar)) continue;
            if (optional.Any(m => m.VarName == searchVar)) continue;

            var isEmpty = !vars.TryGetValue(searchVar, out var sv)
                          || string.IsNullOrEmpty(sv?.ToString());
            if (isEmpty)
                optional.Add(($"{step.Name}.search_input", searchVar));
        }

        return (required, optional);
    }

    // ── Step execution (switch over all step types) ──────────────────────────────

    private async Task<object> ExecuteStepAsync(
        PromptDataCollectionStep step,
        Dictionary<string, object> vars,
        PromptDefinition prompt,
        RunTracer tracer,
        CancellationToken ct)
    {
        if (step.Parameters.Count > 0)
            vars["parameters"] = step.Parameters;

        switch (step.Type.Trim().ToLowerInvariant())
        {
            case "read_file":
            {
                var path = ExpandPath(ResolveSimpleTemplate(step.Source ?? string.Empty, vars));
                if (!File.Exists(path))
                {
                    _logger.LogWarning("read_file step '{Name}': file not found at '{Path}'", step.Name, path);
                    return string.Empty;
                }
                return await File.ReadAllTextAsync(path, ct);
            }

            case "web_api":
            {
                var url = ResolveSimpleTemplate(step.Source ?? string.Empty, vars);
                if (string.IsNullOrWhiteSpace(url)) return string.Empty;
                var doc = await _fetcher.FetchUrlAsync(url, ct);
                return doc.Body;
            }

            case "web_fetch":
            {
                var urlTemplate = ResolveSimpleTemplate(step.Source ?? string.Empty, vars);
                if (string.IsNullOrWhiteSpace(urlTemplate)) return string.Empty;

                var maxChars = step.MaxCharsPerPage > 0 ? step.MaxCharsPerPage : 4000;
                var items = string.IsNullOrWhiteSpace(step.IterateOver)
                    ? (IEnumerable<string>)[urlTemplate]
                    : ResolveCollection(step.IterateOver, vars)
                        .Select(item => urlTemplate
                            .Replace("{{symbol}}", item).Replace("{symbol}", item)
                            .Replace("{{item}}", item).Replace("{item}", item));

                var sections = new List<string>();
                foreach (var url in items.Take(10))
                {
                    try
                    {
                        using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        pageCts.CancelAfter(TimeSpan.FromSeconds(15));

                        var doc = await _fetcher.FetchUrlAsync(url, pageCts.Token);
                        var text = await HtmlTextExtractor.ExtractAsync(doc.Body, maxChars);

                        if (!string.IsNullOrWhiteSpace(text) && text.Length > 100)
                        {
                            sections.Add($"## Page: {url}\n{text}");
                            _logger.LogInformation("web_fetch '{Name}': extracted {Chars} chars from {Url}",
                                step.Name, text.Length, url);
                        }
                        else
                        {
                            _logger.LogWarning("web_fetch '{Name}': no usable text from {Url}", step.Name, url);
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "web_fetch '{Name}': failed to fetch {Url}", step.Name, url);
                    }
                }

                return string.Join("\n\n---\n\n", sections);
            }

            case "rss":
            case "atom":
            {
                var feedUrl = ResolveSimpleTemplate(step.Source ?? string.Empty, vars);
                if (string.IsNullOrWhiteSpace(feedUrl)) return string.Empty;
                var entries = await _fetcher.FetchRssAsync(feedUrl, ct);
                return string.Join("\n\n", entries.Select(e =>
                    $"### {e.Title}\nURL: {e.Url}\n{e.Body}".Trim()));
            }

            case "web_api_batch":
            {
                var urlTemplate = ResolveSimpleTemplate(step.Source ?? string.Empty, vars);
                var items       = ResolveCollection(step.IterateOver ?? string.Empty, vars);
                var bodies      = new List<string>();

                foreach (var item in items.Take(_config.GetValue("SAGIDE:Orchestration:WebApiBatchMaxItems", 50)))
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
                var inputStr  = ResolveSimpleTemplate(step.Input ?? string.Empty, vars);
                var condition = ResolveSimpleTemplate(step.Condition ?? string.Empty, vars);
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
                    _logger.LogWarning("web_search_batch step '{Name}': SAGIDE:Rag:SearchUrl not configured — skipped", step.Name);
                    return string.Empty;
                }

                var queryTemplate = ResolveSimpleTemplate(step.Query ?? step.Source ?? string.Empty, vars);
                var items         = string.IsNullOrWhiteSpace(step.IterateOver)
                    ? (IEnumerable<string>)[queryTemplate]
                    : ResolveCollection(step.IterateOver, vars);

                var limitStr   = ResolveSimpleTemplate(step.Limit ?? string.Empty, vars);
                var maxResults = int.TryParse(limitStr, out var l) && l > 0 ? l : 5;

                var sections = new List<string>();
                foreach (var item in items.Take(_config.GetValue("SAGIDE:Orchestration:WebSearchBatchMaxItems", 20)))
                {
                    var query = queryTemplate
                        .Replace("{{symbol}}", item).Replace("{{item}}", item)
                        .Replace("{symbol}",   item).Replace("{item}",   item);

                    _logger.LogDebug("web_search_batch '{Name}': querying '{Query}'", step.Name, query);
                    var fetchPages      = step.FetchPages;
                    var maxCharsPerPage = step.MaxCharsPerPage > 0 ? step.MaxCharsPerPage : 3000;
                    var result = fetchPages > 0
                        ? await _search.SearchWithPageContentAsync(query, maxResults, fetchPages, maxCharsPerPage, prompt.Domain, ct)
                        : await _search.SearchAsync(query, maxResults, prompt.Domain, ct);
                    if (!string.IsNullOrWhiteSpace(result))
                        sections.Add($"## Search: {query}\n{result}");
                }

                return string.Join("\n\n---\n\n", sections);
            }

            case "llm_queries":
            {
                if (!_search.IsConfigured)
                {
                    _logger.LogWarning("llm_queries step '{Name}': search not configured — skipped", step.Name);
                    tracer.Write($"step-{step.Name}-skipped", new { reason = "search_not_configured" });
                    return string.Empty;
                }

                if (string.IsNullOrWhiteSpace(step.PlanningPrompt))
                {
                    _logger.LogWarning("llm_queries step '{Name}': no planning_prompt defined — skipped", step.Name);
                    tracer.Write($"step-{step.Name}-skipped", new { reason = "no_planning_prompt" });
                    return string.Empty;
                }

                var planningPromptText = ResolveSimpleTemplate(step.PlanningPrompt, vars);
                tracer.WriteText($"step-{step.Name}-planning-prompt", planningPromptText);

                var modelSpecRaw = string.IsNullOrWhiteSpace(step.Model)
                    ? (prompt.ModelPreference?.Orchestrator ?? string.Empty)
                    : step.Model;
                var modelSpec = ResolveSimpleTemplate(modelSpecRaw, vars);
                var (planProvider, planModelId, planEndpoint) = ParseModelSpec(modelSpec);

                tracer.Write($"step-{step.Name}-planning-call", new
                {
                    model    = planModelId,
                    endpoint = planEndpoint,
                    provider = planProvider.ToString(),
                });

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
                _logger.LogInformation("llm_queries '{Name}': planning task {TaskId} submitted (model: {Model}@{Host})",
                    step.Name, planTaskId, planModelId, planEndpoint ?? "auto");

                var planResult = await WaitForTaskAsync(planTaskId, ct);
                var queries    = ParseJsonStringArray(planResult);

                tracer.WriteText($"step-{step.Name}-llm-raw-response", planResult);

                if (queries.Count == 0)
                {
                    _logger.LogWarning("llm_queries '{Name}': LLM returned no parseable queries. Raw: {Raw}",
                        step.Name, planResult.Length > 300 ? planResult[..300] + "..." : planResult);
                    tracer.Write($"step-{step.Name}-skipped", new
                    {
                        reason  = "no_parseable_queries",
                        llm_raw = planResult.Length > 500 ? planResult[..500] + "..." : planResult,
                    });
                    return string.Empty;
                }

                tracer.Write($"step-{step.Name}-queries", new { count = queries.Count, queries });
                _logger.LogInformation("llm_queries '{Name}': {Count} queries generated — {List}",
                    step.Name, queries.Count, string.Join("; ", queries));

                var limitStr   = ResolveSimpleTemplate(step.Limit ?? string.Empty, vars);
                var maxResults = int.TryParse(limitStr, out var lq) && lq > 0 ? lq : 5;
                var maxQueries = _config.GetValue("SAGIDE:Orchestration:LlmQueriesMaxQueries", 10);

                var sections = new List<string>();
                var qi = 0;
                foreach (var query in queries.Take(maxQueries))
                {
                    qi++;
                    _logger.LogDebug("llm_queries '{Name}': searching '{Query}'", step.Name, query);
                    var fetchPages      = step.FetchPages;
                    var maxCharsPerPage = step.MaxCharsPerPage > 0 ? step.MaxCharsPerPage : 3000;
                    var searchResult = fetchPages > 0
                        ? await _search.SearchWithPageContentAsync(query, maxResults, fetchPages, maxCharsPerPage, prompt.Domain, ct)
                        : await _search.SearchAsync(query, maxResults, prompt.Domain, ct);
                    tracer.Write($"step-{step.Name}-search-q{qi:D2}", new
                    {
                        query,
                        results_chars = searchResult.Length,
                        results       = searchResult,
                    });
                    if (!string.IsNullOrWhiteSpace(searchResult))
                        sections.Add($"## Search: {query}\n{searchResult}");
                }

                return string.Join("\n\n---\n\n", sections);
            }

            case "llm_per_section":
            {
                var searchVarName      = step.SearchResultsVar ?? string.Empty;
                var searchDataPreCheck = !string.IsNullOrEmpty(searchVarName) && vars.TryGetValue(searchVarName, out var srPre)
                    ? srPre?.ToString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrEmpty(searchVarName) && searchDataPreCheck.Length == 0)
                {
                    _logger.LogWarning(
                        "llm_per_section '{Name}': search data var '{Var}' is empty — proceeding with other available data (yahoo_quote_data, stockanalysis_data).",
                        step.Name, searchVarName);
                    tracer.Write($"step-{step.Name}-search-data-missing", new
                    {
                        var_name = searchVarName,
                        hint     = "Sections will rely on direct-fetch data only; web search results unavailable",
                    });
                }

                var modelSpecRaw = string.IsNullOrWhiteSpace(step.Model)
                    ? (prompt.ModelPreference?.Orchestrator ?? string.Empty)
                    : step.Model;
                var modelSpec = ResolveSimpleTemplate(modelSpecRaw, vars);
                var (secProvider, secModelId, secEndpoint) = ParseModelSpec(modelSpec);

                List<string> sectionNames;
                var maxSectionsStr = ResolveSimpleTemplate(step.MaxSections ?? "5", vars);
                var maxSections    = int.TryParse(maxSectionsStr, out var ms) && ms > 0 ? ms : 5;

                var resolvedSectionTitle = !string.IsNullOrWhiteSpace(step.SectionTitle)
                    ? ResolveSimpleTemplate(step.SectionTitle, vars) : null;

                if (!string.IsNullOrWhiteSpace(resolvedSectionTitle) && maxSections == 1)
                {
                    sectionNames = [resolvedSectionTitle];
                    _logger.LogInformation("llm_per_section '{Name}': implicit single-section mode — '{Title}'",
                        step.Name, resolvedSectionTitle);
                    tracer.Write($"step-{step.Name}-sections", new
                    {
                        count    = 1,
                        sections = sectionNames,
                        mode     = "implicit_single_section",
                    });
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(step.PlanningPrompt))
                    {
                        _logger.LogWarning("llm_per_section step '{Name}': no planning_prompt — skipped", step.Name);
                        tracer.Write($"step-{step.Name}-skipped", new { reason = "no_planning_prompt" });
                        return string.Empty;
                    }

                    var planText = ResolveSimpleTemplate(step.PlanningPrompt, vars);
                    tracer.WriteText($"step-{step.Name}-planning-prompt", planText);
                    tracer.Write($"step-{step.Name}-planning-call", new
                    {
                        model    = secModelId,
                        endpoint = secEndpoint,
                        provider = secProvider.ToString(),
                    });

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
                    sectionNames = ParseJsonStringArray(planResult).Take(maxSections).ToList();

                    tracer.WriteText($"step-{step.Name}-planning-llm-raw", planResult);

                    if (sectionNames.Count == 0)
                    {
                        _logger.LogWarning("llm_per_section '{Name}': LLM returned no section names. Raw: {Raw}",
                            step.Name, planResult.Length > 300 ? planResult[..300] + "..." : planResult);
                        return string.Empty;
                    }

                    tracer.Write($"step-{step.Name}-sections", new { count = sectionNames.Count, sections = sectionNames });
                    _logger.LogInformation("llm_per_section '{Name}': {Count} sections — {List}",
                        step.Name, sectionNames.Count, string.Join(" | ", sectionNames));
                }

                if (string.IsNullOrWhiteSpace(step.SectionAnalysisPrompt))
                {
                    _logger.LogWarning("llm_per_section '{Name}': no section_analysis_prompt — returning headings only", step.Name);
                    return string.Join("\n\n", sectionNames.Select(s => $"## {s}"));
                }

                if (!string.IsNullOrEmpty(searchVarName))
                {
                    tracer.Write($"step-{step.Name}-search-data-summary", new
                    {
                        var_name = searchVarName,
                        chars    = searchDataPreCheck.Length,
                        is_empty = false,
                    });
                }

                // Process sections sequentially — parallel requests to a single model
                // instance cause dropped/empty responses (especially LM Studio).
                var sectionResults = new List<(string sectionName, string result)>();
                foreach (var sectionName in sectionNames)
                {
                    var sectionVars = new Dictionary<string, object>(vars, StringComparer.OrdinalIgnoreCase)
                    {
                        ["section_name"]   = sectionName,
                        ["search_results"] = searchDataPreCheck,
                        ["parameters"]     = step.Parameters,
                        ["blocks"]         = _skillRegistry?.PromptBlocks
                                             ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(),
                    };
                    var sectionPromptText = ResolveSimpleTemplate(step.SectionAnalysisPrompt, sectionVars);

                    tracer.WriteText($"step-{step.Name}-section-{sectionName}-prompt", sectionPromptText);

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
                            ["step_name"]     = step.Name, ["step_type"] = "llm_per_section_analysis",
                            ["section_name"]  = sectionName,
                            ["prompt_domain"] = prompt.Domain, ["prompt_name"] = prompt.Name,
                        },
                    };
                    if (!string.IsNullOrEmpty(secEndpoint)) sTask.Metadata["modelEndpoint"] = secEndpoint;

                    var sTaskId = await _taskSubmission.SubmitTaskAsync(sTask, ct);
                    _logger.LogInformation("llm_per_section '{Name}': section '{Section}' → task {TaskId}",
                        step.Name, sectionName, sTaskId);
                    var result = await WaitForTaskAsync(sTaskId, ct);
                    tracer.WriteText($"step-{step.Name}-section-{sectionName}-result", result);
                    sectionResults.Add((sectionName, result));
                }

                return string.Join("\n\n", sectionResults
                    .Where(r => !string.IsNullOrWhiteSpace(r.result))
                    .Select(r => $"## {r.sectionName}\n\n{r.result.Trim()}"));
            }

            case "llm":
            {
                if (string.IsNullOrWhiteSpace(step.PromptTemplate))
                {
                    _logger.LogWarning("llm step '{Name}': no prompt_template — skipped", step.Name);
                    return string.Empty;
                }

                var promptText = ResolveSimpleTemplate(step.PromptTemplate, vars);
                tracer.WriteText($"step-{step.Name}-prompt", promptText);

                var modelSpecRaw = string.IsNullOrWhiteSpace(step.Model)
                    ? (prompt.ModelPreference?.Orchestrator ?? string.Empty)
                    : step.Model;
                var (provider, modelId, endpoint) = ParseModelSpec(ResolveSimpleTemplate(modelSpecRaw, vars));

                var llmTask = new AgentTask
                {
                    AgentType     = AgentType.Generic,
                    ModelProvider = provider,
                    ModelId       = modelId,
                    Description   = promptText,
                    SourceTag     = prompt.SourceTag ?? $"{prompt.Domain}_dc_llm",
                    Priority      = 1,
                    Metadata      = new() { ["step_name"] = step.Name, ["step_type"] = "llm" },
                };
                if (!string.IsNullOrEmpty(endpoint)) llmTask.Metadata["modelEndpoint"] = endpoint;

                var llmTaskId = await _taskSubmission.SubmitTaskAsync(llmTask, ct);
                _logger.LogInformation("llm step '{Name}': task {TaskId} submitted (model: {Model}@{Host})",
                    step.Name, llmTaskId, modelId, endpoint ?? "auto");

                var llmResult = await WaitForTaskAsync(llmTaskId, ct);
                tracer.WriteText($"step-{step.Name}-result", llmResult);
                return llmResult;
            }

            case "vector_search":
            {
                if (_ragPipeline is null)
                {
                    _logger.LogWarning("vector_search step '{Name}' skipped — RagPipeline not available", step.Name);
                    tracer.Write($"step-{step.Name}-skipped", new { reason = "rag_pipeline_not_available" });
                    return string.Empty;
                }

                var queryTemplate = step.PromptTemplate ?? step.Query ?? string.Empty;
                var query         = PromptTemplate.RenderRaw(queryTemplate, vars);
                var sourceTag     = step.Source ?? string.Empty;
                var topK = 20;
                if (step.Parameters.TryGetValue("top_k", out var topKObj)
                    && int.TryParse(topKObj?.ToString(), out var parsed))
                    topK = parsed;

                _logger.LogInformation("vector_search step '{Name}': query={Query}, tag={Tag}, topK={K}",
                    step.Name, query[..Math.Min(query.Length, 80)], sourceTag, topK);

                var context = await _ragPipeline.GetRelevantContextAsync(query, topK, sourceTag, ct);
                tracer.WriteText($"step-{step.Name}-result", context);
                return context;
            }

            case "json_extract":
            {
                var sourceVar = step.Source
                    ?? step.Parameters.GetValueOrDefault("source_var")?.ToString()
                    ?? string.Empty;
                if (string.IsNullOrEmpty(sourceVar) || !vars.TryGetValue(sourceVar, out var jsonObj)
                    || string.IsNullOrWhiteSpace(jsonObj?.ToString()))
                {
                    _logger.LogWarning("json_extract step '{Name}': source var '{Var}' is empty", step.Name, sourceVar);
                    return string.Empty;
                }

                var jsonStr = jsonObj.ToString()!.Trim();
                if (jsonStr.StartsWith("```"))
                {
                    var firstNewline = jsonStr.IndexOf('\n');
                    if (firstNewline > 0) jsonStr = jsonStr[(firstNewline + 1)..];
                    if (jsonStr.EndsWith("```")) jsonStr = jsonStr[..^3].TrimEnd();
                }

                try
                {
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        _logger.LogWarning("json_extract step '{Name}': expected JSON object, got {Kind}", step.Name, root.ValueKind);
                        return jsonStr;
                    }

                    var extracted = new List<string>();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        {
                            vars[prop.Name] = prop.Value.ToString();
                            extracted.Add($"{prop.Name}={prop.Value}");
                        }
                    }

                    foreach (var kv in step.Parameters)
                    {
                        if (kv.Key is "source_var" or "source") continue;
                        var aliasTarget = kv.Value?.ToString() ?? string.Empty;
                        if (vars.TryGetValue(aliasTarget, out var aliasVal))
                        {
                            vars[kv.Key] = aliasVal;
                            extracted.Add($"{kv.Key}={aliasVal} (alias of {aliasTarget})");
                        }
                    }

                    _logger.LogInformation("json_extract step '{Name}': extracted [{Fields}]", step.Name, string.Join(", ", extracted));
                    tracer.Write($"step-{step.Name}-extracted", new { source = sourceVar, fields = extracted });
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "json_extract step '{Name}': failed to parse JSON from '{Var}'", step.Name, sourceVar);
                }
                return jsonStr;
            }

            default:
                _logger.LogWarning("Unknown data_collection step type '{Type}' in step '{Name}'", step.Type, step.Name);
                return string.Empty;
        }
    }

    // ── JSON array parsing ──────────────────────────────────────────────────────

    private static List<string> ParseJsonStringArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var fence   = text.IndexOf('\n');
            var closing = text.LastIndexOf("```");
            if (fence > 0 && closing > fence)
                text = text[(fence + 1)..closing].Trim();
        }

        var start = text.IndexOf('[');
        var end   = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        var json = text[start..(end + 1)];
        try   { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
