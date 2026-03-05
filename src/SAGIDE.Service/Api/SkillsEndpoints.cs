using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Api;

/// <param name="Parameters">Skill parameter values — merged over the skill's YAML defaults.</param>
/// <param name="Variables">Extra template variables available in the skill's prompts (e.g. date overrides).</param>
internal record SkillRunRequest(
    Dictionary<string, object>? Parameters = null,
    Dictionary<string, string>? Variables  = null);

internal static class SkillsEndpoints
{
    internal static IEndpointRouteBuilder MapSkillsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/skills — list all skills with summary metadata
        app.MapGet("/api/skills", (SkillRegistry registry) =>
        {
            var skills = registry.GetAll().Select(s => new
            {
                s.Name,
                s.Domain,
                s.Version,
                s.Description,
                s.ProtocolImplements,
                CapabilitySlots     = s.CapabilityRequirements.Keys.ToList(),
                ImplementationSteps = s.Implementation.Count,
            });
            return Results.Ok(skills);
        });

        // GET /api/skills/{domain}/{name} — full skill definition
        app.MapGet("/api/skills/{domain}/{name}", (string domain, string name, SkillRegistry registry) =>
        {
            var skill = registry.GetByKey(domain, name);
            return skill is null ? Results.NotFound() : Results.Ok(skill);
        });

        // GET /api/skills/graph — skill composition DAG for a given prompt
        // Returns nodes (skill instances) and edges (data flow) suitable for visual rendering.
        // Query param: ?prompt=domain/name
        app.MapGet("/api/skills/graph", (string? prompt, SkillRegistry skillRegistry,
            SAGIDE.Service.Prompts.PromptRegistry promptRegistry) =>
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return Results.BadRequest("prompt query parameter required (e.g. ?prompt=research/idea-to-product-seq)");

            var slash = prompt.IndexOf('/');
            if (slash < 0)
                return Results.BadRequest("prompt must be domain/name format");

            var domain = prompt[..slash];
            var name   = prompt[(slash + 1)..];
            var def    = promptRegistry.GetByKey(domain, name);
            if (def is null)
                return Results.NotFound($"Prompt '{prompt}' not found");

            // Build node list from data_collection steps + subtasks
            var nodes = new List<object>();
            var edges = new List<object>();

            // Data collection steps → nodes
            if (def.DataCollection is not null)
            {
                foreach (var step in def.DataCollection.Steps)
                {
                    var skillDef = string.IsNullOrEmpty(step.Skill)
                        ? null
                        : skillRegistry.Resolve(step.Skill);

                    nodes.Add(new
                    {
                        Id          = step.Name,
                        Label       = step.Name,
                        Type        = string.IsNullOrEmpty(step.Skill) ? "primitive" : "skill",
                        SkillRef    = step.Skill,
                        SkillDomain = skillDef?.Domain,
                        StepType    = step.Type,
                        OutputVar   = step.OutputVar,
                    });
                }
            }

            // Objects (Phase 5) → nodes
            foreach (var obj in def.Objects)
            {
                nodes.Add(new
                {
                    Id          = obj.Name,
                    Label       = obj.Name,
                    Type        = "object",
                    SkillRef    = obj.Skill,
                    SkillDomain = (string?)null,
                    StepType    = (string?)null,
                    OutputVar   = (string?)null,
                });
            }

            // Subtasks → nodes
            foreach (var subtask in def.Subtasks)
            {
                nodes.Add(new
                {
                    Id          = subtask.Name,
                    Label       = subtask.Name,
                    Type        = "subtask",
                    SkillRef    = (string?)null,
                    SkillDomain = (string?)null,
                    StepType    = (string?)null,
                    OutputVar   = $"{subtask.Name}_result",
                });

                // Subtask depends_on → edges
                foreach (var dep in subtask.DependsOn)
                    edges.Add(new { From = dep, To = subtask.Name, DataFlow = $"{dep}_result" });

                // Subtask input_vars → edges from data collection steps
                foreach (var inputVar in subtask.InputVars)
                {
                    var sourceStep = def.DataCollection?.Steps
                        .FirstOrDefault(s => s.OutputVar == inputVar);
                    if (sourceStep is not null)
                        edges.Add(new { From = sourceStep.Name, To = subtask.Name, DataFlow = inputVar });
                }
            }

            return Results.Ok(new { Nodes = nodes, Edges = edges, PromptName = def.Name, PromptDomain = def.Domain });
        });

        // POST /api/skills/{domain}/{name}/run
        // Runs a single skill in isolation for debugging.
        // Body: { "parameters": { "ticker": "MSFT", ... }, "variables": { "date": "2026-03-02" } }
        // Response: { "output": "...", "trace_folder": "~/reports/traces/..." }
        //
        // The skill executes through the same SubtaskCoordinator pipeline as a full workflow run,
        // so RunTracing produces the same numbered trace files in the trace folder.
        app.MapPost("/api/skills/{domain}/{name}/run",
            async (string domain, string name,
                   SkillRunRequest? request,
                   SkillRegistry skillRegistry,
                   SubtaskCoordinator coordinator,
                   CancellationToken ct) =>
            {
                var skill = skillRegistry.GetByKey(domain, name);
                if (skill is null)
                    return Results.NotFound(new { error = $"Skill '{domain}/{name}' not found in registry" });

                // Build a minimal synthetic PromptDefinition that runs just this skill
                var prompt = new PromptDefinition
                {
                    Name       = skill.Name,
                    Domain     = skill.Domain,
                    SourceTag  = $"skill_debug_{domain}_{name}",
                    // Expose the request parameters as prompt variables so {{param}} templates resolve
                    Variables  = request?.Parameters
                                     ?.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToString()!)
                                 ?? [],
                    DataCollection = new PromptDataCollection
                    {
                        Steps =
                        [
                            new PromptDataCollectionStep
                            {
                                Name       = "skill_run",
                                Type       = "skill",
                                Skill      = $"{domain}/{name}",
                                Parameters = request?.Parameters
                                                 ?.ToDictionary(kv => kv.Key, kv => (object)kv.Value)
                                             ?? [],
                                OutputVar  = "skill_output",
                            }
                        ]
                    },
                    // Synthesis just echoes the step output as the final result
                    Synthesis = new PromptSynthesis { PromptTemplate = "{{skill_output}}" },
                };

                var result = await coordinator.RunAsync(prompt, request?.Variables, ct);

                return Results.Ok(new
                {
                    skill        = $"{domain}/{name}",
                    output       = result.SynthesizedOutput,
                    chars        = result.SynthesizedOutput.Length,
                    trace_folder = result.TraceFolderPath,
                    instance_id  = result.InstanceId,
                });
            });

        return app;
    }
}
