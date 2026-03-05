using System.Diagnostics;
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
    private static readonly ActivitySource _activitySource = new("SAGIDE.SubtaskCoordinator", "1.0.0");

    private readonly ITaskSubmissionService _taskSubmission;
    private readonly WebFetcher _fetcher;
    private readonly WebSearchAdapter _search;
    private readonly IConfiguration _config;
    private readonly ILogger<SubtaskCoordinator> _logger;
    private readonly OllamaHostHealthMonitor? _healthMonitor;
    private readonly SkillRegistry? _skillRegistry;
    private readonly IReadOnlyList<string> _allOllamaUrls;

    // Maps expanded step name → its source skill definition, used for Phase 3 output validation.
    // Populated during ExpandSkillRefs; cleared at the start of each RunAsync call.
    private readonly Dictionary<string, SkillDefinition> _expandedSkillMap = new(StringComparer.OrdinalIgnoreCase);

    // Maps the LAST expanded step of a multi-step skill → the parent step's output_var.
    // When the last step executes, its result is also stored under the parent alias
    // so that downstream abort checks find the expected variable.
    private readonly Dictionary<string, string> _parentOutputVarAliases = new(StringComparer.OrdinalIgnoreCase);

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
        OllamaHostHealthMonitor? healthMonitor = null,
        SkillRegistry? skillRegistry = null)
    {
        _taskSubmission = taskSubmission;
        _fetcher        = fetcher;
        _search        = search;
        _config        = config;
        _logger        = logger;
        _healthMonitor = healthMonitor;
        _skillRegistry = skillRegistry;
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

        // Phase 5: Expand objects:/workflow: into flat data_collection + subtasks (no-op if absent)
        if ((prompt.Objects.Count > 0 || prompt.Workflow.Count > 0) && _skillRegistry is not null)
            WorkflowExpander.Expand(prompt, _skillRegistry, _logger);

        // Clear per-run skill map (populated during skill expansion below)
        _expandedSkillMap.Clear();
        _parentOutputVarAliases.Clear();

        // 1. Build variable context (YAML vars + caller overrides + auto date/time)
        var vars = BuildVarContext(prompt, variableOverrides);

        // Create a per-run tracer co-located with the report output (no-op when disabled).
        // Trace folder = same directory and base name as the primary output file, e.g.:
        //   ~/reports/finance/2026-03-02-msft-analysis.md  →  ~/reports/finance/2026-03-02-msft-analysis/
        // Falls back to SAGIDE:RunTracing:Path/{domain}-{name}-{datestamp} when no output is configured.
        var traceDir = ComputeTraceFolderPath(prompt, vars);
        var tracer   = RunTracer.Create(_config, traceDir, prompt.Domain, prompt.Name, vars["datestamp"].ToString()!);
        if (tracer.IsEnabled)
            tracer.Write("run-start", new
            {
                domain      = prompt.Domain,
                name        = prompt.Name,
                instance_id = instanceId,
                variables   = vars
                    .Where(kv => !kv.Key.Equals("model_preference", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()),
            });

        // 2. Execute data_collection steps sequentially (each step can reference prior outputs)
        await ExecuteDataCollectionAsync(prompt, vars, tracer, ct);

        // 2b. Guard: abort before subtask dispatch if critical collection vars are empty.
        // Empty search/analysis outputs mean downstream LLMs will hallucinate — fail fast instead.
        // Steps marked optional_output: true contribute a warning summary but do NOT abort.
        (var missingRequired, var missingOptional) = prompt.Subtasks.Count > 0
            ? ClassifyEmptyVars(prompt, vars)
            : ([], []);

        // Build missing_data_summary from optional empties and inject into vars so section
        // analysis prompts can acknowledge what data is unavailable.
        if (missingOptional.Count > 0)
        {
            var summaryLines = missingOptional.Select(m =>
                $"<span style=\"color:red\">⚠ **Missing data**</span>: `{m.VarName}` " +
                $"(step `{m.StepName}` returned no results — search engine found nothing for this topic)");
            vars["missing_data_summary"] = string.Join("\n", summaryLines);
            _logger.LogWarning(
                "Optional data-collection var(s) empty — run continues with partial data: {Vars}",
                string.Join(", ", missingOptional.Select(m => m.VarName)));
        }
        else
        {
            vars["missing_data_summary"] = string.Empty;
        }

        if (missingRequired.Count > 0)
        {
            var varList = string.Join(", ", missingRequired.Select(m => $"'{m.VarName}' (step: {m.StepName})"));
            _logger.LogError(
                "Run aborted: {Count} required data-collection var(s) are empty after data collection: {Vars}. " +
                "This usually means skill steps were skipped (SkillRegistry empty?) or the search service is unreachable.",
                missingRequired.Count, varList);
            tracer.Write("data-collection-aborted", new
            {
                reason       = "empty_required_vars",
                missing_vars = missingRequired.Select(m => new { step = m.StepName, var_name = m.VarName }).ToList(),
                hint         = "Restart the service via kill-and-start.ps1 to ensure the SkillRegistry loads correctly, and verify the search service is reachable.",
            });

            // Write a brief error report to the configured output so the user sees why the run failed.
            var errorReport =
                $"# Run Aborted — Missing Search Data\n\n" +
                $"The following required data-collection outputs were empty after data collection ran:\n\n" +
                string.Join("\n", missingRequired.Select(m => $"- **{m.VarName}** (from step `{m.StepName}`)")) +
                $"\n\n## Likely Causes\n\n" +
                $"- **SkillRegistry is empty**: restart the service with `kill-and-start.ps1` to set the correct `--SAGIDE:SkillsPath`\n" +
                $"- **Search service unreachable**: verify the SearxNG/search endpoint is running\n" +
                $"- **No queries generated**: check the skill's planning_prompt and model routing\n\n" +
                $"No LLM subtasks were dispatched. Re-run once the root cause is fixed.\n";

            if (!string.IsNullOrEmpty(prompt.Output?.Destination))
                await WriteOutputAsync(prompt.Output.Destination!, errorReport, vars);
            foreach (var extra in prompt.Outputs.Where(o => !string.IsNullOrEmpty(o.Destination)))
                await WriteOutputAsync(extra.Destination!, errorReport, vars);

            return new SubtaskRunResult(instanceId, errorReport, [], tracer.FolderPath);
        }

        // 3. Dispatch all subtasks in parallel
        var subtaskResults = await DispatchSubtasksAsync(prompt, vars, instanceId, tracer, ct);

        // 4. Merge subtask results into vars so synthesis template can reference them.
        // DispatchSubtasksAsync already does this using OutputVar; this pass ensures
        // any subtasks that were not dispatched (skipped/failed) also get their key set.
        var subtaskByName = prompt.Subtasks.ToDictionary(st => st.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, output) in subtaskResults)
        {
            var varKey = subtaskByName.TryGetValue(name, out var st) && !string.IsNullOrEmpty(st.OutputVar)
                ? st.OutputVar
                : $"{name}_result";
            vars[varKey] = output;
        }

        // 5. Run synthesis (or aggregate if no synthesis template)
        var synthesized = RenderSynthesisOrAggregate(prompt, vars, subtaskResults);

        // 6. Write to output destination if configured
        if (!string.IsNullOrEmpty(prompt.Output?.Destination))
            await WriteOutputAsync(prompt.Output.Destination, synthesized, vars);

        // 7. Write additional outputs (multi-file support via `outputs:` YAML list).
        //    Each entry may name a `source` subtask result variable; falls back to synthesised output.
        foreach (var extra in prompt.Outputs)
        {
            if (string.IsNullOrEmpty(extra.Destination)) continue;

            string content;
            if (!string.IsNullOrEmpty(extra.Source))
            {
                // Source was explicitly named — only write if it resolved to non-empty content.
                // An empty var (e.g. a file that wasn't found) should not produce a blank artifact.
                if (!vars.TryGetValue(extra.Source, out var v)
                    || v is null
                    || string.IsNullOrWhiteSpace(v.ToString()))
                {
                    _logger.LogWarning(
                        "Skipping output '{Dest}': source var '{Source}' is empty or missing",
                        extra.Destination, extra.Source);
                    continue;
                }
                content = v.ToString()!;
            }
            else
            {
                content = synthesized;
            }

            await WriteOutputAsync(extra.Destination, content, vars);
        }

        _logger.LogInformation("SubtaskCoordinator [{Id}] complete", instanceId);
        return new SubtaskRunResult(instanceId, synthesized, subtaskResults, tracer.FolderPath);
    }

    // ── Variable context ────────────────────────────────────────────────────────

    private static Dictionary<string, object> BuildVarContext(
        PromptDefinition prompt,
        Dictionary<string, string>? overrides)
    {
        var now = DateTime.UtcNow;
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"]      = now.ToString("yyyy-MM-dd"),
            ["datestamp"] = now.ToString("yyyy-MM-dd-HH-mm"),
            ["datetime"]  = now.ToString("O"),
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

    // ── Trace folder helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Computes the per-run trace folder co-located with the primary output file.
    /// Returns null when no output destination is configured (caller falls back to the
    /// SAGIDE:RunTracing:Path global directory).
    /// Example: "~/reports/finance/2026-03-02-msft-analysis.md"
    ///       →  "~/reports/finance/2026-03-02-msft-analysis"
    /// </summary>
    private string? ComputeTraceFolderPath(PromptDefinition prompt, Dictionary<string, object> vars)
    {
        var dest = prompt.Output?.Destination
                   ?? prompt.Outputs.FirstOrDefault(o => !string.IsNullOrEmpty(o.Destination))?.Destination;
        if (string.IsNullOrEmpty(dest)) return null;

        var rendered = ExpandPath(ResolveSimpleTemplate(dest, vars));
        var dir      = Path.GetDirectoryName(rendered);
        var baseName = Path.GetFileNameWithoutExtension(rendered);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return null;

        return Path.Combine(dir, baseName);
    }

    // ── Data collection steps ─────────────────────────────────────────────────

    private async Task ExecuteDataCollectionAsync(
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        RunTracer tracer,
        CancellationToken ct)
    {
        if (prompt.DataCollection is null) return;

        // Phase 2: Expand skill: references into their concrete implementation steps.
        // The expanded list replaces the original; _expandedSkillMap is populated for Phase 3 validation.
        var steps = _skillRegistry is not null
            ? ExpandSkillRefs(prompt.DataCollection.Steps, vars)
            : prompt.DataCollection.Steps;

        // Trace the expanded step list so operators can immediately see what will run.
        tracer.Write("data-collection-plan", new
        {
            step_count = steps.Count,
            steps = steps.Select(s => new { s.Name, s.Type, skill = s.Skill ?? "(none)", output_var = s.OutputVar ?? "(none)" }).ToList(),
        });

        foreach (var step in steps)
        {
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

                // Also store under the parent output var alias (multi-step skill rollup)
                if (_parentOutputVarAliases.TryGetValue(step.Name, out var parentVar))
                    vars[parentVar] = result;

                // Trace the final output for step types that don't self-trace
                // (llm_queries / llm_per_section write their own granular trace files)
                if (tracer.IsEnabled
                    && step.Type is not "llm_queries" and not "llm_per_section")
                {
                    tracer.WriteText($"step-{step.Name}-output",
                        result?.ToString() ?? "(empty)");
                }

                // Phase 3: Validate output against skill's outputs_schema (warnings only)
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
    }

    // ── Data quality guard ───────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether data-collection steps that should have produced search/analysis results
    /// actually populated their output variables. Returns a list of (stepName, varName) pairs
    /// for any critical step whose output is missing or empty after data collection ran.
    ///
    /// "Critical" steps are those of type <c>skill</c>, <c>llm_queries</c>,
    /// <c>web_search_batch</c>, or <c>llm_per_section</c> — all of which feed downstream
    /// subtasks with factual source material. Empty outputs signal that something failed
    /// (missing skill, unreachable search service, etc.) and downstream LLMs will hallucinate.
    /// </summary>
    /// <summary>
    /// Classifies empty data-collection output vars into required (abort) and optional (warn).
    /// Steps with <see cref="PromptDataCollectionStep.OptionalOutput"/> = true contribute to the
    /// optional list only — run continues and a <c>missing_data_summary</c> var is injected.
    /// </summary>
    private static (List<(string StepName, string VarName)> Required,
                    List<(string StepName, string VarName)> Optional)
        ClassifyEmptyVars(PromptDefinition prompt, Dictionary<string, object> vars)
    {
        if (prompt.DataCollection is null) return ([], []);

        // Step types whose output vars must be non-empty for a run to be meaningful.
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

        // Also check search_results_var for llm_per_section steps whose analysis var might
        // differ from their own output_var (e.g. run_analyses reads all_search_results).
        foreach (var step in prompt.DataCollection.Steps)
        {
            if (step.Type is not "llm_per_section") continue;
            var searchVar = step.SearchResultsVar;
            if (string.IsNullOrEmpty(searchVar)) continue;
            if (required.Any(m => m.VarName == searchVar)) continue; // already flagged
            if (optional.Any(m => m.VarName == searchVar)) continue;

            var isEmpty = !vars.TryGetValue(searchVar, out var sv)
                          || string.IsNullOrEmpty(sv?.ToString());
            if (isEmpty)
                required.Add(($"{step.Name}.search_input", searchVar));
        }

        return (required, optional);
    }

    // ── Phase 2: Skill reference expansion ──────────────────────────────────────

    /// <summary>
    /// Expands steps that have a <c>skill:</c> reference into their concrete implementation steps.
    /// Parameters from the calling step are merged over the skill's defaults, then rendered via Scriban.
    /// Non-skill steps pass through unchanged.
    /// </summary>
    internal List<PromptDataCollectionStep> ExpandSkillRefs(
        List<PromptDataCollectionStep> steps,
        Dictionary<string, object> vars)
    {
        if (_skillRegistry is null) return steps;

        var expanded = new List<PromptDataCollectionStep>(steps.Count);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Skill))
            {
                expanded.Add(step);
                continue;
            }

            var skill = _skillRegistry.Resolve(step.Skill);
            if (skill is null)
            {
                _logger.LogWarning("Skill '{Ref}' not found — step '{Name}' will be skipped", step.Skill, step.Name);
                continue;
            }

            // Merge: skill defaults ← step parameters.
            // Simple template values like "{{ticker}}" are pre-rendered against current vars so that
            // CloneStepRendered sees the concrete value ("msft") rather than the literal template
            // string ("{{ticker}}").  Values containing Scriban control-flow tags ({{ if, {{ else,
            // {{ for …}) are intentional pass-through templates used for later rendering (e.g.
            // section_analysis_prompt overrides) and must be kept verbatim.
            var mergedParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in skill.Parameters) mergedParams[kv.Key] = PreRenderParam(kv.Value, vars);
            foreach (var kv in step.Parameters)  mergedParams[kv.Key] = PreRenderParam(kv.Value, vars);

            // Honour the calling step's output_var if set (overrides skill default)
            if (!string.IsNullOrWhiteSpace(step.OutputVar))
                mergedParams["output_var"] = step.OutputVar;

            // Build capability → model mapping for {{capability.X}} template vars
            var capabilityMap = BuildCapabilityMap(skill, vars);

            // Inject parameters, capability, and shared prompt blocks into vars for rendering
            var skillVars = new Dictionary<string, object>(vars, StringComparer.OrdinalIgnoreCase)
            {
                ["parameters"] = mergedParams,
                ["capability"] = capabilityMap,
                ["blocks"]     = _skillRegistry.PromptBlocks,
            };

            // Clone each implementation step, rendering its fields with the merged vars
            foreach (var impl in skill.Implementation)
            {
                var clone = CloneStepRendered(impl, skillVars, $"{step.Name}.{impl.Name}");
                // Preserve merged parameters on the clone so that section_analysis_prompt
                // can reference {{parameters.X}} at section-execution time (not expansion time).
                clone.Parameters = new Dictionary<string, object>(mergedParams, StringComparer.OrdinalIgnoreCase);

                // If parameters include prompt overrides, apply them directly to the step fields.
                // The skill template wraps these in {{parameters.X}} interpolation, but Scriban only
                // does one pass — embedded {{ if }} tags in the parameter value are emitted as literal
                // text, not re-evaluated.  Setting the field directly lets execution-time rendering
                // process the conditionals with section_name and other vars in scope.
                if (mergedParams.TryGetValue("section_analysis_prompt", out var sapVal)
                    && sapVal is string sapStr && !string.IsNullOrWhiteSpace(sapStr))
                    clone.SectionAnalysisPrompt = sapStr;
                if (mergedParams.TryGetValue("planning_prompt", out var ppVal)
                    && ppVal is string ppStr && !string.IsNullOrWhiteSpace(ppStr))
                    clone.PlanningPrompt = ppStr;

                expanded.Add(clone);

                // Register in skill map so Phase 3 can validate the output
                _expandedSkillMap[clone.Name] = skill;
            }

            // For multi-step skills, map the last expanded step to the parent output var
            // so that abort checks find the expected variable (e.g. "evidence_normalizer_results").
            if (skill.Implementation.Count > 1
                && !string.IsNullOrWhiteSpace(step.OutputVar)
                && expanded.Count > 0)
            {
                var lastClone = expanded[^1];
                if (lastClone.OutputVar != step.OutputVar)
                    _parentOutputVarAliases[lastClone.Name] = step.OutputVar;
            }

            _logger.LogDebug(
                "Expanded skill '{Skill}' ({ImplCount} steps) for step '{Name}'",
                step.Skill, skill.Implementation.Count, step.Name);
        }
        return expanded;
    }

    /// <summary>
    /// Resolves capability slots declared in a skill's capability_requirements against
    /// the SAGIDE:Routing:Capabilities config, returning a dict usable in templates.
    /// </summary>
    private Dictionary<string, object> BuildCapabilityMap(SkillDefinition skill, Dictionary<string, object> vars)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, req) in skill.CapabilityRequirements)
        {
            // Try capability key built from needs joined with "+"
            var capKey = string.Join("+", req.Needs);
            var resolved = _config[$"SAGIDE:Routing:Capabilities:{capKey}"];

            // Fall back to model_preference.orchestrator if no capability mapping found
            if (string.IsNullOrWhiteSpace(resolved))
            {
                if (vars.TryGetValue("model_preference", out var mp) &&
                    mp is Dictionary<string, object> mpd &&
                    mpd.TryGetValue("orchestrator", out var orch))
                    resolved = orch?.ToString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(resolved))
                _logger.LogWarning(
                    "No capability mapping found for slot '{Slot}' (needs: [{Needs}]) in skill '{Skill}'",
                    slot, capKey, skill.Name);

            map[slot] = resolved ?? string.Empty;
        }
        return map;
    }

    /// <summary>
    /// Creates a shallow clone of a step with all string fields rendered through Scriban.
    /// </summary>
    private static PromptDataCollectionStep CloneStepRendered(
        PromptDataCollectionStep src,
        Dictionary<string, object> vars,
        string nameOverride)
    {
        string Render(string? s) => string.IsNullOrEmpty(s) ? string.Empty : PromptTemplate.RenderRaw(s, vars);

        return new PromptDataCollectionStep
        {
            Name                  = nameOverride,
            Type                  = Render(src.Type),
            Source                = Render(src.Source),
            Query                 = Render(src.Query),
            IterateOver           = Render(src.IterateOver),
            Input                 = Render(src.Input),
            Condition             = Render(src.Condition),
            Limit                 = Render(src.Limit),
            OutputVar             = Render(src.OutputVar),
            PlanningPrompt        = Render(src.PlanningPrompt),
            Model                 = Render(src.Model),
            SectionAnalysisPrompt = src.SectionAnalysisPrompt, // intentional pass-through: rendered per-section at execution time with section_name + parameters in scope
            SearchResultsVar      = Render(src.SearchResultsVar),
            MaxSections           = Render(src.MaxSections),
            SectionTitle          = Render(src.SectionTitle),
            PromptTemplate        = src.PromptTemplate, // intentional pass-through (rendered at execution)
            InputVars             = [..src.InputVars],
        };
    }

    /// <summary>
    /// Pre-renders a skill parameter value against the current variable context.
    /// Simple template refs like <c>"{{ticker}}"</c> are resolved now so that
    /// <see cref="CloneStepRendered"/> sees the concrete value rather than a
    /// template string that would be double-escaped by Scriban filters.
    /// Values containing Scriban control-flow tags are intentional pass-through
    /// templates (e.g. section_analysis_prompt overrides) and are kept verbatim.
    /// </summary>
    private static object PreRenderParam(object value, Dictionary<string, object> vars)
    {
        if (value is not string s) return value;
        if (!s.Contains("{{")) return value;  // no template syntax — nothing to render

        // Detect Scriban control flow: these are pass-through templates for later rendering
        if (s.Contains("{{ if ")   || s.Contains("{{- if ")   ||
            s.Contains("{{ else")  || s.Contains("{{- else")  ||
            s.Contains("{{ for ")  || s.Contains("{{- for ")  ||
            s.Contains("{{ while") || s.Contains("{{- while"))
            return value;

        return PromptTemplate.RenderRaw(s, vars);
    }

    // ── Phase 3: Output schema validation ───────────────────────────────────────

    /// <summary>
    /// Validates a skill step's output against the skill's declared outputs_schema.
    /// Violations are logged as warnings — never hard failures (LLM output is inherently fuzzy).
    /// Uses System.Text.Json; no extra NuGet package required.
    /// </summary>
    private void ValidateSkillOutput(string stepName, string output, SkillDefinition skill)
    {
        if (skill.OutputsSchema.Count == 0) return;
        if (string.IsNullOrWhiteSpace(output)) return;

        // If the schema declares type: string, any non-empty output is valid
        if (skill.OutputsSchema.TryGetValue("type", out var typeVal) &&
            typeVal?.ToString() == "string") return;

        // For object schemas, try to parse JSON and check required fields
        if (!skill.OutputsSchema.TryGetValue("required", out var reqObj)) return;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            var required = reqObj switch
            {
                List<object> list => list.Select(o => o.ToString()!),
                System.Collections.IEnumerable ie => ie.Cast<object>().Select(o => o.ToString()!),
                _ => []
            };

            foreach (var field in required)
            {
                if (!doc.RootElement.TryGetProperty(field, out _))
                    _logger.LogWarning(
                        "Skill output validation: step '{Step}' (skill '{Skill}') missing required field '{Field}'",
                        stepName, skill.Name, field);
            }
        }
        catch (JsonException)
        {
            // Output is not JSON — acceptable for text-type skills; skip validation
        }
    }

    private async Task<object> ExecuteStepAsync(
        PromptDataCollectionStep step,
        Dictionary<string, object> vars,
        PromptDefinition prompt,
        RunTracer tracer,
        CancellationToken ct)
    {
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
                    tracer.Write($"step-{step.Name}-skipped", new
                    {
                        reason = "search_not_configured",
                        hint   = "Set SAGIDE:Rag:SearchUrl or add SearchUrl to a server in SAGIDE:Ollama:Servers",
                    });
                    return string.Empty;
                }

                if (string.IsNullOrWhiteSpace(step.PlanningPrompt))
                {
                    _logger.LogWarning(
                        "llm_queries step '{Name}': no planning_prompt defined — skipped", step.Name);
                    tracer.Write($"step-{step.Name}-skipped", new { reason = "no_planning_prompt" });
                    return string.Empty;
                }

                // Render planning prompt with current vars
                var planningPromptText = ResolveSimpleTemplate(step.PlanningPrompt, vars);

                tracer.WriteText($"step-{step.Name}-planning-prompt", planningPromptText);

                // Determine model: step.Model → orchestrator model → empty (orchestrator default)
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

                tracer.WriteText($"step-{step.Name}-llm-raw-response", planResult);

                if (queries.Count == 0)
                {
                    _logger.LogWarning(
                        "llm_queries '{Name}': LLM returned no parseable queries. Raw: {Raw}",
                        step.Name, planResult.Length > 300 ? planResult[..300] + "..." : planResult);
                    tracer.Write($"step-{step.Name}-skipped", new
                    {
                        reason   = "no_parseable_queries",
                        llm_raw  = planResult.Length > 500 ? planResult[..500] + "..." : planResult,
                    });
                    return string.Empty;
                }

                tracer.Write($"step-{step.Name}-queries", new { count = queries.Count, queries });

                _logger.LogInformation(
                    "llm_queries '{Name}': {Count} queries generated — {List}",
                    step.Name, queries.Count, string.Join("; ", queries));

                // Execute each query via web search
                var limitStr   = ResolveSimpleTemplate(step.Limit ?? string.Empty, vars);
                var maxResults = int.TryParse(limitStr, out var lq) && lq > 0 ? lq : 5;
                var maxQueries = _config.GetValue("SAGIDE:Orchestration:LlmQueriesMaxQueries", 10);

                var sections = new List<string>();
                var qi = 0;
                foreach (var query in queries.Take(maxQueries))
                {
                    qi++;
                    _logger.LogDebug("llm_queries '{Name}': searching '{Query}'", step.Name, query);
                    var searchResult = await _search.SearchAsync(query, maxResults, ct);
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
                // ── Guard: refuse to dispatch expensive LLM calls when search data is missing ──
                // Only fires when search_results_var is EXPLICITLY set on the step.
                // Steps whose prompts reference multiple source vars (e.g. section-analyst) do NOT
                // set search_results_var and therefore bypass this guard.
                var searchVarName      = step.SearchResultsVar ?? string.Empty;
                var searchDataPreCheck = !string.IsNullOrEmpty(searchVarName) && vars.TryGetValue(searchVarName, out var srPre)
                    ? srPre?.ToString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrEmpty(searchVarName) && searchDataPreCheck.Length == 0)
                {
                    _logger.LogError(
                        "llm_per_section '{Name}': search data var '{Var}' is empty — " +
                        "aborting to prevent hallucination. Ensure search steps ran successfully.",
                        step.Name, searchVarName);
                    tracer.Write($"step-{step.Name}-aborted", new
                    {
                        reason   = "empty_search_data",
                        var_name = searchVarName,
                        hint     = "Check that skill steps ran (SkillRegistry loaded?) and search service is reachable",
                    });
                    return string.Empty;
                }

                // Resolve model spec early — needed by both planning and analysis phases.
                var modelSpecRaw = string.IsNullOrWhiteSpace(step.Model)
                    ? (prompt.ModelPreference?.Orchestrator ?? string.Empty)
                    : step.Model;
                var modelSpec = ResolveSimpleTemplate(modelSpecRaw, vars);
                var (secProvider, secModelId, secEndpoint) = ParseModelSpec(modelSpec);

                List<string> sectionNames;
                var maxSectionsStr = ResolveSimpleTemplate(step.MaxSections ?? "5", vars);
                var maxSections = int.TryParse(maxSectionsStr, out var ms) && ms > 0 ? ms : 5;

                // ── Implicit single-section mode ──────────────────────────────
                // When section_title is set and max_sections is 1, skip the planning LLM
                // call entirely — the section name is known at definition time.  This saves
                // a full LLM round-trip for every single-section skill (~15 skills today).
                var resolvedSectionTitle = !string.IsNullOrWhiteSpace(step.SectionTitle)
                    ? ResolveSimpleTemplate(step.SectionTitle, vars) : null;

                if (!string.IsNullOrWhiteSpace(resolvedSectionTitle) && maxSections == 1)
                {
                    sectionNames = [resolvedSectionTitle];
                    _logger.LogInformation(
                        "llm_per_section '{Name}': implicit single-section mode — '{Title}'",
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
                    // ── Phase 1: ask LLM which sections to write ──────────────────
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
                    sectionNames = ParseJsonStringArray(planResult);

                    tracer.WriteText($"step-{step.Name}-planning-llm-raw", planResult);

                    sectionNames = sectionNames.Take(maxSections).ToList();

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

                // ── Phase 2: dispatch all section analysis tasks in parallel ──
                if (!string.IsNullOrEmpty(searchVarName))
                {
                    tracer.Write($"step-{step.Name}-search-data-summary", new
                    {
                        var_name = searchVarName,
                        chars    = searchDataPreCheck.Length,
                        is_empty = false, // empty case already aborted above
                    });
                }

                var sectionTasks = sectionNames.Select(async sectionName =>
                {
                    var sectionVars = new Dictionary<string, object>(vars, StringComparer.OrdinalIgnoreCase)
                    {
                        ["section_name"]   = sectionName,
                        ["search_results"] = searchDataPreCheck,
                        // Inject merged skill parameters so {{parameters.X}} refs in
                        // section_analysis_prompt resolve correctly at execution time.
                        ["parameters"]     = step.Parameters,
                        // Shared prompt blocks from prompt-blocks.yaml
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
                    tracer.WriteText($"step-{step.Name}-section-{sectionName}-result", result);
                    return (sectionName, result);
                });

                var sectionResults = await Task.WhenAll(sectionTasks);

                // Reassemble in planning order; skip empty results
                return string.Join("\n\n", sectionNames
                    .Select(name => sectionResults.FirstOrDefault(r => r.sectionName == name))
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
                _logger.LogInformation(
                    "llm step '{Name}': task {TaskId} submitted (model: {Model}@{Host})",
                    step.Name, llmTaskId, modelId, endpoint ?? "auto");

                var llmResult = await WaitForTaskAsync(llmTaskId, ct);
                tracer.WriteText($"step-{step.Name}-result", llmResult);
                return llmResult;
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

    /// <summary>
    /// Dispatches subtasks in dependency order using a wave-based topological sort.
    /// Subtasks without <c>depends_on</c> all run in the first wave (identical to pre-DAG behaviour).
    /// After each wave the results are injected into <paramref name="vars"/> as
    /// <c>{name}_result</c> so subsequent waves can reference them in their prompt templates.
    /// </summary>
    private async Task<Dictionary<string, string>> DispatchSubtasksAsync(
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        string instanceId,
        RunTracer tracer,
        CancellationToken ct)
    {
        if (prompt.Subtasks.Count == 0)
            return [];

        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        var done    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = prompt.Subtasks.ToList();

        while (pending.Count > 0)
        {
            // Collect all subtasks whose dependencies are fully satisfied
            var wave = pending.Where(st => st.DependsOn.All(d => done.Contains(d))).ToList();

            if (wave.Count == 0)
            {
                _logger.LogError(
                    "SubtaskCoordinator [{Id}]: unresolvable depends_on in [{Names}] — possible cycle or missing name",
                    instanceId, string.Join(", ", pending.Select(s => s.Name)));
                break;
            }

            foreach (var st in wave) pending.Remove(st);

            // Submit the wave in parallel
            var submitted = (await Task.WhenAll(
                    wave.Select(st => SubmitSubtaskAsync(st, prompt, vars, instanceId, tracer, ct))))
                .Where(s => s is not null).Select(s => s!).ToList();

            // Wait for all wave tasks to reach a terminal state
            await Task.WhenAll(submitted.Select(async s =>
            {
                var output = await WaitForTaskAsync(s.TaskId, ct);
                lock (results) results[s.Name] = output;
                tracer.WriteText($"subtask-{s.Name}-result", output);
            }));

            // One retry pass per wave for server-side failures
            if (_healthMonitor is not null)
                await RetryFailedSubtasksAsync(submitted, wave, prompt, vars, instanceId, tracer, results, ct);

            // Inject wave results into vars so subsequent dependent waves can use them.
            // Use OutputVar (from skill's output_var param) when set; fall back to {Name}_result.
            foreach (var st in wave)
            {
                if (results.TryGetValue(st.Name, out var r))
                {
                    var varKey = !string.IsNullOrEmpty(st.OutputVar) ? st.OutputVar : $"{st.Name}_result";
                    vars[varKey] = r;
                }
                done.Add(st.Name);
            }
        }

        return results;
    }

    /// <summary>
    /// Retries any subtasks in <paramref name="wave"/> that failed with a retryable server error,
    /// routing each to a healthy alternative endpoint. Updates <paramref name="results"/> in place.
    /// </summary>
    private async Task RetryFailedSubtasksAsync(
        List<SubtaskSubmission> submitted,
        List<PromptSubtask> wave,
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        string instanceId,
        RunTracer tracer,
        Dictionary<string, string> results,
        CancellationToken ct)
    {
        var retryTasks = submitted
            .Where(s => string.IsNullOrEmpty(results.GetValueOrDefault(s.Name)))
            .Select(async s =>
            {
                var status = _taskSubmission.GetTaskStatus(s.TaskId);
                if (status?.Status != AgentTaskStatus.Failed) return;
                if (!IsRetryableServerError(status.StatusMessage ?? string.Empty)) return;

                var alternatives = _allOllamaUrls
                    .Where(u => !string.Equals(u, s.Endpoint, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var fallback = _healthMonitor!.TryGetBestHost(s.ModelId, string.Empty, alternatives);

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

                var subtask = wave.First(st => st.Name == s.Name);
                var retry   = await SubmitSubtaskAsync(subtask, prompt, vars, instanceId, tracer, ct, endpointOverride: fallback);
                if (retry is null) return;

                var retryOutput = await WaitForTaskAsync(retry.TaskId, ct);
                if (!string.IsNullOrEmpty(retryOutput))
                    lock (results) results[s.Name] = retryOutput;
            })
            .ToList();

        if (retryTasks.Count > 0)
            await Task.WhenAll(retryTasks);
    }

    private async Task<SubtaskSubmission?> SubmitSubtaskAsync(
        PromptSubtask subtask,
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        string instanceId,
        RunTracer tracer,
        CancellationToken ct,
        string? endpointOverride = null)
    {
        using var subtaskActivity = _activitySource.StartActivity($"subtask.{subtask.Name}");
        subtaskActivity?.SetTag("subtask.name", subtask.Name);
        subtaskActivity?.SetTag("subtask.depends_on", string.Join(",", subtask.DependsOn));
        subtaskActivity?.SetTag("prompt.domain", prompt.Domain);
        subtaskActivity?.SetTag("prompt.name", prompt.Name);
        try
        {
            // Resolve model spec — may be a Scriban template
            var modelSpec = ResolveSimpleTemplate(subtask.Model ?? string.Empty, vars);
            var (provider, modelId, parsedEndpoint) = ParseModelSpec(modelSpec);
            subtaskActivity?.SetTag("subtask.model", modelId);
            subtaskActivity?.SetTag("subtask.provider", provider.ToString());

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
                tracer.Write($"subtask-{subtask.Name}-skipped", new
                {
                    reason           = "empty_prompt",
                    template_is_null = subtask.PromptTemplate is null,
                    template_chars   = subtask.PromptTemplate?.Length ?? 0,
                    output_var       = subtask.OutputVar,
                    model            = modelId,
                });
                return null;
            }

            tracer.Write($"subtask-{subtask.Name}-dispatch", new
            {
                model       = modelId,
                endpoint    = endpoint,
                provider    = provider.ToString(),
                depends_on  = subtask.DependsOn,
                prompt_chars = renderedPrompt.Length,
            });
            tracer.WriteText($"subtask-{subtask.Name}-prompt", renderedPrompt);

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
            tracer.Write($"subtask-{subtask.Name}-error", new
            {
                error      = ex.Message,
                type       = ex.GetType().Name,
                output_var = subtask.OutputVar,
                model      = subtask.Model,
            });
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
        {
            // No InputVars filter — pass everything. But still inject skill parameters
            // (set by WorkflowExpander) so {{parameters.X}} refs in the PromptTemplate resolve.
            if (subtask.Parameters.Count == 0)
                return allVars;

            // Clone allVars rather than mutating the shared dict.
            return new Dictionary<string, object>(allVars, StringComparer.OrdinalIgnoreCase)
            {
                ["parameters"] = subtask.Parameters,
            };
        }

        // Seed with auto-computed date vars.
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"]      = allVars.GetValueOrDefault("date",      string.Empty),
            ["datestamp"] = allVars.GetValueOrDefault("datestamp", string.Empty),
            ["datetime"]  = allVars.GetValueOrDefault("datetime",  string.Empty),
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

        // Inject skill parameters so templates can reference {{parameters.X}}
        if (subtask.Parameters.Count > 0)
            result["parameters"] = subtask.Parameters;

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
    // ── Phase 4: Capability-based routing ───────────────────────────────────────

    /// <summary>
    /// Resolves a capability name (e.g. "deep_reasoning+long_context_understanding") to a
    /// concrete model spec via SAGIDE:Routing:Capabilities config. Returns null if not configured.
    /// </summary>
    private string? ResolveCapability(string capabilityName) =>
        _config[$"SAGIDE:Routing:Capabilities:{capabilityName}"];

    private (ModelProvider Provider, string ModelId, string? Endpoint) ParseModelSpec(string spec)
    {
        string? endpoint = null;

        // Phase 4: resolve capability: prefix before any other processing
        if (spec.StartsWith("capability:", StringComparison.OrdinalIgnoreCase))
        {
            var capName = spec[11..].Trim();
            var resolved = ResolveCapability(capName);
            if (!string.IsNullOrWhiteSpace(resolved))
                spec = resolved;
            else
                _logger.LogWarning("Capability '{Cap}' not found in SAGIDE:Routing:Capabilities — using spec as-is", capName);
        }

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
            return str.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Where(s => !s.Equals("symbol", StringComparison.OrdinalIgnoreCase));
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
    Dictionary<string, string> SubtaskResults,
    string? TraceFolderPath = null);
