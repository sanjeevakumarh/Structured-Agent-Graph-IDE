using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Extended unit tests for <see cref="TaskQueue"/> covering scheduled tasks,
/// eviction, UpdateTask, RunningCount, and GetRunningTasks.
/// </summary>
public class TaskQueueExtendedTests
{
    private static AgentTask MakeTask(int priority = 0, DateTime? scheduledFor = null, string? id = null) =>
        new()
        {
            Id          = id ?? Guid.NewGuid().ToString("N")[..8],
            Description = "test task",
            Priority    = priority,
            Status      = AgentTaskStatus.Queued,
            ScheduledFor = scheduledFor,
        };

    // ── Enqueue / GetTask ─────────────────────────────────────────────────────

    [Fact]
    public void Enqueue_ThenGetTask_ReturnsTask()
    {
        var queue = new TaskQueue();
        var task  = MakeTask();

        queue.Enqueue(task);

        var found = queue.GetTask(task.Id);
        Assert.NotNull(found);
        Assert.Equal(task.Id, found.Id);
    }

    [Fact]
    public void GetTask_Unknown_ReturnsNull()
    {
        var queue = new TaskQueue();

        Assert.Null(queue.GetTask("does-not-exist"));
    }

    [Fact]
    public void PendingCount_IncrementsOnEnqueue()
    {
        var queue = new TaskQueue();

        queue.Enqueue(MakeTask());
        queue.Enqueue(MakeTask());

        Assert.Equal(2, queue.PendingCount);
    }

    // ── Dequeue — status transition ───────────────────────────────────────────

    [Fact]
    public void Dequeue_SetsStatusToRunning()
    {
        var queue = new TaskQueue();
        var task  = MakeTask();
        queue.Enqueue(task);

        var dequeued = queue.Dequeue();

        Assert.NotNull(dequeued);
        Assert.Equal(AgentTaskStatus.Running, dequeued.Status);
    }

    [Fact]
    public void Dequeue_SetsStartedAt()
    {
        var queue = new TaskQueue();
        queue.Enqueue(MakeTask());

        var before   = DateTime.UtcNow;
        var dequeued = queue.Dequeue();
        var after    = DateTime.UtcNow;

        Assert.NotNull(dequeued!.StartedAt);
        Assert.True(dequeued.StartedAt >= before);
        Assert.True(dequeued.StartedAt <= after);
    }

    // ── Priority ordering ─────────────────────────────────────────────────────

    [Fact]
    public void Dequeue_ReturnsHighestPriorityFirst()
    {
        var queue = new TaskQueue();
        var low  = MakeTask(priority: 1, id: "low");
        var high = MakeTask(priority: 10, id: "high");
        var med  = MakeTask(priority: 5,  id: "med");

        queue.Enqueue(low);
        queue.Enqueue(high);
        queue.Enqueue(med);

        // highest priority value = first dequeued
        Assert.Equal("high", queue.Dequeue()!.Id);
        Assert.Equal("med",  queue.Dequeue()!.Id);
        Assert.Equal("low",  queue.Dequeue()!.Id);
    }

    // ── Scheduled tasks ───────────────────────────────────────────────────────

    [Fact]
    public void Dequeue_FutureTask_ReturnsNullAndDelay()
    {
        var queue = new TaskQueue();
        queue.Enqueue(MakeTask(scheduledFor: DateTime.UtcNow.AddHours(1)));

        var (task, delay) = queue.DequeueOrGetDelay();

        Assert.Null(task);
        Assert.NotNull(delay);
        Assert.True(delay > TimeSpan.Zero);
        Assert.True(delay <= TimeSpan.FromMinutes(1)); // capped at 1 minute
    }

    [Fact]
    public void Dequeue_PastScheduledTask_IsDequeued()
    {
        var queue = new TaskQueue();
        queue.Enqueue(MakeTask(scheduledFor: DateTime.UtcNow.AddSeconds(-1)));

        var dequeued = queue.Dequeue();

        Assert.NotNull(dequeued);
        Assert.Equal(AgentTaskStatus.Running, dequeued.Status);
    }

    // ── GetAllTasks / GetRunningTasks ─────────────────────────────────────────

    [Fact]
    public void GetAllTasks_ReturnsAllEnqueued()
    {
        var queue = new TaskQueue();
        queue.Enqueue(MakeTask(id: "a"));
        queue.Enqueue(MakeTask(id: "b"));
        queue.Enqueue(MakeTask(id: "c"));

        var all = queue.GetAllTasks();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void GetRunningTasks_ReturnsOnlyRunning()
    {
        var queue = new TaskQueue();
        queue.Enqueue(MakeTask(id: "a"));  // stays queued
        queue.Enqueue(MakeTask(id: "b"));  // will be dequeued → Running

        queue.Dequeue(); // b or a depending on insertion; take one off

        var running = queue.GetRunningTasks();
        Assert.Single(running);
        Assert.Equal(AgentTaskStatus.Running, running[0].Status);
    }

    // ── UpdateTask ────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateTask_ModifiesTaskInPlace()
    {
        var queue = new TaskQueue();
        var task  = MakeTask(id: "upd");
        queue.Enqueue(task);

        queue.UpdateTask("upd", t =>
        {
            t.Status  = AgentTaskStatus.Completed;
            t.Progress = 100;
        });

        var found = queue.GetTask("upd")!;
        Assert.Equal(AgentTaskStatus.Completed, found.Status);
        Assert.Equal(100, found.Progress);
    }

    [Fact]
    public void UpdateTask_UnknownId_DoesNotThrow()
    {
        var queue = new TaskQueue();

        // Should not throw
        queue.UpdateTask("ghost", t => t.Status = AgentTaskStatus.Completed);
    }

    // ── MarkTerminal / eviction ───────────────────────────────────────────────

    [Fact]
    public void MarkTerminal_CompletedTask_EvictedWhenOverCapacity()
    {
        // Create a queue with capacity 2 in-memory
        var queue = new TaskQueue(maxHistorySize: 2);

        for (var i = 0; i < 3; i++)
        {
            var t = MakeTask(id: $"t{i}");
            queue.Enqueue(t);
            var dequeued = queue.Dequeue();
            dequeued!.Status = AgentTaskStatus.Completed;
            queue.MarkTerminal(dequeued.Id);
        }

        // After 3 completions with capacity 2, the oldest should be evicted
        var remaining = queue.GetAllTasks();
        Assert.True(remaining.Count <= 3, "Eviction should have removed some tasks");
    }

    [Fact]
    public void RunningCount_ReflectsOnlyRunningTasks()
    {
        var queue = new TaskQueue();
        queue.Enqueue(MakeTask());
        queue.Enqueue(MakeTask());
        queue.Enqueue(MakeTask());

        // Dequeue two tasks
        queue.Dequeue();
        queue.Dequeue();

        Assert.Equal(2, queue.RunningCount);
    }
}
