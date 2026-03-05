using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Phase 5: Expands the high-level <c>objects:</c> + <c>workflow:</c> syntax into the
/// flat <c>data_collection.steps[]</c> and <c>subtasks[]</c> structures that
/// <see cref="SubtaskCoordinator"/> already executes.
///
/// This is a pure pre-pass: it mutates <see cref="PromptDefinition"/> in place and
/// returns. All existing execution logic runs unchanged on the expanded result.
/// No-op when both <c>Objects</c> and <c>Workflow</c> are empty (backward compatibility).
/// </summary>
public static class WorkflowExpander
{
    // Methods that map to data_collection steps (run before subtasks, produce vars)
    private static readonly HashSet<string> _collectMethods =
        new(StringComparer.OrdinalIgnoreCase) { "collect", "read", "search", "fetch", "load" };

    // Methods that map to subtasks (dispatched to LLMs, produce *_result vars)
    private static readonly HashSet<string> _analyzeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "analyze", "analyse", "validate", "compile", "score", "evaluate", "assemble", "write" };

    /// <summary>
    /// Expands objects/workflow into flat data_collection.steps + subtasks.
    /// Mutates <paramref name="prompt"/> in place. No-op if no objects or workflow defined.
    /// </summary>
    public static void Expand(PromptDefinition prompt, SkillRegistry skills, ILogger logger)
    {
        if (prompt.Objects.Count == 0 && prompt.Workflow.Count == 0) return;

        logger.LogDebug(
            "WorkflowExpander: expanding {ObjCount} objects, {CallCount} workflow calls for {Domain}/{Name}",
            prompt.Objects.Count, prompt.Workflow.Count, prompt.Domain, prompt.Name);

        // Build object → skill lookup
        var objectSkills = new Dictionary<string, (PromptObject Obj, SkillDefinition? Skill)>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in prompt.Objects)
        {
            var skill = skills.Resolve(obj.Skill);
            if (skill is null)
                logger.LogWarning("WorkflowExpander: skill '{Skill}' not found for object '{Name}'", obj.Skill, obj.Name);
            objectSkills[obj.Name] = (obj, skill);
        }

        // Track which workflow calls depend on which earlier calls (for subtask depends_on)
        var completedCalls       = new List<string>();
        var dataSteps            = new List<PromptDataCollectionStep>();
        var subtasks             = new List<PromptSubtask>();
        var prevWaveSubtaskNames = new List<string>(); // persists across workflow entries for sequential depends_on

        foreach (var wfCall in prompt.Workflow)
        {
            // Normalise: single call or parallel block
            var calls = wfCall.Parallel.Count > 0
                ? wfCall.Parallel
                : wfCall.Call is not null ? [wfCall.Call] : [];

            var thisWaveSubtaskNames = new List<string>(); // subtasks produced in this wave

            foreach (var callStr in calls)
            {
                var dot = callStr.IndexOf('.');
                if (dot < 0)
                {
                    logger.LogWarning("WorkflowExpander: invalid call '{Call}' — must be 'object.method'", callStr);
                    continue;
                }

                var objName    = callStr[..dot].Trim();
                var methodName = callStr[(dot + 1)..].Trim();

                if (!objectSkills.TryGetValue(objName, out var entry))
                {
                    logger.LogWarning("WorkflowExpander: object '{Obj}' not declared in objects:", objName);
                    continue;
                }

                var (obj, skill) = entry;

                // Merge args: skill defaults ← object args ← call args
                var mergedArgs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (skill is not null)
                    foreach (var kv in skill.Parameters) mergedArgs[kv.Key] = kv.Value;
                foreach (var kv in obj.Args)      mergedArgs[kv.Key] = kv.Value;
                foreach (var kv in wfCall.Args)   mergedArgs[kv.Key] = kv.Value;

                var stepName = $"{objName}.{methodName}";

                if (_collectMethods.Contains(methodName))
                {
                    // Map to data_collection skill: step
                    var outputVar = mergedArgs.TryGetValue("output_var", out var ov)
                        ? ov.ToString()!
                        : $"{objName}_results";

                    dataSteps.Add(new PromptDataCollectionStep
                    {
                        Name           = stepName,
                        Type           = "skill",
                        Skill          = obj.Skill,
                        OutputVar      = outputVar,
                        OptionalOutput = obj.Optional,
                        Parameters = mergedArgs.ToDictionary(
                            kv => kv.Key,
                            kv => kv.Value,
                            StringComparer.OrdinalIgnoreCase),
                    });

                    completedCalls.Add(stepName);
                }
                else if (_analyzeMethods.Contains(methodName))
                {
                    // Map to subtask; depends_on all prior subtask wave names
                    var modelSpec = mergedArgs.TryGetValue("model", out var m)
                        ? m.ToString()!
                        : skill?.CapabilityRequirements.Keys.FirstOrDefault() is { Length: > 0 } capSlot
                            ? $"capability:{capSlot}"
                            : prompt.ModelPreference?.Orchestrator ?? string.Empty;

                    // Build input_vars from object's merged args or previous object outputs
                    var inputVars = new List<string>();
                    if (mergedArgs.TryGetValue("results_vars", out var rvObj) && rvObj is List<object> rvList)
                        inputVars.AddRange(rvList.Select(o => o.ToString()!));
                    else if (mergedArgs.TryGetValue("input_vars", out var ivObj) && ivObj is List<object> ivList)
                        inputVars.AddRange(ivList.Select(o => o.ToString()!));

                    // Build prompt template from skill implementation (use first llm_per_section or llm_queries step)
                    var implStep = skill?.Implementation.FirstOrDefault();
                    var promptTpl = implStep?.SectionAnalysisPrompt ?? implStep?.PlanningPrompt
                        ?? $"Perform {methodName} analysis for {{{{idea}}}} using the collected evidence.";

                    var outputVar = mergedArgs.TryGetValue("output_var", out var ov)
                        ? ov.ToString()
                        : null;

                    subtasks.Add(new PromptSubtask
                    {
                        Name           = stepName,
                        Model          = modelSpec,
                        InputVars      = inputVars,
                        DependsOn      = prevWaveSubtaskNames.Count > 0 ? [.. prevWaveSubtaskNames] : [],
                        PromptTemplate = promptTpl,
                        OutputVar      = outputVar,
                        Parameters     = new Dictionary<string, object>(mergedArgs, StringComparer.OrdinalIgnoreCase),
                    });

                    thisWaveSubtaskNames.Add(stepName);
                    completedCalls.Add(stepName);
                }
                else
                {
                    logger.LogWarning(
                        "WorkflowExpander: unrecognised method '{Method}' on object '{Obj}' — skipped",
                        methodName, objName);
                }
            }

            // Subtasks produced in this wave become the dependency for the next sequential wave
            if (thisWaveSubtaskNames.Count > 0)
                prevWaveSubtaskNames = thisWaveSubtaskNames;
        }

        // Merge into prompt: prepend expanded steps/subtasks (don't overwrite any explicitly declared ones).
        // Guard against accumulation: PromptDefinition is shared across runs (cached by PromptRegistry),
        // so this method must be idempotent — skip any step/subtask already present by name.
        prompt.DataCollection ??= new PromptDataCollection();

        var existingStepNames = new HashSet<string>(
            prompt.DataCollection.Steps.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var newDataSteps = dataSteps.Where(s => !existingStepNames.Contains(s.Name)).ToList();
        prompt.DataCollection.Steps.InsertRange(0, newDataSteps);

        var existingSubtaskNames = new HashSet<string>(
            prompt.Subtasks.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var newSubtasks = subtasks.Where(s => !existingSubtaskNames.Contains(s.Name)).ToList();
        prompt.Subtasks.InsertRange(0, newSubtasks);

        logger.LogDebug(
            "WorkflowExpander: expanded to {Steps} new data steps + {Subtasks} new subtasks ({SkippedSteps} steps / {SkippedSubtasks} subtasks already present)",
            newDataSteps.Count, newSubtasks.Count,
            dataSteps.Count - newDataSteps.Count, subtasks.Count - newSubtasks.Count);
    }
}
