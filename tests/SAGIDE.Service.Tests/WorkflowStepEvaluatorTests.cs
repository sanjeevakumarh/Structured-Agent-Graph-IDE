using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Tests;

// ── Router evaluation ─────────────────────────────────────────────────────────

public class WorkflowStepEvaluatorConditionTests
{
    [Fact]
    public void EvaluateCondition_HasIssues_TrueWhenIssueCountPositive()
    {
        var exec = new WorkflowStepExecution { IssueCount = 3 };
        Assert.True(WorkflowStepEvaluators.EvaluateCondition("hasIssues", exec));
    }

    [Fact]
    public void EvaluateCondition_HasIssues_FalseWhenZeroIssues()
    {
        var exec = new WorkflowStepExecution { IssueCount = 0 };
        Assert.False(WorkflowStepEvaluators.EvaluateCondition("hasIssues", exec));
    }

    [Fact]
    public void EvaluateCondition_HasIssuesUnderscore_TrueWhenIssueCountPositive()
    {
        var exec = new WorkflowStepExecution { IssueCount = 1 };
        Assert.True(WorkflowStepEvaluators.EvaluateCondition("has_issues", exec));
    }

    [Fact]
    public void EvaluateCondition_Success_TrueWhenCompletedZeroIssues()
    {
        var exec = new WorkflowStepExecution { IssueCount = 0 };
        exec.Status = WorkflowStepStatus.Completed;
        Assert.True(WorkflowStepEvaluators.EvaluateCondition("success", exec));
    }

    [Fact]
    public void EvaluateCondition_Success_FalseWhenCompletedWithIssues()
    {
        var exec = new WorkflowStepExecution { IssueCount = 2 };
        exec.Status = WorkflowStepStatus.Completed;
        Assert.False(WorkflowStepEvaluators.EvaluateCondition("success", exec));
    }

    [Fact]
    public void EvaluateCondition_Failed_TrueWhenStatusFailed()
    {
        var exec = new WorkflowStepExecution();
        exec.Status = WorkflowStepStatus.Failed;
        Assert.True(WorkflowStepEvaluators.EvaluateCondition("failed", exec));
    }

    [Fact]
    public void EvaluateCondition_OutputContains_TrueWhenOutputHasText()
    {
        var exec = new WorkflowStepExecution { Output = "Build PASS: 42 tests" };
        Assert.True(WorkflowStepEvaluators.EvaluateCondition("output.contains('PASS')", exec));
    }

    [Fact]
    public void EvaluateCondition_OutputContains_FalseWhenOutputLacksText()
    {
        var exec = new WorkflowStepExecution { Output = "Build FAIL: 3 errors" };
        Assert.False(WorkflowStepEvaluators.EvaluateCondition("output.contains('PASS')", exec));
    }

    [Fact]
    public void EvaluateCondition_OutputContains_CaseInsensitive()
    {
        var exec = new WorkflowStepExecution { Output = "all tests passed" };
        Assert.True(WorkflowStepEvaluators.EvaluateCondition("output.contains('PASSED')", exec));
    }

    [Fact]
    public void EvaluateCondition_UnknownCondition_ReturnsFalse()
    {
        var exec = new WorkflowStepExecution();
        Assert.False(WorkflowStepEvaluators.EvaluateCondition("unknown_condition", exec));
    }
}

// ── Constraint expression evaluation ─────────────────────────────────────────

public class WorkflowStepEvaluatorConstraintTests
{
    private static WorkflowInstance MakeInst(string stepId, int exitCode)
    {
        var inst = new WorkflowInstance();
        inst.StepExecutions[stepId] = new WorkflowStepExecution
        {
            StepId   = stepId,
            ExitCode = exitCode,
            Status   = WorkflowStepStatus.Completed,
        };
        return inst;
    }

    [Fact]
    public void ExitCode_PassesWhenEqual()
    {
        var inst = MakeInst("build", 0);
        var (passed, reason) = WorkflowStepEvaluators.EvaluateConstraintExpr("exit_code(build) == 0", inst);
        Assert.True(passed, reason);
    }

    [Fact]
    public void ExitCode_FailsWhenNotEqual()
    {
        var inst = MakeInst("build", 1);
        var (passed, _) = WorkflowStepEvaluators.EvaluateConstraintExpr("exit_code(build) == 0", inst);
        Assert.False(passed);
    }

    [Fact]
    public void ExitCode_GreaterThan()
    {
        var inst = MakeInst("build", 5);
        var (passed, _) = WorkflowStepEvaluators.EvaluateConstraintExpr("exit_code(build) > 3", inst);
        Assert.True(passed);
    }

    [Fact]
    public void IssueCount_PassesWhenZero()
    {
        var inst = new WorkflowInstance();
        inst.StepExecutions["review"] = new WorkflowStepExecution
        {
            StepId = "review", IssueCount = 0, Status = WorkflowStepStatus.Completed
        };
        var (passed, _) = WorkflowStepEvaluators.EvaluateConstraintExpr("issue_count(review) == 0", inst);
        Assert.True(passed);
    }

    [Fact]
    public void IssueCount_FailsWhenPositive()
    {
        var inst = new WorkflowInstance();
        inst.StepExecutions["review"] = new WorkflowStepExecution
        {
            StepId = "review", IssueCount = 3, Status = WorkflowStepStatus.Completed
        };
        var (passed, _) = WorkflowStepEvaluators.EvaluateConstraintExpr("issue_count(review) == 0", inst);
        Assert.False(passed);
    }

    [Fact]
    public void OutputContains_PassesWhenTextFound()
    {
        var inst = new WorkflowInstance();
        inst.StepExecutions["test"] = new WorkflowStepExecution
        {
            StepId = "test", Output = "All tests PASS", Status = WorkflowStepStatus.Completed
        };
        var (passed, _) = WorkflowStepEvaluators.EvaluateConstraintExpr("output(test).contains('PASS')", inst);
        Assert.True(passed);
    }

    [Fact]
    public void OutputContains_FailsWhenTextAbsent()
    {
        var inst = new WorkflowInstance();
        inst.StepExecutions["test"] = new WorkflowStepExecution
        {
            StepId = "test", Output = "Tests FAILED", Status = WorkflowStepStatus.Completed
        };
        var (passed, _) = WorkflowStepEvaluators.EvaluateConstraintExpr("output(test).contains('PASS')", inst);
        Assert.False(passed);
    }

    [Fact]
    public void MalformedExpression_ReturnsFalseWithReason()
    {
        var inst = new WorkflowInstance();
        var (passed, reason) = WorkflowStepEvaluators.EvaluateConstraintExpr("garbage_expression", inst);
        Assert.False(passed);
        Assert.Contains("Unrecognized", reason);
    }

    [Fact]
    public void StepNotFound_ReturnsFalseWithReason()
    {
        var inst = new WorkflowInstance(); // no steps added
        var (passed, reason) = WorkflowStepEvaluators.EvaluateConstraintExpr("exit_code(missing) == 0", inst);
        Assert.False(passed);
        Assert.Contains("missing", reason);
    }
}

// ── DAG / convergence helpers ─────────────────────────────────────────────────

public class WorkflowStepEvaluatorDagTests
{
    private static (WorkflowInstance Inst, WorkflowDefinition Def) MakeLinear()
    {
        // a → b → c
        var def = new WorkflowDefinition
        {
            Id    = "test",
            Name  = "Test",
            Steps =
            [
                new WorkflowStepDef { Id = "a", Type = "agent" },
                new WorkflowStepDef { Id = "b", Type = "agent", DependsOn = ["a"] },
                new WorkflowStepDef { Id = "c", Type = "agent", DependsOn = ["b"] },
            ]
        };
        var inst = new WorkflowInstance { DefinitionId = "test" };
        foreach (var s in def.Steps)
            inst.StepExecutions[s.Id] = new WorkflowStepExecution { StepId = s.Id };
        return (inst, def);
    }

    [Fact]
    public void IsInstanceDone_FalseWhenStepsPending()
    {
        var (inst, def) = MakeLinear();
        Assert.False(WorkflowStepEvaluators.IsInstanceDone(inst, def));
    }

    [Fact]
    public void IsInstanceDone_TrueWhenAllTerminal()
    {
        var (inst, def) = MakeLinear();
        foreach (var exec in inst.StepExecutions.Values)
            exec.Status = WorkflowStepStatus.Completed;
        Assert.True(WorkflowStepEvaluators.IsInstanceDone(inst, def));
    }

    [Fact]
    public void IsInstanceDone_TrueWithMixOfCompletedSkippedFailed()
    {
        var (inst, def) = MakeLinear();
        inst.StepExecutions["a"].Status = WorkflowStepStatus.Completed;
        inst.StepExecutions["b"].Status = WorkflowStepStatus.Failed;
        inst.StepExecutions["c"].Status = WorkflowStepStatus.Skipped;
        Assert.True(WorkflowStepEvaluators.IsInstanceDone(inst, def));
    }

    [Fact]
    public void SkipDownstream_MarksDependentsSkipped()
    {
        var (inst, def) = MakeLinear();
        inst.StepExecutions["a"].Status = WorkflowStepStatus.Completed;
        inst.StepExecutions["b"].Status = WorkflowStepStatus.Pending;
        inst.StepExecutions["c"].Status = WorkflowStepStatus.Pending;

        WorkflowStepEvaluators.SkipDownstream("a", inst, def);

        Assert.Equal(WorkflowStepStatus.Skipped, inst.StepExecutions["b"].Status);
        Assert.Equal(WorkflowStepStatus.Skipped, inst.StepExecutions["c"].Status);
    }

    [Fact]
    public void SkipDownstream_DoesNotSkipAlreadyCompletedSteps()
    {
        var (inst, def) = MakeLinear();
        inst.StepExecutions["a"].Status = WorkflowStepStatus.Completed;
        inst.StepExecutions["b"].Status = WorkflowStepStatus.Completed;
        inst.StepExecutions["c"].Status = WorkflowStepStatus.Pending;

        // Skipping from a should skip c (via b's Pending → Skipped path)
        // but b is not Pending so the traversal stops at b
        WorkflowStepEvaluators.SkipDownstream("a", inst, def);

        Assert.Equal(WorkflowStepStatus.Completed, inst.StepExecutions["b"].Status); // unchanged
        Assert.Equal(WorkflowStepStatus.Pending, inst.StepExecutions["c"].Status);   // not reached
    }

    [Fact]
    public void GetDescendantStepIds_ReturnsTransitiveClosure()
    {
        var def = new WorkflowDefinition
        {
            Id    = "test",
            Name  = "Test",
            Steps =
            [
                new WorkflowStepDef { Id = "root",  Type = "agent" },
                new WorkflowStepDef { Id = "child1", Type = "agent", DependsOn = ["root"] },
                new WorkflowStepDef { Id = "child2", Type = "agent", DependsOn = ["root"] },
                new WorkflowStepDef { Id = "grand",  Type = "agent", DependsOn = ["child1"] },
                new WorkflowStepDef { Id = "other",  Type = "agent" }, // disconnected
            ]
        };

        var descendants = WorkflowStepEvaluators.GetDescendantStepIds("root", def);

        Assert.Contains("child1", descendants);
        Assert.Contains("child2", descendants);
        Assert.Contains("grand",  descendants);
        Assert.DoesNotContain("root",  descendants);
        Assert.DoesNotContain("other", descendants);
    }

    [Fact]
    public void ResetLoopBodyForNewIteration_ResetsDescendantSteps()
    {
        // root → child → grand; reset from root should clear child+grand
        var def = new WorkflowDefinition
        {
            Id    = "test",
            Name  = "Test",
            Steps =
            [
                new WorkflowStepDef { Id = "root",  Type = "agent" },
                new WorkflowStepDef { Id = "child", Type = "agent", DependsOn = ["root"] },
                new WorkflowStepDef { Id = "grand", Type = "agent", DependsOn = ["child"] },
            ]
        };
        var inst = new WorkflowInstance();
        foreach (var s in def.Steps)
        {
            inst.StepExecutions[s.Id] = new WorkflowStepExecution
            {
                StepId    = s.Id,
                Status    = WorkflowStepStatus.Completed,
                Output    = "old output",
                IssueCount = 5,
            };
        }

        WorkflowStepEvaluators.ResetLoopBodyForNewIteration("root", inst, def);

        // root itself is NOT reset
        Assert.Equal(WorkflowStepStatus.Completed, inst.StepExecutions["root"].Status);
        Assert.Equal("old output", inst.StepExecutions["root"].Output);

        // descendants are reset
        Assert.Equal(WorkflowStepStatus.Pending, inst.StepExecutions["child"].Status);
        Assert.Null(inst.StepExecutions["child"].Output);
        Assert.Equal(0, inst.StepExecutions["child"].IssueCount);

        Assert.Equal(WorkflowStepStatus.Pending, inst.StepExecutions["grand"].Status);
    }

    [Fact]
    public void InjectConvergenceHints_AddsHintsToInputContext()
    {
        var inst = new WorkflowInstance();
        var validationStep = new WorkflowStepDef { Id = "validate" };
        var validationExec = new WorkflowStepExecution
        {
            StepId     = "validate",
            IssueCount = 2,
            Output     = "Found 2 code issues",
            Status     = WorkflowStepStatus.Completed,
        };
        var refactorExec = new WorkflowStepExecution
        {
            StepId    = "refactor",
            Iteration = 1,
            Status    = WorkflowStepStatus.Completed,
        };

        WorkflowStepEvaluators.InjectConvergenceHints(validationStep, validationExec, refactorExec, inst);

        Assert.True(inst.InputContext.ContainsKey("convergence_hints"));
        Assert.Contains("2 issue", inst.InputContext["convergence_hints"]);
        Assert.Contains("Found 2 code issues", inst.InputContext["convergence_hints"]);
    }
}
