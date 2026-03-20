using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

// ── Fake IWorkflowRepository ──────────────────────────────────────────────────

internal sealed class FakeWorkflowRepository : IWorkflowRepository
{
    private readonly List<WorkflowInstance> _initialInstances;
    public List<WorkflowInstance> Saved { get; } = [];

    public FakeWorkflowRepository(IEnumerable<WorkflowInstance>? initialInstances = null)
    {
        _initialInstances = initialInstances?.ToList() ?? [];
    }

    public Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        Saved.Add(instance);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkflowInstance>> LoadRunningInstancesAsync()
        => Task.FromResult<IReadOnlyList<WorkflowInstance>>(_initialInstances);

    public Task DeleteWorkflowInstanceAsync(string instanceId) => Task.CompletedTask;
}

// ── Recovery test harness ─────────────────────────────────────────────────────

internal sealed class RecoveryTestHarness : IDisposable
{
    public FakeTaskSubmitter Submitter { get; } = new();
    public FakeWorkflowRepository Repository { get; }
    public WorkflowEngine Engine { get; }
    public string WorkspaceDir { get; }

    private readonly string _workflowsDir;

    public RecoveryTestHarness(IEnumerable<WorkflowInstance>? initialInstances = null)
    {
        WorkspaceDir  = Path.Combine(Path.GetTempPath(), $"wf-rec-{Guid.NewGuid():N}");
        _workflowsDir = Path.Combine(WorkspaceDir, ".sagide", "workflows");
        Directory.CreateDirectory(_workflowsDir);

        Repository = new FakeWorkflowRepository(initialInstances);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:BuiltInTemplatesPath"] = Path.Combine(WorkspaceDir, "__none__")
            })
            .Build();

        var loader = new WorkflowDefinitionLoader(
            NullLogger<WorkflowDefinitionLoader>.Instance, config);

        var policy = new WorkflowPolicyEngine(
            new WorkflowPolicyConfig { Enabled = false },
            NullLogger<WorkflowPolicyEngine>.Instance);

        Engine = new WorkflowEngine(
            Submitter,
            loader,
            new AgentLimitsConfig(),
            new TaskAffinitiesConfig(),
            policy,
            new GitService(NullLogger<GitService>.Instance),
            new NullWorkflowStepRenderer(),
            NullLogger<WorkflowEngine>.Instance,
            Repository);
    }

    public void AddWorkflowYaml(string id, string yaml)
        => File.WriteAllText(Path.Combine(_workflowsDir, $"{id}.yaml"), yaml);

    public void Dispose()
    {
        try { Directory.Delete(WorkspaceDir, recursive: true); }
        catch { /* best-effort */ }
    }
}

// ── Crash-recovery tests ──────────────────────────────────────────────────────

public class WorkflowRecoveryTests
{
    private const string LinearWorkflowYaml = """
        id: linear_rec
        name: Linear Recovery Test
        steps:
          - id: step_a
            type: agent
            agent: coder
            prompt: "Step A"
          - id: step_b
            type: agent
            agent: coder
            depends_on: [step_a]
            prompt: "Step B"
          - id: step_c
            type: agent
            agent: coder
            depends_on: [step_b]
            prompt: "Step C"
        """;

    /// <summary>
    /// Builds a WorkflowInstance that looks like it was persisted mid-execution:
    /// step_a = Completed, step_b = Running (has a task ID), step_c = Pending.
    /// </summary>
    private WorkflowInstance BuildMidExecutionInstance(string workspacePath)
        => new()
        {
            InstanceId           = "rec001",
            DefinitionId         = "linear_rec",
            DefinitionName       = "Linear Recovery Test",
            Status               = WorkflowStatus.Running,
            WorkspacePath        = workspacePath,
            DefaultModelId       = "test-model",
            DefaultModelProvider = "Ollama",
            StepExecutions =
            {
                ["step_a"] = new WorkflowStepExecution
                {
                    StepId      = "step_a",
                    Status      = WorkflowStepStatus.Completed,
                    Output      = "done",
                    CompletedAt = DateTime.UtcNow.AddMinutes(-5),
                },
                ["step_b"] = new WorkflowStepExecution
                {
                    StepId  = "step_b",
                    TaskId  = "task-running-b",
                    Status  = WorkflowStepStatus.Running,
                },
                ["step_c"] = new WorkflowStepExecution
                {
                    StepId = "step_c",
                    Status = WorkflowStepStatus.Pending,
                },
            },
        };

    // ── 1. Running agent step is re-registered in reverse lookup ─────────────

    [Fact]
    public async Task Recovery_RunningAgentStep_RegisteredInTaskToStep()
    {
        var inst = BuildMidExecutionInstance(string.Empty);
        using var h = new RecoveryTestHarness([inst]);
        h.AddWorkflowYaml("linear_rec", LinearWorkflowYaml);
        inst.WorkspacePath = h.WorkspaceDir;

        await h.Engine.RecoverRunningInstancesAsync(CancellationToken.None);

        // The instance should be back in the active set
        var recovered = h.Engine.GetInstance("rec001");
        Assert.NotNull(recovered);

        // step_b is Running with a task ID — no new tasks should be submitted
        // (step_c depends on step_b which is still Running, so it's not ready)
        Assert.Empty(h.Submitter.SubmittedTasks);

        // Complete step_b via OnTaskUpdateAsync — engine should pick it up and submit step_c
        await h.Engine.OnTaskUpdateAsync(new TaskStatusResponse
        {
            TaskId      = "task-running-b",
            Status      = AgentTaskStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            Result      = new AgentResult { TaskId = "task-running-b", Success = true, Output = "ok" },
        });

        // step_c should now be submitted
        Assert.Single(h.Submitter.SubmittedTasks);
    }

    // ── 2. Pending step with satisfied deps is re-submitted ──────────────────

    [Fact]
    public async Task Recovery_PendingStepWithDepsMet_IsResubmitted()
    {
        // step_a and step_b are both completed; step_c is pending (deps met)
        var inst = new WorkflowInstance
        {
            InstanceId           = "rec002",
            DefinitionId         = "linear_rec",
            DefinitionName       = "Linear Recovery Test",
            Status               = WorkflowStatus.Running,
            DefaultModelId       = "test-model",
            DefaultModelProvider = "Ollama",
            StepExecutions =
            {
                ["step_a"] = new WorkflowStepExecution { StepId = "step_a", Status = WorkflowStepStatus.Completed },
                ["step_b"] = new WorkflowStepExecution { StepId = "step_b", Status = WorkflowStepStatus.Completed },
                ["step_c"] = new WorkflowStepExecution { StepId = "step_c", Status = WorkflowStepStatus.Pending },
            },
        };

        using var h = new RecoveryTestHarness([inst]);
        h.AddWorkflowYaml("linear_rec", LinearWorkflowYaml);
        inst.WorkspacePath = h.WorkspaceDir;

        await h.Engine.RecoverRunningInstancesAsync(CancellationToken.None);

        // step_c deps (step_a, step_b) are Completed → step_c re-submitted
        Assert.Single(h.Submitter.SubmittedTasks);
    }

    // ── 3. Pending step with unmet deps is NOT submitted ─────────────────────

    [Fact]
    public async Task Recovery_PendingStepWithDepsPending_NotSubmitted()
    {
        // step_a is still running, step_b is pending — step_b's dep (step_a) is not done
        var inst = new WorkflowInstance
        {
            InstanceId     = "rec003",
            DefinitionId   = "linear_rec",
            DefinitionName = "Linear Recovery Test",
            Status         = WorkflowStatus.Running,
            StepExecutions =
            {
                ["step_a"] = new WorkflowStepExecution { StepId = "step_a", TaskId = "t-a", Status = WorkflowStepStatus.Running },
                ["step_b"] = new WorkflowStepExecution { StepId = "step_b", Status = WorkflowStepStatus.Pending },
                ["step_c"] = new WorkflowStepExecution { StepId = "step_c", Status = WorkflowStepStatus.Pending },
            },
        };

        using var h = new RecoveryTestHarness([inst]);
        h.AddWorkflowYaml("linear_rec", LinearWorkflowYaml);
        inst.WorkspacePath = h.WorkspaceDir;

        await h.Engine.RecoverRunningInstancesAsync(CancellationToken.None);

        // step_a is Running (has a task) — step_b and step_c deps not met → nothing re-submitted
        Assert.Empty(h.Submitter.SubmittedTasks);
    }

    // ── 4. Tool step that was Running is marked Failed (process lost) ─────────

    [Fact]
    public async Task Recovery_RunningToolStep_MarkedFailedAndDownstreamSkipped()
    {
        const string toolWorkflowYaml = """
            id: tool_rec
            name: Tool Recovery Test
            steps:
              - id: build
                type: tool
                command: "dotnet build"
              - id: test
                type: agent
                agent: coder
                depends_on: [build]
                prompt: "Write tests"
            """;

        var inst = new WorkflowInstance
        {
            InstanceId     = "rec004",
            DefinitionId   = "tool_rec",
            DefinitionName = "Tool Recovery Test",
            Status         = WorkflowStatus.Running,
            StepExecutions =
            {
                ["build"] = new WorkflowStepExecution
                {
                    StepId = "build",
                    Status = WorkflowStepStatus.Running, // tool was running when service died
                    TaskId = "t-build",
                },
                ["test"] = new WorkflowStepExecution
                {
                    StepId = "test",
                    Status = WorkflowStepStatus.Pending,
                },
            },
        };

        using var h = new RecoveryTestHarness([inst]);
        h.AddWorkflowYaml("tool_rec", toolWorkflowYaml);
        inst.WorkspacePath = h.WorkspaceDir;

        await h.Engine.RecoverRunningInstancesAsync(CancellationToken.None);

        var recovered = h.Engine.GetInstance("rec004");
        Assert.NotNull(recovered);

        // build tool step should be marked Failed (process was lost on restart)
        Assert.Equal(WorkflowStepStatus.Failed, recovered.StepExecutions["build"].Status);
        Assert.Contains("Service restarted", recovered.StepExecutions["build"].Error ?? "");

        // test step (downstream of failed build) should be Skipped
        Assert.Equal(WorkflowStepStatus.Skipped, recovered.StepExecutions["test"].Status);

        // Nothing should be submitted (workflow failed)
        Assert.Empty(h.Submitter.SubmittedTasks);
    }

    // ── 5. Unknown definition ID → instance skipped ───────────────────────────

    [Fact]
    public async Task Recovery_UnknownDefinitionId_InstanceSkipped()
    {
        var inst = new WorkflowInstance
        {
            InstanceId     = "rec005",
            DefinitionId   = "does_not_exist",
            DefinitionName = "Ghost Workflow",
            Status         = WorkflowStatus.Running,
            WorkspacePath  = Path.GetTempPath(),
        };

        using var h = new RecoveryTestHarness([inst]);
        // No YAML written for "does_not_exist"

        await h.Engine.RecoverRunningInstancesAsync(CancellationToken.None);

        // The instance should NOT be in the active set
        Assert.Null(h.Engine.GetInstance("rec005"));
        Assert.Empty(h.Submitter.SubmittedTasks);
    }

    // ── 6. Multiple instances recovered simultaneously ────────────────────────

    [Fact]
    public async Task Recovery_MultipleInstances_AllRegistered()
    {
        var inst1 = new WorkflowInstance
        {
            InstanceId           = "multi-1",
            DefinitionId         = "linear_rec",
            Status               = WorkflowStatus.Running,
            DefaultModelId       = "test-model",
            DefaultModelProvider = "Ollama",
            StepExecutions =
            {
                ["step_a"] = new WorkflowStepExecution { StepId = "step_a", Status = WorkflowStepStatus.Completed },
                ["step_b"] = new WorkflowStepExecution { StepId = "step_b", Status = WorkflowStepStatus.Pending },
                ["step_c"] = new WorkflowStepExecution { StepId = "step_c", Status = WorkflowStepStatus.Pending },
            },
        };

        var inst2 = new WorkflowInstance
        {
            InstanceId           = "multi-2",
            DefinitionId         = "linear_rec",
            Status               = WorkflowStatus.Running,
            DefaultModelId       = "test-model",
            DefaultModelProvider = "Ollama",
            StepExecutions =
            {
                ["step_a"] = new WorkflowStepExecution { StepId = "step_a", Status = WorkflowStepStatus.Completed },
                ["step_b"] = new WorkflowStepExecution { StepId = "step_b", Status = WorkflowStepStatus.Pending },
                ["step_c"] = new WorkflowStepExecution { StepId = "step_c", Status = WorkflowStepStatus.Pending },
            },
        };

        using var h = new RecoveryTestHarness([inst1, inst2]);
        h.AddWorkflowYaml("linear_rec", LinearWorkflowYaml);
        inst1.WorkspacePath = h.WorkspaceDir;
        inst2.WorkspacePath = h.WorkspaceDir;

        await h.Engine.RecoverRunningInstancesAsync(CancellationToken.None);

        // Both instances should be active
        Assert.Equal(2, h.Engine.GetAllInstances().Count);

        // Each has step_b ready (step_a completed, step_b pending) → 2 submissions
        Assert.Equal(2, h.Submitter.SubmittedTasks.Count);
    }

    // ── 7. Missing step in definition added to execution record ──────────────

    [Fact]
    public async Task Recovery_MissingStepInExecution_AddedAsPending()
    {
        // Instance was persisted when definition only had step_a and step_b;
        // now definition also has step_c (new step added after crash)
        var inst = new WorkflowInstance
        {
            InstanceId           = "rec007",
            DefinitionId         = "linear_rec",
            Status               = WorkflowStatus.Running,
            DefaultModelId       = "test-model",
            DefaultModelProvider = "Ollama",
            StepExecutions =
            {
                // Only step_a and step_b in the saved execution record
                ["step_a"] = new WorkflowStepExecution { StepId = "step_a", Status = WorkflowStepStatus.Completed },
                ["step_b"] = new WorkflowStepExecution { StepId = "step_b", Status = WorkflowStepStatus.Completed },
                // step_c was not present in the persisted record
            },
        };

        using var h = new RecoveryTestHarness([inst]);
        h.AddWorkflowYaml("linear_rec", LinearWorkflowYaml); // this def has step_c
        inst.WorkspacePath = h.WorkspaceDir;

        await h.Engine.RecoverRunningInstancesAsync(CancellationToken.None);

        var recovered = h.Engine.GetInstance("rec007");
        Assert.NotNull(recovered);

        // step_c should have been added as Pending (and its deps are met → submitted)
        Assert.True(recovered.StepExecutions.ContainsKey("step_c"),
            "step_c should have been added to StepExecutions during recovery");
        Assert.Single(h.Submitter.SubmittedTasks); // step_c submitted
    }

    // ── 8. No running instances → no-op ──────────────────────────────────────

    [Fact]
    public async Task Recovery_NoRunningInstances_NoOp()
    {
        using var h = new RecoveryTestHarness([]); // empty list

        await h.Engine.RecoverRunningInstancesAsync(CancellationToken.None);

        Assert.Empty(h.Engine.GetAllInstances());
        Assert.Empty(h.Submitter.SubmittedTasks);
    }
}
