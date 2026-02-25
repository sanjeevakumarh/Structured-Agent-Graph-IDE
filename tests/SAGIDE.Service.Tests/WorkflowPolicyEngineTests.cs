using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="WorkflowPolicyEngine"/> covering:
/// - Disabled policy → unconditional Allow
/// - Blocked agent types
/// - Protected path glob patterns (*, **, ?)
/// - MaxStepsPerWorkflow enforcement
/// </summary>
public class WorkflowPolicyEngineTests
{
    private static WorkflowPolicyEngine Make(WorkflowPolicyConfig config)
        => new(config, NullLogger<WorkflowPolicyEngine>.Instance);

    private static WorkflowStepDef Step(string id, string? agent = null)
        => new() { Id = id, Agent = agent ?? id };

    private static WorkflowInstance Instance(
        string[] filePaths,
        int executedStepCount = 0)
    {
        var inst = new WorkflowInstance { InstanceId = "i1" };
        inst.FilePaths = [.. filePaths];
        // Populate StepExecutions with Completed entries to simulate executed steps
        for (var i = 0; i < executedStepCount; i++)
            inst.StepExecutions[$"s{i}"] = new WorkflowStepExecution
            {
                StepId = $"s{i}",
                Status = WorkflowStepStatus.Completed
            };
        return inst;
    }

    // ── Policy disabled ───────────────────────────────────────────────────────

    [Fact]
    public void Disabled_AlwaysAllows_EvenWithBlockedAgentAndProtectedPath()
    {
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled              = false,
            BlockedAgentTypes    = ["deployer"],
            ProtectedPathPatterns = ["**/*.env"],
        });

        var result = engine.Check(Step("deploy", "deployer"),
            Instance(["config/.env"]));

        Assert.True(result.IsAllowed);
        Assert.Null(result.DenyReason);
    }

    // ── No restrictions ───────────────────────────────────────────────────────

    [Fact]
    public void Enabled_NothingConfigured_Allows()
    {
        var engine = Make(new WorkflowPolicyConfig { Enabled = true });

        var result = engine.Check(Step("review"), Instance(["src/Foo.cs"]));

        Assert.True(result.IsAllowed);
    }

    // ── Blocked agent types ───────────────────────────────────────────────────

    [Fact]
    public void BlockedAgent_ByStepAgent_Denies()
    {
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled           = true,
            BlockedAgentTypes = ["deployer"],
        });

        var result = engine.Check(Step("step1", "deployer"), Instance([]));

        Assert.False(result.IsAllowed);
        Assert.Contains("deployer", result.DenyReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlockedAgent_CaseInsensitive_Denies()
    {
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled           = true,
            BlockedAgentTypes = ["Deployer"],
        });

        var result = engine.Check(Step("step1", "DEPLOYER"), Instance([]));

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void NonBlockedAgent_Allows()
    {
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled           = true,
            BlockedAgentTypes = ["deployer"],
        });

        var result = engine.Check(Step("review", "reviewer"), Instance([]));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void BlockedAgent_FallsBackToStepId_WhenAgentIsNull()
    {
        // When step.Agent is null, the engine falls back to step.Id for the agent check.
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled           = true,
            BlockedAgentTypes = ["dangerous_step"],
        });

        // Step.Agent = null → engine uses step.Id = "dangerous_step"
        var step   = new WorkflowStepDef { Id = "dangerous_step", Agent = null };
        var result = engine.Check(step, Instance([]));

        Assert.False(result.IsAllowed);
    }

    // ── Protected path patterns ───────────────────────────────────────────────

    [Fact]
    public void ProtectedPath_DoubleStarGlob_MatchesNestedEnvFile()
    {
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled               = true,
            ProtectedPathPatterns = ["**/*.env"],
        });

        var result = engine.Check(Step("s"), Instance(["config/secrets/.env"]));

        Assert.False(result.IsAllowed);
        Assert.Contains(".env", result.DenyReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedPath_DoubleStarGlob_SafeFileAllowed()
    {
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled               = true,
            ProtectedPathPatterns = ["**/*.env"],
        });

        var result = engine.Check(Step("s"), Instance(["src/Foo.cs"]));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void ProtectedPath_SingleStarGlob_DoesNotMatchSubdirectory()
    {
        // "*.env" with a single * should not match "config/.env" because * can't cross /
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled               = true,
            ProtectedPathPatterns = ["*.env"],
        });

        // "*.env" matches ".env" at root level but NOT "sub/.env"
        var rootResult = engine.Check(Step("s"), Instance([".env"]));
        var subResult  = engine.Check(Step("s"), Instance(["sub/.env"]));

        Assert.False(rootResult.IsAllowed);  // root .env matched
        Assert.True(subResult.IsAllowed);    // sub-dir not matched by single *
    }

    [Fact]
    public void ProtectedPath_NoFiles_Allows()
    {
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled               = true,
            ProtectedPathPatterns = ["**/*.env"],
        });

        var result = engine.Check(Step("s"), Instance([]));

        Assert.True(result.IsAllowed);
    }

    // ── MaxStepsPerWorkflow ───────────────────────────────────────────────────

    [Fact]
    public void MaxSteps_Exceeded_Denies()
    {
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled              = true,
            MaxStepsPerWorkflow  = 3,
        });

        // 3 already-executed steps = limit reached
        var result = engine.Check(Step("next"), Instance([], executedStepCount: 3));

        Assert.False(result.IsAllowed);
        Assert.Contains("MaxStepsPerWorkflow", result.DenyReason);
    }

    [Fact]
    public void MaxSteps_NotExceeded_Allows()
    {
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled             = true,
            MaxStepsPerWorkflow = 5,
        });

        var result = engine.Check(Step("next"), Instance([], executedStepCount: 4));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void MaxSteps_Zero_Disabled_Allows()
    {
        // MaxStepsPerWorkflow = 0 means the guard is off
        var engine = Make(new WorkflowPolicyConfig
        {
            Enabled             = true,
            MaxStepsPerWorkflow = 0,
        });

        var result = engine.Check(Step("next"), Instance([], executedStepCount: 999));

        Assert.True(result.IsAllowed);
    }
}
