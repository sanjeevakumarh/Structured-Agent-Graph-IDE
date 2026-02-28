# SAG IDE — Code and Design Improvement Recommendations

> **Scope:** Full review of the `src/`, `tests/`, `tools/`, `utils/`, and `prompts/` directories as they exist in this repository.
> Each section is ordered by impact (highest first). Items tagged `[quick-win]` require ≤ 1 day of effort; `[medium]` ≤ 1 sprint; `[long-term]` ≥ multiple sprints or architectural work.

---

## Table of Contents

1. [Architecture](#1-architecture)
2. [Security](#2-security)
3. [Resilience and Reliability](#3-resilience-and-reliability)
4. [Code Quality](#4-code-quality)
5. [Performance and Scalability](#5-performance-and-scalability)
6. [Testing](#6-testing)
7. [Observability](#7-observability)
8. [Configuration Management](#8-configuration-management)
9. [VS Code Extension](#9-vs-code-extension)
10. [Developer Experience](#10-developer-experience)
11. [Documentation](#11-documentation)

---

## 1. Architecture

### 1.1 Replace the Service-Locator Fallback in `AgentOrchestrator` `[medium]`

**Location:** `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs` (constructor, lines 70–72)

```csharp
// Current — falls back to service locator if providerFactory is null
_providers = providerFactory is not null
    ? providerFactory.GetAllProviders().ToDictionary(p => p.Provider)
    : serviceProvider.GetServices<IAgentProvider>().ToDictionary(p => p.Provider);
```

`IServiceProvider` is stored on the class and used for nothing else. The service-locator branch was kept "for tests" but hides dependencies and makes unit testing harder.

**Recommendation:** Make `ProviderFactory` non-nullable in the constructor signature. Provide a test-specific constructor that accepts `IEnumerable<IAgentProvider>` directly, and remove the `IServiceProvider` field entirely.

---

### 1.2 Introduce a Circuit Breaker for Provider Endpoints `[medium]`

**Location:** `src/SAGIDE.Service/Resilience/ResilientHttpHandler.cs`, `src/SAGIDE.Service/Providers/OllamaHostHealthMonitor.cs`

The current resilience model retries up to `MaxRetries` times per request but has no concept of an open circuit: a provider that has been failing for minutes continues to receive new requests. The `OllamaHostHealthMonitor` detects unhealthy hosts but only for Ollama—not for Claude, Codex, or Gemini.

**Recommendation:**
- Add a `CircuitBreaker<ModelProvider>` component (failure threshold, half-open probe interval) consulted by `ProviderFactory.GetProvider()`.
- Microsoft.Extensions.Http.Resilience (part of .NET 8+) ships a pipeline-based circuit breaker; adopting it also standardizes the retry and timeout policies already in `ResilientHttpHandler`.
- Extend `OllamaHostHealthMonitor`'s health logic to cover cloud providers (simple `/models` or `/health` ping).

---

### 1.3 Add Bulkhead Isolation per Provider `[medium]`

**Location:** `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs` (`_concurrencyLimiter`)

A single `SemaphoreSlim(_maxConcurrent)` is shared across all providers. A slow Claude request consumes a slot that could have been used for a fast Ollama request.

**Recommendation:** Create a `Dictionary<ModelProvider, SemaphoreSlim>` with configurable per-provider concurrency limits (e.g., `SAGIDE:AgentLimits:Providers:Claude:MaxConcurrent`).

---

### 1.4 Introduce Saga / Compensation Logic for Workflow Failures `[long-term]`

**Location:** `src/SAGIDE.Service/Orchestrator/WorkflowEngine.cs`

When a workflow step fails, downstream steps are skipped, but there is no rollback of side effects produced by already-completed steps (e.g., a `tool` step that committed code, or a `workspace_provision` step that created a shadow worktree).

**Recommendation:** Add an optional `compensate:` list of step IDs to `WorkflowStepDef`. On failure, the engine traverses the compensation chain in reverse-topological order, mirroring the Saga pattern used by Temporal and other workflow engines.

---

### 1.5 Split `SqliteTaskRepository` into Focused Repositories `[medium]`

**Location:** `src/SAGIDE.Service/Persistence/SqliteTaskRepository.cs`

The single class implements four interfaces (`ITaskRepository`, `IActivityRepository`, `IWorkflowRepository`, `ISchedulerRepository`). While the "same instance" approach avoids multiple SQLite connections, the class itself is very large and violates the Single Responsibility Principle.

**Recommendation:** Keep the single SQLite connection pooled in a `SqliteConnectionFactory` singleton. Create `TaskRepository`, `ActivityRepository`, `WorkflowRepository`, and `SchedulerRepository` partials or separate classes that each inject the factory. This keeps the connection-per-request semantics while distributing the SQL queries into manageable units.

---

### 1.6 CQRS: Separate Read and Write Paths `[long-term]`

Task queries (status polling, history retrieval) and mutations (submit, update status, persist result) share the same code path. Under load, read-heavy dashboards and write-heavy workflows compete for the same `SemaphoreSlim` and WAL write lock.

**Recommendation:** Define explicit command objects (`SubmitTaskCommand`, `CompleteTaskCommand`) and query objects (`GetTaskStatusQuery`, `ListHistoryQuery`). The query path can read from an in-memory `TaskQueue` cache first and fall back to SQLite; the write path persists to SQLite and invalidates the cache.

---

## 2. Security

### 2.1 Remove Plaintext API Keys from `appsettings.json` `[quick-win]`

**Location:** `src/SAGIDE.Service/appsettings.json` (lines 52–56)

```json
"ApiKeys": {
  "Anthropic": "sk-",
  "OpenAI": "sk-",
  "Google": "AI"
}
```

Even with placeholder values checked in, the pattern teaches users to put real keys in `appsettings.json`, which is typically version-controlled.

**Recommendations:**
- Remove the `ApiKeys` section from `appsettings.json` entirely; add it to `appsettings.Template.json` with explicit `<YOUR_KEY_HERE>` placeholders and a comment pointing to the preferred mechanism.
- Document environment-variable override (`SAGIDE__ApiKeys__Anthropic`) as the primary secrets mechanism for local development.
- For production or team use, document integration with `dotnet user-secrets`, Azure Key Vault, AWS Secrets Manager, or HashiCorp Vault.
- Add `appsettings.json` to `.gitignore` (or add a pre-commit hook that rejects files containing `sk-ant-`, `sk-`, `AIza`, etc.) to prevent accidental secret commits.

---

### 2.2 Fix Named Pipe ACL on Unix `[quick-win]`

**Location:** `src/SAGIDE.Service/Communication/NamedPipeServer.cs`

On Windows, `NamedPipeServerStream` uses the current-user ACL. On Linux/macOS the pipe is a socket under `/tmp/`, which is world-readable by default.

**Recommendation:** After creating the Unix socket file, call `File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite)` to restrict permissions to the owning user. Add a test in `EnvironmentLeakTests` that verifies the socket file mode on non-Windows hosts.

---

### 2.3 Sanitize Streaming Output in Extension Webviews `[quick-win]`

**Location:** `src/vscode-extension/src/views/StreamingOutputPanel.ts` (and `DiffApprovalPanel.ts`, `ComparisonPanel.ts`)

LLM output is rendered directly in webviews. If a model returns HTML or JavaScript tags, the VS Code webview could execute arbitrary scripts.

**Recommendation:**
- Set `retainContextWhenHidden: false` and apply VS Code's built-in `getNonce()` + `Content-Security-Policy` header in every webview panel:
  ```html
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; script-src 'nonce-${nonce}'; style-src ${webview.cspSource};">
  ```
- Escape `<`, `>`, `&`, `"` in any LLM text before injecting it into `innerHTML`.

---

### 2.4 Add HTTPS Support with a Self-Signed Certificate Option `[medium]`

**Location:** `src/SAGIDE.Service/Program.cs` (Kestrel config, line 46)

The REST API is HTTP-only on loopback. If the service is ever exposed on a LAN or container network (common in team deployments), traffic is unencrypted.

**Recommendation:** Add a Kestrel HTTPS endpoint using a development certificate (`dotnet dev-certs https`) by default, controllable via `SAGIDE:RestApi:UseTls: true`. Document how to replace the dev certificate for team/server deployments.

---

### 2.5 Validate and Bound `tool` Step Commands `[medium]`

**Location:** `src/SAGIDE.Core/Models/WorkflowDefinition.cs` (`WorkflowStepDef.Command`)

`tool` steps execute arbitrary shell commands. The `WorkflowPolicyEngine.ProtectedPathPatterns` guards against writing to sensitive files, but does not restrict which commands can be run.

**Recommendation:**
- Add an optional `SAGIDE:WorkflowPolicy:AllowedCommands` allowlist (glob patterns like `dotnet *`, `npm *`) evaluated before a `tool` step is submitted.
- Log and audit every command execution at `Warning` level even when allowed.
- Consider running tool steps in a restricted subprocess (no network, no `sudo`, limited environment variables).

---

### 2.6 Store DLQ Error Messages with Scrubbing `[quick-win]`

**Location:** `src/SAGIDE.Service/Resilience/DeadLetterQueue.cs` (`Enqueue`, line 43)

`ErrorMessage` is stored verbatim to SQLite. LLM providers sometimes echo the full request body—including file contents or prompt text—in error responses, which could expose sensitive code or PII.

**Recommendation:** Apply the same `LoggingConfig` redaction rules used for structured logs to `ErrorMessage` before persisting to the DLQ. Truncate `ErrorMessage` to a configurable `MaxDlqErrorMessageChars` (default 2000).

---

## 3. Resilience and Reliability

### 3.1 Add Jitter to Exponential Backoff `[quick-win]`

**Location:** `src/SAGIDE.Service/Resilience/RetryPolicy.cs` (`GetDelay`, line 12)

```csharp
BackoffStrategy.Exponential => InitialDelay * Math.Pow(2, attempt),
```

Pure exponential backoff causes thundering-herd collisions when many tasks retry simultaneously after a provider outage.

**Recommendation:** Apply full jitter: `TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * base.TotalMilliseconds)`. This is the AWS-recommended approach for distributed retry workloads.

---

### 3.2 Propagate RAG Pipeline Cancellation and Add Timeout `[quick-win]`

**Location:** `src/SAGIDE.Service/Rag/RagPipeline.cs`, `src/SAGIDE.Service/Rag/WebFetcher.cs`

The RAG pipeline operations (`IndexDataSourcesAsync`, `GetRelevantContextAsync`) accept a `CancellationToken` but there is no dedicated timeout budget. A slow embedding server can block an entire workflow step indefinitely, consuming the global `TaskExecutionMs` timeout.

**Recommendation:**
- Add a `SAGIDE:Rag:PipelineTimeoutMs` config key (default 60 000 ms).
- In `RagPipeline.FetchAndGetContextAsync`, wrap the call in a `CancellationTokenSource.CreateLinkedTokenSource` with that budget.

---

### 3.3 Persist Broadcast-Drop Metrics `[quick-win]`

**Location:** `src/SAGIDE.Service/Communication/NamedPipeServer.cs` (`_droppedMessageCount`)

`DroppedMessageCount` is incremented in memory but reset to zero on service restart. Operators cannot tell whether drops are chronic or transient.

**Recommendation:** Periodically flush `_droppedMessageCount` to an `IActivityRepository` counter row (or Serilog metric), resetting the in-memory counter after each flush.

---

### 3.4 Handle `PromptRegistry` Hot-Reload Race Condition `[quick-win]`

**Location:** `src/SAGIDE.Service/Prompts/PromptRegistry.cs`

The registry uses a `volatile` reference swap on file changes. If a consumer reads the dictionary during a reload that replaces hundreds of prompts, it may observe a partially-built index.

**Recommendation:** Replace the `volatile` swap with `Interlocked.Exchange` on an immutable `IReadOnlyDictionary<string, PromptDefinition>` that is fully built before the swap. No lock is needed for readers because they always see a complete, consistent snapshot.

---

### 3.5 Add a Startup Health Check Endpoint `[quick-win]`

**Location:** `src/SAGIDE.Service/Program.cs`

There is a `/api/health` mention in comments but no ASP.NET Core health check is registered with `AddHealthChecks()`.

**Recommendation:**
```csharp
builder.Services.AddHealthChecks()
    .AddCheck<SqliteHealthCheck>("sqlite")
    .AddCheck<OllamaHealthCheck>("ollama");
app.MapHealthChecks("/api/health");
```
This integrates with Docker/Kubernetes probes and makes `OllamaHostHealthMonitor` status queryable without parsing logs.

---

## 4. Code Quality

### 4.1 Replace Fire-and-Forget `_ = SomeAsync()` with Proper Async Handling `[quick-win]`

**Locations:**
- `src/SAGIDE.Service/Resilience/DeadLetterQueue.cs` (lines 52, 77, 80, 111)
- `src/SAGIDE.Service/ActivityLogging/ActivityLogger.cs` (multiple)

```csharp
_ = PersistEntryAsync(entry);   // exceptions silently swallowed
```

If `PersistEntryAsync` throws, the exception is silently lost and the entry is never written to the database.

**Recommendation:** Use a fire-and-forget wrapper that logs exceptions:
```csharp
private void FireAndForget(Task task) =>
    task.ContinueWith(t => _logger.LogError(t.Exception, "Background task failed"),
                      TaskContinuationOptions.OnlyOnFaulted);
```
Or, for operations that must eventually succeed, enqueue them on a `Channel<Func<Task>>` drained by a background loop.

---

### 4.2 Introduce Domain-Specific Exception Types `[medium]`

**Locations:** Throughout providers, orchestrator, workflow engine

Catch-all `catch (Exception ex)` blocks are used in many places. This makes it difficult to distinguish between transient errors (worth retrying) and permanent errors (not worth retrying).

**Recommendation:** Add a small exception hierarchy to `SAGIDE.Core`:
```
SagideException (base)
├── ProviderException
│   ├── ProviderTimeoutException
│   └── ProviderRateLimitException
├── WorkflowException
│   ├── InvalidWorkflowDefinitionException
│   └── WorkflowStepFailedException
└── TaskSubmissionException
```
Update catch blocks to handle these types specifically before falling back to `Exception`.

---

### 4.3 Enforce Input Validation at Service Boundaries `[medium]`

**Location:** `src/SAGIDE.Service/Communication/MessageHandler.cs`, `src/SAGIDE.Service/Api/TaskEndpoints.cs`

`SubmitTaskRequest` and `StartWorkflowRequest` are deserialized without validation. A missing `AgentType` or negative `Priority` will propagate deep into the orchestrator before failing.

**Recommendation:**
- Annotate DTOs in `SAGIDE.Core/DTOs` with `System.ComponentModel.DataAnnotations` attributes (`[Required]`, `[Range]`, `[MaxLength]`).
- Call `Validator.TryValidateObject` in `MessageHandler` before dispatching and return a structured error response when validation fails.
- Consider adopting FluentValidation for richer rule sets.

---

### 4.4 Replace Magic Numbers with Named Constants `[quick-win]`

**Locations (examples):**
- `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs`: `_broadcastThrottleMs` default `200`, `_maxFileSizeChars` default `32_000`
- `src/SAGIDE.Service/Communication/NamedPipeServer.cs`: heartbeat interval `30s`, consecutive-error back-off `5s`
- `src/SAGIDE.Service/Rag/VectorStore.cs`: LRU TTL `4h`, cache size

**Recommendation:** Extract these into a `SagideDefaults` static class in `SAGIDE.Core` or bind them from `appsettings.json` sections that already exist (many already have config keys — just ensure all code paths read from config, not hardcoded fallbacks scattered across files).

---

### 4.5 Fix Inconsistent ID Generation `[quick-win]`

**Locations:**
- `SAGIDE.Core/Models/AgentTask.cs`: `Guid.NewGuid().ToString("N")[..12]` — 12 hex chars (48 bits of randomness)
- `SAGIDE.Core/Models/WorkflowInstance.cs`: `Guid.NewGuid().ToString("N")[..8]` — 8 hex chars (32 bits of randomness)

Two different ID lengths mean collision probability differs across entity types, and the lengths are too short for production volumes (birthday paradox starts at ~65,000 entities for 8-char IDs).

**Recommendation:** Use `Guid.NewGuid().ToString("N")` (full 32-char) or a ULID/NanoID library for sortable, URL-safe IDs. Standardise to a single `IdGenerator.NewId()` helper so the scheme can be changed in one place.

---

### 4.6 Eliminate Nullable Optionals on Required Constructor Parameters `[medium]`

**Location:** `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs` (constructor)

```csharp
ITaskRepository? repository = null,
ActivityLogger? activityLogger = null,
Infrastructure.GitService? gitService = null,
...
```

These are nullable only for test convenience, but in production they are always provided. The nullable annotation misleads readers and causes null-guard boilerplate throughout the class.

**Recommendation:** Make them non-nullable and create a dedicated `AgentOrchestratorTestBuilder` (or use a mocking framework) in test code to supply stubs. The production DI registration already provides all of them.

---

### 4.7 Remove BOM from C# Source Files `[quick-win]`

**Locations:** Several `.cs` files begin with `﻿` (UTF-8 BOM character), e.g., `AgentOrchestrator.cs` (line 1), `WorkflowEngine.cs`, `AgentTask.cs`.

BOM characters cause issues with `diff`, `grep`, and some CI tools. .NET itself does not require them.

**Recommendation:** Run `find src -name '*.cs' | xargs sed -i 's/^\xEF\xBB\xBF//'` (Linux) or use an `.editorconfig` rule `charset = utf-8` (without BOM) and re-save via your IDE.

---

## 5. Performance and Scalability

### 5.1 Pool SQLite Connections Properly `[medium]`

**Location:** `src/SAGIDE.Service/Persistence/SqliteTaskRepository.cs`

Each repository method opens a new `SqliteConnection`. While SQLite connections are cheap, under burst workloads (e.g., 50 parallel tasks completing simultaneously) this creates connection churn.

**Recommendation:**
- Use `Microsoft.Data.Sqlite`'s connection string cache (`Cache=Shared`) or a small `SemaphoreSlim`-guarded connection pool.
- For write-heavy paths, keep a single long-lived write connection and a separate read connection pool of 2–4 connections to exploit WAL's concurrent-read capability.

---

### 5.2 Batch Database Writes During Broadcast `[medium]`

**Location:** `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs` (streaming output handler)

Every streaming chunk that triggers a status update calls `_repository.UpsertTaskAsync()`. At 100 tok/sec with 5 concurrent tasks, this is 500 SQLite writes/sec.

**Recommendation:** Debounce status writes: accumulate updates in memory and flush to SQLite at most once per `BroadcastThrottleMs` (already configurable). The in-memory `TaskQueue` is the source of truth; SQLite writes can be eventual.

---

### 5.3 Avoid Repeated Linear Scans in `TaskQueue` `[quick-win]`

**Location:** `src/SAGIDE.Service/Orchestrator/TaskQueue.cs`

If `GetByStatus()` or `GetAll()` scans the full history dictionary on every call, callers like the scheduler and the REST `/api/results` endpoint can be expensive as history grows toward `maxHistory=1000`.

**Recommendation:** Add secondary indexes (e.g., `ConcurrentDictionary<AgentTaskStatus, HashSet<string>>`) maintained on enqueue/status-change. Return `IReadOnlyList<AgentTask>` from the index rather than a LINQ-filtered full scan.

---

### 5.4 Limit Scriban Template Rendering to Changed Variables Only `[medium]`

**Location:** `src/SAGIDE.Service/Prompts/PromptTemplate.cs`

Context variable substitution (`{{var}}`, `{{step.output}}`) re-renders the full Scriban template each time a step completes, even if the step's output is not referenced by any downstream step.

**Recommendation:** Pre-parse each template's referenced variable set at workflow-load time (`Scriban.Template.Parse().Page.Body.Statements`). Re-render only when a referenced variable changes.

---

## 6. Testing

### 6.1 Add End-to-End Named Pipe Tests `[medium]`

**Location:** `tests/SAGIDE.Service.Tests/`

All integration tests use the REST API (`WebApplicationFactory`). The named pipe IPC path—message framing, heartbeat, auth handshake, broadcast fan-out—has no automated tests.

**Recommendation:** Add an `IpcIntegrationTests` class that:
1. Starts a real `NamedPipeServer` (via `TestServer` or a background thread).
2. Connects with the TypeScript-equivalent C# `NamedPipeClient` stub.
3. Submits a task via pipe, awaits a `task_update` broadcast, and verifies the JSON payload.

---

### 6.2 Add Negative-Path Tests for Malformed Workflow YAML `[quick-win]`

**Location:** `tests/SAGIDE.Service.Tests/WorkflowDefinitionLoaderTests.cs`

Current tests verify that valid YAML parses correctly. There are no tests for:
- Missing required fields (`id`, `name`, `steps`)
- Circular dependency graphs
- Unknown step types
- Back-edges that exceed `MaxIterations`

**Recommendation:** Add a `WorkflowDefinitionValidatorTests` class using parameterized `[Theory]` tests covering each invalid case, verifying that the loader throws a descriptive `InvalidWorkflowDefinitionException` (see §4.2).

---

### 6.3 Increase Provider Error-Path Coverage `[medium]`

**Location:** `tests/SAGIDE.Service.Tests/ProviderErrorPathTests.cs`

Existing error-path tests mock HTTP responses. The following scenarios are not covered:
- Provider returns a well-formed HTTP 200 but with a malformed JSON body.
- Provider streams a partial SSE event that is cut off mid-token.
- Ollama health monitor marks a host unhealthy mid-request (race condition).

---

### 6.4 Add Property-Based Tests for `TextChunker` and `FilterConditionEvaluator` `[medium]`

**Locations:**
- `tests/SAGIDE.Service.Tests/TextChunkerTests.cs`
- `tests/SAGIDE.Service.Tests/FilterConditionEvaluatorTests.cs`

Deterministic unit tests cannot cover the range of real-world inputs. Property-based testing with FsCheck or CsCheck can quickly find edge cases (e.g., chunks larger than the document, filter expressions with nested parentheses).

---

### 6.5 Add Load / Stress Tests `[long-term]`

There are no tests exercising high concurrency (>100 simultaneous tasks), high message throughput (>1000 pipe messages/sec), or large payloads (>1 MB prompt context).

**Recommendation:** Add a `LoadTests` project using NBomber or k6 with a target of:
- 100 tasks/sec submitted via the REST API without DLQ overflow.
- Named pipe broadcast latency < 10 ms at 500 messages/sec.
- SQLite write throughput > 200 rows/sec without `busy_timeout` errors.

---

### 6.6 Enforce Code Coverage Thresholds in CI `[quick-win]`

There is no minimum coverage gate in the build pipeline.

**Recommendation:** Add `coverlet` to the test project and a coverage step in the build script:
```powershell
dotnet test --collect:"XPlat Code Coverage" `
    /p:CoverletOutputFormat=cobertura `
    /p:Threshold=70 /p:ThresholdType=line
```
Target ≥ 70% line coverage initially, raising to 80% over subsequent sprints.

---

## 7. Observability

### 7.1 Add Distributed Tracing with OpenTelemetry `[medium]`

**Location:** `src/SAGIDE.Service/Program.cs`

Serilog provides structured logging but there is no distributed trace correlation. A single task submission may touch the orchestrator, provider, database, and broadcast path—these are invisible as a single trace.

**Recommendation:**
```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(b => b
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("SAGIDE.*")
        .AddOtlpExporter());
```
Propagate `Activity.Current?.Id` as a `traceId` field in pipe messages and log entries. This enables correlation across the service and extension without changing the pipe protocol.

---

### 7.2 Expose Metrics Endpoint `[medium]`

Key operational metrics are currently only visible via logs:
- Active task count / DLQ depth
- Provider call latency by provider and model
- Broadcast channel drop rate
- Workflow step duration by step type

**Recommendation:** Register `AddOpenTelemetryMetrics()` with a Prometheus exporter (or `System.Diagnostics.Metrics`) and expose `/metrics`. Alternatively, include these counters in the `/api/health` response as a `stats` sub-object.

---

### 7.3 Include Correlation IDs in Pipe Messages `[quick-win]`

**Location:** `src/SAGIDE.Service/Communication/Messages/`

Each `PipeMessage` carries a `RequestId` (client-to-server correlation), but server-to-client broadcast messages do not carry a `TraceId` that links them back to the originating request.

**Recommendation:** Add a `string? TraceId` field to `PipeMessage`. Set it from `Activity.Current?.TraceId.ToString()` when creating broadcast messages, and forward it in the TypeScript extension's event payloads for end-to-end log correlation.

---

### 7.4 Add Structured Audit Log for Workflow State Transitions `[medium]`

**Location:** `src/SAGIDE.Core/Models/WorkflowInstance.cs` (`WorkflowStepExecution.AuditLog`)

`AuditLog` is populated in-memory but is not persisted to SQLite and is lost on restart.

**Recommendation:** Serialize `AuditLog` as a JSON column in the workflow instance row (or a separate `workflow_audit` table). This enables post-mortem queries like "how many times did step X loop before escalating?" without replaying logs.

---

## 8. Configuration Management

### 8.1 Add a Config Validation Step at Startup `[quick-win]`

**Location:** `src/SAGIDE.Service/Program.cs`

Configuration is read eagerly at startup, but missing or invalid values (e.g., a negative `MaxConcurrentAgents`, or a `PromptsPath` that does not exist) only surface as runtime errors deep in the call stack.

**Recommendation:**
```csharp
builder.Services.AddOptions<SagideConfig>()
    .Bind(builder.Configuration.GetSection("SAGIDE"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```
Decorate `SagideConfig` fields with `[Range(1, 100)]`, `[Required]`, etc. This fails fast with a clear message before the application accepts traffic.

---

### 8.2 Centralise Configuration into a Single Options Class `[medium]`

**Location:** `src/SAGIDE.Service/Program.cs` (multiple local `new XConfig()` + manual `Bind()` calls)

There are currently 10+ separate configuration objects bound individually. This makes it difficult to validate cross-cutting constraints (e.g., `ChunkSize` must be > `ChunkOverlap`).

**Recommendation:** Create a `SagideOptions` root class with nested sub-options (`OrchestratorOptions`, `RagOptions`, `ResilienceOptions`, etc.) and register them via `IOptions<T>` / `IOptionsMonitor<T>`. The structured approach also enables runtime config reload for non-sensitive settings.

---

### 8.3 Support Environment-Specific Config Files `[quick-win]`

**Location:** `src/SAGIDE.Service/Program.cs`

Only `appsettings.json` is loaded. There is no `appsettings.Development.json` or `appsettings.Production.json`.

**Recommendation:** ASP.NET Core already loads `appsettings.{Environment}.json` by convention when `ASPNETCORE_ENVIRONMENT` is set. Add an `appsettings.Development.json` with relaxed logging levels and no placeholder API keys, and document it in the README.

---

## 9. VS Code Extension

### 9.1 Reconnection Back-Off Should Be Bounded `[quick-win]`

**Location:** `src/vscode-extension/src/client/NamedPipeClient.ts`

The reconnect interval grows on each failure but the `PipeReconnectMaxMs` limit from config may not be respected in all code paths.

**Recommendation:** Cap the reconnect delay at `Configuration.pipeReconnectMaxMs` explicitly in every retry branch and add a unit test that verifies the cap is never exceeded.

---

### 9.2 Dispose Webview Panels on Extension Deactivation `[quick-win]`

**Location:** `src/vscode-extension/src/extension.ts`, panel registrations

Webview panels (`StreamingOutputPanel`, `DiffApprovalPanel`, `ComparisonPanel`, `WorkflowGraphPanel`) create VS Code webviews but may not push their `dispose()` calls to `context.subscriptions`.

**Recommendation:** Push every panel's `Disposable` into `context.subscriptions` so VS Code disposes them automatically on deactivation, preventing memory leaks during repeated extension reload cycles.

---

### 9.3 Replace `any` Types in TypeScript Sources `[medium]`

**Location:** `src/vscode-extension/src/` (various)

TypeScript's type safety is undermined by `any`-typed message payloads from the pipe. This is the most common source of silent bugs in extension code.

**Recommendation:** Define a discriminated union type for all pipe message types:
```typescript
type PipeMessage =
  | { type: 'task_update'; payload: TaskStatusResponse }
  | { type: 'streaming_output'; payload: StreamingOutputMessage }
  | { type: 'workflow_update'; payload: WorkflowInstance }
  // ...
```
Use `unknown` instead of `any` for deserialized JSON, and narrow with type guards.

---

### 9.4 Add Extension Integration Tests `[medium]`

**Location:** `src/vscode-extension/`

There are no automated tests for the extension. VS Code provides `@vscode/test-electron` for running tests inside a real VS Code instance, and `@vscode/test-cli` for headless environments.

**Recommendation:** Add a minimal test suite covering:
- Tree view providers render correctly with mock task data.
- `ServiceConnection` event emitters fire when mock pipe messages arrive.
- Commands are registered and invoke the correct handlers.

---

## 10. Developer Experience

### 10.1 Add a `docker-compose.yml` for Local Development `[medium]`

**Location:** project root

Setting up the full stack (service + Ollama + SearXNG) requires manually following multi-step README instructions and running PowerShell scripts that are Windows-only.

**Recommendation:**
```yaml
# docker-compose.yml
services:
  sagide:
    build: ./src/SAGIDE.Service
    ports: ["5100:5100"]
    environment:
      SAGIDE__Ollama__Servers__0__BaseUrl: http://ollama:11434
  ollama:
    image: ollama/ollama
  searxng:
    image: searxng/searxng
```
Also add a cross-platform shell alternative (`utils/start-dev.sh`) to the Windows-only `kill-and-start.ps1`.

---

### 10.2 Add `.editorconfig` and Code Style Enforcement `[quick-win]`

**Location:** project root (missing)

There is no `.editorconfig` and no `dotnet-format` / `eslint` step in the build pipeline. Code style varies between files (BOM presence, spacing, casing conventions).

**Recommendation:**
- Add `.editorconfig` with `indent_size = 4`, `charset = utf-8`, `trim_trailing_whitespace = true`.
- Add `dotnet format --verify-no-changes` to `utils/build-all.ps1`.
- Add `eslint` + `prettier` to the VS Code extension package and a lint step to `package.json`.

---

### 10.3 Provide a Cross-Platform Build Script `[quick-win]`

**Location:** `utils/build-all.ps1`, `deploy.ps1`, `kill-and-start.ps1`

All build and run scripts are PowerShell (Windows-first). Contributors on macOS or Linux must install `pwsh` or adapt the scripts manually.

**Recommendation:** Add a `Makefile` (or `justfile`) with targets `build`, `test`, `run`, `clean` that wrap the PowerShell scripts on Windows and call `dotnet` / `npm` directly on Unix. The `utils/runSearxng.sh` file shows the pattern already exists — just extend it.

---

### 10.4 Pin Dependency Versions `[quick-win]`

**Location:** `src/SAGIDE.Service/SAGIDE.Service.csproj`, `src/vscode-extension/package.json`

Several `PackageReference` entries use floating version ranges or implicit transitive dependencies. This makes builds non-reproducible across machines.

**Recommendation:**
- Use exact versions in `.csproj` (`Version="x.y.z"`) and run `dotnet outdated` periodically.
- Add a `package-lock.json` for the VS Code extension (commit it to the repo) and use `npm ci` in CI.

---

## 11. Documentation

### 11.1 Add Architecture Decision Records (ADRs) `[quick-win]`

The README explains *what* SAG IDE does but not *why* key decisions were made (e.g., named pipes over WebSockets, SQLite over PostgreSQL, Scriban over Handlebars).

**Recommendation:** Create a `docs/adr/` folder with numbered decision records following the Michael Nygard template:
- `001-named-pipes-over-websockets.md`
- `002-sqlite-wal-for-persistence.md`
- `003-scriban-for-templating.md`

ADRs help future contributors understand trade-offs and avoid re-litigating settled decisions.

---

### 11.2 Generate API Documentation `[quick-win]`

**Location:** `src/SAGIDE.Service/Api/`

The REST API endpoints have no OpenAPI / Swagger documentation. Consumers must read source code to discover routes, request shapes, and response schemas.

**Recommendation:**
```csharp
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
app.UseSwaggerUI(); // only in Development environment
```
Add `[ProducesResponseType]` attributes to minimal API endpoint lambdas.

---

### 11.3 Document the Named Pipe Protocol `[medium]`

**Location:** `src/SAGIDE.Service/Communication/Messages/`

The binary framing format (4-byte length prefix + JSON payload) and all 15+ message types are documented only in code comments. Anyone building a third client (CLI, Logseq plugin, web app) must reverse-engineer the protocol.

**Recommendation:** Add a `docs/pipe-protocol.md` that specifies:
- Frame format (byte order, length field, JSON encoding)
- Full message type catalogue with request/response shapes and examples
- Authentication handshake sequence diagram
- Heartbeat / reconnect behaviour

---

### 11.4 Add a `CONTRIBUTING.md` `[quick-win]`

**Location:** project root (missing)

There is no contributor guide explaining how to set up a development environment, run tests, or submit a pull request.

**Recommendation:** Create `CONTRIBUTING.md` covering:
- Prerequisites (exact SDK/Node versions)
- Local development setup (one-command or step-by-step)
- How to run the test suite and interpret results
- Branch naming and PR conventions
- How to add a new LLM provider

---

## Summary Matrix

| # | Area | Item | Effort | Impact |
|---|------|------|--------|--------|
| 2.1 | Security | Remove plaintext API keys | Quick-win | Critical |
| 2.3 | Security | Sanitize webview output | Quick-win | High |
| 4.1 | Code Quality | Fix fire-and-forget exceptions | Quick-win | High |
| 3.1 | Resilience | Add jitter to backoff | Quick-win | Medium |
| 1.2 | Architecture | Circuit breaker for providers | Medium | High |
| 1.3 | Architecture | Per-provider bulkhead | Medium | Medium |
| 4.2 | Code Quality | Domain exception hierarchy | Medium | Medium |
| 6.1 | Testing | Named pipe E2E tests | Medium | High |
| 7.1 | Observability | OpenTelemetry tracing | Medium | High |
| 8.1 | Config | Startup config validation | Quick-win | High |
| 2.2 | Security | Unix pipe ACL hardening | Quick-win | Medium |
| 6.6 | Testing | Coverage threshold in CI | Quick-win | Medium |
| 10.2 | DX | EditorConfig + dotnet-format | Quick-win | Low |
| 11.2 | Docs | OpenAPI / Swagger | Quick-win | Medium |
| 1.4 | Architecture | Saga compensation logic | Long-term | High |
| 1.5 | Architecture | Split SqliteTaskRepository | Medium | Medium |
| 6.5 | Testing | Load / stress tests | Long-term | Medium |
| 9.3 | Extension | Remove `any` types | Medium | Medium |
| 1.6 | Architecture | CQRS read/write separation | Long-term | Medium |
