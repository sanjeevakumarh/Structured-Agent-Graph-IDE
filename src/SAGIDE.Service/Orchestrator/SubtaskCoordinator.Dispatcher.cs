using System.Diagnostics;
using SAGIDE.Contracts;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Subtask dispatch phase of <see cref="SubtaskCoordinator"/>:
/// wave-based topological dispatch, health-aware routing, retry on server failure,
/// task waiting, and subtask variable context building.
/// </summary>
public sealed partial class SubtaskCoordinator
{
    // ── Subtask dispatch ────────────────────────────────────────────────────────

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
            var wave = pending.Where(st => st.DependsOn.All(d => done.Contains(d))).ToList();

            if (wave.Count == 0)
            {
                _logger.LogError(
                    "SubtaskCoordinator [{Id}]: unresolvable depends_on in [{Names}] — possible cycle or missing name",
                    instanceId, string.Join(", ", pending.Select(s => s.Name)));
                break;
            }

            foreach (var st in wave) pending.Remove(st);

            var submitted = (await Task.WhenAll(
                    wave.Select(st => SubmitSubtaskAsync(st, prompt, vars, instanceId, tracer, ct))))
                .Where(s => s is not null).Select(s => s!).ToList();

            await Task.WhenAll(submitted.Select(async s =>
            {
                var output = await WaitForTaskAsync(s.TaskId, ct);
                lock (results) results[s.Name] = output;
                tracer.WriteText($"subtask-{s.Name}-result", output);
            }));

            if (_healthMonitor is not null)
                await RetryFailedSubtasksAsync(submitted, wave, prompt, vars, instanceId, tracer, results, ct);

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
            var modelSpec = ResolveSimpleTemplate(subtask.Model ?? string.Empty, vars);
            var (provider, modelId, parsedEndpoint) = ParseModelSpec(modelSpec);
            subtaskActivity?.SetTag("subtask.model", modelId);
            subtaskActivity?.SetTag("subtask.provider", provider.ToString());

            var endpoint = endpointOverride ?? parsedEndpoint;

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
                    _logger.LogInformation("Health routing: subtask '{Name}' rerouted from {Old} to {New}",
                        subtask.Name, endpoint, bestHost);
                    endpoint = bestHost;
                }
            }

            var subtaskVars = BuildSubtaskVars(subtask, prompt, vars);
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
                model        = modelId,
                endpoint     = endpoint,
                provider     = provider.ToString(),
                depends_on   = subtask.DependsOn,
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
            _logger.LogInformation("Subtask '{Name}' submitted as task {TaskId} (model: {Model}@{Host})",
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
            if (subtask.Parameters.Count == 0)
                return allVars;

            var rendered = PreRenderParameters(subtask.Parameters, allVars);
            return new Dictionary<string, object>(allVars, StringComparer.OrdinalIgnoreCase)
            {
                ["parameters"] = rendered,
            };
        }

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"]      = allVars.GetValueOrDefault("date",      string.Empty),
            ["datestamp"] = allVars.GetValueOrDefault("datestamp", string.Empty),
            ["datetime"]  = allVars.GetValueOrDefault("datetime",  string.Empty),
        };

        foreach (var kv in prompt.Variables)
            if (allVars.TryGetValue(kv.Key, out var overriddenVal))
                result[kv.Key] = overriddenVal;

        foreach (var key in (ReadOnlySpan<string>)["topic_slug", "ticker_upper", "model_preference"])
            if (allVars.TryGetValue(key, out var computedVal))
                result[key] = computedVal;

        foreach (var key in subtask.InputVars)
            if (allVars.TryGetValue(key, out var val))
                result[key] = val;

        if (subtask.Parameters.Count > 0)
            result["parameters"] = PreRenderParameters(subtask.Parameters, allVars);

        return result;
    }

    private static Scriban.Runtime.ScriptObject PreRenderParameters(
        Dictionary<string, object> parameters,
        Dictionary<string, object> vars)
    {
        var rendered = new Scriban.Runtime.ScriptObject();
        foreach (var kv in parameters)
        {
            if (kv.Value is string s && s.Contains("{{"))
                rendered[kv.Key] = PromptTemplate.RenderRaw(s, vars);
            else
                rendered[kv.Key] = kv.Value;
        }
        return rendered;
    }

    private async Task<string> WaitForTaskAsync(string taskId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(taskId)) return string.Empty;

        var deadline = DateTime.UtcNow.AddHours(2);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(2_000, ct);

            var status = _taskSubmission.GetTaskStatus(taskId);

            // Task evicted from in-memory queue — fall back to SQLite repository.
            // This happens when many concurrent subtasks push _allTasks past MaxTaskHistoryInMemory.
            if (status is null)
            {
                if (_taskRepository is null) break;

                var dbTask = await _taskRepository.GetTaskAsync(taskId);
                if (dbTask is null) break; // genuinely unknown task

                if (dbTask.Status is AgentTaskStatus.Completed)
                {
                    var dbResult = await _taskRepository.GetResultAsync(taskId);
                    _logger.LogInformation(
                        "Subtask {TaskId} was evicted from memory but found completed in DB ({OutputLen} chars)",
                        taskId, dbResult?.Output?.Length ?? 0);
                    return dbResult?.Output ?? string.Empty;
                }
                if (dbTask.Status is AgentTaskStatus.Failed)
                {
                    _logger.LogWarning("Subtask {TaskId} failed (from DB): {Err}", taskId, dbTask.StatusMessage);
                    return string.Empty;
                }
                if (dbTask.Status is AgentTaskStatus.Cancelled)
                    return string.Empty;

                // Still running in DB but evicted from memory — keep polling DB
                continue;
            }

            switch (status.Status)
            {
                case AgentTaskStatus.Completed:
                    return status.Result?.Output ?? string.Empty;
                case AgentTaskStatus.Failed:
                    _logger.LogWarning("Subtask {TaskId} failed: {Err}", taskId, status.Result?.ErrorMessage);
                    return string.Empty;
                case AgentTaskStatus.Cancelled:
                    return string.Empty;
            }
        }

        _logger.LogWarning("Timed out waiting for subtask task {TaskId}", taskId);
        return string.Empty;
    }
}
