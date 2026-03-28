# SAGIDE — Code and Design Improvements

This document captures a full code-review and architecture audit of the SAGIDE
(**Structured Agent Graph IDE**) codebase. Findings are grouped by severity and
topic, with concrete file/line references and recommended fixes.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Priority 1 — Security](#2-priority-1--security)
3. [Priority 2 — Stability & Correctness](#3-priority-2--stability--correctness)
4. [Priority 3 — Design & Maintainability](#4-priority-3--design--maintainability)
5. [Priority 4 — Performance](#5-priority-4--performance)
6. [Priority 5 — Testing Gaps](#6-priority-5--testing-gaps)
7. [Priority 6 — Developer Experience](#7-priority-6--developer-experience)
8. [Schema & YAML Improvements](#8-schema--yaml-improvements)
9. [VS Code Extension](#9-vs-code-extension)
10. [Summary Score Card](#10-summary-score-card)

---

## 1. Architecture Overview

SAGIDE is a **local-first, deterministic agent orchestration engine** composed of:

| Layer | Technology | Role |
|-------|-----------|------|
| `.NET 9 Service` | ASP.NET Core Minimal API | LLM routing, workflow DAG, RAG, IPC server |
| `VS Code Extension` | TypeScript | Thin UI client — tree views, streaming panels, diff approval |
| `Prompts / Skills` | YAML | 35+ domain-specific agent definitions |
| `SQLite (WAL)` | Microsoft.Data.Sqlite | Durable task, workflow, and vector storage |
| `Named Pipes` | System.IO.Pipes | Sub-10 ms IPC between extension and service |
| `LLM Providers` | Ollama · Claude · Codex · Gemini | Multi-provider routing with affinity failover |

The design is sound: async-first, DI-centric, repository pattern for persistence,
event bus for internal fan-out. The items below are refinements, not rewrites.

---

## 2. Priority 1 — Security

### 2.1 API keys stored as plaintext in `appsettings.json`

**File:** `src/SAGIDE.Service/appsettings.json` lines 57–61

```json
"ApiKeys": {
  "Anthropic": "sk-",
  "OpenAI": "sk-",
  "Google": "AI"
}
```

Real keys committed here will leak into version control and process logs.

**Recommendation:**
- Use [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)
  in development: `dotnet user-secrets set "SAGIDE:ApiKeys:Anthropic" "sk-…"`.
- In CI/CD and production, read from environment variables
  (`SAGIDE__ApiKeys__Anthropic`) or a secret manager (Azure Key Vault, HashiCorp
  Vault).
- Add `appsettings.*.json` to `.gitignore` if real values are ever stored there.
- Provide an `appsettings.Template.json` with placeholder strings and document the
  setup steps in the README security section.

---

### 2.2 No secret redaction in Serilog logs

**File:** `src/SAGIDE.Service/Program.cs` lines 14–18

Structured log properties are never filtered. If a task `Description` or metadata
value contains an API key or bearer token fragment it will be written to
`logs/sagide-*.log` in plaintext.

**Recommendation:**
```csharp
// Add a destructuring policy before CreateLogger()
.Destructure.ByTransforming<AgentTask>(t => new
{
    t.TaskId, t.AgentType, t.ModelProvider,
    Description = t.Description[..Math.Min(80, t.Description.Length)] + "…"
})
```
Or use Serilog's `Enrichers` package and a custom `IDestructuringPolicy` that
masks any string property whose key contains `key`, `token`, `password`, or
`secret`.

---

### 2.3 Bearer-token guard is bypassable with length-padding attack

**File:** `src/SAGIDE.Service/Program.cs` lines 105–118

```csharp
var expectedBytes = Encoding.UTF8.GetBytes($"Bearer {restBearerToken}");
// …
var headerBytes = Encoding.UTF8.GetBytes(header);
if (!CryptographicOperations.FixedTimeEquals(headerBytes, expectedBytes))
```

`CryptographicOperations.FixedTimeEquals` requires both spans to be the same
length; if they differ, .NET returns `false` immediately (short-circuit). An
attacker can probe the token length cheaply.

**Recommendation:** Use HMAC so both sides are always the same length before
`FixedTimeEquals` is called. Store only the HMAC of the expected token and compare
the HMAC of the incoming header:
```csharp
// Derive a stable per-process key (or use a fixed secret stored in config)
private static readonly byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);

// At startup, compute the expected HMAC once:
var expectedHmac = HMACSHA256.HashData(_hmacKey,
    Encoding.UTF8.GetBytes($"Bearer {restBearerToken}"));

// In the middleware:
var incomingHmac = HMACSHA256.HashData(_hmacKey,
    Encoding.UTF8.GetBytes(header));

// Both arrays are always 32 bytes — FixedTimeEquals is now meaningful:
if (!CryptographicOperations.FixedTimeEquals(incomingHmac, expectedHmac))
```
Raw SHA-256 without a key does not prevent an attacker who can influence the input
from exploiting differences in hash-computation time. HMAC with a secret key
removes that vector.

---

### 2.4 Named-pipe ACL relies on Windows-only SID resolution

**File:** `src/SAGIDE.Service/Communication/NamedPipeServer.cs` line 240

The code resolves the current Windows user's SID to restrict pipe access. On
non-Windows systems this call may fail silently, leaving the pipe world-readable.

**Recommendation:**
- Add a runtime guard: `if (!OperatingSystem.IsWindows()) { /* skip ACL */ }`
  and log a warning.
- Document in the README that on Linux/macOS the pipe is protected only by Unix
  file-socket permissions (the default path under `/tmp`).

---

### 2.5 No HTTPS by default

**File:** `src/SAGIDE.Service/appsettings.json` line 12  
**File:** `src/SAGIDE.Service/Program.cs` line 35

The service defaults to `http://127.0.0.1:5100`. For loopback-only use this is
acceptable, but the README does not warn users who expose the port to a wider
network.

**Recommendation:**
- Document a Kestrel HTTPS configuration example using a dev certificate:
  `dotnet dev-certs https --trust`.
- For production, document a reverse-proxy (nginx / Caddy) setup.
- Add a startup warning log when the listen address is not loopback and HTTPS is
  not active.

---

### 2.6 Prompt variable interpolation not sanitised

**File:** `src/SAGIDE.Service/Orchestrator/WorkflowStepDispatcher.cs`

Workflow step `prompt` fields rendered with Scriban/Handlebars receive
caller-supplied variables without pre-validation. A malformed expression could
cause template-engine exceptions that surface internal state in error responses.

**Recommendation:**
- Validate template syntax at workflow-load time in `WorkflowDefinitionLoader` and
  return a structured error before execution begins.
- Consider an allowlist of template functions; disable file-system or reflection
  helpers in the Scriban `TemplateContext`.

---

## 3. Priority 2 — Stability & Correctness

### 3.1 Blocking `.GetAwaiter().GetResult()` during DI startup

**File:** `src/SAGIDE.Service/Infrastructure/ServiceCollectionExtensions.cs`
lines 61, 93–94, 307–308

```csharp
repo.InitializeAsync().GetAwaiter().GetResult();          // line 61
repo.PruneOldSamplesAsync(retentionDays).GetAwaiter().GetResult();  // lines 93-94
store.InitializeAsync().GetAwaiter().GetResult();         // line 307
```

These synchronous blocks inside DI factory lambdas can cause **thread-pool
starvation** under load (all available threads blocked waiting for SQLite I/O)
and make startup failures harder to diagnose.

**Recommendation:** Move database initialisation to `IHostedService.StartAsync`
or an `IHostApplicationLifetime.ApplicationStarted` callback that runs after the
DI container is fully built:

```csharp
// In AddSagidePersistence, register a hosted service instead:
services.AddSingleton<SqliteTaskRepository>(sp =>
    new SqliteTaskRepository(dbPath, sp.GetRequiredService<ILogger<…>>()));
services.AddSingleton<ITaskRepository>(sp => sp.GetRequiredService<SqliteTaskRepository>());

services.AddHostedService<DatabaseInitializer>();   // runs InitializeAsync in StartAsync
```

---

### 3.2 Fire-and-forget task in prompt execution endpoint

**File:** `src/SAGIDE.Service/Api/PromptEndpoints.cs` line 69

```csharp
_ = Task.Run(() => coordinator.RunAsync(prompt, variables, CancellationToken.None));
```

- Any unhandled exception inside `RunAsync` is silently swallowed.
- `CancellationToken.None` means the background task cannot be cancelled when the
  host shuts down gracefully.
- The caller has no way to correlate the accepted response with the running task.

**Recommendation:**
```csharp
// Register background tasks with the host's IHostApplicationLifetime
var cts = CancellationTokenSource.CreateLinkedTokenSource(
    ct, app.Lifetime.ApplicationStopping);

var runningTask = coordinator.RunAsync(prompt, variables, cts.Token);

// Track it so the host can await it during shutdown
backgroundTaskRegistry.Register(runningTask);

return Results.Accepted($"/api/tasks/{sourceTag}", new { status = "accepted", … });
```

---

### 3.3 `EmbeddingService` silently returns empty vectors when no model is found

**File:** `src/SAGIDE.Service/Rag/EmbeddingService.cs` lines 53–54

```csharp
return (string.Empty, string.Empty);   // silent failure
```

When no embedding model is configured, every call to `EmbedAsync` / `EmbedBatchAsync`
silently returns zero-length arrays, causing downstream RAG queries to return empty
results without any diagnostic information.

**Recommendation:**
```csharp
_logger.LogWarning(
    "No embedding model found in SAGIDE:Ollama:Servers. " +
    "Configured servers: {Servers}. RAG will be disabled.",
    string.Join(", ", all.Select(s => s["Name"] ?? "?")));
// Store a sentinel; guard EmbedAsync:
if (string.IsNullOrEmpty(_model))
    throw new InvalidOperationException(
        "RAG embedding is not configured. Add a model whose name contains 'embed'.");
```

---

### 3.4 `DeadLetterQueue` async persistence is fire-and-forget

**File:** `src/SAGIDE.Service/Resilience/DeadLetterQueue.cs` line 52

`PersistEntryAsync` is `_ = Task.Run(…)` with no exception handling. A transient
SQLite write failure will be lost silently, allowing a task to be retried from
memory only to disappear after a restart.

**Recommendation:** Either await the persistence call inside an `async` method or
wrap it:
```csharp
_ = PersistEntryAsync(entry).ContinueWith(t =>
    _logger.LogError(t.Exception, "DLQ persistence failed for task {Id}", entry.TaskId),
    TaskContinuationOptions.OnlyOnFaulted);
```

---

### 3.5 Race condition: workflow recovery before orchestrator initialisation

**File:** `src/SAGIDE.Service/Services/ServiceLifetime.cs`

`RecoverRunningInstancesAsync` depends on the orchestrator's in-memory task history
being populated first. The code awaits `InitializationCompleted` before recovery,
but if `StartProcessingAsync` throws, `_initCompleted` is never set and recovery
hangs indefinitely.

**Recommendation:**
```csharp
try   { await orchestrator.StartProcessingAsync(ct); }
catch { _initCompleted.TrySetException(ex); throw; }
finally { _initCompleted.TrySetResult(); }
```
Ensure `_initCompleted` is always resolved (result or exception) even on failure.

---

### 3.6 All timeouts set to 2 hours in `appsettings.json`

**File:** `src/SAGIDE.Service/appsettings.json` lines 117–128

```json
"NamedPipeRequestMs": 7200000,
"HealthCheckMs": 7200000,
"TaskExecutionMs": 7200000
```

A health-check timeout of 2 hours means a dead provider will be considered healthy
for up to 2 hours, blocking all failover. The README documents tighter defaults
(5 s health check, 30 min task execution) that are contradicted by the committed
config.

**Recommendation:** Restore sensible defaults:
- `HealthCheckMs`: 5 000 – 15 000 ms
- `TaskExecutionMs`: 1 800 000 ms (30 min)
- `NamedPipeRequestMs`: 300 000 ms (5 min)

Document that these can be overridden per-deployment.

---

## 4. Priority 3 — Design & Maintainability

### 4.1 `AgentOrchestrator` constructor has 23 parameters

**File:** `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs` lines 69–93

A constructor this wide is difficult to read, test, and extend. Adding a new
dependency requires touching every test factory.

**Recommendation:** Introduce a parameter-object record:
```csharp
public sealed record OrchestratorOptions(
    int MaxConcurrentTasks    = 5,
    int BroadcastThrottleMs   = 200,
    int MaxFileSizeChars      = 32_000);
```
and group optional collaborators into a `OrchestratorDependencies` bag. The
existing `OrchestratorFactory` in tests already does this; surfacing it in
production code reduces friction.

---

### 4.2 Service-locator pattern fallback in `AgentOrchestrator`

**File:** `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs` lines 98–101

```csharp
_providers = providerFactory is not null
    ? providerFactory.GetAllProviders().ToDictionary(p => p.Provider)
    : serviceProvider.GetServices<IAgentProvider>().ToDictionary(p => p.Provider);
```

The `IServiceProvider` parameter is retained solely for this fallback. Passing the
whole container into a domain object is an anti-pattern that hides dependencies and
complicates testing.

**Recommendation:** Remove the `IServiceProvider` dependency entirely. Tests that
need specific providers should pass a `ProviderFactory` instance (or a fake)
directly. The production wiring in `ServiceCollectionExtensions` already supplies
`providerFactory`.

---

### 4.3 `ParseProviderFromModelId` duplicated across codebase

**File:** `src/SAGIDE.Service/Api/PromptEndpoints.cs` lines 130–138  
Similar logic exists in `ProviderFactory`, `AgentOrchestrator`, and the extension.

**Recommendation:** Move this logic to a single static helper in `SAGIDE.Core` (e.g.
`ModelIdParser.ParseProvider(string modelId)`) and use it everywhere.

---

### 4.4 `WorkflowEngine` constructor builds its own sub-objects

**File:** `src/SAGIDE.Service/Orchestrator/WorkflowEngine.cs` lines 44–63

`WorkflowEngine` directly instantiates `WorkflowInstanceStore`,
`WorkflowLoopController`, `WorkflowApprovalGate`, `WorkflowStepDispatcher`, and
`WorkflowLifecycleManager`. This makes the engine difficult to unit-test in
isolation and couples construction order.

**Recommendation:** Register each sub-component in DI and inject the interface (or
concrete) through the constructor. Alternatively, accept a
`WorkflowComponents` record that can be swapped in tests.

---

### 4.5 Anonymous projection types in endpoint handlers

**File:** `src/SAGIDE.Service/Api/PromptEndpoints.cs` lines 15–24,  
`src/SAGIDE.Service/Api/TaskEndpoints.cs`, `ReportsEndpoints.cs`

Anonymous types are useful for quick prototyping but produce inconsistent JSON
shapes that break client contracts silently when field names are refactored.

**Recommendation:** Define named response record types in `SAGIDE.Core.DTOs`, e.g.:
```csharp
public sealed record PromptSummaryResponse(
    string Name, string Domain, string Version,
    string? Schedule, string? SourceTag, string? Description, bool HasSubtasks);
```
This also enables OpenAPI schema generation and client SDK generation.

---

### 4.6 Hardcoded magic strings and numbers scattered across the service

Examples:
- `"SAGIDE:NamedPipeName"`, `"SAGIDE:MaxConcurrentAgents"`, etc. (multiple files)
- `MaxFailoverAttempts = 5`, `MaxBusyRetries = 10` (AgentOrchestrator)
- `"nomic-embed-text"` substring match (EmbeddingService)

**Recommendation:** Centralise in a `SagideConstants` static class in
`SAGIDE.Core`:
```csharp
public static class SagideConstants
{
    public const string ConfigSection       = "SAGIDE";
    public const string NamedPipeNameKey    = "SAGIDE:NamedPipeName";
    public const int    DefaultMaxConcurrent = 5;
    // …
}
```

---

### 4.7 `PromptEndpoints` mixes parsing, routing, and HTTP concerns

**File:** `src/SAGIDE.Service/Api/PromptEndpoints.cs`

The single file handles: registry lookup, provider parsing, model-prefix stripping,
variable merging, template rendering, task submission, and background fire-and-forget.

**Recommendation:** Extract a `PromptExecutionService` that encapsulates the
orchestration logic. The endpoint handler becomes a thin HTTP adapter:
```csharp
var result = await promptExecutionService.RunAsync(domain, name, variables, ct);
return result.IsAccepted ? Results.Accepted(…) : Results.NotFound(…);
```

---

## 5. Priority 4 — Performance

### 5.1 `EmbedBatchAsync` loops with individual HTTP calls

**File:** `src/SAGIDE.Service/Rag/EmbeddingService.cs` lines 76–95

The code batches texts into groups of `_batchSize` but then sends one HTTP request
per text inside the batch. The outer "batch" loop does nothing beyond chunking the
input list.

**Recommendation:** Use the Ollama batch embeddings endpoint if supported, or at
minimum send all texts in one POST body per batch window:
```csharp
var payload = new { model = _model, prompts = batch };
// POST /api/embed (Ollama v0.3+) returns { "embeddings": [[...], [...]] }
```

---

### 5.2 `VectorStore` cosine-similarity search is O(n) in-memory scan

**File:** `src/SAGIDE.Service/Rag/VectorStore.cs`

All vectors are loaded from SQLite and compared in .NET. This is fine for small
corpora (<10 k chunks) but will degrade noticeably at scale.

**Recommendation:**
- Document the known scale limit (e.g. "up to ~50 k chunks on a modern laptop").
- For larger deployments, consider [sqlite-vec](https://github.com/asg017/sqlite-vec)
  (SQLite extension for approximate nearest-neighbour) or Qdrant.
- Add a `VectorStore.Count` gauge to the existing metrics endpoint so operators can
  monitor growth.

---

### 5.3 `TaskQueue` dual-heap is rebuilt on every `DequeueAsync`

**File:** `src/SAGIDE.Service/Orchestrator/TaskQueue.cs`

The comment says O(log n) dequeue, but if the priority heap is implemented as a
sorted list rebuild on each insertion/removal this degrades to O(n log n). Verify
that a true heap (e.g. `PriorityQueue<T,P>` from .NET 6+) is used.

**Recommendation:** Use .NET's built-in `PriorityQueue<TElement, TPriority>` which
provides O(log n) insert and dequeue, and is well-tested.

---

### 5.4 Broadcast throttle uses `Thread.Sleep` / polling

**File:** `src/SAGIDE.Service/Orchestrator/AgentOrchestrator.cs`

The `_broadcastThrottleMs` delay is applied with `await Task.Delay(…)` inside the
hot broadcast loop. Under high task throughput (> 50 tasks/sec) this creates
unnecessary latency for the final batch of events.

**Recommendation:** Use a `System.Threading.Timer` or a `PeriodicTimer`
(introduced in .NET 6) to flush batched events on a fixed schedule, decoupled from
the task-completion path.

---

## 6. Priority 5 — Testing Gaps

### 6.1 No streaming timeout / back-pressure tests

The retry logic in `ResilientHttpHandler` is tested for 429/503 responses, but
there are no tests for:
- Provider returning a partial stream then hanging (slow-loris scenario)
- Token-rate exceeding `MaxTokens` causing a truncated response

**Recommendation:** Add a `FakeStreamingProvider` that pauses mid-stream and verify
that the task times out and moves to DLQ with an appropriate error code.

---

### 6.2 No chaos / fault-injection tests for SQLite persistence

`SqliteTaskRepository` has CRUD tests but no tests for:
- Disk full during write
- Concurrent writers (WAL contention)
- Database file locked by external process

**Recommendation:** Inject a `Func<bool> shouldFail` hook in test builds, or use
SQLite's `pragma writable_schema` to force corruption and verify recovery.

---

### 6.3 VS Code extension has zero automated tests

**Directory:** `src/vscode-extension/`

All extension behaviour is manually tested. The TypeScript code (`ServiceConnection`,
`NamedPipeClient`, tree-view providers) is complex enough to warrant unit tests.

**Recommendation:**
- Add [Mocha](https://mochajs.org/) + [@vscode/test-electron](https://github.com/microsoft/vscode-test)
  for extension unit tests.
- Mock `vscode.workspace` and `net.Socket` to test `ServiceConnection` state
  transitions without a running service.
- Add at least one integration test that starts the service and connects.

---

### 6.4 `PromptWorkflowIntegrationTests` relies on real file paths

**File:** `tests/SAGIDE.Service.Tests/PromptWorkflowIntegrationTests.cs`

The test constructs a `PromptRegistry` pointing at `../../prompts` relative to the
test binary. If the working directory changes (CI, Docker) the test fails with a
misleading "no prompts loaded" message.

**Recommendation:** Copy the required YAML fixtures into `testData/` and point the
registry at a deterministic path:
```csharp
var promptsPath = Path.Combine(
    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
    "testData", "prompts");
```

---

### 6.5 No load / concurrency tests

There are no tests that submit > 10 tasks concurrently. The semaphore-based
concurrency limiter, channel broadcast, and DLQ have untested behaviour under
contention.

**Recommendation:** Add a parameterised test that submits 50 tasks with 5 concurrency
slots and verifies that all tasks complete, no tasks are lost, and the DLQ is empty
afterwards.

---

## 7. Priority 6 — Developer Experience

### 7.1 README missing security setup section

The README covers installation, config, and quick-start but has no section on:
- How to supply API keys securely (User Secrets, env vars)
- How to enable HTTPS for non-loopback deployments
- What the bearer token protects and how to generate a strong one

**Recommendation:** Add a **Security Setup** section immediately after the
configuration section.

---

### 7.2 `deploy.ps1` does not validate prerequisites

**File:** `deploy.ps1`

The script silently continues if `dotnet`, `node`, or `npm` are absent.

**Recommendation:** Add a `Test-Command` guard at the top:
```powershell
function Assert-Command($cmd) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Write-Error "$cmd is required but not found."; exit 1
    }
}
Assert-Command dotnet
Assert-Command node
Assert-Command npm
```

---

### 7.3 No structured `CONTRIBUTING.md`

New contributors have no guidance on:
- Branching strategy
- Commit message conventions
- How to run the full test suite
- How to add a new LLM provider
- How to add a new domain skill

**Recommendation:** Create `CONTRIBUTING.md` with at minimum:
- Development setup (one-liner to bootstrap)
- Test command (`dotnet test tests/SAGIDE.Service.Tests`)
- Adding a provider (point to `BaseHttpAgentProvider` + `ProviderFactory`)
- Adding a skill/prompt (point to `docs/skill-schema.json`)

---

### 7.4 `kill-and-start.ps1` is undocumented

**File:** `kill-and-start.ps1`

No comments explain what process this kills, why it is needed, or when it should
be used.

**Recommendation:** Add a comment header:
```powershell
# Kills any running SAGIDE service process and restarts it.
# Use during development to pick up code changes without a full rebuild.
```

---

### 7.5 OpenAPI only available in development

**File:** `src/SAGIDE.Service/Program.cs` line 124

```csharp
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
```

The `/openapi/v1.json` schema is unavailable in staging/production, making it
impossible to generate API clients for those environments.

**Recommendation:** Optionally expose it behind the bearer token in all environments:
```csharp
// Expose OpenAPI in all environments; protect with bearer token if configured
app.MapOpenApi().RequireAuthorization();
```
Or add a config flag: `"SAGIDE:RestApi:ExposeOpenApi": true`.

---

## 8. Schema & YAML Improvements

### 8.1 Skill YAML `capability_requirements` values are freeform strings

**File:** `docs/skill-schema.json`

The `capability_requirements` field accepts any string. An invalid capability name
(typo, deprecated value) silently results in no provider being matched.

**Recommendation:** Add an `enum` constraint in the JSON schema:
```json
"capability_requirements": {
  "type": "array",
  "items": {
    "type": "string",
    "enum": ["fast_general", "deep_analyst", "coder", "extractor", "critic"]
  }
}
```
The allowed values should be derived from `SAGIDE:Routing:Capabilities` in
`appsettings.json`.

---

### 8.2 Prompt YAML `model_preference.primary` is not validated against known providers

**File:** `docs/prompt-schema.json`

Any string is accepted for `model_preference.primary`. A prompt that references a
provider that is not running will fail at runtime with a non-obvious error.

**Recommendation:** Add a `pattern` or `enum` to the schema and validate prompts at
registry-load time:
```csharp
if (!_providers.ContainsKey(ParseProvider(prompt.ModelPreference?.Primary)))
    _logger.LogWarning("Prompt '{Key}' references unknown provider '{P}'", key, p);
```

---

### 8.3 `protocols/*.yaml` not referenced in schema validation

**Files:** `protocols/analyzable.yaml`, `collectible.yaml`, `reportable.yaml`

These protocol definitions are loaded and checked by `SkillYamlIntegrityTests` but
there is no JSON schema file for them, so property names and required fields are
never validated by editors or CI.

**Recommendation:** Create `docs/protocol-schema.json` and add it to the VS Code
extension's `contributes.jsonValidation` list alongside the existing skill and
prompt schemas.

---

## 9. VS Code Extension

### 9.1 `NamedPipeClient` reconnect loop has no maximum-attempt cap

**File:** `src/vscode-extension/src/ServiceConnection.ts`

The reconnect timer backs off up to `PipeReconnectMaxMs` but the number of
reconnect cycles is unbounded. A user who stops the service permanently will see
log spam until VS Code is restarted.

**Recommendation:** Add a `maxReconnectAttempts` limit (e.g. 20) after which the
client emits a user-visible warning and stops retrying:
```typescript
if (this._reconnectAttempts >= MAX_RECONNECT_ATTEMPTS) {
    vscode.window.showWarningMessage(
        'SAGIDE: service unreachable after 20 attempts. ' +
        'Run "SAGIDE: Start Service" to retry.');
    return;
}
```

---

### 9.2 Webview panels use inline scripts without a Content Security Policy

**File:** `src/vscode-extension/src/`

Webview panels that build HTML dynamically and inject content from LLM responses
are vulnerable to XSS if the CSP does not restrict inline scripts.

**Recommendation:** Set a strict CSP in every webview `html` property:
```html
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; script-src ${webview.cspSource};
               style-src ${webview.cspSource} 'unsafe-inline';">
```
Escape or sanitise any user- or LLM-supplied content before inserting it into the
DOM.

---

### 9.3 Extension `package.json` missing `engines.vscode` minimum version

**File:** `src/vscode-extension/package.json`

Without a minimum engine constraint, the extension may be installed on VS Code
versions that lack required APIs (e.g. `vscode.TreeItem2`, `FileDecoration`).

**Recommendation:** Set `"engines": { "vscode": "^1.85.0" }` (the version that
introduced all APIs used) and test against the minimum declared version in CI.

---

### 9.4 No activation event for the named-pipe connection status

**File:** `src/vscode-extension/package.json`

The extension activates on `onStartupFinished`, so the connection to the service is
attempted even when no SAGIDE workspace is open.

**Recommendation:** Add `"workspaceContains:.sagide"` as a primary activation event
to avoid unnecessary IPC overhead in non-SAGIDE workspaces:
```json
"activationEvents": [
  "workspaceContains:.sagide",
  "onStartupFinished"
]
```

---

## 10. Summary Score Card

| Dimension | Current | Target | Key Action |
|-----------|---------|--------|-----------|
| **Security** | 6 / 10 | 9 / 10 | Move API keys to secrets; fix timing-safe compare; CSP on webviews |
| **Stability** | 7 / 10 | 9 / 10 | Async DI init; fire-and-forget tracking; timeout defaults |
| **Maintainability** | 8 / 10 | 9 / 10 | Reduce constructor width; extract PromptExecutionService; named DTOs |
| **Performance** | 7 / 10 | 9 / 10 | True batch embeddings; sqlite-vec for scale; PeriodicTimer broadcast |
| **Test Coverage** | 7 / 10 | 9 / 10 | Extension tests; chaos/concurrency tests; fixture-based integration tests |
| **Developer Experience** | 6 / 10 | 9 / 10 | Security docs; CONTRIBUTING.md; OpenAPI in all environments |
| **Overall** | 7 / 10 | 9 / 10 | |

---

*Generated: 2026-03-05 — based on full codebase review of
[sanjeevakumarh/Structured-Agent-Graph-IDE](https://github.com/sanjeevakumarh/Structured-Agent-Graph-IDE).*
