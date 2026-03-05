using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Events;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Tests;

public class WorkflowLoopControllerTests
{
    private static (WorkflowInstanceStore Store, WorkflowLoopController Controller) Make()
    {
        var bus   = new NullEventBus();
        var store = new WorkflowInstanceStore(
            null,
            new GitService(NullLogger<GitService>.Instance),
            bus,
            NullLogger<WorkflowInstanceStore>.Instance);
        var ctrl = new WorkflowLoopController(store, NullLogger<WorkflowLoopController>.Instance);
        return (store, ctrl);
    }

    private static (WorkflowInstance Inst, WorkflowDefinition Def) MakeRunning(WorkflowInstanceStore store)
    {
        var def = new WorkflowDefinition
        {
            Id    = "test",
            Name  = "Test",
            Steps = [new WorkflowStepDef { Id = "refactor", Type = "agent" }]
        };
        var inst = new WorkflowInstance { DefinitionId = "test", Status = WorkflowStatus.Running };
        inst.StepExecutions["refactor"] = new WorkflowStepExecution { StepId = "refactor" };
        store.Add(inst, def);
        return (inst, def);
    }

    [Fact]
    public async Task EscalateLoop_Cancel_SetsInstanceFailed()
    {
        var (store, ctrl) = Make();
        var (inst, def)   = MakeRunning(store);
        var stepDef  = def.Steps[0];
        var stepExec = inst.StepExecutions["refactor"];

        await ctrl.EscalateLoopAsync(
            inst, def, stepDef, stepExec,
            AgentType.Refactoring, "Too many iterations", "CANCEL");

        Assert.Equal(WorkflowStatus.Failed, inst.Status);
        Assert.Equal(WorkflowStepStatus.Failed, stepExec.Status);
        Assert.NotNull(stepExec.Error);
    }

    [Fact]
    public async Task EscalateLoop_DLQ_SetsInstanceFailed()
    {
        var (store, ctrl) = Make();
        var (inst, def)   = MakeRunning(store);
        var stepDef  = def.Steps[0];
        var stepExec = inst.StepExecutions["refactor"];

        await ctrl.EscalateLoopAsync(
            inst, def, stepDef, stepExec,
            AgentType.Refactoring, "Contradiction detected", "DLQ");

        Assert.Equal(WorkflowStatus.Failed, inst.Status);
        Assert.Equal(WorkflowStepStatus.Failed, stepExec.Status);
        Assert.Contains("DLQ", stepExec.Error);
    }

    [Fact]
    public async Task EscalateLoop_HumanApproval_PausesInstance()
    {
        var (store, ctrl) = Make();
        var (inst, def)   = MakeRunning(store);
        var stepDef  = def.Steps[0];
        var stepExec = inst.StepExecutions["refactor"];

        await ctrl.EscalateLoopAsync(
            inst, def, stepDef, stepExec,
            AgentType.Refactoring, "Loop exceeded", "HUMAN_APPROVAL");

        Assert.Equal(WorkflowStatus.Paused, inst.Status);
        Assert.True(inst.IsPaused);
        Assert.Equal(WorkflowStepStatus.WaitingForApproval, stepExec.Status);
    }

    [Fact]
    public async Task EscalateLoop_HumanApproval_PublishesApprovalNeededEvent()
    {
        var bus   = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var store = new WorkflowInstanceStore(
            null,
            new GitService(NullLogger<GitService>.Instance),
            bus,
            NullLogger<WorkflowInstanceStore>.Instance);
        var ctrl = new WorkflowLoopController(store, NullLogger<WorkflowLoopController>.Instance);

        var def = new WorkflowDefinition
        {
            Id    = "test",
            Name  = "Test",
            Steps = [new WorkflowStepDef { Id = "step1", Type = "agent" }]
        };
        var inst = new WorkflowInstance { DefinitionId = "test", Status = WorkflowStatus.Running };
        inst.StepExecutions["step1"] = new WorkflowStepExecution { StepId = "step1" };
        store.Add(inst, def);

        WorkflowApprovalNeededEvent? receivedEvent = null;
        bus.Subscribe<WorkflowApprovalNeededEvent>(e => receivedEvent = e);

        await ctrl.EscalateLoopAsync(
            inst, def, def.Steps[0], inst.StepExecutions["step1"],
            AgentType.Refactoring, "Requires human review", "HUMAN_APPROVAL");

        Assert.NotNull(receivedEvent);
        Assert.Equal(inst.InstanceId, receivedEvent!.InstanceId);
        Assert.Equal("step1", receivedEvent.StepId);
    }

    [Fact]
    public void GetDescendantStepIds_ReturnsAllReachable()
    {
        var def = new WorkflowDefinition
        {
            Id    = "test",
            Name  = "Test",
            Steps =
            [
                new WorkflowStepDef { Id = "root",  Type = "agent" },
                new WorkflowStepDef { Id = "a",     Type = "agent", DependsOn = ["root"] },
                new WorkflowStepDef { Id = "b",     Type = "agent", DependsOn = ["root"] },
                new WorkflowStepDef { Id = "c",     Type = "agent", DependsOn = ["a"] },
                new WorkflowStepDef { Id = "other", Type = "agent" },
            ]
        };

        var result = WorkflowStepEvaluators.GetDescendantStepIds("root", def);

        Assert.Contains("a",     result);
        Assert.Contains("b",     result);
        Assert.Contains("c",     result);
        Assert.DoesNotContain("root",  result);
        Assert.DoesNotContain("other", result);
    }
}
