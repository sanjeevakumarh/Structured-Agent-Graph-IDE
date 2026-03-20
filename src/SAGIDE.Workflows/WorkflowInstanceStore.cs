using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Events;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Workflows;

/// <summary>
/// Holds the four shared in-memory maps that all WorkflowEngine sub-services need.
/// Also exposes the common helpers (PersistAsync, BroadcastUpdate, ScheduleCleanup)
/// so sub-classes don't each have to carry IWorkflowRepository + GitService + IEventBus
/// as constructor parameters.
/// </summary>
public sealed class WorkflowInstanceStore
{
    // ── Shared runtime state ───────────────────────────────────────────────────

    /// <summary>instanceId → (WorkflowInstance, WorkflowDefinition)</summary>
    public readonly ConcurrentDictionary<string, (WorkflowInstance Inst, WorkflowDefinition Def)> Active = new();

    /// <summary>taskId → (instanceId, stepId) — reverse lookup for OnTaskUpdateAsync</summary>
    public readonly ConcurrentDictionary<string, (string InstanceId, string StepId)> TaskToStep = new();

    /// <summary>Per-instance semaphore — serialises DAG evaluation on a single instance</summary>
    public readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    /// <summary>instanceId → reverse adjacency map (dependencyId → list of dependent stepIds)</summary>
    public readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> RevDepsCache = new();

    // ── Common services ────────────────────────────────────────────────────────

    private readonly IWorkflowRepository? _repository;
    private readonly IWorkflowGitService _gitService;
    private readonly IEventBus _eventBus;
    private readonly ILogger _logger;

    public WorkflowInstanceStore(
        IWorkflowRepository? repository,
        IWorkflowGitService gitService,
        IEventBus eventBus,
        ILogger logger)
    {
        _repository = repository;
        _gitService  = gitService;
        _eventBus    = eventBus;
        _logger      = logger;
    }

    // ── Instance registration ──────────────────────────────────────────────────

    public void Add(WorkflowInstance inst, WorkflowDefinition def)
    {
        Active[inst.InstanceId]          = (inst, def);
        Locks[inst.InstanceId]           = new SemaphoreSlim(1, 1);
        RevDepsCache[inst.InstanceId]    = BuildReverseDeps(def);
    }

    public bool TryGet(string instanceId, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out WorkflowInstance inst, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out WorkflowDefinition def)
    {
        if (Active.TryGetValue(instanceId, out var entry))
        {
            inst = entry.Inst;
            def  = entry.Def;
            return true;
        }
        inst = null;
        def  = null;
        return false;
    }

    public SemaphoreSlim GetLock(string instanceId) => Locks[instanceId];

    public int Count => Active.Count;

    // ── Common helpers ─────────────────────────────────────────────────────────

    public void BroadcastUpdate(WorkflowInstance inst) =>
        _eventBus.Publish(new WorkflowUpdatedEvent(inst));

    public void PublishApprovalNeeded(string instanceId, string stepId, string prompt) =>
        _eventBus.Publish(new WorkflowApprovalNeededEvent(instanceId, stepId, prompt));

    public async Task PersistAsync(WorkflowInstance inst)
    {
        if (_repository is null) return;
        try
        {
            // Auto-destroy shadow on terminal failure/cancel (fire-and-forget)
            if (inst.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled
                && !string.IsNullOrEmpty(inst.ShadowWorkspacePath))
            {
                var shadow = inst.ShadowWorkspacePath;
                var wsPath = inst.WorkspacePath ?? "";
                inst.ShadowWorkspacePath = null;
                inst.InputContext.Remove("shadow_path");
                _ = Task.Run(() => _gitService.DestroyShadowAsync(wsPath, shadow, CancellationToken.None));
            }

            await _repository.SaveWorkflowInstanceAsync(inst);

            if (inst.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                ScheduleCleanup(inst.InstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist workflow instance {Id}", inst.InstanceId);
        }
    }

    /// <summary>
    /// Removes a terminal workflow instance from the in-memory active set after a
    /// short grace period so the UI can still query it post-completion.
    /// </summary>
    public void ScheduleCleanup(string instanceId)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (Active.TryRemove(instanceId, out var removed))
            {
                foreach (var exec in removed.Inst.StepExecutions.Values)
                    if (exec.TaskId is not null)
                        TaskToStep.TryRemove(exec.TaskId, out _);
            }
            Locks.TryRemove(instanceId, out _);
            RevDepsCache.TryRemove(instanceId, out _);
            _logger.LogDebug("Cleaned up completed instance {Id} from active set", instanceId);
        });
    }

    // ── Static utilities ───────────────────────────────────────────────────────

    /// <summary>Builds a reverse adjacency map: dependencyId → list of stepIds that depend on it.</summary>
    public static Dictionary<string, List<string>> BuildReverseDeps(WorkflowDefinition def)
    {
        var revDeps = new Dictionary<string, List<string>>(def.Steps.Count, StringComparer.Ordinal);
        foreach (var step in def.Steps)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!revDeps.TryGetValue(dep, out var list))
                    revDeps[dep] = list = new List<string>(4);
                list.Add(step.Id);
            }
        }
        return revDeps;
    }
}
