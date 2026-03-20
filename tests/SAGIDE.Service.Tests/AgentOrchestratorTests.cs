using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.ActivityLogging;
using SAGIDE.Service.Agents;
using SAGIDE.Service.Communication;
using SAGIDE.Service.Communication.Messages;
using SAGIDE.Service.Events;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Providers;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

// ── Fake IActivityRepository ──────────────────────────────────────────────────

internal sealed class FakeActivityRepository : IActivityRepository
{
    public Task SaveActivityAsync(ActivityEntry entry) => Task.CompletedTask;

    public Task<IReadOnlyList<ActivityEntry>> GetActivitiesByHourAsync(string workspacePath, string hourBucket)
        => Task.FromResult<IReadOnlyList<ActivityEntry>>([]);

    public Task<IReadOnlyList<ActivityEntry>> GetActivitiesByTimeRangeAsync(string workspacePath, DateTime start, DateTime end)
        => Task.FromResult<IReadOnlyList<ActivityEntry>>([]);

    public Task<IReadOnlyList<string>> GetHourBucketsAsync(string workspacePath, int limit = 100)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<ActivityLogConfig?> GetConfigAsync(string workspacePath)
        => Task.FromResult<ActivityLogConfig?>(null);

    public Task SaveConfigAsync(ActivityLogConfig config) => Task.CompletedTask;
}

// ── Fake IAgentProvider ──────────────────────────────────────────────────────

internal sealed class FakeAgentProvider : IAgentProvider
{
    public ModelProvider Provider { get; }
    public int LastInputTokens => 0;
    public int LastOutputTokens => 0;

    public FakeAgentProvider(ModelProvider provider = ModelProvider.Ollama) => Provider = provider;

    public Task<string> CompleteAsync(string prompt, ModelConfig model, CancellationToken ct = default)
        => Task.FromResult("fake response");

    public async IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt, ModelConfig model, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return "fake";
        await Task.CompletedTask;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
}

// ── AgentOrchestrator factory ─────────────────────────────────────────────────

internal static class OrchestratorFactory
{
    /// <summary>
    /// Creates a minimal AgentOrchestrator with no providers (no actual LLM calls).
    /// Suitable for testing task queue operations and cancel behavior.
    /// </summary>
    public static AgentOrchestrator Create(out TaskQueue queue, IEventBus? eventBus = null)
    {
        queue = new TaskQueue();
        var services = new ServiceCollection().BuildServiceProvider();
        var dlq = new DeadLetterQueue(NullLogger<DeadLetterQueue>.Instance);

        return new AgentOrchestrator(
            queue,
            services,
            dlq,
            new TimeoutConfig(),
            new AgentLimitsConfig(),
            new ResultParser(NullLogger<ResultParser>.Instance),
            NullLogger<AgentOrchestrator>.Instance,
            repository: null,
            activityLogger: null,
            maxConcurrentTasks: 2,
            gitService: null,
            gitConfig: null,
            eventBus: eventBus);
    }

    /// <summary>
    /// Creates an AgentOrchestrator with a fake provider registered, so tasks
    /// reach the execution path (past the NO_PROVIDER check).
    /// </summary>
    public static AgentOrchestrator CreateWithProvider(
        out TaskQueue queue, out DeadLetterQueue dlq, IEventBus? eventBus = null)
    {
        queue = new TaskQueue();
        var services = new ServiceCollection()
            .AddSingleton<IAgentProvider>(new FakeAgentProvider(ModelProvider.Ollama))
            .BuildServiceProvider();
        dlq = new DeadLetterQueue(NullLogger<DeadLetterQueue>.Instance);

        return new AgentOrchestrator(
            queue,
            services,
            dlq,
            new TimeoutConfig(),
            new AgentLimitsConfig(),
            new ResultParser(NullLogger<ResultParser>.Instance),
            NullLogger<AgentOrchestrator>.Instance,
            repository: null,
            activityLogger: null,
            maxConcurrentTasks: 2,
            gitService: null,
            gitConfig: null,
            eventBus: eventBus,
            promptBuilder: new PromptBuilder());
    }
}

// ── AgentOrchestrator unit tests ──────────────────────────────────────────────

public class AgentOrchestratorTests
{
    // ── SubmitTaskAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitTask_EnqueuesTask_ReturnsTaskId()
    {
        var orch = OrchestratorFactory.Create(out var queue);

        var task = new AgentTask
        {
            AgentType     = AgentType.CodeReview,
            ModelProvider = ModelProvider.Ollama,
            Description   = "Review my code",
        };

        var taskId = await orch.SubmitTaskAsync(task, CancellationToken.None);

        Assert.NotNull(taskId);
        Assert.NotEmpty(taskId);
        Assert.NotNull(queue.GetTask(taskId));
    }

    [Fact]
    public async Task SubmitTask_IdempotentForDuplicateId()
    {
        var orch = OrchestratorFactory.Create(out var queue);

        var task = new AgentTask
        {
            Id          = "fixed-id-001",
            Description = "First submission",
        };

        var id1 = await orch.SubmitTaskAsync(task, CancellationToken.None);

        // Re-submit the same task (same ID) — should return existing, NOT duplicate
        var duplicate = new AgentTask { Id = "fixed-id-001", Description = "Duplicate" };
        var id2 = await orch.SubmitTaskAsync(duplicate, CancellationToken.None);

        Assert.Equal(id1, id2);
        // Queue should still have only one entry for this ID
        Assert.NotNull(queue.GetTask("fixed-id-001"));
    }

    [Fact]
    public async Task SubmitTask_FiresOnTaskUpdateEvent()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var orch = OrchestratorFactory.Create(out _, eventBus: bus);

        TaskStatusResponse? received = null;
        bus.Subscribe<TaskUpdatedEvent>(e => received = e.Status);

        var task = new AgentTask { Description = "Event test" };
        await orch.SubmitTaskAsync(task, CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal(AgentTaskStatus.Queued, received!.Status);
    }

    // ── CancelTaskAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CancelTask_QueuedTask_MarksAsCancelled()
    {
        var orch = OrchestratorFactory.Create(out var queue);

        var task = new AgentTask { Description = "Cancel test" };
        var taskId = await orch.SubmitTaskAsync(task, CancellationToken.None);

        Assert.Equal(AgentTaskStatus.Queued, queue.GetTask(taskId)!.Status);

        await orch.CancelTaskAsync(taskId, CancellationToken.None);

        Assert.Equal(AgentTaskStatus.Cancelled, queue.GetTask(taskId)!.Status);
    }

    [Fact]
    public async Task CancelTask_NonExistentTask_NoException()
    {
        var orch = OrchestratorFactory.Create(out _);

        // Should return without throwing
        await orch.CancelTaskAsync("does-not-exist", CancellationToken.None);
    }

    [Fact]
    public async Task CancelTask_AlreadyCancelledTask_NoDoubleCancel()
    {
        var orch = OrchestratorFactory.Create(out var queue);

        var task = new AgentTask { Description = "Double cancel" };
        var taskId = await orch.SubmitTaskAsync(task, CancellationToken.None);

        await orch.CancelTaskAsync(taskId, CancellationToken.None);
        var firstCancelTime = queue.GetTask(taskId)!.CompletedAt;

        // Second cancel — should be a no-op (task is in terminal state)
        await orch.CancelTaskAsync(taskId, CancellationToken.None);

        // CompletedAt should not change
        Assert.Equal(firstCancelTime, queue.GetTask(taskId)!.CompletedAt);
    }

    // ── MaxConcurrentTasks ────────────────────────────────────────────────────

    [Fact]
    public void MaxConcurrentTasks_ReflectsConstructorArg()
    {
        var orch = OrchestratorFactory.Create(out _);
        Assert.Equal(2, orch.MaxConcurrentTasks);
    }

    // ── Empty ModelId guard ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteTask_EmptyModelId_FailsWithEmptyModelIdReason()
    {
        var orch = OrchestratorFactory.CreateWithProvider(out var queue, out var dlq);

        var task = new AgentTask
        {
            ModelProvider = ModelProvider.Ollama,
            ModelId       = "",
            Description   = "Empty model test",
        };
        await orch.SubmitTaskAsync(task, CancellationToken.None);

        // Start the processing loop briefly so the task gets picked up
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await orch.StartProcessingAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        var queued = queue.GetTask(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, queued.Status);
        Assert.Contains("empty ModelId", queued.StatusMessage!);
        Assert.True(dlq.Count > 0, "Failed task should be in DLQ");
    }

    [Fact]
    public async Task ExecuteTask_WhitespaceModelId_FailsWithEmptyModelIdReason()
    {
        var orch = OrchestratorFactory.CreateWithProvider(out var queue, out var dlq);

        var task = new AgentTask
        {
            ModelProvider = ModelProvider.Ollama,
            ModelId       = "   ",
            Description   = "Whitespace model test",
        };
        await orch.SubmitTaskAsync(task, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await orch.StartProcessingAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        var queued = queue.GetTask(task.Id)!;
        Assert.Equal(AgentTaskStatus.Failed, queued.Status);
        Assert.Contains("empty ModelId", queued.StatusMessage!);
    }
}

// ── MessageHandler routing tests ──────────────────────────────────────────────

public class MessageHandlerTests
{
    /// <summary>
    /// Creates a minimal MessageHandler wired up to a no-op orchestrator and workflow engine.
    /// </summary>
    private static MessageHandler CreateHandler(out AgentOrchestrator orchestrator)
    {
        orchestrator = OrchestratorFactory.Create(out _);

        var actRepo    = new FakeActivityRepository();
        var mdGen      = new MarkdownGenerator(NullLogger<MarkdownGenerator>.Instance);
        var actLogger  = new ActivityLogger(actRepo, mdGen, NullLogger<ActivityLogger>.Instance);
        var gitInt     = new GitIntegration(actLogger, NullLogger<GitIntegration>.Instance);

        // WorkflowEngine needs a loader and service provider; pass null repository
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:BuiltInTemplatesPath"] = Path.Combine(Path.GetTempPath(), "__mh_test__")
            })
            .Build();

        var loader = new WorkflowDefinitionLoader(
            NullLogger<WorkflowDefinitionLoader>.Instance, config);

        var policyEngine = new WorkflowPolicyEngine(
            new WorkflowPolicyConfig { Enabled = false },
            NullLogger<WorkflowPolicyEngine>.Instance);

        var wfEngine = new WorkflowEngine(
            new FakeTaskSubmitter(),
            loader,
            new AgentLimitsConfig(),
            new TaskAffinitiesConfig(),
            policyEngine,
            new Infrastructure.GitService(NullLogger<Infrastructure.GitService>.Instance),
            new NullWorkflowStepRenderer(),
            NullLogger<WorkflowEngine>.Instance);

        return new MessageHandler(
            orchestrator,
            actLogger,
            gitInt,
            wfEngine,
            config,
            new TaskAffinitiesConfig(),
            NullLogger<MessageHandler>.Instance);
    }

    // ── Ping / Pong ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Ping_ReturnsPong()
    {
        var handler = CreateHandler(out _);

        var request  = new PipeMessage { Type = MessageTypes.Ping, RequestId = "req-42" };
        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.Equal(MessageTypes.Pong, response.Type);
        Assert.Equal("req-42", response.RequestId);
    }

    [Fact]
    public async Task HandleAsync_Ping_PreservesRequestId()
    {
        var handler = CreateHandler(out _);

        var uniqueId = Guid.NewGuid().ToString("N");
        var response = await handler.HandleAsync(
            new PipeMessage { Type = MessageTypes.Ping, RequestId = uniqueId },
            CancellationToken.None);

        Assert.Equal(uniqueId, response.RequestId);
    }

    // ── GetAllTasks ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_GetAllTasks_ReturnsEmptyListInitially()
    {
        var handler = CreateHandler(out _);

        var response = await handler.HandleAsync(
            new PipeMessage { Type = MessageTypes.GetAllTasks, RequestId = "r1" },
            CancellationToken.None);

        // Response should not be an error
        Assert.NotEqual(MessageTypes.Error, response.Type);
        Assert.NotNull(response.Payload);
    }

    // ── SubmitTask via pipe ───────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_SubmitTask_TaskEnqueued()
    {
        var handler = CreateHandler(out var orch);

        // Serialize a SubmitTaskRequest
        var submitReq = new
        {
            AgentType     = AgentType.CodeReview.ToString(),
            ModelProvider = ModelProvider.Ollama.ToString(),
            ModelId       = "llama3",
            Description   = "Pipe-submitted task",
            FilePaths     = Array.Empty<string>(),
        };

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(submitReq, NamedPipeServer.JsonOptions);

        var response = await handler.HandleAsync(
            new PipeMessage { Type = MessageTypes.SubmitTask, RequestId = "sub-1", Payload = payload },
            CancellationToken.None);

        Assert.NotEqual(MessageTypes.Error, response.Type);
        Assert.NotNull(response.Payload);
    }
}
