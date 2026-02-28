using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

// ── Fake ITaskSubmissionService ───────────────────────────────────────────────

/// <summary>
/// Captures SubmitTaskAsync calls and returns deterministic task IDs.
/// Also records CancelTaskAsync calls so tests can assert cancellations.
/// </summary>
internal sealed class FakeTaskSubmitter : ITaskSubmissionService
{
    private int _counter;

    /// <summary>Step IDs in submission order (not task IDs — the submitted AgentTask.Description).</summary>
    public List<string> SubmittedDescriptions { get; } = [];

    /// <summary>Maps task ID → the AgentTask submitted.</summary>
    public Dictionary<string, AgentTask> SubmittedTasks { get; } = new();

    public List<string> CancelledTaskIds { get; } = [];

    public Task<string> SubmitTaskAsync(AgentTask task, CancellationToken ct)
    {
        var taskId = $"task-{Interlocked.Increment(ref _counter):D3}";
        SubmittedTasks[taskId] = task;
        SubmittedDescriptions.Add(task.Description ?? string.Empty);
        return Task.FromResult(taskId);
    }

    public Task CancelTaskAsync(string taskId, CancellationToken ct)
    {
        CancelledTaskIds.Add(taskId);
        return Task.CompletedTask;
    }

    public TaskStatusResponse? GetTaskStatus(string taskId)
    {
        if (!SubmittedTasks.TryGetValue(taskId, out var task)) return null;
        return new TaskStatusResponse
        {
            TaskId        = taskId,
            Status        = AgentTaskStatus.Completed,
            AgentType     = task.AgentType,
            ModelProvider = task.ModelProvider,
            ModelId       = task.ModelId ?? string.Empty,
        };
    }
}

// ── Test harness ──────────────────────────────────────────────────────────────

/// <summary>
/// Wires up a WorkflowEngine with a FakeTaskSubmitter and a temp workspace directory.
/// Workflow YAML files are written to {WorkspaceDir}/.sagide/workflows/.
/// </summary>
internal sealed class WorkflowTestHarness : IDisposable
{
    public FakeTaskSubmitter Submitter { get; } = new();
    public WorkflowEngine Engine { get; }

    /// <summary>Temp directory used as the workflow workspace.</summary>
    public string WorkspaceDir { get; }

    private readonly string _workflowsDir;

    public WorkflowTestHarness()
    {
        WorkspaceDir  = Path.Combine(Path.GetTempPath(), $"wf-test-{Guid.NewGuid():N}");
        _workflowsDir = Path.Combine(WorkspaceDir, ".sagide", "workflows");
        Directory.CreateDirectory(_workflowsDir);

        // Loader: point built-in templates at a non-existent directory so none are loaded
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:BuiltInTemplatesPath"] = Path.Combine(WorkspaceDir, "__no_templates__")
            })
            .Build();

        var loader = new WorkflowDefinitionLoader(
            NullLogger<WorkflowDefinitionLoader>.Instance, config);

        // Policy disabled so agent-type/file-path rules don't interfere with DAG logic tests
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
            NullLogger<WorkflowEngine>.Instance);
    }

    /// <summary>Writes a workflow YAML file and returns its definition ID (= filename stem).</summary>
    public string AddWorkflow(string id, string yaml)
    {
        File.WriteAllText(Path.Combine(_workflowsDir, $"{id}.yaml"), yaml);
        return id;
    }

    /// <summary>Starts a workflow using the harness workspace as the workspace path.</summary>
    public Task<WorkflowInstance> StartAsync(string definitionId,
        Dictionary<string, string>? inputs = null)
        => Engine.StartAsync(new StartWorkflowRequest
        {
            DefinitionId  = definitionId,
            WorkspacePath = WorkspaceDir,
            Inputs        = inputs ?? [],
        }, CancellationToken.None);

    /// <summary>Simulates a task completing successfully (no issues).</summary>
    public Task CompleteTaskAsync(string taskId, string output = "ok", int issueCount = 0)
        => Engine.OnTaskUpdateAsync(new TaskStatusResponse
        {
            TaskId      = taskId,
            Status      = AgentTaskStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            Result      = new AgentResult
            {
                TaskId  = taskId,
                Success = true,
                Output  = output,
                Issues  = Enumerable.Range(0, issueCount)
                              .Select(_ => new Issue { Message = "issue", Severity = IssueSeverity.Low })
                              .ToList(),
            },
        });

    /// <summary>Simulates a task failing.</summary>
    public Task FailTaskAsync(string taskId, string error = "failed")
        => Engine.OnTaskUpdateAsync(new TaskStatusResponse
        {
            TaskId         = taskId,
            Status         = AgentTaskStatus.Failed,
            StatusMessage  = error,
            CompletedAt    = DateTime.UtcNow,
        });

    /// <summary>Retrieves the task ID that was submitted for a given description fragment.</summary>
    public string TaskIdFor(string descriptionContains)
        => Submitter.SubmittedTasks
               .First(kv => (kv.Value.Description ?? "").Contains(descriptionContains))
               .Key;

    public void Dispose()
    {
        try { Directory.Delete(WorkspaceDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}

// ── WorkflowEngine DAG Tests ──────────────────────────────────────────────────

public class WorkflowEngineTests
{
    // ── Linear DAG ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LinearDag_StepsExecuteInOrder()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("linear", """
            id: linear
            name: Linear Test
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
            """);

        var inst = await h.StartAsync("linear");

        // Only step_a should be submitted at this point
        Assert.Single(h.Submitter.SubmittedTasks);
        Assert.Equal(WorkflowStatus.Running, inst.Status);

        // Complete step_a → step_b should be submitted
        var taskA = h.Submitter.SubmittedTasks.Keys.First();
        await h.CompleteTaskAsync(taskA);
        Assert.Equal(2, h.Submitter.SubmittedTasks.Count);

        // Complete step_b → step_c should be submitted
        var taskB = h.Submitter.SubmittedTasks.Keys.Skip(1).First();
        await h.CompleteTaskAsync(taskB);
        Assert.Equal(3, h.Submitter.SubmittedTasks.Count);

        // Complete step_c → workflow Completed
        var taskC = h.Submitter.SubmittedTasks.Keys.Skip(2).First();
        await h.CompleteTaskAsync(taskC);

        var updated = h.Engine.GetInstance(inst.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, updated!.Status);
        Assert.Equal(WorkflowStepStatus.Completed, updated.StepExecutions["step_a"].Status);
        Assert.Equal(WorkflowStepStatus.Completed, updated.StepExecutions["step_b"].Status);
        Assert.Equal(WorkflowStepStatus.Completed, updated.StepExecutions["step_c"].Status);
    }

    // ── Parallel steps ────────────────────────────────────────────────────────

    [Fact]
    public async Task ParallelSteps_BothSubmittedAfterRoot()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("parallel", """
            id: parallel
            name: Parallel Test
            steps:
              - id: root
                type: agent
                agent: coder
                prompt: "Root"
              - id: branch_b
                type: agent
                agent: coder
                depends_on: [root]
                prompt: "Branch B"
              - id: branch_c
                type: agent
                agent: coder
                depends_on: [root]
                prompt: "Branch C"
              - id: final
                type: agent
                agent: coder
                depends_on: [branch_b, branch_c]
                prompt: "Final"
            """);

        var inst = await h.StartAsync("parallel");

        // Only root submitted initially
        Assert.Single(h.Submitter.SubmittedTasks);

        // Complete root → both branches submitted (in parallel)
        var rootTask = h.Submitter.SubmittedTasks.Keys.First();
        await h.CompleteTaskAsync(rootTask);
        Assert.Equal(3, h.Submitter.SubmittedTasks.Count); // root + B + C

        // Complete B — final should NOT submit yet (C still pending)
        var taskB = h.Submitter.SubmittedTasks.Keys.Skip(1).First();
        await h.CompleteTaskAsync(taskB);
        Assert.Equal(3, h.Submitter.SubmittedTasks.Count); // still 3

        // Complete C — final should now be submitted
        var taskC = h.Submitter.SubmittedTasks.Keys.Skip(2).First();
        await h.CompleteTaskAsync(taskC);
        Assert.Equal(4, h.Submitter.SubmittedTasks.Count);

        // Complete final → workflow done
        var taskFinal = h.Submitter.SubmittedTasks.Keys.Skip(3).First();
        await h.CompleteTaskAsync(taskFinal);

        Assert.Equal(WorkflowStatus.Completed, h.Engine.GetInstance(inst.InstanceId)!.Status);
    }

    // ── Router: hasIssues branch ──────────────────────────────────────────────
    //
    // Design note: the router submits the matching branch in Pass 1 (sync step evaluation).
    // Both branch targets also have deps on the preceding step, so Pass 2 submits the OTHER
    // branch too. The DIFFERENCE between conditions is which branch is submitted FIRST
    // (by the router in Pass 1 vs by the ready-agent scan in Pass 2).

    private const string RouterWorkflowYaml = """
        id: router_wf
        name: Router Test
        steps:
          - id: step_a
            type: agent
            agent: coder
            prompt: "Step A"
          - id: route
            type: router
            depends_on: [step_a]
            branches:
              - condition: hasIssues
                target: step_b
              - condition: success
                target: step_c
          - id: step_b
            type: agent
            agent: coder
            depends_on: [step_a]
            prompt: "Step B fix"
          - id: step_c
            type: agent
            agent: coder
            depends_on: [step_a]
            prompt: "Step C done"
        """;

    [Fact]
    public async Task Router_HasIssuesBranch_SubmitsFixFirst()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("router_wf", RouterWorkflowYaml);

        await h.StartAsync("router_wf");

        // Only step_a is a root step (route/step_b/step_c all have depends_on)
        Assert.Single(h.Submitter.SubmittedTasks);

        var taskA = h.Submitter.SubmittedTasks.Keys.First();

        // Complete step_a WITH issues → router fires → step_b (hasIssues) submitted by router (Pass 1),
        // then step_c also submitted by Pass 2 (deps met). Total: 3 tasks.
        await h.CompleteTaskAsync(taskA, output: "found issues", issueCount: 3);

        Assert.Equal(3, h.Submitter.SubmittedTasks.Count); // step_a + step_b (router) + step_c (pass2)

        // The router step itself is marked Completed (synchronously evaluated)
        var inst = h.Engine.GetAllInstances().FirstOrDefault(i => i.DefinitionId == "router_wf")!;
        Assert.Equal(WorkflowStepStatus.Completed, inst.StepExecutions["route"].Status);

        // step_b was submitted FIRST by the router (Pass 1), before step_c (Pass 2)
        // So step_b's description appears before step_c's in the ordered list
        var descs = h.Submitter.SubmittedDescriptions;
        var bIndex = descs.IndexOf("Step B fix");
        var cIndex = descs.IndexOf("Step C done");
        Assert.True(bIndex >= 0, "step_b ('Step B fix') was not submitted");
        Assert.True(cIndex >= 0, "step_c ('Step C done') was not submitted");
        Assert.True(bIndex < cIndex, "Router should have submitted hasIssues target (step_b) before step_c");
    }

    [Fact]
    public async Task Router_SuccessBranch_SubmitsDoneFirst()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("router_wf", RouterWorkflowYaml);

        await h.StartAsync("router_wf");

        var taskA = h.Submitter.SubmittedTasks.Keys.First();

        // Complete step_a WITHOUT issues → router fires → step_c (success) submitted by router (Pass 1),
        // then step_b also submitted by Pass 2 (deps met). Total: 3 tasks.
        await h.CompleteTaskAsync(taskA, output: "all good", issueCount: 0);

        Assert.Equal(3, h.Submitter.SubmittedTasks.Count);

        var descs = h.Submitter.SubmittedDescriptions;
        var bIndex = descs.IndexOf("Step B fix");
        var cIndex = descs.IndexOf("Step C done");
        Assert.True(bIndex >= 0 && cIndex >= 0, "Both step_b and step_c should be submitted");
        Assert.True(cIndex < bIndex, "Router should have submitted success target (step_c) before step_b");
    }

    // ── Feedback loop ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FeedbackLoop_RerunsUntilCapThenCancels()
    {
        // partial_retry_scope: FROM_CODEGEN resets `review` to Pending on each code re-run,
        // so the full code→review loop runs twice before the cap is hit.
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("loop", """
            id: loop
            name: Loop Test
            convergence_policy:
              max_iterations: 2
              escalation_target: CANCEL
              partial_retry_scope: FROM_CODEGEN
            steps:
              - id: code
                type: agent
                agent: coder
                prompt: "Write code"
              - id: review
                type: agent
                agent: reviewer
                depends_on: [code]
                prompt: "Review code"
                next: code
                max_iterations: 2
            """);

        var inst = await h.StartAsync("loop");

        // Iteration 1: code submitted
        Assert.Single(h.Submitter.SubmittedTasks);
        var codeTask1 = h.Submitter.SubmittedTasks.Keys.First();

        // code completes → review submitted
        await h.CompleteTaskAsync(codeTask1);
        Assert.Equal(2, h.Submitter.SubmittedTasks.Count);
        var reviewTask1 = h.Submitter.SubmittedTasks.Keys.Skip(1).First();

        // review completes with issues → code re-submitted (iter 2); review reset to Pending (FROM_CODEGEN)
        await h.CompleteTaskAsync(reviewTask1, issueCount: 2);
        Assert.Equal(3, h.Submitter.SubmittedTasks.Count);
        var codeTask2 = h.Submitter.SubmittedTasks.Keys.Skip(2).First();

        // code (iteration 2) completes → review (now Pending again) submitted again
        await h.CompleteTaskAsync(codeTask2);
        Assert.Equal(4, h.Submitter.SubmittedTasks.Count);
        var reviewTask2 = h.Submitter.SubmittedTasks.Keys.Skip(3).First();

        // review completes with issues again → code.Iteration=2, effectiveMax=2 → 2 < 2 is FALSE → CANCEL
        await h.CompleteTaskAsync(reviewTask2, issueCount: 1);

        // Workflow should be Failed or Cancelled (CANCEL escalation)
        var updated = h.Engine.GetInstance(inst.InstanceId);
        Assert.True(updated is null
                    || updated.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled,
                    $"Expected Failed or Cancelled but got {updated?.Status}");

        // code should NOT have been submitted a third time
        Assert.Equal(4, h.Submitter.SubmittedTasks.Count);
    }

    [Fact]
    public async Task FeedbackLoop_NoIssuesOnFirstReview_BreaksLoop()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("loop_clean", """
            id: loop_clean
            name: Loop Clean
            convergence_policy:
              max_iterations: 3
              escalation_target: CANCEL
              partial_retry_scope: FROM_CODEGEN
            steps:
              - id: code
                type: agent
                agent: coder
                prompt: "Write code"
              - id: review
                type: agent
                agent: reviewer
                depends_on: [code]
                prompt: "Review code"
                next: code
                max_iterations: 3
            """);

        await h.StartAsync("loop_clean");

        var codeTask = h.Submitter.SubmittedTasks.Keys.First();
        await h.CompleteTaskAsync(codeTask);

        var reviewTask = h.Submitter.SubmittedTasks.Keys.Skip(1).First();

        // review completes WITHOUT issues → loop does NOT trigger (IssueCount == 0)
        await h.CompleteTaskAsync(reviewTask, issueCount: 0);

        // Only 2 tasks total — code + review, no re-run of code
        Assert.Equal(2, h.Submitter.SubmittedTasks.Count);
    }

    // ── Step failure skips downstream ─────────────────────────────────────────

    [Fact]
    public async Task StepFailure_SkipsDownstreamSteps()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("fail", """
            id: fail
            name: Failure Test
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
            """);

        var inst = await h.StartAsync("fail");

        var taskA = h.Submitter.SubmittedTasks.Keys.First();
        await h.FailTaskAsync(taskA, "Compilation error");

        // step_b and step_c should be Skipped; only 1 task was submitted
        Assert.Single(h.Submitter.SubmittedTasks);
        Assert.Equal(WorkflowStepStatus.Failed,  inst.StepExecutions["step_a"].Status);
        Assert.Equal(WorkflowStepStatus.Skipped, inst.StepExecutions["step_b"].Status);
        Assert.Equal(WorkflowStepStatus.Skipped, inst.StepExecutions["step_c"].Status);
        Assert.Equal(WorkflowStatus.Failed,      inst.Status);
    }

    // ── Pause / Resume ────────────────────────────────────────────────────────

    [Fact]
    public async Task Pause_BlocksNewStepSubmission_ResumeUnblocks()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("pause", """
            id: pause
            name: Pause Test
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
            """);

        var inst = await h.StartAsync("pause");

        // Pause before step_a completes
        await h.Engine.PauseAsync(inst.InstanceId);
        Assert.Equal(WorkflowStatus.Paused, inst.Status);

        // Complete step_a — step_b should NOT be submitted (paused)
        var taskA = h.Submitter.SubmittedTasks.Keys.First();
        await h.CompleteTaskAsync(taskA);
        Assert.Single(h.Submitter.SubmittedTasks); // still only step_a was submitted

        // Resume — step_b should now be submitted
        await h.Engine.ResumeAsync(inst.InstanceId);
        Assert.Equal(WorkflowStatus.Running, inst.Status);
        Assert.Equal(2, h.Submitter.SubmittedTasks.Count);
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_SkipsPendingSteps_CancelsSubmittedTask()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("cancel", """
            id: cancel
            name: Cancel Test
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
            """);

        var inst = await h.StartAsync("cancel");

        // step_a is running, step_b is pending
        var taskA = h.Submitter.SubmittedTasks.Keys.First();

        // Cancel the workflow before step_a finishes
        await h.Engine.CancelAsync(inst.InstanceId);

        Assert.Equal(WorkflowStatus.Cancelled, inst.Status);

        // step_a's task should have been cancelled
        Assert.Contains(taskA, h.Submitter.CancelledTaskIds);

        // step_b should be Skipped (never submitted)
        Assert.Equal(WorkflowStepStatus.Skipped, inst.StepExecutions["step_b"].Status);
        Assert.Single(h.Submitter.SubmittedTasks); // step_b was not submitted
    }

    // ── Parameter defaults applied ────────────────────────────────────────────

    [Fact]
    public async Task Parameters_DefaultsAppliedToMissingInputs()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("params", """
            id: params
            name: Parameter Defaults
            parameters:
              - name: language
                default: csharp
              - name: framework
                default: xunit
            steps:
              - id: gen
                type: agent
                agent: coder
                prompt: "Generate {{language}} tests using {{framework}}"
            """);

        // Pass no inputs — defaults should be applied
        var inst = await h.StartAsync("params");

        Assert.Equal("csharp", inst.InputContext["language"]);
        Assert.Equal("xunit",  inst.InputContext["framework"]);
    }

    // ── Context update ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateContext_ChangesInputContextWhileRunning()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("ctx", """
            id: ctx
            name: Context Update
            steps:
              - id: step_a
                type: agent
                agent: coder
                prompt: "Step A"
            """);

        var inst = await h.StartAsync("ctx");

        await h.Engine.UpdateContextAsync(inst.InstanceId,
            new Dictionary<string, string> { ["my_var"] = "hello" });

        Assert.Equal("hello", inst.InputContext["my_var"]);
    }

    // ── GetInstance / GetAllInstances ─────────────────────────────────────────

    [Fact]
    public async Task GetInstance_ReturnsCorrectInstance()
    {
        using var h = new WorkflowTestHarness();
        h.AddWorkflow("query", """
            id: query
            name: Query Test
            steps:
              - id: only
                type: agent
                agent: coder
                prompt: "Only step"
            """);

        var inst = await h.StartAsync("query");

        var fetched = h.Engine.GetInstance(inst.InstanceId);
        Assert.NotNull(fetched);
        Assert.Equal(inst.InstanceId, fetched.InstanceId);

        Assert.Single(h.Engine.GetAllInstances());
    }

    // ── WorkflowDefinitionLoader — ParseYaml ─────────────────────────────────

    [Fact]
    public void ParseYaml_LinearWorkflow_ParsesCorrectly()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:BuiltInTemplatesPath"] = Path.Combine(Path.GetTempPath(), "__none__")
            })
            .Build();

        var loader = new WorkflowDefinitionLoader(
            NullLogger<WorkflowDefinitionLoader>.Instance, config);

        var def = loader.ParseYaml("""
            id: my_wf
            name: My Workflow
            steps:
              - id: a
                type: agent
                agent: coder
                prompt: "Do A"
              - id: b
                type: agent
                agent: reviewer
                depends_on: [a]
                prompt: "Do B"
            """, "fallback");

        Assert.Equal("my_wf", def.Id);
        Assert.Equal("My Workflow", def.Name);
        Assert.Equal(2, def.Steps.Count);
        Assert.Equal("a", def.Steps[0].Id);
        Assert.Equal("b", def.Steps[1].Id);
        Assert.Single(def.Steps[1].DependsOn);
        Assert.Equal("a", def.Steps[1].DependsOn[0]);
    }

    [Fact]
    public void MapAgentName_ReturnsCorrectAgentType()
    {
        Assert.Equal(AgentType.CodeReview,    WorkflowDefinitionLoader.MapAgentName("reviewer"));
        Assert.Equal(AgentType.Refactoring,   WorkflowDefinitionLoader.MapAgentName("coder"));
        Assert.Equal(AgentType.TestGeneration, WorkflowDefinitionLoader.MapAgentName("tester"));
        Assert.Equal(AgentType.Debug,          WorkflowDefinitionLoader.MapAgentName("debug"));
        Assert.Equal(AgentType.Documentation,  WorkflowDefinitionLoader.MapAgentName("documenter"));
        Assert.Equal(AgentType.SecurityReview, WorkflowDefinitionLoader.MapAgentName("security"));
        // Unknown falls back to CodeReview
        Assert.Equal(AgentType.CodeReview,    WorkflowDefinitionLoader.MapAgentName("unknown_xyz"));
    }
}
