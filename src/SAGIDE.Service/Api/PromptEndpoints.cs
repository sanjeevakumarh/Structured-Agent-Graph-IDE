using SAGIDE.Contracts;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Api;

internal static class PromptEndpoints
{
    private static ILogger? _logger;

    internal static IEndpointRouteBuilder MapPromptEndpoints(this IEndpointRouteBuilder app)
    {
        _logger = app.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("SAGIDE.PromptEndpoints");
        // GET /api/prompts — all registered prompts (summary fields)
        app.MapGet("/api/prompts", (IPromptRegistry registry) =>
        {
            var prompts = registry.GetAll().Select(p => new
            {
                name        = p.Name,
                domain      = p.Domain,
                version     = p.Version,
                schedule    = p.Schedule,
                sourceTag   = p.SourceTag,
                description = p.Description,
                hasSubtasks = p.Subtasks.Count > 0,
            });
            return Results.Ok(prompts);
        });

        // GET /api/prompts/{domain} — prompts for a specific domain (e.g. "finance")
        app.MapGet("/api/prompts/{domain}", (string domain, IPromptRegistry registry) =>
        {
            var prompts = registry.GetByDomain(domain).Select(p => new
            {
                name        = p.Name,
                domain      = p.Domain,
                version     = p.Version,
                schedule    = p.Schedule,
                sourceTag   = p.SourceTag,
                description = p.Description,
                hasSubtasks = p.Subtasks.Count > 0,
            });
            return Results.Ok(prompts);
        });

        // GET /api/prompts/{domain}/{name} — full prompt definition
        app.MapGet("/api/prompts/{domain}/{name}", (string domain, string name, IPromptRegistry registry) =>
        {
            var prompt = registry.GetByKey(domain, name);
            return prompt is not null ? Results.Ok(prompt) : Results.NotFound();
        });

        // POST /api/prompts/{domain}/{name}/run — ad-hoc execution of a prompt
        // Body: optional Dictionary<string, string> of variable overrides
        app.MapPost("/api/prompts/{domain}/{name}/run", async (
            string domain, string name,
            Dictionary<string, string>? variables,
            IPromptRegistry registry,
            AgentOrchestrator orchestrator,
            ISubtaskCoordinator coordinator,
            CancellationToken ct) =>
        {
            var prompt = registry.GetByKey(domain, name);
            if (prompt is null)
                return Results.NotFound(new { error = $"Prompt '{domain}/{name}' not found" });

            // Multi-model prompt: hand off to SubtaskCoordinator (runs in background).
            // Check inline subtasks OR objects/workflow declarations (WorkflowExpander runs inside RunAsync).
            if (prompt.Subtasks.Count > 0 || prompt.Objects.Count > 0 || prompt.DataCollection?.Steps.Count > 0)
            {
                // Use the host shutdown token so RunAsync stops cleanly on service shutdown.
                // Log exceptions instead of silently discarding the Task.
                var hostLifetime = app.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await coordinator.RunAsync(prompt, variables, hostLifetime.ApplicationStopping);
                    }
                    catch (OperationCanceledException) { /* host shutting down */ }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Background RunAsync failed for {Domain}/{Name}", domain, name);
                    }
                }, hostLifetime.ApplicationStopping);

                return Results.Accepted($"/api/prompts/{domain}/{name}", new
                {
                    status    = "accepted",
                    mode      = "subtask_coordinator",
                    subtasks  = prompt.Subtasks.Select(s => s.Name),
                    prompt    = $"{domain}/{name}",
                    sourceTag = prompt.SourceTag ?? $"{domain}_adhoc",
                });
            }

            // Single-model prompt: render the Scriban template with caller-supplied variables,
            // then submit as a regular AgentTask.
            var modelId     = prompt.ModelPreference?.Primary
                           ?? prompt.ModelPreference?.Orchestrator
                           ?? string.Empty;
            var providerStr = ModelIdParser.ParseProvider(modelId);
            var cleanModel  = ModelIdParser.StripPrefix(modelId);

            // Merge YAML defaults with caller overrides, then render the Scriban template.
            var renderVars = prompt.Variables
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            if (variables is not null)
                foreach (var kv in variables)
                    renderVars[kv.Key] = kv.Value;

            var renderedPrompt = PromptTemplate.Render(prompt, renderVars);

            var task = new AgentTask
            {
                AgentType     = AgentType.Generic,
                ModelProvider = providerStr,
                ModelId       = cleanModel,
                Description   = renderedPrompt,
                SourceTag     = prompt.SourceTag ?? $"{domain}_adhoc",
                Priority      = 1,
                Metadata      = new Dictionary<string, string>
                {
                    ["prompt_domain"] = domain,
                    ["prompt_name"]   = name,
                    ["triggered_by"]  = "api",
                },
            };

            if (variables is not null)
                foreach (var kv in variables)
                    task.Metadata[$"var_{kv.Key}"] = kv.Value;

            var taskId = await orchestrator.SubmitTaskAsync(task, ct);
            return Results.Accepted($"/api/tasks/{taskId}", new
            {
                taskId,
                sourceTag = task.SourceTag,
                prompt    = $"{domain}/{name}",
            });
        });

        // ── Registration endpoints ────────────────────────────────────────────

        // POST /api/prompts/register — register a single prompt definition
        app.MapPost("/api/prompts/register", (PromptDefinition prompt, IPromptRegistrationService registration) =>
        {
            try
            {
                registration.Register(prompt);
                return Results.Created($"/api/prompts/{prompt.Domain}/{prompt.Name}", new
                {
                    prompt  = $"{prompt.Domain}/{prompt.Name}",
                    version = prompt.Version,
                    status  = "registered",
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/prompts/register/bulk — register multiple prompt definitions at once
        app.MapPost("/api/prompts/register/bulk", (List<PromptDefinition> prompts, IPromptRegistrationService registration) =>
        {
            registration.RegisterBulk(prompts);
            return Results.Ok(new
            {
                count  = prompts.Count,
                status = "registered",
            });
        });

        // DELETE /api/prompts/{domain}/{name} — unregister an API-registered prompt
        app.MapDelete("/api/prompts/{domain}/{name}", (string domain, string name, IPromptRegistrationService registration) =>
        {
            var removed = registration.Unregister(domain, name);
            return removed
                ? Results.Ok(new { prompt = $"{domain}/{name}", status = "unregistered" })
                : Results.NotFound(new { error = $"No API-registered prompt '{domain}/{name}' found" });
        });

        return app;
    }

}
