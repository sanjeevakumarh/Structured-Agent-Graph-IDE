# SAG IDE — Code and Design Improvement Review

This document reviews the current codebase and provides actionable recommendations across security, architecture, code quality, testing, performance, observability, resilience, and developer experience.

---

## Table of Contents
1. [Security](#1-security)
2. [Architecture and Design](#2-architecture-and-design)
3. [Code Quality](#3-code-quality)
4. [Testing](#4-testing)
5. [Performance](#5-performance)
6. [Observability](#6-observability)
7. [Resilience](#7-resilience)
8. [Developer Experience and Documentation](#8-developer-experience-and-documentation)
9. [Dependency and Package Hygiene](#9-dependency-and-package-hygiene)
10. [Summary Checklist](#10-summary-checklist)

---

## 1. Security

### 1.1 API Keys in `appsettings.json`
**File:** `src/SAGIDE.Service/appsettings.json`

API key placeholders use real provider-pattern prefixes (`"sk-"`, `"AI"`), which can be mistaken for real keys or accidentally replaced with real secrets and committed to source control.

**Recommendation:**
- Replace the file with an `appsettings.Example.json` template (empty strings for all keys) and add `appsettings.json` to `.gitignore`.
- Prefer environment variables or a secret manager (`dotnet user-secrets`, Azure Key Vault, AWS Secrets Manager) for runtime key injection.

```json
// appsettings.Example.json — check this in; keep appsettings.json gitignored
"ApiKeys": {
  "Anthropic": "",
  "OpenAI": "",
  "Google": ""
}
```

### 1.2 Named Pipe Server Has No Authentication
**File:** `src/SAGIDE.Service/Communication/NamedPipeServer.cs`

Any process running as the same (or higher-privileged) OS user can connect to the named pipe and submit arbitrary tasks, cancel workflows, or read task output.

**Recommendation:**
- Add a shared-secret handshake on connection (e.g., a token generated at service startup and passed via VS Code settings).
- Alternatively, use `PipeSecurity` (Windows) to restrict pipe access to the current user's SID.

### 1.3 REST API Has No Authentication or Rate Limiting
**File:** `src/SAGIDE.Service/Api/TaskEndpoints.cs`

All REST endpoints (`/api/tasks`, `/api/workflows`, `/api/prompts`) are exposed without any authentication or rate limiting. On a shared machine this allows any local process to enumerate, submit, or cancel tasks.

**Recommendation:**
- Add bearer-token middleware (shared secret derived from a startup-time GUID stored in user-local storage).
- Add ASP.NET Core rate limiting middleware (`app.UseRateLimiter()`) for the public endpoints.

### 1.4 Sensitive Data in Log Output
**File:** `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs`

`task.Metadata` is serialised into log messages. If a caller stores an API key or file content in metadata, it will appear in `logs/agentic-ide-*.log` in plain text.

**Recommendation:**
- Introduce a metadata key allowlist for log output (or use a `[Sensitive]` attribute pattern).
- Redact or truncate long metadata values in structured log output.

---

## 2. Architecture and Design

### 2.1 God Classes: `WorkflowEngine` and `AgentOrchestrator`
**Files:** `src/SAGIDE.Service/Orchestrator/WorkflowEngine.cs` (77.5 KB), `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs` (35.7 KB)

Both files violate the Single Responsibility Principle. `WorkflowEngine` handles DAG evaluation, feedback loops, router evaluation, constraint evaluation, context retrieval, workspace provisioning, human approval gates, pause/resume, cancel, and persistence — all in one class.

**Recommendation — split `WorkflowEngine` into focused collaborators:**

| Proposed Class | Responsibility |
|---|---|
| `WorkflowLifecycleManager` | Start, pause, resume, cancel, recovery |
| `WorkflowStepDispatcher` | Evaluate DAG, submit ready steps |
| `WorkflowLoopController` | Feedback loop iteration, convergence, escalation |
| `WorkflowApprovalManager` | Human approval gates, SLA timeouts |
| `WorkflowStepEvaluators` | Router, constraint, context\_retrieval (static helpers or small classes) |

**Recommendation — split `AgentOrchestrator`:**

| Proposed Class | Responsibility |
|---|---|
| `TaskDispatcher` | Queue management, concurrency limiter, task execution |
| `TaskLifecycleTracker` | Status tracking, broadcast, persistence |
| `PromptBuilder` | File reads, prompt assembly, cache lookup |

### 2.2 `SqliteTaskRepository` Implements Four Interfaces
**File:** `src/SAGIDE.Service/Persistence/SqliteTaskRepository.cs`

```csharp
public class SqliteTaskRepository
    : ITaskRepository, IActivityRepository, IWorkflowRepository, ISchedulerRepository
```

This class has grown to cover all persistence concerns. Any change to one interface risks breaking the others, and all four concern areas must be tested together.

**Recommendation:**
- Split into `SqliteTaskRepository`, `SqliteActivityRepository`, `SqliteWorkflowRepository`, and `SqliteSchedulerRepository`, each backed by the same connection string.
- Alternatively, use a `SqliteRepositoryBase` for shared connection/migration logic and compose the four implementations.

### 2.3 Concrete Type Dependencies in Service Layer
**Files:** `src/SAGIDE.Service/Scheduling/SchedulerService.cs`, `src/SAGIDE.Service/Orchestrator/SubtaskCoordinator.cs`, `src/SAGIDE.Service/Api/TaskEndpoints.cs`

`SchedulerService` and `SubtaskCoordinator` both depend on the concrete `AgentOrchestrator` rather than the `ITaskSubmissionService` interface. `TaskEndpoints.MapPost` also injects `AgentOrchestrator` directly.

**Recommendation:**
- Replace all constructor and delegate injection of `AgentOrchestrator` with `ITaskSubmissionService`.
- This eliminates the circular-dependency workaround in `Program.cs` and makes these classes independently testable.

### 2.4 `HttpClient` Created Outside `IHttpClientFactory`
**Files:** `src/SAGIDE.Service/Providers/BaseHttpAgentProvider.cs`, `src/SAGIDE.Service/Providers/OllamaProvider.cs`

Both providers create `HttpClient` instances directly. `OllamaProvider` stores per-URL clients in a `ConcurrentDictionary` with no eviction or lifecycle management. This risks socket exhaustion under high parallelism.

**Recommendation:**
- Register all providers via `builder.Services.AddHttpClient<TProvider>()` in `Program.cs`.
- Inject `IHttpClientFactory` into provider constructors and call `_httpClientFactory.CreateClient(providerName)`.
- Remove the manual `ConcurrentDictionary<string, HttpClient>` in `OllamaProvider`.

### 2.5 Two Incompatible Template Engines
**Files:** `src/SAGIDE.Service/Orchestrator/PromptTemplateEngine.cs`, `src/SAGIDE.Service/Orchestrator/SubtaskCoordinator.cs`

Workflow step prompts are rendered by `PromptTemplateEngine` (custom Regex, `{{var}}` syntax), while `SubtaskCoordinator` prompts are rendered by Scriban (Liquid-like `{{ var }}` syntax). Contributors working on prompt authoring face two different template languages depending on where the prompt is used.

**Recommendation:**
- Standardise on Scriban for all prompt rendering.
- Extend `SubtaskCoordinator`'s Scriban context to expose step execution outputs (already available as a `Dictionary<string, string>`).
- Deprecate `PromptTemplateEngine` once the migration is complete.

### 2.6 Raw `Action<>` Events Instead of a Mediator or Message Bus
**File:** `src/SAGIDE.Service/Orchestrator/WorkflowEngine.cs`, `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs`

```csharp
public event Action<WorkflowInstance>? OnWorkflowUpdate;
public event Action<string, string, string>? OnApprovalNeeded;
public event Action<TaskStatusResponse>? OnTaskUpdate;
```

Coupling is done via raw `Action<>` delegates wired in `ServiceLifetime`. Adding a new subscriber requires touching `ServiceLifetime`; error in one subscriber can propagate to others.

**Recommendation:**
- Introduce a lightweight in-process event bus (e.g., `IPublisher` / `ISubscriber<TEvent>` pattern, or MediatR notifications).
- Each service subscribes to the events it cares about without requiring central wiring.

### 2.7 `Program.cs` Is a 240-Line Composition Root
**File:** `src/SAGIDE.Service/Program.cs`

All DI registrations, configuration binding, and service wiring are inlined in `Program.cs`. This file is hard to navigate and impossible to unit-test.

**Recommendation:**
- Extract DI registration into extension methods:
  ```csharp
  builder.Services.AddOrchestration(builder.Configuration);
  builder.Services.AddProviders(builder.Configuration, loggerFactory, timeoutConfig, ollamaMonitor);
  builder.Services.AddRagPipeline(builder.Configuration, dbPath);
  builder.Services.AddCommunication(builder.Configuration, pipeName);
  ```
- Place extension methods in `src/SAGIDE.Service/Infrastructure/ServiceCollectionExtensions.cs` (already partially done).

---

## 3. Code Quality

### 3.1 Bug in `RecoverRunningInstancesAsync` — Loop Calls Wrong Method
**File:** `src/SAGIDE.Service/Orchestrator/WorkflowEngine.cs`, line ~199

```csharp
var pendingSteps = def.Steps
    .Where(s => inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending ...)
    .ToList();

foreach (var step in pendingSteps)          // ← iterates N steps
    await SubmitReadyStepsAsync(inst, def, ct); // ← re-evaluates ALL ready steps N times
```

`SubmitReadyStepsAsync` already evaluates the entire DAG and submits all ready steps. Calling it once after the `foreach` is sufficient; the current loop calls it N times (once per pending step), causing redundant submissions that rely on idempotency to mask the bug.

**Recommendation:**
```csharp
if (pendingSteps.Count > 0)
    await SubmitReadyStepsAsync(inst, def, ct);
```

### 3.2 Fire-and-Forget Persistence in `DeadLetterQueue`
**File:** `src/SAGIDE.Service/Resilience/DeadLetterQueue.cs`, line ~52

```csharp
_ = PersistEntryAsync(entry);
```

If persistence fails (disk full, SQLite locked), the exception is silently dropped and the DLQ entry exists only in memory. After a restart it is lost.

**Recommendation:**
- Await the persistence call, or use a retry channel pattern:
  ```csharp
  await PersistEntryAsync(entry).ConfigureAwait(false);
  ```
- At minimum, log the failure:
  ```csharp
  _ = PersistEntryAsync(entry).ContinueWith(t =>
      _logger.LogError(t.Exception, "DLQ persistence failed for {Id}", entry.Id),
      TaskContinuationOptions.OnlyOnFaulted);
  ```

### 3.3 `ScheduleApprovalTimeout` Silently Swallows Exceptions
**File:** `src/SAGIDE.Service/Orchestrator/WorkflowEngine.cs`, ~line 437

```csharp
_ = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromHours(slaHours), ct);
    // ... no try/catch
}, ct);
```

Any exception thrown inside the timeout task is silently ignored. If the instance has already been removed from `_active` when the timeout fires, the code throws `KeyNotFoundException` that is never observed.

**Recommendation:**
- Wrap the body in `try/catch(Exception ex)` and log errors.
- Use `IHostedService` with a managed timer or `System.Timers.Timer` for structured cancellation.

### 3.4 Empty `catch` Blocks
Several locations silently swallow exceptions:

| File | Location | Issue |
|---|---|---|
| `GitService.cs` | `PruneStaleWorktreesAsync` | `catch { /* best effort */ }` loses the exception type |
| `WorkflowEngine.cs` | `GetAvailableDefinitions` | `catch (Exception ex)` logs warning but drops inner exceptions |
| `FilterConditionEvaluator.cs` | `TryParseJsonArray/Object` | `catch { return false; }` hides malformed JSON silently |

**Recommendation:** At minimum log the exception at `Debug` level so it can be correlated with user-visible errors. Reserve truly silent catches only for expected benign failures (e.g., duplicate column migration).

### 3.5 `GitService.IsAvailable` Is Not Thread-Safe
**File:** `src/SAGIDE.Service/Infrastructure/GitService.cs`

```csharp
if (_available.HasValue) return _available.Value;
var (ok, _) = RunGitSync(".", "--version");
_available = ok;
```

`_available` is a `bool?` field with no synchronisation. If two threads call `IsAvailable` concurrently before the first check completes, `RunGitSync` is executed twice.

**Recommendation:**
```csharp
private readonly Lazy<bool> _available;

public GitService(ILogger<GitService> logger)
{
    _available = new Lazy<bool>(() => RunGitSync(".", "--version").Ok);
}

public bool IsAvailable => _available.Value;
```

### 3.6 Hardcoded URL and `any` Cast in VS Code Extension
**Files:** `src/vscode-extension/src/extension.ts` (line 32), `src/vscode-extension/src/extension.ts` (line 142)

```typescript
const restBaseUrl = 'http://localhost:5100';          // hardcoded
agentType as any,                                     // type assertion bypasses strict checks
```

**Recommendation:**
- Read the REST URL from VS Code configuration:
  ```typescript
  const restBaseUrl = vscode.workspace.getConfiguration('sagIDE').get<string>('serviceUrl', 'http://localhost:5100');
  ```
- Resolve the `any` cast by defining `AgentType` as a `string` union in `StreamingOutputPanel.update`'s signature or widen the accepted type.

### 3.7 `PromptTemplateEngine.MaxOutputChars` Is a Magic Number
**File:** `src/SAGIDE.Service/Orchestrator/PromptTemplateEngine.cs`

```csharp
private const int MaxOutputChars = 4000;
```

4,000 characters may truncate meaningful output from earlier steps and is not configurable.

**Recommendation:**
- Expose this as a configurable value via `appsettings.json` (`SAGIDE:Orchestration:MaxStepOutputChars`).
- Pass it as a constructor parameter to `PromptTemplateEngine` (convert from `static` to instance class).

### 3.8 `EnvironmentLeakTests` Has Empty Pattern Arrays — Tests Always Pass
**File:** `tests/SAGIDE.Service.Tests/UnitTest1.cs`

```csharp
private static readonly HashSet<string> ApprovedAliases = [ ];
private static readonly string[] ForbiddenPatterns = [ ];
```

`SharedFiles_ContainNoForbiddenHostnames` always passes because `ForbiddenPatterns` is empty. `PromptYamls_MachineNames_AreApprovedAliases` flags every `@alias` as unapproved because `ApprovedAliases` is empty, so this test will fail as soon as any prompt YAML references a machine name.

**Recommendation:**
- Populate both arrays with the values documented in the comments above them.
- Move them to a separate, clearly-named test class so they don't get missed during review.

---

## 4. Testing

### 4.1 No Tests for `WorkflowEngine` DAG Evaluation
The DAG evaluation logic in `WorkflowEngine` (dependency resolution, router evaluation, feedback loops, convergence detection) is the most critical and complex path in the codebase, yet it has no direct unit tests. The existing tests focus on lower-level components (queue, result parser, DLQ).

**Recommendation:**
- Add unit tests for `WorkflowEngine` using an `ITaskSubmissionService` mock:
  - Linear DAG: verify steps execute in dependency order.
  - Parallel steps: verify concurrent submission.
  - Router: verify correct branch selected.
  - Feedback loop: verify iteration counter and escalation.
  - Pause/resume: verify no new tasks submitted while paused.
  - Cancel: verify downstream steps skipped.

### 4.2 No Crash-Recovery Integration Tests
**File:** `tests/SAGIDE.Service.Tests/`

There are no tests that exercise the crash recovery path (`RecoverRunningInstancesAsync`). A regression in this path would cause workflows to stall after service restarts without any test failure.

**Recommendation:**
- Add an integration test that:
  1. Starts an `AgentOrchestrator` with a real (in-memory or temp) `SqliteTaskRepository`.
  2. Submits tasks, simulates a restart by recreating the orchestrator.
  3. Verifies pending tasks are reloaded and completed.

### 4.3 Test File Naming
**File:** `tests/SAGIDE.Service.Tests/UnitTest1.cs`

The file is named `UnitTest1.cs` but contains six unrelated test classes (`TaskQueueTests`, `RetryPolicyTests`, `DeadLetterQueueTests`, `ResultParserTests`, `TimeoutConfigTests`, `EnvironmentLeakTests`, `AgentLimitsConfigTests`). This makes it hard to locate tests for a specific component.

**Recommendation:**
- Split into one file per test class, matching the naming convention of the other test files in the same directory (e.g., `TaskQueueTests.cs`, `RetryPolicyTests.cs`).

### 4.4 Missing Provider Tests for Error Paths
**File:** `tests/SAGIDE.Service.Tests/`

`ProviderFactoryTests.cs` exists, but there are no tests for provider error handling: 429 rate-limit retries, 5xx server errors, malformed JSON responses, streaming SSE parse failures.

**Recommendation:**
- Add tests using `HttpMessageHandler` mocks (e.g., via `Moq` or `WireMock`) to cover:
  - Retry on 429 with backoff.
  - Token-limit exceeded response.
  - Streaming mid-response disconnection.

---

## 5. Performance

### 5.1 SQLite: New Connection Per Operation
**File:** `src/SAGIDE.Service/Persistence/SqliteTaskRepository.cs`

Every repository method opens a new `SqliteConnection`. For high-throughput workflows (50 steps, all completing close together), this creates 50+ sequential connection-open/close cycles.

**Recommendation:**
- Use Microsoft.Data.Sqlite's built-in connection pooling by reusing the same connection string (already done — pooling is on by default for `Data Source=<file>`).
- For write-heavy paths, batch inserts within a single transaction rather than one transaction per `SaveTaskAsync` call.
- Consider a `SemaphoreSlim(1)` write gate to serialise writes without opening multiple connections.

### 5.2 `SubmitReadyStepsAsync` Is O(n) on Every Step Completion
**File:** `src/SAGIDE.Service/Orchestrator/WorkflowEngine.cs`

On each step completion, `SubmitReadyStepsAsync` scans all steps in the definition to find ready ones. For large workflows (50 steps), this is 50 × 50 = 2,500 comparisons on completion of the last step.

**Recommendation:**
- Maintain a `Dictionary<string, List<string>>` of step → steps that depend on it (reverse adjacency list), computed once at workflow start.
- On step completion, check only the direct dependents of the completed step.

### 5.3 `_taskToStep` and `_active` Dictionaries Grow Unbounded
**File:** `src/SAGIDE.Service/Orchestrator/WorkflowEngine.cs`

Completed workflow instances stay in `_active` and their task reverse-lookups stay in `_taskToStep` indefinitely. Under continuous operation this is a memory leak.

**Recommendation:**
- On workflow completion/cancellation, remove the instance from `_active`, `_taskToStep`, and `_locks`.
- Archive the final `WorkflowInstance` state to SQLite (already persisted) before evicting from memory.

### 5.4 Brute-Force Vector Search in `VectorStore`
**File:** `src/SAGIDE.Service/Rag/VectorStore.cs`

The comment acknowledges this: "Retrieval uses brute-force cosine similarity. Upgrade path: swap to ChromaDB if scale exceeds ~100K chunks."

**Recommendation:**
- Document the current limit more precisely (benchmark at 10K, 50K, 100K chunks on target hardware).
- Add a configuration-driven migration shim so swapping the vector backend only requires changing `appsettings.json`.
- Consider `sqlite-vss` (vector search extension for SQLite) as an intermediate option that keeps the single-file deployment model.

### 5.5 `AgentOrchestrator.BuildPrompt` Re-Reads Files on Every Prompt Build
**File:** `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs`

File contents are re-read from disk on every `BuildPrompt` call. For iterative feedback loops (up to 5 iterations) on the same files, the same content is read 5 times.

**Recommendation:**
- Cache file reads within the task execution scope (a `Dictionary<string, string>` local to `ExecuteTaskAsync`).
- Respect `MaxFileSizeChars` before adding to cache to bound memory.

---

## 6. Observability

### 6.1 No Distributed Tracing
The system has structured logging via Serilog, but no distributed tracing. Correlating a user action → workflow → step → LLM call requires manual log grepping across multiple log lines.

**Recommendation:**
- Add OpenTelemetry tracing (`dotnet add package OpenTelemetry.Extensions.Hosting`).
- Create spans for: task submission, task execution, LLM call, workflow step, RAG index/query.
- Export to Jaeger (local) or OTLP (cloud) depending on deployment.

### 6.2 No Metrics
Task throughput, p99 latency, token usage, DLQ depth, and active workflow count are available in code but never exported as metrics.

**Recommendation:**
- Add `System.Diagnostics.Metrics.Meter` counters:
  - `sag.tasks.submitted`, `sag.tasks.completed`, `sag.tasks.failed`
  - `sag.llm.input_tokens`, `sag.llm.output_tokens` (labelled by provider/model)
  - `sag.dlq.depth`, `sag.workflows.active`
- Expose via Prometheus scrape endpoint (`/metrics`) or OTLP push.

### 6.3 No Structured Log Context Propagation
Individual log lines include `taskId` or `instanceId` inline, but there is no ambient log scope that automatically attaches these IDs to all nested log calls.

**Recommendation:**
- Use Serilog's `LogContext.PushProperty` (or `ILogger` scopes) at the start of `ExecuteTaskAsync` and `OnTaskUpdateAsync`:
  ```csharp
  using var _ = _logger.BeginScope(new { TaskId = task.Id, WorkflowId = inst?.InstanceId });
  ```

---

## 7. Resilience

### 7.1 Default Timeouts Are All Set to 2 Hours
**File:** `src/SAGIDE.Service/appsettings.json`

```json
"Timeouts": {
  "TaskExecutionMs": 7200000,
  "Providers": {
    "Claude": 7200000,
    "Codex": 7200000,
    "Gemini": 7200000,
    "Ollama": 7200000
  }
}
```

2-hour timeouts for cloud providers (Claude, Codex, Gemini) mean a hung request will block a concurrency slot for up to 2 hours. The appropriate timeout for a cloud API call is typically 60–300 seconds, not 7,200 seconds.

**Recommendation:**
- Set cloud provider timeouts to 120–300 seconds (configurable).
- Keep Ollama timeouts higher (600–1800 seconds) to support large local models.
- Add a circuit breaker per provider (using Polly `CircuitBreakerPolicy`) that trips after N consecutive timeouts.

### 7.2 Broadcast Channel Silently Drops Messages Under Load
**File:** `src/SAGIDE.Service/Communication/NamedPipeServer.cs`

```csharp
_broadcastChannel = Channel.CreateBounded<PipeMessage>(new BoundedChannelOptions(...)
{
    FullMode = BoundedChannelFullMode.DropOldest,
```

Under high streaming throughput (many concurrent tasks), `DropOldest` will silently discard task-completed or workflow-completed notifications. The VS Code extension may never receive the completion event and will appear to hang.

**Recommendation:**
- Emit a log warning when a message is dropped.
- Increase the default `MaxBroadcastQueueSize` (currently 10,000) or make it adaptive.
- For critical lifecycle events (task completed, workflow completed), consider a separate unbounded or much larger channel so they are never dropped.

### 7.3 `ScheduleApprovalTimeout` Uses `Task.Run` Without Supervision
**File:** `src/SAGIDE.Service/Orchestrator/WorkflowEngine.cs`

```csharp
_ = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromHours(slaHours), ct);
    // modifies shared state without error handling
}, ct);
```

Fire-and-forget `Task.Run` calls that modify shared state are fragile. If an exception occurs inside the lambda, it is silently swallowed by the garbage collector (no `UnobservedTaskException` handler is registered).

**Recommendation:**
- Replace with a `CancellationToken`-linked `IHostedService` timer or register an `UnobservedTaskException` handler.
- Alternatively, store pending SLA timeouts in the database and evaluate them during the scheduler tick.

---

## 8. Developer Experience and Documentation

### 8.1 README Has Inconsistent Pipe Name
**File:** `README.md`

The quick-connectivity-check section references `AgenticIDEPipe`, but the actual default in `appsettings.json` is `SAGIDEPipe`.

> "Service terminal shows `NamedPipeServer` listening on ... `\\.\pipe\AgenticIDEPipe`"

**Recommendation:**
- Standardise the pipe name throughout: replace `AgenticIDEPipe` with `SAGIDEPipe` (or vice versa) in the README.

### 8.2 No OpenAPI / Swagger Endpoint
**File:** `src/SAGIDE.Service/Api/`

The REST API (`/api/tasks`, `/api/workflows`, `/api/prompts`, `/api/reports`) is used by the CLI, extension, and dashboard but has no machine-readable schema. New contributors must read the source to discover endpoints.

**Recommendation:**
- Add `builder.Services.AddEndpointsApiExplorer()` and `builder.Services.AddSwaggerGen()`.
- Enable in development: `if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }`

### 8.3 No Workflow YAML Schema
Workflow and prompt YAML files are only documented by example. Developers must read `WorkflowDefinition.cs` and `PromptDefinition.cs` to understand valid fields, constraints, and defaults.

**Recommendation:**
- Publish a JSON Schema for workflow YAML (`docs/workflow-schema.json`) and prompt YAML (`docs/prompt-schema.json`).
- Reference the schema in YAML files via `# yaml-language-server: $schema=../../docs/workflow-schema.json` for IDE validation.

### 8.4 No CONTRIBUTING Guide or ADRs
There is no `CONTRIBUTING.md` describing:
- How to run the service locally.
- How to run the test suite.
- Commit message conventions.
- How to add a new agent provider.

There are no Architecture Decision Records (ADRs) explaining key decisions (e.g., why named pipes instead of WebSockets, why SQLite instead of a proper message queue, why Scriban for some templates and custom Regex for others).

**Recommendation:**
- Add `CONTRIBUTING.md` with build/test/run instructions.
- Add `docs/adr/` directory with at least ADRs for: IPC mechanism choice, persistence choice, dual template engine, and multi-host Ollama routing.

### 8.5 `WorkflowParameter.Type` Is Undocumented and Unvalidated
**File:** `src/SAGIDE.Core/Models/WorkflowDefinition.cs`

```csharp
public string Type { get; set; } = "string";
```

The `Type` property is documented as `"string"` by default but valid values are not documented anywhere, and `WorkflowDefinitionLoader` never validates them.

**Recommendation:**
- Document valid types in XML doc and in the workflow YAML schema.
- Add validation in `WorkflowDefinitionLoader` with a clear error message when an unsupported type is used.

### 8.6 No `.editorconfig` for Code Style Consistency
There is no `.editorconfig` file. Contributors using different IDEs may apply different indentation, line endings, or `using` directive placement, causing noisy diffs.

**Recommendation:**
- Add a `.editorconfig` aligned with the existing C# and TypeScript code style (4-space indent, LF line endings for TypeScript, `using` directives inside namespace, etc.).

---

## 9. Dependency and Package Hygiene

### 9.1 `VectorStore` and `SqliteTaskRepository` Share a Database File
**Files:** `src/SAGIDE.Service/Program.cs` (lines 61–63, 186–190)

Both `SqliteTaskRepository` and `VectorStore` are initialised with the same `dbPath`. SQLite WAL mode allows concurrent readers, but a write from `VectorStore` (embedding upserts during RAG indexing) can contend with a write from `SqliteTaskRepository` (task persistence under load).

**Recommendation:**
- Separate the databases: `agentic-ide.db` for orchestration state and `rag-vectors.db` for embeddings.
- Or move `VectorStore` schema init into `SqliteTaskRepository.InitializeAsync()` so both share one managed connection lifecycle.

### 9.2 `Scriban` and Custom Regex Template Engine Coexist
**Files:** `src/SAGIDE.Service/Orchestrator/PromptTemplateEngine.cs`, `src/SAGIDE.Service/Orchestrator/SubtaskCoordinator.cs`

Prompt authors must learn two different template syntaxes (`{{var}}` vs `{{ var }}` with Scriban's full scripting) depending on whether the prompt is a workflow step or a SubtaskCoordinator prompt.

**Recommendation (same as §2.5):**
- Consolidate on Scriban for all template rendering.
- Remove `PromptTemplateEngine.cs` after migrating workflow step prompts.

### 9.3 `Cronos` Library Version Not Pinned
**File:** `src/SAGIDE.Service/SAGIDE.Service.csproj`

The `Cronos` library used by `SchedulerService` should be pinned to a specific version in the project file to avoid unexpected breaking changes on restore.

**Recommendation:**
- Verify all `<PackageReference>` entries have explicit version numbers (not floating `*` or version ranges).
- Run `dotnet list package --outdated` periodically and update dependencies with a dedicated PR.

---

## 10. Summary Checklist

### High Priority (Security / Correctness)
- [ ] Move API keys to environment variables; add `appsettings.Example.json`; gitignore `appsettings.json`
- [ ] Fix loop bug in `RecoverRunningInstancesAsync` (§3.1)
- [ ] Add error handling and logging to `ScheduleApprovalTimeout` fire-and-forget (§3.3, §7.3)
- [ ] Add at minimum a log warning for dropped broadcast channel messages (§7.2)
- [ ] Fix `EnvironmentLeakTests` empty pattern arrays (§3.8)

### Medium Priority (Architecture / Design)
- [ ] Replace concrete `AgentOrchestrator` dependencies with `ITaskSubmissionService` in `SchedulerService`, `SubtaskCoordinator`, and `TaskEndpoints` (§2.3)
- [ ] Migrate all providers to `IHttpClientFactory` (§2.4)
- [ ] Split `Program.cs` DI registration into extension methods (§2.7)
- [ ] Standardise on one template engine (§2.5, §9.2)
- [ ] Evict completed workflow instances from `_active` and `_taskToStep` (§5.3)

### Medium Priority (Code Quality / Testing)
- [ ] Fix `DeadLetterQueue` fire-and-forget persistence (§3.2)
- [ ] Make `GitService.IsAvailable` thread-safe via `Lazy<bool>` (§3.5)
- [ ] Move `UnitTest1.cs` contents into per-class test files (§4.3)
- [ ] Add `WorkflowEngine` DAG unit tests (§4.1)
- [ ] Add crash-recovery integration tests (§4.2)
- [ ] Make `PromptTemplateEngine.MaxOutputChars` configurable (§3.7)

### Lower Priority (Performance / Observability / DX)
- [ ] Add OpenTelemetry tracing and metrics (§6.1, §6.2)
- [ ] Reduce cloud provider timeouts to sensible values; add circuit breaker (§7.1)
- [ ] Add OpenAPI/Swagger endpoint (§8.2)
- [ ] Publish workflow and prompt YAML schemas (§8.3)
- [ ] Add CONTRIBUTING.md and ADRs (§8.4)
- [ ] Add `.editorconfig` (§8.6)
- [ ] Fix README pipe name inconsistency (§8.1)
- [ ] Pre-compute reverse adjacency list in `WorkflowEngine` for O(1) ready-step lookup (§5.2)
- [ ] Separate `VectorStore` and `SqliteTaskRepository` database files (§9.1)
- [ ] Add structured log context propagation with `ILogger.BeginScope` (§6.3)
