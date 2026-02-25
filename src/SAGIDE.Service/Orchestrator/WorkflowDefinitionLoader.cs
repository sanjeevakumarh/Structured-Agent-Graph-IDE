using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Loads WorkflowDefinition objects from:
///   1. Built-in YAML templates — directory configured by SAGIDE:BuiltInTemplatesPath
///      (defaults to "Orchestrator/Templates" relative to the executable; copied there by the build)
///   2. Workspace .agentide/workflows/*.yaml files (workspace-specific)
/// </summary>
public class WorkflowDefinitionLoader
{
    private readonly ILogger<WorkflowDefinitionLoader> _logger;
    private readonly IDeserializer _deserializer;
    private readonly string _builtInTemplatesDir;

    public WorkflowDefinitionLoader(ILogger<WorkflowDefinitionLoader> logger, IConfiguration configuration)
    {
        _logger = logger;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // Path can be absolute or relative to the executable directory
        var configuredPath = configuration["SAGIDE:BuiltInTemplatesPath"] ?? "Orchestrator/Templates";
        _builtInTemplatesDir = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    /// <summary>Maps YAML agent names to AgentType enum values.</summary>
    public static AgentType MapAgentName(string name) => name.ToLowerInvariant() switch
    {
        "coder" or "codegenerator" or "generator" => AgentType.Refactoring,
        "reviewer" or "codereviewer" or "codereview" => AgentType.CodeReview,
        "tester" or "testgeneration" or "unittester" => AgentType.TestGeneration,
        "security" or "securityreview" or "securityreviewer" => AgentType.SecurityReview,
        "documenter" or "documentation" or "documentor" => AgentType.Documentation,
        "debug" or "debugger" => AgentType.Debug,
        "refactoring" or "refactor" => AgentType.Refactoring,
        _ => AgentType.CodeReview
    };

    /// <summary>Loads all built-in workflow definitions from the configured templates directory.</summary>
    public List<WorkflowDefinition> GetBuiltInDefinitions()
    {
        var result = new List<WorkflowDefinition>();

        if (!Directory.Exists(_builtInTemplatesDir))
        {
            _logger.LogWarning(
                "Built-in templates directory not found: {Dir}. No built-in workflows will be available.",
                _builtInTemplatesDir);
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(_builtInTemplatesDir, "*.yaml")
                    .Concat(Directory.EnumerateFiles(_builtInTemplatesDir, "*.yml")))
        {
            try
            {
                var yaml = File.ReadAllText(file);
                var id   = Path.GetFileNameWithoutExtension(file);
                var def  = ParseYaml(yaml, id);
                def.IsBuiltIn = true;
                result.Add(def);
                _logger.LogDebug("Loaded built-in workflow '{Name}' from {File}", def.Name, file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse built-in workflow template '{File}'", file);
            }
        }

        return result;
    }

    /// <summary>Scans workspacePath/.agentide/workflows/*.yaml and parses each file.</summary>
    public List<WorkflowDefinition> LoadFromWorkspace(string workspacePath)
    {
        var result = new List<WorkflowDefinition>();
        var dir = Path.Combine(workspacePath, ".agentide", "workflows");
        if (!Directory.Exists(dir))
            return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.yaml")
                    .Concat(Directory.EnumerateFiles(dir, "*.yml")))
        {
            try
            {
                var yaml = File.ReadAllText(file);
                var id   = Path.GetFileNameWithoutExtension(file);
                var def  = ParseYaml(yaml, id);
                def.IsBuiltIn = false;
                result.Add(def);
                _logger.LogDebug("Loaded workflow '{Name}' from {File}", def.Name, file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse workflow file '{File}'", file);
            }
        }
        return result;
    }

    /// <summary>Parses a YAML string into a WorkflowDefinition.</summary>
    public WorkflowDefinition ParseYaml(string yaml, string fallbackId)
    {
        var raw = _deserializer.Deserialize<YamlWorkflowDefinition>(yaml);

        var def = new WorkflowDefinition
        {
            Id          = raw.Id ?? fallbackId,
            Name        = raw.Name ?? fallbackId,
            Description = raw.Description ?? string.Empty,
        };

        if (raw.ConvergencePolicy is { } cp)
        {
            def.ConvergencePolicy = new ConvergencePolicy
            {
                MaxIterations          = cp.MaxIterations,
                EscalationTarget       = cp.EscalationTarget,
                PartialRetryScope      = cp.PartialRetryScope,
                ConvergenceHintMemory  = cp.ConvergenceHintMemory,
                TimeoutPerIterationSec = cp.TimeoutPerIterationSec,
                ContradictionDetection = cp.ContradictionDetection,
            };
        }

        if (raw.Parameters is not null)
        {
            foreach (var p in raw.Parameters)
            {
                def.Parameters.Add(new WorkflowParameter
                {
                    Name    = p.Name ?? string.Empty,
                    Type    = p.Type ?? "string",
                    Default = p.Default,
                });
            }
        }

        if (raw.Steps is not null)
        {
            foreach (var s in raw.Steps)
            {
                var step = new WorkflowStepDef
                {
                    Id                = s.Id ?? string.Empty,
                    Type              = s.Type ?? "agent",
                    Agent             = s.Agent,
                    DependsOn         = s.DependsOn ?? [],
                    Prompt            = s.Prompt,
                    ModelId           = s.ModelId,
                    ModelProvider     = s.ModelProvider,
                    Next              = s.Next,
                    MaxIterations     = s.MaxIterations > 0 ? s.MaxIterations : 1,
                    Command           = s.Command,
                    WorkingDir        = s.WorkingDir,
                    ExitCodePolicy    = s.ExitCodePolicy ?? "FAIL_ON_NONZERO",
                    TimeoutSec        = s.TimeoutSec,
                    ConstraintExpr    = s.ConstraintExpr,
                    OnConstraintFail  = s.OnConstraintFail ?? "fail",
                    SlaHours          = s.SlaHours,
                    TimeoutAction     = s.TimeoutAction ?? "cancel",
                    ApprovalPrompt    = s.ApprovalPrompt,
                    ContextVarName    = s.ContextVarName,
                    SourceSteps       = s.SourceSteps ?? [],
                    ShadowBranch      = s.ShadowBranch ?? "HEAD",
                    ShadowAction      = s.ShadowAction ?? "promote",
                };

                if (s.Branches is { Count: > 0 })
                {
                    step.Router = new RouterConfig
                    {
                        Branches = s.Branches.Select(b => new RouterBranch
                        {
                            Condition = b.Condition ?? string.Empty,
                            Target    = b.Target ?? string.Empty,
                        }).ToList()
                    };
                }

                def.Steps.Add(step);
            }
        }

        var validationErrors = ValidateWorkflow(def);
        if (validationErrors.Count > 0)
            throw new InvalidOperationException(
                $"Workflow '{def.Name}' has {validationErrors.Count} validation error(s):\n  - " +
                string.Join("\n  - ", validationErrors));

        return def;
    }

    /// <summary>
    /// Validates a parsed WorkflowDefinition for:
    ///   1. Unknown step IDs referenced in depends_on, next:, and router branch targets
    ///   2. Cycles in the depends_on graph (next: back-edges are intentional and excluded)
    /// Returns a list of human-readable error strings (empty list = valid).
    /// </summary>
    private static List<string> ValidateWorkflow(WorkflowDefinition def)
    {
        var errors = new List<string>();
        var stepIds = def.Steps.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── 1. Reference validation ────────────────────────────────────────────
        foreach (var step in def.Steps)
        {
            foreach (var dep in step.DependsOn)
                if (!stepIds.Contains(dep))
                    errors.Add($"Step '{step.Id}': depends_on references unknown step '{dep}'.");

            if (step.Next is not null && !stepIds.Contains(step.Next))
                errors.Add($"Step '{step.Id}': next: references unknown step '{step.Next}'.");

            if (step.Router is not null)
                foreach (var branch in step.Router.Branches)
                    if (!string.IsNullOrEmpty(branch.Target) && !stepIds.Contains(branch.Target))
                        errors.Add(
                            $"Step '{step.Id}': router branch (condition '{branch.Condition}') " +
                            $"targets unknown step '{branch.Target}'.");

            if (step.Type == "tool" && string.IsNullOrWhiteSpace(step.Command))
                errors.Add($"Step '{step.Id}' (type: tool) must have a 'command' field.");

            if (step.Type == "constraint" && string.IsNullOrWhiteSpace(step.ConstraintExpr))
                errors.Add($"Step '{step.Id}' (type: constraint) must have a 'constraint_expr' field.");

            if (step.Type == "context_retrieval")
            {
                if (string.IsNullOrWhiteSpace(step.ContextVarName))
                    errors.Add($"Step '{step.Id}' (type: context_retrieval) must have a 'context_var_name' field.");
                if (step.SourceSteps.Count == 0)
                    errors.Add($"Step '{step.Id}' (type: context_retrieval) must have at least one 'source_steps' entry.");
                foreach (var src in step.SourceSteps)
                    if (!stepIds.Contains(src))
                        errors.Add($"Step '{step.Id}': source_steps references unknown step '{src}'.");
            }
        }

        // Workflows with back-edges (next:) should declare a convergence_policy.
        var hasLoop = def.Steps.Any(s => s.Next is not null);
        if (hasLoop && def.ConvergencePolicy is null)
            errors.Add(
                "Workflow has feedback loop steps (next:) but no 'convergence_policy' is declared. " +
                "Add a 'convergence_policy' block with at least 'max_iterations' and 'escalation_target'.");

        // Validate convergence_policy fields if present
        if (def.ConvergencePolicy is { } policy)
        {
            var validTargets = new[] { "HUMAN_APPROVAL", "DLQ", "CANCEL" };
            if (!validTargets.Contains(policy.EscalationTarget, StringComparer.OrdinalIgnoreCase))
                errors.Add(
                    $"convergence_policy.escalation_target '{policy.EscalationTarget}' is invalid. " +
                    $"Valid values: {string.Join(", ", validTargets)}.");

            var validScopes = new[] { "FAILING_NODES_ONLY", "FROM_CODEGEN", "FULL_WORKFLOW" };
            if (!validScopes.Contains(policy.PartialRetryScope, StringComparer.OrdinalIgnoreCase))
                errors.Add(
                    $"convergence_policy.partial_retry_scope '{policy.PartialRetryScope}' is invalid. " +
                    $"Valid values: {string.Join(", ", validScopes)}.");
        }

        // ── 2. Cycle detection in depends_on DAG ──────────────────────────────
        // 3-color DFS: 0=unvisited, 1=in-stack (gray = currently being processed), 2=done (black)
        // Note: next: back-edges are intentional feedback loops and are NOT checked here.
        var color = def.Steps.ToDictionary(
            s => s.Id,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);

        void Dfs(string id)
        {
            if (!color.ContainsKey(id)) return;
            color[id] = 1;

            var stepDeps = def.Steps
                .FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?.DependsOn ?? [];

            foreach (var dep in stepDeps)
            {
                if (!color.TryGetValue(dep, out var depColor)) continue;
                if (depColor == 1)
                    errors.Add(
                        $"Circular dependency: step '{dep}' ← step '{id}' creates a cycle in depends_on. " +
                        "Tip: use next: for intentional feedback loops, not depends_on.");
                else if (depColor == 0)
                    Dfs(dep);
            }
            color[id] = 2;
        }

        foreach (var step in def.Steps)
            if (color.TryGetValue(step.Id, out var c) && c == 0)
                Dfs(step.Id);

        return errors;
    }

    // ── YAML raw deserialization POCOs (snake_case fields via YamlDotNet) ──────

    private class YamlWorkflowDefinition
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<YamlParameter>? Parameters { get; set; }
        public List<YamlStep>? Steps { get; set; }
        public YamlConvergencePolicy? ConvergencePolicy { get; set; }
    }

    private class YamlConvergencePolicy
    {
        public int MaxIterations { get; set; } = 3;
        public string EscalationTarget { get; set; } = "CANCEL";
        public string PartialRetryScope { get; set; } = "FAILING_NODES_ONLY";
        public bool ConvergenceHintMemory { get; set; } = false;
        public int TimeoutPerIterationSec { get; set; } = 0;
        public bool ContradictionDetection { get; set; } = true;
    }

    private class YamlParameter
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? Default { get; set; }
    }

    private class YamlStep
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Agent { get; set; }
        public List<string>? DependsOn { get; set; }
        public string? Prompt { get; set; }
        public string? ModelId { get; set; }
        public string? ModelProvider { get; set; }
        public string? Next { get; set; }
        public int MaxIterations { get; set; } = 1;
        public List<YamlBranch>? Branches { get; set; }
        // Tool step fields
        public string? Command { get; set; }
        public string? WorkingDir { get; set; }
        public string? ExitCodePolicy { get; set; }
        public int TimeoutSec { get; set; } = 0;
        // Constraint step fields
        public string? ConstraintExpr { get; set; }
        public string? OnConstraintFail { get; set; }
        // Context retrieval step fields
        public string? ContextVarName { get; set; }
        public List<string>? SourceSteps { get; set; }
        // Human approval step fields
        public int SlaHours { get; set; } = 0;
        public string? TimeoutAction { get; set; }
        public string? ApprovalPrompt { get; set; }
        // Shadow workspace step fields ()
        public string? ShadowBranch { get; set; }
        public string? ShadowAction { get; set; }
    }

    private class YamlBranch
    {
        public string? Condition { get; set; }
        public string? Target { get; set; }
    }
}

// ── Built-in YAML templates have been moved to Orchestrator/Templates/*.yaml ──
// They are copied to the output directory by the build (CopyToOutputDirectory=Always)
// and loaded at startup from the path configured in SAGIDE:BuiltInTemplatesPath.
// Users can add or modify templates by editing the YAML files next to the executable.

