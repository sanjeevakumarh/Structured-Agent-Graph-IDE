using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Events;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Prompts;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// DAG evaluation and step execution for workflow instances.
/// Extracted from WorkflowEngine: responsible for determining which steps are ready
/// and executing them according to their type (agent, tool, router, constraint, etc.).
/// </summary>
internal sealed class WorkflowStepDispatcher
{
    private readonly WorkflowInstanceStore _store;
    private readonly ITaskSubmissionService _orchestrator;
    private readonly WorkflowPolicyEngine _policyEngine;
    private readonly TaskAffinitiesConfig _taskAffinitiesConfig;
    private readonly AgentLimitsConfig _agentLimitsConfig;
    private readonly WorkflowLoopController _loopController;
    private readonly WorkflowApprovalGate _approvalGate;
    private readonly GitService _gitService;
    private readonly ILogger<WorkflowStepDispatcher> _logger;

    internal WorkflowStepDispatcher(
        WorkflowInstanceStore store,
        ITaskSubmissionService orchestrator,
        WorkflowPolicyEngine policyEngine,
        TaskAffinitiesConfig taskAffinitiesConfig,
        AgentLimitsConfig agentLimitsConfig,
        WorkflowLoopController loopController,
        WorkflowApprovalGate approvalGate,
        GitService gitService,
        ILogger<WorkflowStepDispatcher> logger)
    {
        _store                = store;
        _orchestrator         = orchestrator;
        _policyEngine         = policyEngine;
        _taskAffinitiesConfig = taskAffinitiesConfig;
        _agentLimitsConfig    = agentLimitsConfig;
        _loopController       = loopController;
        _approvalGate         = approvalGate;
        _gitService           = gitService;
        _logger               = logger;
    }

    // ── Task update callback ───────────────────────────────────────────────────

    /// <summary>
    /// Called by WorkflowEngine.OnTaskUpdateAsync when a task reaches a terminal state.
    /// Advances the DAG from the completed step.
    /// </summary>
    internal async Task OnTaskCompletedAsync(
        TaskStatusResponse status,
        WorkflowInstance inst,
        WorkflowDefinition def,
        string stepId)
    {
        var stepExec = inst.StepExecutions[stepId];

        switch (status.Status)
        {
            case AgentTaskStatus.Running:
                WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.Running);
                stepExec.StartedAt = status.StartedAt;
                _store.BroadcastUpdate(inst);
                return;

            case AgentTaskStatus.Completed:
                stepExec.Output      = status.Result?.Output;
                stepExec.IssueCount  = status.Result?.Issues?.Count ?? 0;
                stepExec.CompletedAt = status.CompletedAt;
                WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.Completed);
                break;

            case AgentTaskStatus.Failed:
                stepExec.Error       = status.StatusMessage;
                stepExec.CompletedAt = status.CompletedAt;
                WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.Failed, status.StatusMessage);
                WorkflowStepEvaluators.SkipDownstream(stepId, inst, def);
                break;

            case AgentTaskStatus.Cancelled:
                WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.Skipped, "Task cancelled");
                WorkflowStepEvaluators.SkipDownstream(stepId, inst, def);
                break;

            default:
                return;
        }

        _store.TaskToStep.TryRemove(status.TaskId, out _);

        _logger.LogDebug(
            "Workflow {InstanceId} step '{StepId}' → {Status}",
            inst.InstanceId, stepId, stepExec.Status);

        await EvaluateNextStepsAsync(inst, def, stepId);

        if (WorkflowStepEvaluators.IsInstanceDone(inst, def))
        {
            inst.Status      = inst.StepExecutions.Values.Any(s => s.Status == WorkflowStepStatus.Failed)
                                 ? WorkflowStatus.Failed
                                 : WorkflowStatus.Completed;
            inst.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation(
                "Workflow '{Name}' {Status} (instance {Id})",
                inst.DefinitionName, inst.Status, inst.InstanceId);
        }

        await _store.PersistAsync(inst);
        _store.BroadcastUpdate(inst);
    }

    // ── DAG evaluation ─────────────────────────────────────────────────────────

    internal async Task EvaluateNextStepsAsync(
        WorkflowInstance inst, WorkflowDefinition def, string completedStepId)
    {
        var completedStep = def.Steps.FirstOrDefault(s => s.Id == completedStepId);
        if (completedStep is null) return;

        var completedExec = inst.StepExecutions[completedStepId];

        // Feedback loop: step has a Next back-edge and found issues
        if (completedStep.Next is not null
            && completedExec.Status == WorkflowStepStatus.Completed
            && completedExec.IssueCount > 0)
        {
            var loopTargetDef = def.Steps.FirstOrDefault(s => s.Id == completedStep.Next);
            if (loopTargetDef is not null)
            {
                var loopTargetExec   = inst.StepExecutions[loopTargetDef.Id];
                var agentType        = WorkflowDefinitionLoader.MapAgentName(loopTargetDef.Agent ?? loopTargetDef.Id);
                var policy           = def.ConvergencePolicy;
                var yamlMax          = completedStep.MaxIterations;
                var globalMax        = _agentLimitsConfig.GetMaxIterations(agentType);
                var effectiveMax     = Math.Min(yamlMax, globalMax);
                var escalationTarget = (policy?.EscalationTarget ?? "CANCEL").ToUpperInvariant();

                // timeout_per_iteration_sec
                if (policy?.TimeoutPerIterationSec > 0 && loopTargetExec.StartedAt.HasValue)
                {
                    var elapsed = (DateTime.UtcNow - loopTargetExec.StartedAt.Value).TotalSeconds;
                    if (elapsed > policy.TimeoutPerIterationSec)
                    {
                        var msg = $"Iteration {loopTargetExec.Iteration} exceeded the " +
                            $"{policy.TimeoutPerIterationSec}s per-iteration timeout ({elapsed:F0}s elapsed).";
                        _logger.LogWarning(
                            "Workflow {Id} step '{Target}' per-iteration timeout — escalating",
                            inst.InstanceId, loopTargetDef.Id);
                        await _loopController.EscalateLoopAsync(
                            inst, def, loopTargetDef, loopTargetExec, agentType, msg, escalationTarget);
                        return;
                    }
                }

                // contradiction_detection
                if (policy?.ContradictionDetection == true
                    && loopTargetExec.Iteration > 1
                    && completedExec.IssueCount > 0
                    && completedExec.IssueCount >= loopTargetExec.PreviousIssueCount)
                {
                    var msg = $"Contradiction detected at iteration {loopTargetExec.Iteration}: " +
                        $"{completedExec.IssueCount} issue(s) ≥ prior {loopTargetExec.PreviousIssueCount} — " +
                        "constraints may be mutually exclusive.";
                    _logger.LogWarning(
                        "Workflow {Id} step '{Target}' contradiction detected — escalating to HUMAN_APPROVAL",
                        inst.InstanceId, loopTargetDef.Id);
                    await _loopController.EscalateLoopAsync(
                        inst, def, loopTargetDef, loopTargetExec, agentType, msg, "HUMAN_APPROVAL");
                    return;
                }

                // Normal iteration cap check
                if (loopTargetExec.Iteration < effectiveMax)
                {
                    loopTargetExec.PreviousIssueCount = completedExec.IssueCount;
                    loopTargetExec.Iteration++;
                    loopTargetExec.Status = WorkflowStepStatus.Pending;
                    loopTargetExec.Output = null;
                    loopTargetExec.TaskId = null;

                    _logger.LogInformation(
                        "Workflow {Id} feedback loop: re-running '{Target}' (iteration {N}/{Max}; global cap {Global})",
                        inst.InstanceId, loopTargetDef.Id, loopTargetExec.Iteration, yamlMax, globalMax);

                    var scope = (policy?.PartialRetryScope ?? "FAILING_NODES_ONLY").ToUpperInvariant();
                    if (scope == "FULL_WORKFLOW")
                        WorkflowStepEvaluators.ResetAllStepsForNewIteration(loopTargetDef.Id, inst, def);
                    else if (scope == "FROM_CODEGEN")
                        WorkflowStepEvaluators.ResetLoopBodyForNewIteration(loopTargetDef.Id, inst, def);

                    if (policy?.ConvergenceHintMemory == true)
                        WorkflowStepEvaluators.InjectConvergenceHints(completedStep, completedExec, loopTargetExec, inst);

                    await SubmitStepAsync(loopTargetDef, inst, def, CancellationToken.None);
                    return;
                }
                else
                {
                    var capMessage =
                        $"Max iterations reached ({effectiveMax}): step '{loopTargetDef.Id}' " +
                        $"exceeded the configured limit (YAML: {yamlMax}, global: {globalMax}).";
                    _logger.LogWarning(
                        "Workflow {Id} step '{Target}' hit max iterations (YAML: {Yaml}, Global: {Global}) — escalating",
                        inst.InstanceId, loopTargetDef.Id, yamlMax, globalMax);
                    await _loopController.EscalateLoopAsync(
                        inst, def, loopTargetDef, loopTargetExec, agentType, capMessage, escalationTarget);
                    return;
                }
            }
        }

        if (inst.IsPaused) return;

        await SubmitReadyStepsAsync(inst, def, CancellationToken.None, completedStepId);
    }

    /// <summary>
    /// Evaluates routers and constraint steps (synchronously), then submits all ready
    /// agent and tool steps. Loops until no more synchronous steps are ready.
    /// </summary>
    internal async Task SubmitReadyStepsAsync(
        WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct,
        string? triggerStepId = null)
    {
        HashSet<string>? candidates = null;
        if (triggerStepId is not null
            && _store.RevDepsCache.TryGetValue(inst.InstanceId, out var revDeps)
            && revDeps.TryGetValue(triggerStepId, out var directSuccessors))
        {
            candidates = new HashSet<string>(directSuccessors, StringComparer.Ordinal);
        }

        // Pass 1: drain synchronous steps (routers + constraints + context_retrieval + workspace)
        bool anySync;
        do
        {
            anySync = false;

            var readyRouters = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "router"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            foreach (var router in readyRouters)
            {
                WorkflowStepEvaluators.RecordAudit(
                    inst.StepExecutions[router.Id], WorkflowStepStatus.Completed, "Router evaluated");
                var targetId = WorkflowStepEvaluators.EvaluateRouter(router, inst);
                if (targetId is not null)
                {
                    var targetDef = def.Steps.FirstOrDefault(s => s.Id == targetId);
                    if (targetDef is not null && inst.StepExecutions[targetId].Status == WorkflowStepStatus.Pending)
                        await SubmitStepAsync(targetDef, inst, def, ct);
                }
                else
                {
                    _logger.LogWarning(
                        "Router '{RouterId}' had no matching condition — skipping downstream", router.Id);
                }
                anySync = true;
                ExpandCandidates(router.Id);
            }

            var readyConstraints = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "constraint"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            foreach (var constraint in readyConstraints)
            {
                ExecuteConstraintStep(constraint, inst, def);
                anySync = true;
                ExpandCandidates(constraint.Id);
            }

            var readyCtxRetrieval = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "context_retrieval"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            foreach (var ctxStep in readyCtxRetrieval)
            {
                ExecuteContextRetrievalStep(ctxStep, inst);
                anySync = true;
                ExpandCandidates(ctxStep.Id);
            }

            var readyProvisions = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "workspace_provision"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();
            foreach (var ps in readyProvisions)
            {
                await ExecuteWorkspaceProvisionStepAsync(ps, inst, def, ct);
                anySync = true;
                ExpandCandidates(ps.Id);
            }

            var readyTeardowns = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "workspace_teardown"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();
            foreach (var ts in readyTeardowns)
            {
                await ExecuteWorkspaceTeardownStepAsync(ts, inst, def, ct);
                anySync = true;
                ExpandCandidates(ts.Id);
            }
        }
        while (anySync);

        // Pass 1b: activate human_approval gates
        var readyApprovals = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
            .Where(s => s.Type == "human_approval"
                     && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                     && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                             && e.Status == WorkflowStepStatus.Completed))
            .ToList();

        foreach (var approvalStep in readyApprovals)
            await _approvalGate.ActivateApprovalStepAsync(approvalStep, inst, ct);

        // Pass 2: submit async steps (tools + agents)
        var readyTools = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
            .Where(s => s.Type == "tool"
                     && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                     && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                             && e.Status == WorkflowStepStatus.Completed))
            .ToList();

        foreach (var tool in readyTools)
            ExecuteToolStepInBackground(tool, inst, def);

        var readyAgents = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
            .Where(s => s.Type == "agent"
                     && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                     && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                             && e.Status == WorkflowStepStatus.Completed))
            .ToList();

        if (readyAgents.Count > 0)
            await Task.WhenAll(readyAgents.Select(s => SubmitStepAsync(s, inst, def, ct)));

        void ExpandCandidates(string completedId)
        {
            if (candidates is null || !_store.RevDepsCache.TryGetValue(inst.InstanceId, out var rv)) return;
            if (rv.TryGetValue(completedId, out var successors))
                foreach (var s in successors) candidates.Add(s);
        }
    }

    internal async Task SubmitStepAsync(
        WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct)
    {
        if (inst.StepExecutions[stepDef.Id].Status == WorkflowStepStatus.Running)
            return;

        var policyResult = _policyEngine.Check(stepDef, inst);
        if (!policyResult.IsAllowed)
        {
            var stepExec = inst.StepExecutions[stepDef.Id];
            stepExec.Error = $"[Policy] {policyResult.DenyReason}";
            WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.Failed, stepExec.Error);
            WorkflowStepEvaluators.SkipDownstream(stepDef.Id, inst, def);
            _logger.LogWarning(
                "Workflow {Id} step '{StepId}' blocked by policy: {Reason}",
                inst.InstanceId, stepDef.Id, policyResult.DenyReason);
            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
            return;
        }

        var basePrompt     = stepDef.Prompt ?? $"Process the following with a {stepDef.Agent ?? stepDef.Id} agent.";
        var resolvedPrompt = PromptTemplate.RenderWorkflowStep(
            basePrompt, inst.InputContext, inst.StepExecutions, PromptTemplate.MaxOutputChars);

        var agentType = WorkflowDefinitionLoader.MapAgentName(stepDef.Agent ?? stepDef.Id);

        var modelProvider = stepDef.ModelProvider;
        var modelId       = stepDef.ModelId;
        string? modelEndpoint = null;

        if (string.IsNullOrEmpty(modelId) &&
            inst.StepModelOverrides.TryGetValue(stepDef.Id, out var stepOverride) &&
            !string.IsNullOrEmpty(stepOverride.ModelId))
        {
            modelProvider = stepOverride.Provider;
            modelId       = stepOverride.ModelId;
            modelEndpoint = stepOverride.Endpoint;
        }

        if (string.IsNullOrEmpty(modelProvider)) modelProvider = inst.DefaultModelProvider;
        if (string.IsNullOrEmpty(modelId))       modelId       = inst.DefaultModelId;

        if (string.IsNullOrEmpty(modelProvider) || string.IsNullOrEmpty(modelId))
        {
            var (affinityProvider, affinityModel) = _taskAffinitiesConfig.GetDefaultFor(agentType);
            if (string.IsNullOrEmpty(modelProvider)) modelProvider = affinityProvider;
            if (string.IsNullOrEmpty(modelId))       modelId       = affinityModel;

            _logger.LogDebug(
                "Workflow {Id} step '{StepId}': no model specified, using affinity → {Provider}/{Model}",
                inst.InstanceId, stepDef.Id, modelProvider, modelId);
        }

        if (!Enum.TryParse<ModelProvider>(modelProvider, ignoreCase: true, out var mp))
        {
            _logger.LogWarning(
                "Workflow {Id} step '{StepId}': unrecognised provider '{Provider}'; defaulting to Ollama",
                inst.InstanceId, stepDef.Id, modelProvider);
            mp = ModelProvider.Ollama;
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            var stepExec2 = inst.StepExecutions[stepDef.Id];
            stepExec2.Error = "No model configured: step definition, instance defaults, and task affinities are all empty.";
            WorkflowStepEvaluators.RecordAudit(stepExec2, WorkflowStepStatus.Failed, stepExec2.Error);
            WorkflowStepEvaluators.SkipDownstream(stepDef.Id, inst, def);
            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
            _logger.LogError(
                "Workflow {Id} step '{StepId}': no model ID resolved after all fallbacks — failing step. " +
                "Set a model in the step definition, workflow request, or SAGIDE:AgentAffinities config.",
                inst.InstanceId, stepDef.Id);
            return;
        }

        var task = new AgentTask
        {
            AgentType     = agentType,
            ModelProvider = mp,
            ModelId       = modelId,
            Description   = resolvedPrompt,
            FilePaths     = inst.FilePaths,
            Metadata      = new Dictionary<string, string>
            {
                ["workflowInstanceId"] = inst.InstanceId,
                ["workflowStepId"]     = stepDef.Id,
                ["workflowStepLabel"]  = stepDef.Id,
            }
        };

        var effectiveEndpoint = modelEndpoint ?? inst.ModelEndpoint;
        if (!string.IsNullOrEmpty(effectiveEndpoint))
            task.Metadata["modelEndpoint"] = effectiveEndpoint;

        var taskId = await _orchestrator.SubmitTaskAsync(task, ct);

        _store.TaskToStep[taskId] = (inst.InstanceId, stepDef.Id);

        var exec     = inst.StepExecutions[stepDef.Id];
        exec.TaskId    = taskId;
        exec.StartedAt = DateTime.UtcNow;
        WorkflowStepEvaluators.RecordAudit(
            exec, WorkflowStepStatus.Running, $"Submitted as task {taskId[..Math.Min(8, taskId.Length)]}");

        _logger.LogInformation(
            "Workflow {InstanceId} submitted step '{StepId}' as task {TaskId} ({Agent} via {Provider}/{Model})",
            inst.InstanceId, stepDef.Id, taskId[..Math.Min(8, taskId.Length)],
            agentType, mp, modelId);
    }

    // ── Synchronous step handlers ──────────────────────────────────────────────

    private void ExecuteConstraintStep(WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Running);
        exec.StartedAt = DateTime.UtcNow;

        var (passed, reason) = WorkflowStepEvaluators.EvaluateConstraintExpr(stepDef.ConstraintExpr ?? "", inst);
        exec.Output      = reason;
        exec.CompletedAt = DateTime.UtcNow;

        if (passed)
        {
            WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Completed, reason);
            _logger.LogInformation(
                "Workflow {Id} constraint '{StepId}' passed: {Reason}",
                inst.InstanceId, stepDef.Id, reason);
        }
        else if (stepDef.OnConstraintFail.Equals("warn", StringComparison.OrdinalIgnoreCase))
        {
            exec.IssueCount = 1;
            WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Completed, $"warn: {reason}");
            _logger.LogWarning(
                "Workflow {Id} constraint '{StepId}' failed (warn): {Reason}",
                inst.InstanceId, stepDef.Id, reason);
        }
        else
        {
            exec.Error = $"Constraint failed: {reason}";
            WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
            WorkflowStepEvaluators.SkipDownstream(stepDef.Id, inst, def);
            _logger.LogWarning(
                "Workflow {Id} constraint '{StepId}' failed: {Reason}",
                inst.InstanceId, stepDef.Id, reason);
        }
    }

    private void ExecuteContextRetrievalStep(WorkflowStepDef stepDef, WorkflowInstance inst)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Running);
        exec.StartedAt = DateTime.UtcNow;

        var sb      = new System.Text.StringBuilder();
        var found   = new List<string>();
        var missing = new List<string>();

        foreach (var srcId in stepDef.SourceSteps)
        {
            if (inst.StepExecutions.TryGetValue(srcId, out var srcExec) &&
                !string.IsNullOrEmpty(srcExec.Output))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"=== {srcId} ===");
                sb.Append(srcExec.Output);
                found.Add(srcId);
            }
            else { missing.Add(srcId); }
        }

        var varName  = stepDef.ContextVarName!;
        var combined = sb.ToString();

        inst.InputContext[varName] = combined;
        exec.Output      = combined;
        exec.CompletedAt = DateTime.UtcNow;

        var summary = $"context_retrieval: '{varName}' populated from [{string.Join(", ", found)}]" +
            (missing.Count > 0 ? $"; missing/empty: [{string.Join(", ", missing)}]" : "");

        WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Completed, summary);
        _logger.LogInformation(
            "Workflow {Id} context_retrieval '{StepId}': {Summary}", inst.InstanceId, stepDef.Id, summary);
    }

    // ── Tool step ──────────────────────────────────────────────────────────────

    private void ExecuteToolStepInBackground(
        WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        exec.StartedAt = DateTime.UtcNow;
        WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Running, $"Command: {stepDef.Command}");

        _logger.LogInformation(
            "Workflow {InstanceId} starting tool step '{StepId}': {Command}",
            inst.InstanceId, stepDef.Id, stepDef.Command);

        _store.BroadcastUpdate(inst);

        var instanceId = inst.InstanceId;
        var command    = PromptTemplate.RenderWorkflowStep(
            stepDef.Command ?? "", inst.InputContext, inst.StepExecutions, PromptTemplate.MaxOutputChars);
        var workingDir = string.IsNullOrWhiteSpace(stepDef.WorkingDir)
            ? inst.WorkspacePath ?? Directory.GetCurrentDirectory()
            : PromptTemplate.RenderWorkflowStep(
                stepDef.WorkingDir, inst.InputContext, inst.StepExecutions, PromptTemplate.MaxOutputChars);
        var policy     = stepDef.ExitCodePolicy.ToUpperInvariant();
        var timeoutSec = stepDef.TimeoutSec;

        _ = Task.Run(async () =>
        {
            string output;
            int    exitCode;
            bool   timedOut = false;

            using var toolCts = timeoutSec > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec))
                : new CancellationTokenSource();

            try
            {
                (output, exitCode) = await RunProcessAsync(command, workingDir, toolCts.Token);
            }
            catch (OperationCanceledException) when (toolCts.IsCancellationRequested && timeoutSec > 0)
            {
                output   = $"Tool step timed out after {timeoutSec}s.";
                exitCode = -2;
                timedOut = true;
                _logger.LogWarning(
                    "Workflow {Id} tool step '{StepId}' timed out after {Sec}s",
                    instanceId, stepDef.Id, timeoutSec);
            }
            catch (Exception ex)
            {
                output   = ex.Message;
                exitCode = -1;
                _logger.LogError(ex,
                    "Workflow {Id} tool step '{StepId}' threw an exception", instanceId, stepDef.Id);
            }

            if (!_store.Active.TryGetValue(instanceId, out var entry)) return;
            var (liveInst, liveDef) = entry;
            var lk = _store.Locks[instanceId];
            await lk.WaitAsync();
            try
            {
                var liveExec         = liveInst.StepExecutions[stepDef.Id];
                liveExec.ExitCode    = exitCode;
                liveExec.Output      = output;
                liveExec.CompletedAt = DateTime.UtcNow;

                if (timedOut)
                {
                    liveExec.Error = output;
                    WorkflowStepEvaluators.RecordAudit(liveExec, WorkflowStepStatus.Failed, liveExec.Error);
                    WorkflowStepEvaluators.SkipDownstream(stepDef.Id, liveInst, liveDef);
                }
                else if (exitCode == 0 || policy is "IGNORE")
                {
                    WorkflowStepEvaluators.RecordAudit(liveExec, WorkflowStepStatus.Completed, $"exit code {exitCode}");
                }
                else if (policy == "WARN_ON_NONZERO")
                {
                    liveExec.IssueCount = 1;
                    WorkflowStepEvaluators.RecordAudit(
                        liveExec, WorkflowStepStatus.Completed, $"exit code {exitCode} (WARN_ON_NONZERO)");
                    _logger.LogWarning(
                        "Workflow {Id} tool '{StepId}' exited {Code} (WARN_ON_NONZERO)",
                        instanceId, stepDef.Id, exitCode);
                }
                else
                {
                    liveExec.Error = $"Command exited with code {exitCode}.";
                    WorkflowStepEvaluators.RecordAudit(liveExec, WorkflowStepStatus.Failed, liveExec.Error);
                    WorkflowStepEvaluators.SkipDownstream(stepDef.Id, liveInst, liveDef);
                    _logger.LogWarning(
                        "Workflow {Id} tool '{StepId}' failed (exit code {Code})",
                        instanceId, stepDef.Id, exitCode);
                }

                await EvaluateNextStepsAsync(liveInst, liveDef, stepDef.Id);

                if (WorkflowStepEvaluators.IsInstanceDone(liveInst, liveDef))
                {
                    liveInst.Status = liveInst.StepExecutions.Values.Any(
                        s => s.Status == WorkflowStepStatus.Failed)
                        ? WorkflowStatus.Failed
                        : WorkflowStatus.Completed;
                    liveInst.CompletedAt = DateTime.UtcNow;
                    _logger.LogInformation(
                        "Workflow '{Name}' {Status} (instance {Id})",
                        liveInst.DefinitionName, liveInst.Status, instanceId);
                }

                await _store.PersistAsync(liveInst);
                _store.BroadcastUpdate(liveInst);
            }
            finally { lk.Release(); }
        });
    }

    private static async Task<(string Output, int ExitCode)> RunProcessAsync(
        string command, string workingDir, CancellationToken ct = default)
    {
        var parts = command.Trim().Split(' ', 2);
        var exe   = parts[0];
        var args  = parts.Length > 1 ? parts[1] : string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : stdout + "\n--- stderr ---\n" + stderr;

        return (combined.Trim(), proc.ExitCode);
    }

    // ── Shadow workspace steps ─────────────────────────────────────────────────

    private async Task ExecuteWorkspaceProvisionStepAsync(
        WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        exec.StartedAt = DateTime.UtcNow;
        WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Running);

        try
        {
            if (string.IsNullOrEmpty(inst.WorkspacePath) || !_gitService.IsGitRepo(inst.WorkspacePath))
            {
                exec.Output      = "Shadow workspace skipped: not a git repository.";
                exec.CompletedAt = DateTime.UtcNow;
                WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
                _logger.LogWarning(
                    "Workflow {Id} workspace_provision skipped: {WorkspacePath} is not a git repo",
                    inst.InstanceId, inst.WorkspacePath ?? "(none)");
                await _store.PersistAsync(inst);
                _store.BroadcastUpdate(inst);
                return;
            }

            var shadowPath = await _gitService.ProvisionShadowAsync(
                inst.WorkspacePath, inst.InstanceId, stepDef.ShadowBranch, ct);

            if (shadowPath is null)
            {
                exec.Output      = "Shadow workspace skipped: git not available.";
                exec.CompletedAt = DateTime.UtcNow;
                WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
                await _store.PersistAsync(inst);
                _store.BroadcastUpdate(inst);
                return;
            }

            inst.ShadowWorkspacePath = shadowPath;
            inst.InputContext["shadow_path"] = shadowPath;

            exec.Output      = $"Shadow worktree provisioned at: {shadowPath}";
            exec.CompletedAt = DateTime.UtcNow;
            WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
            _logger.LogInformation(
                "Workflow {Id} shadow workspace provisioned: {Path}", inst.InstanceId, shadowPath);

            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
        }
        catch (Exception ex)
        {
            exec.Error       = $"workspace_provision failed: {ex.Message}";
            exec.CompletedAt = DateTime.UtcNow;
            WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
            _logger.LogError(ex, "Workflow {Id} workspace_provision threw", inst.InstanceId);
            WorkflowStepEvaluators.SkipDownstream(stepDef.Id, inst, def);
            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
        }
    }

    private async Task ExecuteWorkspaceTeardownStepAsync(
        WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        exec.StartedAt = DateTime.UtcNow;
        WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Running);

        try
        {
            if (string.IsNullOrEmpty(inst.ShadowWorkspacePath))
            {
                exec.Output      = "No shadow workspace to tear down.";
                exec.CompletedAt = DateTime.UtcNow;
                WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
                await _store.PersistAsync(inst);
                _store.BroadcastUpdate(inst);
                return;
            }

            var shadowPath = inst.ShadowWorkspacePath;

            if (stepDef.ShadowAction == "promote")
            {
                var (success, summary) = await _gitService.PromoteShadowAsync(inst.WorkspacePath!, shadowPath, ct);
                if (!success)
                {
                    exec.Error       = $"workspace_teardown promote failed: {summary}";
                    exec.CompletedAt = DateTime.UtcNow;
                    WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
                    _logger.LogError(
                        "Workflow {Id} workspace_teardown promote failed: {Err}", inst.InstanceId, summary);
                    WorkflowStepEvaluators.SkipDownstream(stepDef.Id, inst, def);
                    inst.ShadowWorkspacePath = null;
                    inst.InputContext.Remove("shadow_path");
                    await _store.PersistAsync(inst);
                    _store.BroadcastUpdate(inst);
                    return;
                }
                exec.Output = $"Shadow changes promoted to workspace.\n{summary}";
            }
            else
            {
                await _gitService.DestroyShadowAsync(inst.WorkspacePath ?? "", shadowPath, ct);
                exec.Output = "Shadow workspace destroyed.";
            }

            inst.ShadowWorkspacePath = null;
            inst.InputContext.Remove("shadow_path");
            exec.CompletedAt = DateTime.UtcNow;
            WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
            _logger.LogInformation(
                "Workflow {Id} workspace_teardown completed (action: {Action})",
                inst.InstanceId, stepDef.ShadowAction);

            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
        }
        catch (Exception ex)
        {
            exec.Error       = $"workspace_teardown failed: {ex.Message}";
            exec.CompletedAt = DateTime.UtcNow;
            WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
            _logger.LogError(ex, "Workflow {Id} workspace_teardown threw", inst.InstanceId);
            inst.ShadowWorkspacePath = null;
            inst.InputContext.Remove("shadow_path");
            WorkflowStepEvaluators.SkipDownstream(stepDef.Id, inst, def);
            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
        }
    }
}
