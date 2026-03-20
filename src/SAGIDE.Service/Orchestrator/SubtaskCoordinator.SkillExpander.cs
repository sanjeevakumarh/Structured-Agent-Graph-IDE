using System.Text.Json;
using SAGIDE.Contracts;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Skill reference expansion and output validation for <see cref="SubtaskCoordinator"/>:
/// expands skill: steps into their concrete implementation steps, builds capability maps,
/// clones and renders steps, and validates outputs against declared schemas.
/// </summary>
public sealed partial class SubtaskCoordinator
{
    // ── Phase 2: Skill reference expansion ──────────────────────────────────────

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

            var mergedParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in skill.Parameters) mergedParams[kv.Key] = PreRenderParam(kv.Value, vars, kv.Key);
            foreach (var kv in step.Parameters)  mergedParams[kv.Key] = PreRenderParam(kv.Value, vars, kv.Key);

            if (!string.IsNullOrWhiteSpace(step.OutputVar))
                mergedParams["output_var"] = step.OutputVar;

            var capabilityMap = BuildCapabilityMap(skill, vars);

            var skillVars = new Dictionary<string, object>(vars, StringComparer.OrdinalIgnoreCase)
            {
                ["parameters"] = mergedParams,
                ["capability"] = capabilityMap,
                ["blocks"]     = _skillRegistry.PromptBlocks,
            };

            foreach (var impl in skill.Implementation)
            {
                var clone = CloneStepRendered(impl, skillVars, $"{step.Name}.{impl.Name}");
                clone.Parameters = new Dictionary<string, object>(mergedParams, StringComparer.OrdinalIgnoreCase);

                if (mergedParams.TryGetValue("section_analysis_prompt", out var sapVal))
                {
                    var sapStr = sapVal?.ToString();
                    if (!string.IsNullOrWhiteSpace(sapStr))
                        clone.SectionAnalysisPrompt = sapStr;
                }
                if (mergedParams.TryGetValue("planning_prompt", out var ppVal))
                {
                    var ppStr = ppVal?.ToString();
                    if (!string.IsNullOrWhiteSpace(ppStr))
                        clone.PlanningPrompt = ppStr;
                }

                expanded.Add(clone);
                _expandedSkillMap[clone.Name] = skill;
            }

            if (skill.Implementation.Count > 1
                && !string.IsNullOrWhiteSpace(step.OutputVar)
                && expanded.Count > 0)
            {
                var lastClone = expanded[^1];
                if (lastClone.OutputVar != step.OutputVar)
                    _parentOutputVarAliases[lastClone.Name] = step.OutputVar;
            }

            _logger.LogDebug("Expanded skill '{Skill}' ({ImplCount} steps) for step '{Name}'",
                step.Skill, skill.Implementation.Count, step.Name);
        }
        return expanded;
    }

    private Dictionary<string, object> BuildCapabilityMap(SkillDefinition skill, Dictionary<string, object> vars)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, req) in skill.CapabilityRequirements)
        {
            var resolved = _config[$"SAGIDE:Routing:Capabilities:{slot}"];
            if (string.IsNullOrWhiteSpace(resolved))
            {
                var capKey = string.Join("+", req.Needs);
                resolved = _config[$"SAGIDE:Routing:Capabilities:{capKey}"];
            }

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
                    slot, string.Join("+", req.Needs), skill.Name);

            map[slot] = resolved ?? string.Empty;
        }
        return map;
    }

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
            SectionAnalysisPrompt = src.SectionAnalysisPrompt,
            SearchResultsVar      = Render(src.SearchResultsVar),
            MaxSections           = Render(src.MaxSections),
            SectionTitle          = Render(src.SectionTitle),
            PromptTemplate        = src.PromptTemplate,
            InputVars             = [..src.InputVars],
            FetchPages            = src.FetchPages,
            MaxCharsPerPage       = src.MaxCharsPerPage,
        };
    }

    private static object PreRenderParam(object value, Dictionary<string, object> vars,
        string? paramName = null)
    {
        if (value is not string s) return value;
        if (!s.Contains("{{")) return value;

        if (paramName is "section_analysis_prompt" or "planning_prompt" or "prompt_template")
            return value;

        if (s.Contains("{{ if ")   || s.Contains("{{- if ")   ||
            s.Contains("{{ else")  || s.Contains("{{- else")  ||
            s.Contains("{{ for ")  || s.Contains("{{- for ")  ||
            s.Contains("{{ while") || s.Contains("{{- while"))
            return value;

        return PromptTemplate.RenderRaw(s, vars);
    }

    // ── Phase 3: Output schema validation ───────────────────────────────────────

    private void ValidateSkillOutput(string stepName, string output, SkillDefinition skill)
    {
        if (skill.OutputsSchema.Count == 0) return;
        if (string.IsNullOrWhiteSpace(output)) return;

        if (skill.OutputsSchema.TryGetValue("type", out var typeVal) &&
            typeVal?.ToString() == "string") return;

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
}
