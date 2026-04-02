# SAG IDE — Code & Design Improvement Report

> **Generated**: 2026-02-28  
> **Repository**: `sanjeevakumarh/Structured-Agent-Graph-IDE`  
> **Stack**: .NET 9 / C# (backend), TypeScript (VS Code extension), SQLite, Ollama / Claude / Codex / Gemini  

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Architecture Overview](#2-architecture-overview)
3. [High-Priority Issues](#3-high-priority-issues)
4. [Code Quality](#4-code-quality)
5. [Design Improvements](#5-design-improvements)
6. [Security](#6-security)
7. [Testing Gaps](#7-testing-gaps)
8. [Performance](#8-performance)
9. [Observability & Operability](#9-observability--operability)
10. [Documentation](#10-documentation)
11. [Priority Matrix](#11-priority-matrix)

---

## 1. Executive Summary

SAG IDE is a well-architected, production-quality system for orchestrating agent-based workflows over local and cloud LLMs. The codebase demonstrates strong engineering judgement: DAG-based workflow evaluation with O(k) reverse-dependency caching, per-client write-locks in the named-pipe server to prevent concurrent-write races, SQLite WAL mode for concurrent reads, configurable retry/backoff policies, and a comprehensive 33-file test suite.

The improvements below are targeted refinements rather than structural rewrites. The highest-value items focus on:

- **Pluggable provider pattern** to avoid the growing `ProviderFactory` concrete dependency list
- **Startup configuration validation** so misconfigured deployments fail fast rather than fail silently mid-request
- **Fire-and-forget async cleanup** that currently swallows errors if persistence fails
- **Extension test coverage** — the TypeScript layer currently has zero automated tests
- **VectorStore ANN upgrade path** that is documented but not yet scaffolded

---

## 2. Architecture Overview

```
VS Code Extension (TypeScript)
   │  NamedPipe IPC (4-byte framed binary JSON)
   ▼
.NET 9 Service (ASP.NET Core, localhost:5100)
   ├── AgentOrchestrator  ─── TaskQueue ─── SemaphoreSlim concurrency limiter
   ├── WorkflowEngine     ─── DAG evaluation, reverse-dep cache, pause/resume
   ├── SubtaskCoordinator ─── multi-model parallel dispatch + synthesis
   ├── ProviderFactory    ─── Claude / Codex / Gemini / Ollama routing
   ├── RagPipeline        ─── fetch → chunk → embed (nomic-embed-text) → SQLite vector store
   ├── PromptRegistry     ─── YAML hot-reload (FileSystemWatcher + Scriban)
   ├── SchedulerService   ─── Cronos-based cron, persisted last-fired timestamps
   ├── SqliteTaskRepository ─ tasks, workflows, activity log, DLQ, vector cache
   └── NamedPipeServer    ─── bounded broadcast + unbounded lifecycle channels
```

Key strengths to preserve:
- Local-first, cloud-optional design
- All public APIs are async/await with CancellationToken propagation
- Reverse-dependency cache in WorkflowEngine (O(k) DAG evaluation)
- Per-instance semaphore prevents parallel-step race conditions
- Bounded broadcast channel with `DropOldest` policy protects back-pressure
- Lifecycle events on separate unbounded channel — no state event is ever dropped

---

## 3. High-Priority Issues

### 3.1 Fire-and-Forget Persistence in the Hot Path

**Files**: `AgentOrchestrator.cs`, `DeadLetterQueue.cs`

Several persistence calls are deliberately fire-and-forget using the `_ = SomeAsync()` pattern. While the intent is to avoid blocking the task execution loop, the current approach silently discards failures without any retry or circuit-breaker behaviour.

```csharp
// AgentOrchestrator.cs – cache write is non-fatal but completely untracked
_ = _repository.StoreCachedOutputAsync(cacheKey, lastResponse, task.ModelId)
    .ContinueWith(t => _logger.LogWarning(...), TaskContinuationOptions.OnlyOnFaulted);

// DeadLetterQueue.cs – DLQ persistence can silently fail
_ = PersistEntryAsync(entry);
```

**Recommendation**: For DLQ persistence (safety-critical), use a retry loop or background queue rather than a raw fire-and-forget. For cache writes, the existing `ContinueWith` is reasonable — apply the same pattern to DLQ `Enqueue` as well:

```csharp
// DeadLetterQueue.cs – add fault logging consistent with cache writes
_ = PersistEntryAsync(entry)
    .ContinueWith(t => _logger.LogError(t.Exception, "Failed to persist DLQ entry {Id}", entry.Id),
        TaskContinuationOptions.OnlyOnFaulted);
```

---

### 3.2 Startup Configuration Validation

**File**: `Program.cs`, `ProviderFactory.cs`

There is no validation of critical configuration values at startup. A misconfigured `SAGIDE:Ollama:Servers` URL, invalid cron expression, or zero `MaxConcurrentAgents` will only fail at runtime, often mid-request.

**Recommendation**: Add a configuration validator that runs during `builder.Build()`. .NET provides `IValidateOptions<T>` for this purpose:

```csharp
// Example: fail fast if MaxConcurrentAgents is out of range
public class SagideConfigValidator : IValidateOptions<SagideConfig>
{
    public ValidateOptionsResult Validate(string? name, SagideConfig options)
    {
        if (options.MaxConcurrentAgents is < 1 or > 100)
            return ValidateOptionsResult.Fail("MaxConcurrentAgents must be between 1 and 100");
        if (string.IsNullOrWhiteSpace(options.NamedPipeName))
            return ValidateOptionsResult.Fail("NamedPipeName is required");
        return ValidateOptionsResult.Success;
    }
}
```

Apply to all configurable sub-sections: `TimeoutConfig`, `RagConfig`, `AgentLimitsConfig`.

---

### 3.3 Service Locator Anti-Pattern in AgentOrchestrator

**File**: `AgentOrchestrator.cs`, line 273

```csharp
// Called inside ExecuteTaskAsync — reaches back into IServiceProvider at runtime
_ = _serviceProvider.GetService<WorkflowEngine>()?.OnTaskUpdateAsync(ToResponse(task));
```

The orchestrator holds a reference to `IServiceProvider` and resolves `WorkflowEngine` at execution time. This is the service-locator anti-pattern: it hides a circular dependency (`AgentOrchestrator` → `WorkflowEngine` → `AgentOrchestrator`), makes the dependency graph opaque to the DI container, and is difficult to test.

**Recommendation**: Break the cycle using an event/callback pattern (already used elsewhere in the codebase):

```csharp
// In AgentOrchestrator — replace the GetService call with an event
public event Func<TaskStatusResponse, Task>? OnTaskCompleted;

// In WorkflowEngine constructor / ServiceCollectionExtensions — wire it up
orchestrator.OnTaskCompleted += engine.OnTaskUpdateAsync;
```

---

### 3.4 WorkflowEngine Resource Leak on Exception

**File**: `WorkflowEngine.cs`

When `StartAsync` throws after adding `inst` to `_active` and `_locks` but before `PersistInstanceAsync` completes, the in-memory dictionaries retain the partial state, and the `SemaphoreSlim` in `_locks` is never disposed:

```csharp
_active[inst.InstanceId]    = (inst, def);
_locks[inst.InstanceId]     = new SemaphoreSlim(1, 1);
_revDepsCache[inst.InstanceId] = BuildReverseDeps(def);

// If this throws, the above entries are never cleaned up
await PersistInstanceAsync(inst);
```

**Recommendation**: Use a try/catch to clean up on failure, and ensure `SemaphoreSlim` instances in `_locks` are disposed when a workflow completes or is cancelled (call `sem.Dispose()` in the cleanup path).

---

### 3.5 VectorStore Missing WAL Mode

**File**: `Rag/VectorStore.cs`

`SqliteTaskRepository` enables WAL mode and a `busy_timeout` pragma for concurrent access. `VectorStore` uses the same SQLite database file but opens connections without these pragmas:

```csharp
// VectorStore.cs — connection string lacks WAL/busy_timeout
_connectionString = $"Data Source={dbPath}";
```

Under concurrent RAG indexing and retrieval, this can cause `SQLITE_BUSY` errors.

**Recommendation**: Apply the same pragmas used in `SqlQueries.Pragmas` when `VectorStore.InitializeAsync` first opens the database, or consolidate both into a single connection factory/singleton.

---

## 4. Code Quality

### 4.1 `AgentTask.Metadata` Used as a Stringly-Typed Property Bag

**File**: `AgentOrchestrator.cs`, `WorkflowEngine.cs`

`Dictionary<string, string>` metadata is used to pass structured data (JSON arrays, counts, IDs) between components using magic string keys like `"issuesJson"`, `"workflowInstanceId"`, `"modelEndpoint"`. This creates invisible coupling between producers and consumers.

```csharp
task.Metadata["issuesJson"]  = JsonSerializer.Serialize(lastResult.Issues);
task.Metadata["changesJson"] = JsonSerializer.Serialize(lastResult.Changes);
// Then, elsewhere:
if (task.Metadata.ContainsKey("workflowInstanceId"))
    _ = _serviceProvider.GetService<WorkflowEngine>()?.OnTaskUpdateAsync(...);
```

**Recommendation**: Introduce strongly-typed properties or a sealed `TaskResult` record alongside `AgentTask`. At minimum, create a static class of string constants to centralise the key names:

```csharp
public static class TaskMetadataKeys
{
    public const string IssuesJson         = "issuesJson";
    public const string ChangesJson        = "changesJson";
    public const string WorkflowInstanceId = "workflowInstanceId";
    public const string ModelEndpoint      = "modelEndpoint";
    // ...
}
```

---

### 4.2 `SqliteTaskRepository` Implements Four Interfaces on One Class

**File**: `Persistence/SqliteTaskRepository.cs`, `Infrastructure/ServiceCollectionExtensions.cs`

The repository class implements `ITaskRepository`, `IActivityRepository`, `IWorkflowRepository`, and `ISchedulerRepository`. While technically correct, this violates the Interface Segregation Principle and means every class that needs only `ISchedulerRepository` gets a transitive dependency on all task, activity, and workflow logic.

**Recommendation**: The current DI aliasing pattern is acceptable as a pragmatic single-file-SQLite solution. Document the design decision explicitly, and consider splitting into separate partial classes grouped by concern if the file grows beyond ~800 lines:

```csharp
// SqliteTaskRepository.Tasks.cs
// SqliteTaskRepository.Workflows.cs
// SqliteTaskRepository.ActivityLog.cs
// SqliteTaskRepository.Scheduler.cs
```

---

### 4.3 Magic Numbers and Hardcoded Strings

Several constants appear inline rather than in configuration or named constants:

| Location | Value | Concern |
|---|---|---|
| `AgentOrchestrator.cs` | `[..12]` for task ID length | Magic slice — change range if uniqueness needs increase |
| `VectorStore.cs` | Comment: "Upgrade path: ChromaDB if >100K chunks" | Not enforced; no telemetry alert at threshold |
| `WorkflowEngine.cs` | `"tool"`, `"router"`, `"human_approval"`, `"constraint"` | Step type strings used in multiple switch-like comparisons |
| `NamedPipeServer.cs` | `10_000` broadcast queue cap (also in config) | Duplicated — config value and `CommunicationConfig.MaxBroadcastQueueSize` default should be canonical |

**Recommendation**: Define `WorkflowStepType` as an enum or a `static class` of `string` constants in `SAGIDE.Core` to prevent typos in YAML loader and engine:

```csharp
public static class WorkflowStepTypes
{
    public const string Agent        = "agent";
    public const string Tool         = "tool";
    public const string Router       = "router";
    public const string HumanApproval= "human_approval";
    public const string Constraint   = "constraint";
}
```

---

### 4.4 `ProviderFactory.InitializeProviders` Is a Long Imperative Method

**File**: `Providers/ProviderFactory.cs`

`InitializeProviders` constructs all four providers sequentially with repeated conditional logic. As new providers are added, this method will grow linearly.

**Recommendation**: Introduce an `IProviderBuilder` interface and register provider builders in DI, keeping `ProviderFactory` as a thin aggregator:

```csharp
public interface IProviderBuilder
{
    ModelProvider Provider { get; }
    IAgentProvider? TryBuild(IConfiguration config, TimeoutConfig timeout, ILoggerFactory loggerFactory);
}
```

Each provider gets its own `ClaudeProviderBuilder : IProviderBuilder` class, making the provider list open for extension without modifying `ProviderFactory`.

---

### 4.5 `SubtaskCoordinator` Result Synthesis Prompt is Hardcoded

**File**: `Orchestrator/SubtaskCoordinator.cs`

The synthesis step that merges multi-model results uses a hardcoded prompt template embedded in C# code. This is inconsistent with the rest of the codebase, which uses the `PromptRegistry` + YAML system for all other prompts.

**Recommendation**: Move the synthesis prompt to a YAML template under `prompts/shared/synthesis.yaml` and load it via `PromptRegistry`. This makes it hot-reloadable, domain-overridable, and visible to end users.

---

### 4.6 `BaseHttpAgentProvider` Creates `HttpClient` Internally

**File**: `Providers/BaseHttpAgentProvider.cs`

Each provider creates its own `HttpClient` via `new HttpClient { ... }`. This bypasses `IHttpClientFactory`, which handles connection pooling, DNS refresh, and lifecycle management. Under heavy load, this can cause socket exhaustion.

```csharp
_httpClient = new HttpClient
{
    BaseAddress = new Uri(baseUrl),
    Timeout     = System.Threading.Timeout.InfiniteTimeSpan
};
```

**Recommendation**: Inject `IHttpClientFactory` into `ProviderFactory` and use named clients:

```csharp
services.AddHttpClient("Claude").ConfigureHttpClient(c => c.BaseAddress = new Uri(claudeBaseUrl));
// Then: factory.CreateClient("Claude")
```

---

### 4.7 BOM Characters in Source Files

Several C# files (e.g. `AgentOrchestrator.cs`, `WorkflowEngine.cs`, `SqliteTaskRepository.cs`) begin with a UTF-8 BOM (`﻿`). While .NET handles this correctly, it is a code hygiene issue that can cause problems in some diff tools, text processors, and CI pipelines.

**Recommendation**: Configure the `.editorconfig` to enforce `charset = utf-8` (no BOM) and run `dotnet format` to strip existing BOMs.

---

## 5. Design Improvements

### 5.1 Introduce a Workflow Step Type Discriminated Union

**File**: `Core/Models/WorkflowDefinition.cs`

Workflow steps are represented as a single `WorkflowStep` class with nullable fields for each step type (`ToolCommand`, `RouterExpression`, `HumanApprovalPrompt`, etc.). This means the engine must check multiple nullable fields and use string comparisons to determine step behaviour.

**Recommendation**: Use C# 9+ discriminated-union-style inheritance:

```csharp
public abstract class WorkflowStep { public string Id { get; init; } = ""; /* common fields */ }
public sealed class AgentStep     : WorkflowStep { public AgentType AgentType { get; init; } }
public sealed class ToolStep      : WorkflowStep { public string Command { get; init; } = ""; }
public sealed class RouterStep    : WorkflowStep { public string Expression { get; init; } = ""; }
public sealed class HumanApprovalStep : WorkflowStep { public string Prompt { get; init; } = ""; }
```

WorkflowEngine then uses `switch (step)` pattern matching — exhaustive and compiler-checked.

---

### 5.2 `AgentOrchestrator` Violates Single Responsibility

**File**: `Orchestrator/AgentOrchestrator.cs`

`AgentOrchestrator` currently handles: task submission, task execution, concurrency limiting, progress broadcasting, result parsing, cache read/write, DLQ routing, activity logging, Git commit triggering, and workflow notification. At ~800 lines it is the largest class in the service layer.

**Recommendation**: Extract the execution concerns into a dedicated `TaskExecutor` class:

```
AgentOrchestrator  — submission, queue management, lifecycle events
TaskExecutor       — single-task execution loop (iterations, streaming, caching, metrics)
ResultHandler      — parse result, persist, trigger downstream (workflow notify, activity log, git)
```

This aligns with the existing `ResultParser` extraction already in the codebase.

---

### 5.3 Prompt Variable Injection via Metadata is Implicit

**File**: `Prompts/PromptTemplate.cs`, `Orchestrator/AgentOrchestrator.cs`

The prompt template system injects `{{rag_context}}` and step output variables like `{{step_id.output}}`. The mapping from `AgentTask.Metadata` keys to Scriban variables is implicit — there is no schema or documentation listing which keys are available in which contexts.

**Recommendation**: Define a `PromptContext` record that explicitly lists all standard variables, validated before rendering:

```csharp
public record PromptContext(
    string? RagContext,
    IReadOnlyDictionary<string, string> StepOutputs,
    IReadOnlyDictionary<string, string> InputVariables
);
```

Pass `PromptContext` to `PromptTemplate.RenderAsync` instead of a raw dictionary. Add logging for missing variables that were referenced in the template but not provided.

---

### 5.4 Scheduler Tick Granularity is Fixed at One Minute

**File**: `Scheduling/SchedulerService.cs`

The scheduler ticks every 60 seconds. Sub-minute cron expressions (e.g. `*/30 * * * * *`) are silently ineffective.

**Recommendation**: Read the minimum interval from the cron expressions themselves (using `CronExpression.GetOccurrences`) and sleep until the next scheduled time rather than polling every minute. The Cronos library already supports this:

```csharp
var next = scheduledPrompts
    .Select(p => p.CronExpression.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc))
    .Where(n => n.HasValue)
    .Min();
if (next.HasValue)
    await Task.Delay(next.Value - DateTimeOffset.UtcNow, stoppingToken);
```

---

### 5.5 NamedPipeServer Client ID Is a Guid — Consider Using an Authenticated Principal

**File**: `Communication/NamedPipeServer.cs`

Client IDs are generated as `Guid.NewGuid()` strings. The `_taskOwners` map routes streaming output to the submitting client, but there is no binding between a pipe client and the VS Code window that owns it, meaning a misbehaving client could construct a message claiming ownership of another client's tasks.

**Recommendation**: During the handshake phase, require the client to present the `pipeSharedSecret` (already implemented in `ServiceConnection.ts`). Once verified, bind the client ID to the shared secret's hash so spoofing would require knowing the secret.

---

### 5.6 WorkflowEngine `_active` Dictionary Is Never Size-Bounded

**File**: `Orchestrator/WorkflowEngine.cs`

Completed workflow instances are removed from `_active`, `_locks`, and `_revDepsCache` in `FinishWorkflowAsync`. However, if `FinishWorkflowAsync` is never called (e.g., a workflow stuck in `Paused` state indefinitely), these in-memory structures grow without bound.

**Recommendation**: Add a periodic cleanup task that removes paused workflows older than a configurable timeout (e.g., 7 days), consistent with the DLQ retention policy.

---

## 6. Security

### 6.1 API Keys Stored in appsettings.json

**File**: `src/SAGIDE.Service/appsettings.json`

The default `appsettings.json` contains placeholder API key stubs (`"sk-"`, `"AI"`). While these are placeholders, the pattern encourages users to commit real keys to the same file, which is a common credential-leak vector.

**Recommendation**:
1. Remove all `ApiKeys` entries from `appsettings.json` entirely. Document in README that keys must be provided via environment variables or user-secrets.
2. Add `.gitignore` entries for `appsettings.Development.json` and `appsettings.Production.json`.
3. Validate at startup that any configured key doesn't look like a placeholder (`sk-` alone, `AI` alone).

```bash
# Correct way to supply keys (no file commit needed):
export SAGIDE__ApiKeys__Anthropic="sk-ant-..."
dotnet run
```

---

### 6.2 Bearer Token Stored in appsettings.json

**File**: `Program.cs`, `appsettings.json`

`SAGIDE:RestApi:BearerToken` is read from configuration. If stored in `appsettings.json`, it is committed to version control. The current default is empty (disabled), but documentation should clearly warn against enabling it via the config file.

**Recommendation**: Enforce that the bearer token, when enabled, must come from an environment variable or secret store — not from `appsettings.json`. Add a startup check:

```csharp
if (!string.IsNullOrEmpty(restBearerToken) &&
    builder.Environment.IsProduction() &&
    builder.Configuration.GetSection("SAGIDE:RestApi").GetChildren().Any(c => c.Key == "BearerToken"))
{
    throw new InvalidOperationException("BearerToken must not be set in appsettings.json in production.");
}
```

---

### 6.3 WorkflowPolicy `ProtectedPathPatterns` Uses Glob Matching Against User-Supplied Paths

**File**: `Orchestrator/WorkflowPolicyEngine.cs`

The policy engine validates file paths against glob patterns like `**/.env*` and `**/secrets/**`. The matching relies on `Microsoft.Extensions.FileSystemGlobbing`. Path traversal sequences (`../`) in user-supplied file paths could escape the workspace root before glob evaluation.

**Recommendation**: Normalise all incoming file paths to their absolute form relative to the workspace root before glob matching, and reject any path that resolves outside the workspace:

```csharp
var resolved = Path.GetFullPath(filePath, workspaceRoot);
if (!resolved.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
    return PolicyResult.Blocked("Path traversal outside workspace is not permitted");
```

---

### 6.4 Constant-Time Token Comparison Already Present — Ensure It Is Used Everywhere

**File**: `Resilience/ResilientHttpHandler.cs`

The codebase correctly uses `CryptographicOperations.FixedTimeEquals` for the pipe shared-secret check. Ensure this is also used for any future bearer-token validation in `Program.cs` (the current implementation uses a string equality check):

```csharp
// Program.cs — current
if (token != restBearerToken) { context.Response.StatusCode = 401; }
// ↑ vulnerable to timing side-channel; replace with:
if (!CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(token),
        Encoding.UTF8.GetBytes(restBearerToken)))
{ context.Response.StatusCode = 401; }
```

---

### 6.5 HTML in Web Dashboard Not Sanitised

**File**: `src/SAGIDE.Service/wwwroot/`

The web dashboard renders task descriptions and model responses into the DOM. If an LLM response contains `<script>` tags or HTML, it could cause stored XSS in the dashboard.

**Recommendation**: Ensure all LLM-generated content inserted into the DOM uses `textContent` (not `innerHTML`) or a sanitisation library. Apply a strict `Content-Security-Policy` header from Kestrel:

```csharp
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; object-src 'none';";
    await next();
});
```

---

## 7. Testing Gaps

### 7.1 VS Code Extension Has Zero Tests

**Directory**: `src/vscode-extension/`

The TypeScript extension contains non-trivial logic in `ServiceConnection.ts`, `CommandRegistry.ts`, `ComparisonTracker.ts`, `WorkflowGraphPanel.ts`, and `DiagnosticsManager.ts`. None of this is covered by automated tests.

**Recommendation**: Add a Jest or Mocha test suite (VS Code extension testing is supported via `@vscode/test-electron`). Priority test cases:

| Module | Test Cases |
|---|---|
| `ComparisonTracker` | All-complete event fires; partial completion does not; handles duplicate task IDs |
| `ServiceConnection` | Reconnect on pipe error; message routing to correct handler; streaming token accumulation |
| `DiagnosticsManager` | Diagnostics created for `Completed` tasks with issues; cleared on `Cancelled` |
| `WorkflowGraphPanel` | DAG rendered correctly for sequential / parallel / feedback-loop workflows |

---

### 7.2 CLI Tool Has Zero Tests

**Directory**: `tools/cli/`

The CLI parses command-line arguments and calls the REST API. End-to-end CLI tests would increase confidence during refactoring.

**Recommendation**: Add integration tests that spin up `WebApplicationFactory<Program>` (from the service) and invoke CLI commands against it, verifying exit codes and stdout output.

---

### 7.3 No Tests for `ProviderFactory` Configuration Parsing

**File**: `Providers/ProviderFactory.cs`

`ProviderFactory` builds complex routing tables from `IConfiguration`. The existing `ProviderFactoryTests.cs` likely covers basic provider selection, but edge cases like:

- Empty `SAGIDE:Ollama:Servers` array
- Missing `BaseUrl` for a server
- Duplicate model IDs across servers
- Invalid `BackoffStrategy` enum string

are not visibly tested.

**Recommendation**: Add parameterised tests covering these edge cases, verifying that `GetAvailableProviders()` returns the correct set and that no exception is thrown for valid but sparse configurations.

---

### 7.4 No Chaos / Fault-Injection Tests for the Named-Pipe Server

**File**: `Communication/NamedPipeServer.cs`

The broadcast channel uses `DropOldest` — but there are no tests that verify message loss is graceful (no deadlock, no crash) when the channel is at capacity.

**Recommendation**: Add a test that floods the broadcast channel beyond its capacity and verifies:
1. `DroppedMessageCount` increments
2. The drain loop does not stall
3. Lifecycle events on the unbounded channel are still delivered

---

## 8. Performance

### 8.1 VectorStore Brute-Force Cosine Similarity Scales Poorly

**File**: `Rag/VectorStore.cs`

The vector store computes cosine similarity in a SQLite custom function over all stored vectors. This is documented as a known limitation ("Upgrade path: swap to ChromaDB if scale exceeds ~100K chunks"), but no alert or automatic fallback exists.

**Recommendation**:
1. Emit a structured log warning when the vector chunk count exceeds a configurable threshold (e.g., 50,000). This gives operators advance notice.
2. Add a metric counter `rag.chunk_count` to the `SagideMetrics` class so the Prometheus/OTEL scraper can alert on it.
3. Optionally, implement a simple HNSW index using the `Microsoft.SemanticKernel.Memory` package or a small native library, keeping the SQLite store as a fallback.

---

### 8.2 `SqliteTaskRepository.SaveTaskAsync` Opens a New Connection Per Call

**File**: `Persistence/SqliteTaskRepository.cs`

Every persistence call opens a new `SqliteConnection`, runs a command, and disposes the connection. With connection pooling enabled (`Pooling=True`), this is mostly fine, but for high-throughput scenarios (many tasks completing simultaneously), the pool can be exhausted.

**Recommendation**: Consider batching rapid successive writes (e.g., within a 50ms window) into a single transaction. The existing `PersistCompletedAsync` method already groups task status and result writes in one transaction — extend this pattern to the other write paths.

---

### 8.3 `AgentOrchestrator` Broadcast Throttle Is Wall-Clock Based

**File**: `AgentOrchestrator.cs`

The broadcast throttle uses `Task.Delay(_broadcastThrottleMs)` to coalesce updates. Under high concurrency, multiple updates within the throttle window are all dropped except the last, meaning progress updates can stall for `broadcastThrottleMs` (default 200ms) even when there are no other tasks.

**Recommendation**: Use a `Stopwatch`-gated debounce rather than a fixed delay: broadcast immediately if the last broadcast was more than `broadcastThrottleMs` ago, otherwise schedule a single deferred broadcast at the end of the window. This is the standard "leading + trailing edge debounce" pattern.

---

### 8.4 Prompt Hot-Reload Creates One FileSystemWatcher Per Loaded File

**File**: `Prompts/PromptRegistry.cs`

If `PromptRegistry` creates a `FileSystemWatcher` per-file rather than watching the directory, high prompt counts will exhaust the OS file descriptor limit on Linux (`fs.inotify.max_user_watches`).

**Recommendation**: Use a single `FileSystemWatcher` on the prompts root directory with `IncludeSubdirectories = true` and filter on `*.yaml`. Debounce reload events with a short delay (200ms) to handle editors that write files in two phases (truncate + write).

---

## 9. Observability & Operability

### 9.1 `/api/health` Endpoint Is Missing

The service exposes `/api/metrics` but no `/api/health` endpoint. Kubernetes probes, load balancers, and deployment scripts (`kill-and-start.ps1`) cannot distinguish a fully-initialised service from one that is still loading persisted tasks.

**Recommendation**: Add a `/api/health` endpoint that returns:

```json
{
  "status": "healthy",
  "initComplete": true,
  "activeTaskCount": 3,
  "queuedTaskCount": 1,
  "dlqCount": 0,
  "droppedPipeMessages": 0,
  "ollamaHostsHealthy": 1,
  "ollamaHostsTotal": 1
}
```

Gate `"status": "ready"` on `AgentOrchestrator.InitializationCompleted` so orchestration is confirmed before the service reports healthy.

---

### 9.2 Structured Log Correlation Between Task and Workflow

**Files**: `AgentOrchestrator.cs`, `WorkflowEngine.cs`

The orchestrator opens a `BeginScope` with `{ TaskId, Provider, SourceTag }`. The workflow engine logs with `InstanceId` and `StepId`. When a task belongs to a workflow, logs from both components do not share a common correlation field, making end-to-end tracing across a multi-step workflow difficult.

**Recommendation**: Add `WorkflowInstanceId` and `WorkflowStepId` to the orchestrator's log scope when `task.Metadata` contains `"workflowInstanceId"`:

```csharp
using var scope = _logger.BeginScope(new Dictionary<string, object?>
{
    ["TaskId"]             = task.Id,
    ["Provider"]           = task.ModelProvider.ToString(),
    ["SourceTag"]          = task.SourceTag,
    ["WorkflowInstanceId"] = task.Metadata.GetValueOrDefault("workflowInstanceId"),
    ["WorkflowStepId"]     = task.Metadata.GetValueOrDefault("workflowStepId"),
});
```

---

### 9.3 No Distributed Trace / Span Propagation

The service uses `System.Diagnostics.Meters` (OpenTelemetry-compatible) for metrics but does not emit `Activity`/`Span` traces. For long-running multi-step workflows, distributed tracing would make bottleneck identification much faster.

**Recommendation**: Add an `ActivitySource` to `AgentOrchestrator` and `WorkflowEngine`:

```csharp
private static readonly ActivitySource _tracer = new("SAGIDE.Orchestrator", "1.0");

using var span = _tracer.StartActivity("task.execute");
span?.SetTag("task.id",       task.Id);
span?.SetTag("task.provider", task.ModelProvider.ToString());
```

This integrates with OpenTelemetry exporters (Jaeger, Zipkin, OTLP) with zero additional code.

---

## 10. Documentation

### 10.1 README Does Not Document the REST API

The `README.md` describes the extension and CLI but does not document the REST API endpoints (`POST /api/tasks`, `GET /api/tasks/{id}`, etc.). Developers integrating without the VS Code extension (e.g., CI pipelines, the Logseq plugin) have no reference.

**Recommendation**: Add an `API.md` or inline REST reference in the README. Consider adding a `/api/openapi.json` endpoint (ASP.NET Core ships `AddOpenApi()` as of .NET 9).

---

### 10.2 Workflow YAML Schema Is Undocumented

**Directory**: `src/SAGIDE.Service/Orchestrator/Templates/`

The workflow YAML format supports `steps`, `depends_on`, `router`, `convergence_policy`, `max_iterations`, `parameters`, and `model_overrides`. None of these fields are documented except in code comments within `WorkflowDefinitionLoader.cs`.

**Recommendation**: Add a `docs/workflow-schema.md` that documents every supported field with type, default value, and an example.

---

### 10.3 Prompt YAML Schema Is Partially Documented

**Directory**: `prompts/`

Prompt files support `data_sources`, `subtasks`, `synthesis`, `schedule`, `output`, and Scriban template expressions. The `prompts/shared/summarization.yaml` example is helpful but does not cover all fields.

**Recommendation**: Add a `docs/prompt-schema.md` with a fully-annotated reference prompt covering every optional field.

---

### 10.4 No Architecture Decision Records (ADRs)

Several significant design decisions are documented only in code comments (DAG reverse-dep cache, unbounded lifecycle channel, SHA-256 output cache key, WAL mode choice). These are not easily discoverable.

**Recommendation**: Create a `docs/adr/` directory and add ADR files for at least:
- ADR-001: SQLite as primary store (vs PostgreSQL / embedded RocksDB)
- ADR-002: Named-pipe IPC (vs WebSocket / gRPC)
- ADR-003: Bounded broadcast + unbounded lifecycle dual-channel design
- ADR-004: SHA-256 output cache key design
- ADR-005: Reverse-dependency cache in WorkflowEngine

---

## 11. Priority Matrix

| # | Item | Effort | Impact | Priority |
|---|------|--------|--------|----------|
| 3.1 | Fire-and-forget DLQ persistence fault logging | Low | High | **P1** |
| 3.2 | Startup configuration validation | Low | High | **P1** |
| 6.1 | Remove API keys from appsettings.json | Low | High | **P1** |
| 6.3 | Path traversal fix in WorkflowPolicyEngine | Low | High | **P1** |
| 6.4 | Constant-time bearer token comparison | Low | High | **P1** |
| 3.3 | Remove service-locator pattern in AgentOrchestrator | Medium | High | **P2** |
| 3.4 | WorkflowEngine SemaphoreSlim disposal on exception | Low | Medium | **P2** |
| 3.5 | VectorStore WAL mode | Low | Medium | **P2** |
| 4.1 | Strongly-typed task metadata keys | Low | Medium | **P2** |
| 6.5 | CSP header + DOM XSS in dashboard | Low | Medium | **P2** |
| 9.1 | `/api/health` endpoint | Low | High | **P2** |
| 4.3 | WorkflowStepTypes string constants | Low | Low | **P3** |
| 4.6 | IHttpClientFactory for providers | Medium | Medium | **P3** |
| 5.1 | Discriminated union for workflow steps | High | Medium | **P3** |
| 5.2 | AgentOrchestrator SRP split | High | Medium | **P3** |
| 5.4 | Scheduler next-occurrence sleep | Medium | Low | **P3** |
| 7.1 | VS Code extension test suite | High | High | **P3** |
| 7.2 | CLI integration tests | Medium | Medium | **P3** |
| 8.1 | VectorStore scale warning + metric | Low | Medium | **P3** |
| 8.3 | Debounce broadcast throttle | Medium | Low | **P4** |
| 9.2 | Workflow/task log correlation field | Low | Medium | **P3** |
| 9.3 | ActivitySource distributed tracing | Medium | Medium | **P4** |
| 10.1 | REST API reference docs | Low | Medium | **P3** |
| 10.2 | Workflow YAML schema docs | Low | Medium | **P3** |
| 10.4 | Architecture Decision Records | Low | Low | **P4** |

---

*End of report. Items marked P1 are recommended for the next release. Items marked P2 for the following sprint. P3/P4 items are improvements for backlog grooming.*
