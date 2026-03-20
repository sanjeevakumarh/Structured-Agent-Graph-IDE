using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Contracts;
using SAGIDE.Core.Models;
using SAGIDE.Observability;
using SAGIDE.Memory;
using SAGIDE.Service.Providers;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Orchestrates multi-model prompt execution:
///   1. Runs data_collection steps (file reads, HTTP fetches, LLM queries) to gather context.
///   2. Dispatches subtasks in parallel to the models specified in the prompt YAML.
///   3. Waits for all subtask results, then renders the synthesis prompt.
///   4. Writes the synthesized output to the configured destination.
///
/// This file contains the core fields, constructor, and <see cref="RunAsync"/> entry point.
/// The implementation is split across focused partial-class files:
///
/// <list type="bullet">
///   <item><see cref="SubtaskCoordinator.DataCollector"/> — data_collection step execution</item>
///   <item><see cref="SubtaskCoordinator.SkillExpander"/> — skill reference expansion + validation</item>
///   <item><see cref="SubtaskCoordinator.Dispatcher"/>    — subtask dispatch, retry, wait</item>
///   <item><see cref="SubtaskCoordinator.Synthesizer"/>   — synthesis, output writing, model spec parsing</item>
///   <item><see cref="SubtaskCoordinator.TemplateHelpers"/>— variable context, path, slug, collection helpers</item>
/// </list>
/// </summary>
public sealed partial class SubtaskCoordinator : ISubtaskCoordinator
{
    // ── ActivitySource — uses the unified Orchestrator source so SubtaskCoordinator
    // spans appear alongside AgentOrchestrator spans in the same module view.
    // The static field alias keeps all partial files using the same name without
    // each needing an individual using/field declaration.
    private static System.Diagnostics.ActivitySource _activitySource
        => SagideActivitySource.Orchestrator;

    // ── Instance dependencies ──────────────────────────────────────────────────
    private readonly ITaskSubmissionService _taskSubmission;
    private readonly WebFetcher _fetcher;
    private readonly WebSearchAdapter _search;
    private readonly IConfiguration _config;
    private readonly ILogger<SubtaskCoordinator> _logger;
    private readonly OllamaHostHealthMonitor? _healthMonitor;
    private readonly ISkillRegistry? _skillRegistry;
    private readonly IReadOnlyList<string> _allOllamaUrls;
    private readonly RagPipeline? _ragPipeline;
    private readonly QualityScorer? _qualityScorer;
    private readonly ITaskRepository? _taskRepository;

    // ── Per-run mutable state (cleared at the start of each RunAsync call) ─────
    // Maps expanded step name → its source skill definition (used for output validation).
    private readonly Dictionary<string, SkillDefinition> _expandedSkillMap
        = new(StringComparer.OrdinalIgnoreCase);

    // Maps the LAST expanded step of a multi-step skill → parent output_var alias.
    private readonly Dictionary<string, string> _parentOutputVarAliases
        = new(StringComparer.OrdinalIgnoreCase);

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
        ISkillRegistry? skillRegistry = null,
        RagPipeline? ragPipeline = null,
        QualityScorer? qualityScorer = null,
        ITaskRepository? taskRepository = null)
    {
        _taskSubmission = taskSubmission;
        _fetcher        = fetcher;
        _search         = search;
        _config         = config;
        _logger         = logger;
        _healthMonitor  = healthMonitor;
        _skillRegistry  = skillRegistry;
        _ragPipeline    = ragPipeline;
        _qualityScorer  = qualityScorer;
        _taskRepository = taskRepository;
        _allOllamaUrls  = config.GetSection("SAGIDE:Ollama:Servers")
            .GetChildren()
            .Select(s => s["BaseUrl"]?.TrimEnd('/') ?? string.Empty)
            .Where(u => u.Length > 0)
            .ToList();
    }

    // ── ISubtaskCoordinator (interface — thin wrapper, discards rich result) ─────

    async Task ISubtaskCoordinator.RunAsync(
        PromptDefinition prompt,
        Dictionary<string, string>? variableOverrides,
        CancellationToken ct)
        => await RunAsync(prompt, variableOverrides, ct);

    // ── Main entry point ─────────────────────────────────────────────────────────

    public async Task<SubtaskRunResult> RunAsync(
        PromptDefinition prompt,
        Dictionary<string, string>? variableOverrides = null,
        CancellationToken ct = default)
    {
        var instanceId = Guid.NewGuid().ToString("N")[..12];
        _logger.LogInformation(
            "SubtaskCoordinator [{Id}] starting for {Domain}/{Name} ({SubtaskCount} subtasks)",
            instanceId, prompt.Domain, prompt.Name, prompt.Subtasks.Count);

        // Expand objects:/workflow: into flat data_collection + subtasks (no-op when absent)
        if ((prompt.Objects.Count > 0 || prompt.Workflow.Count > 0) && _skillRegistry is not null)
            WorkflowExpander.Expand(prompt, _skillRegistry, _logger);

        // Clear per-run state
        _expandedSkillMap.Clear();
        _parentOutputVarAliases.Clear();

        // 1. Build variable context
        var vars = BuildVarContext(prompt, variableOverrides);

        // Set up per-run tracer
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

        // 2. Data collection
        await ExecuteDataCollectionAsync(prompt, vars, tracer, ct);

        // 2b. Guard against empty required outputs
        (var missingRequired, var missingOptional) = prompt.Subtasks.Count > 0
            ? ClassifyEmptyVars(prompt, vars)
            : ([], []);

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
                "Run aborted: {Count} required data-collection var(s) are empty: {Vars}",
                missingRequired.Count, varList);
            tracer.Write("data-collection-aborted", new
            {
                reason       = "empty_required_vars",
                missing_vars = missingRequired.Select(m => new { step = m.StepName, var_name = m.VarName }).ToList(),
                hint         = "Restart the service and verify the search service is reachable.",
            });

            var errorReport =
                $"# Run Aborted — Missing Search Data\n\n" +
                $"The following required data-collection outputs were empty:\n\n" +
                string.Join("\n", missingRequired.Select(m => $"- **{m.VarName}** (from step `{m.StepName}`)")) +
                $"\n\nNo LLM subtasks were dispatched.\n";

            if (!string.IsNullOrEmpty(prompt.Output?.Destination))
                await WriteOutputAsync(prompt.Output.Destination!, errorReport, vars);
            foreach (var extra in prompt.Outputs.Where(o => !string.IsNullOrEmpty(o.Destination)))
                await WriteOutputAsync(extra.Destination!, errorReport, vars);

            return new SubtaskRunResult(instanceId, errorReport, [], tracer.FolderPath);
        }

        // 3. Dispatch subtasks in parallel waves
        var subtaskResults = await DispatchSubtasksAsync(prompt, vars, instanceId, tracer, ct);

        // 4. Merge subtask results into vars
        var subtaskByName = prompt.Subtasks.ToDictionary(st => st.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, output) in subtaskResults)
        {
            var varKey = subtaskByName.TryGetValue(name, out var st) && !string.IsNullOrEmpty(st.OutputVar)
                ? st.OutputVar
                : $"{name}_result";
            vars[varKey] = output;
        }

        // 5. Synthesis
        var synthesized = RenderSynthesisOrAggregate(prompt, vars, subtaskResults);

        // 5b. Quality scoring (workflow mode — score the final synthesized output)
        if (_qualityScorer?.ShouldScoreWorkflow == true && !string.IsNullOrWhiteSpace(synthesized))
        {
            var promptSummary = $"{prompt.Domain}/{prompt.Name}: {prompt.Description?[..Math.Min(prompt.Description.Length, 200)]}";
            var score = await _qualityScorer.ScoreAsync(promptSummary, synthesized, $"{prompt.Domain}/{prompt.Name}", ct);
            tracer.Write("quality-score", new
            {
                label = score.Label,
                score = score.Score,
                reason = score.Reason,
                mode = "workflow"
            });
            if (score.Score >= 0)
                vars["quality_score"] = score.Score;
        }

        // 6. Primary output
        if (!string.IsNullOrEmpty(prompt.Output?.Destination))
            await WriteOutputAsync(prompt.Output.Destination, synthesized, vars);

        // 7. Additional outputs
        foreach (var extra in prompt.Outputs)
        {
            if (string.IsNullOrEmpty(extra.Destination)) continue;

            string content;
            if (!string.IsNullOrEmpty(extra.Source))
            {
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
}

public record SubtaskRunResult(
    string InstanceId,
    string SynthesizedOutput,
    Dictionary<string, string> SubtaskResults,
    string? TraceFolderPath = null);
