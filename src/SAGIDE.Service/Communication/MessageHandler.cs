using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Communication.Messages;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.ActivityLogging;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Communication;

public class MessageHandler
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly ActivityLogger _activityLogger;
    private readonly GitIntegration _gitIntegration;
    private readonly Infrastructure.GitConfig? _gitConfig;
    private readonly IWorkflowEngine _workflowEngine;
    private readonly IConfiguration _configuration;
    private readonly TaskAffinitiesConfig _taskAffinities;
    private readonly ILogger<MessageHandler> _logger;
    private static readonly JsonSerializerOptions JsonOptions = NamedPipeServer.JsonOptions;

    public MessageHandler(
        AgentOrchestrator orchestrator,
        ActivityLogger activityLogger,
        GitIntegration gitIntegration,
        IWorkflowEngine workflowEngine,
        IConfiguration configuration,
        TaskAffinitiesConfig taskAffinities,
        ILogger<MessageHandler> logger,
        Infrastructure.GitConfig? gitConfig = null)
    {
        _orchestrator    = orchestrator;
        _activityLogger  = activityLogger;
        _gitIntegration  = gitIntegration;
        _workflowEngine  = workflowEngine;
        _configuration   = configuration;
        _taskAffinities  = taskAffinities;
        _gitConfig       = gitConfig;
        _logger          = logger;
    }

    private static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, JsonOptions)!;
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    public async Task<PipeMessage> HandleAsync(PipeMessage message, CancellationToken ct)
    {
        try
        {
            return message.Type switch
            {
                MessageTypes.Ping => new PipeMessage { Type = MessageTypes.Pong, RequestId = message.RequestId },
                MessageTypes.SubmitTask => await HandleSubmitTask(message, ct),
                MessageTypes.CancelTask => await HandleCancelTask(message, ct),
                MessageTypes.GetTaskStatus => HandleGetTaskStatus(message),
                MessageTypes.GetAllTasks => HandleGetAllTasks(message),
                MessageTypes.ApproveTask => await HandleApproveTask(message, ct),
                MessageTypes.GetDlq => HandleGetDlq(message),
                MessageTypes.RetryDlq => await HandleRetryDlq(message, ct),
                MessageTypes.DiscardDlq => HandleDiscardDlq(message),
                MessageTypes.InitializeActivityLog => await HandleInitializeActivityLog(message, ct),
                MessageTypes.GetActivityConfig => await HandleGetActivityConfig(message, ct),
                MessageTypes.UpdateActivityConfig => await HandleUpdateActivityConfig(message, ct),
                MessageTypes.GetActivityHours => await HandleGetActivityHours(message, ct),
                MessageTypes.GetActivityByHour => await HandleGetActivityByHour(message, ct),
                MessageTypes.SyncGitHistory => await HandleSyncGitHistory(message, ct),
                MessageTypes.GenerateCommitMessage => await HandleGenerateCommitMessage(message, ct),
                MessageTypes.ToggleGitAutoCommit => HandleToggleGitAutoCommit(message),
                // Workflow orchestration
                MessageTypes.StartWorkflow         => await HandleStartWorkflow(message, ct),
                MessageTypes.GetWorkflows          => HandleGetWorkflows(message),
                MessageTypes.GetWorkflowInstances  => HandleGetWorkflowInstances(message),
                MessageTypes.CancelWorkflow        => await HandleCancelWorkflow(message, ct),
                MessageTypes.PauseWorkflow         => await HandlePauseWorkflow(message, ct),
                MessageTypes.ResumeWorkflow        => await HandleResumeWorkflow(message, ct),
                MessageTypes.UpdateWorkflowContext => await HandleUpdateWorkflowContext(message, ct),
                MessageTypes.ApproveWorkflowStep   => await HandleApproveWorkflowStep(message, ct),
                MessageTypes.GetModels             => HandleGetModels(message),
                _ => CreateError(message.RequestId, $"Unknown message type: {message.Type}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message type: {Type}", message.Type);
            return CreateError(message.RequestId, ex.Message);
        }
    }

    private PipeMessage HandleToggleGitAutoCommit(PipeMessage message)
    {
        if (_gitConfig is null)
        {
            return CreateError(message.RequestId, "Git config not available");
        }

        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var enabled = req.TryGetValue("enabled", out var el) && el.GetBoolean();
        _gitConfig.AutoCommitResults = enabled;
        _logger.LogInformation("Git auto-commit {State}", enabled ? "enabled" : "disabled");

        return new PipeMessage
        {
            Type = MessageTypes.ActivityResponse,
            RequestId = message.RequestId,
            Payload = Serialize(new Dictionary<string, string> { ["enabled"] = enabled.ToString().ToLowerInvariant() })
        };
    }

    private async Task<PipeMessage> HandleSubmitTask(PipeMessage message, CancellationToken ct)
    {
        var request = Deserialize<SubmitTaskRequest>(message.Payload!);

        // Field-level validation — return a typed Error before touching any service.
        if (string.IsNullOrWhiteSpace(request.Description))
            return CreateError(message.RequestId, "Task description is required");
        if (request.Description.Length > MaxDescriptionLength)
            return CreateError(message.RequestId,
                $"Task description exceeds maximum length ({MaxDescriptionLength} chars)");
        if (request.FilePaths is { Count: > MaxFilePaths })
            return CreateError(message.RequestId,
                $"FilePaths count exceeds limit ({MaxFilePaths})");

        var metadata = request.Metadata ?? [];
        if (!string.IsNullOrEmpty(request.ModelEndpoint))
            metadata["modelEndpoint"] = request.ModelEndpoint;

        var task = new AgentTask
        {
            AgentType = request.AgentType, ModelProvider = request.ModelProvider, ModelId = request.ModelId,
            Description = request.Description, FilePaths = request.FilePaths, Priority = request.Priority,
            Metadata = metadata,
            ScheduledFor = request.ScheduledFor,
            ComparisonGroupId = request.ComparisonGroupId,
            SourceTag = "vscode",
        };
        var taskId = await _orchestrator.SubmitTaskAsync(task, ct);
        var response = new TaskStatusResponse
        {
            TaskId = taskId, Status = AgentTaskStatus.Queued, AgentType = task.AgentType,
            ModelProvider = task.ModelProvider, ModelId = task.ModelId, CreatedAt = task.CreatedAt
        };
        return new PipeMessage { Type = MessageTypes.TaskUpdate, RequestId = message.RequestId, Payload = Serialize(response) };
    }

    private async Task<PipeMessage> HandleCancelTask(PipeMessage message, CancellationToken ct)
    {
        var taskId = Deserialize<string>(message.Payload!);
        if (string.IsNullOrWhiteSpace(taskId))
            return CreateError(message.RequestId, "Task ID is required");
        await _orchestrator.CancelTaskAsync(taskId, ct);
        return new PipeMessage
        {
            Type = MessageTypes.TaskUpdate, RequestId = message.RequestId,
            Payload = Serialize(new TaskStatusResponse { TaskId = taskId, Status = AgentTaskStatus.Cancelled })
        };
    }

    private PipeMessage HandleGetTaskStatus(PipeMessage message)
    {
        var taskId = Deserialize<string>(message.Payload!);
        if (string.IsNullOrWhiteSpace(taskId))
            return CreateError(message.RequestId, "Task ID is required");
        var status = _orchestrator.GetTaskStatus(taskId);
        return new PipeMessage { Type = MessageTypes.TaskUpdate, RequestId = message.RequestId,
            Payload = status is not null ? Serialize(status) : null };
    }

    private PipeMessage HandleGetAllTasks(PipeMessage message) =>
        new() { Type = MessageTypes.TaskUpdate, RequestId = message.RequestId, Payload = Serialize(_orchestrator.GetAllTasks()) };

    private async Task<PipeMessage> HandleApproveTask(PipeMessage message, CancellationToken ct)
    {
        var approval = Deserialize<ApprovalRequest>(message.Payload!);
        await _orchestrator.ApproveTaskAsync(approval.TaskId, approval.Approved, ct);
        return new PipeMessage { Type = MessageTypes.TaskUpdate, RequestId = message.RequestId };
    }

    private PipeMessage HandleGetDlq(PipeMessage message)
    {
        var entries = _orchestrator.DLQ.GetAll();
        return new PipeMessage
        {
            Type = MessageTypes.DlqResponse, RequestId = message.RequestId,
            Payload = Serialize(entries.Select(e => new Dictionary<string, string>
            {
                ["id"] = e.Id, ["originalTaskId"] = e.OriginalTaskId,
                ["agentType"] = e.AgentType.ToString(), ["modelProvider"] = e.ModelProvider.ToString(),
                ["modelId"] = e.ModelId, ["error"] = e.ErrorMessage, ["errorCode"] = e.ErrorCode ?? "",
                ["failedAt"] = e.FailedAt.ToString("O"), ["retryCount"] = e.RetryCount.ToString()
            }).ToList())
        };
    }

    private async Task<PipeMessage> HandleRetryDlq(PipeMessage message, CancellationToken ct)
    {
        var dlqId = Deserialize<string>(message.Payload!);
        var newTaskId = await _orchestrator.RetryFromDlqAsync(dlqId, ct);
        var payload = newTaskId is not null
            ? new Dictionary<string, string> { ["retried"] = "true", ["newTaskId"] = newTaskId }
            : new Dictionary<string, string> { ["retried"] = "false", ["error"] = "DLQ entry not found" };
        return new PipeMessage { Type = MessageTypes.DlqResponse, RequestId = message.RequestId, Payload = Serialize(payload) };
    }

    private PipeMessage HandleDiscardDlq(PipeMessage message)
    {
        var dlqId = Deserialize<string>(message.Payload!);
        var discarded = _orchestrator.DLQ.Discard(dlqId);
        return new PipeMessage
        {
            Type = MessageTypes.DlqResponse, RequestId = message.RequestId,
            Payload = Serialize(new Dictionary<string, string>
                { ["discarded"] = discarded.ToString().ToLowerInvariant(), ["dlqId"] = dlqId })
        };
    }

    // Activity log handlers — TypeScript sends typed objects, not plain strings

    private async Task<PipeMessage> HandleInitializeActivityLog(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var workspacePath = req["workspacePath"].GetString()!;
        await _activityLogger.InitializeWorkspaceAsync(workspacePath, ct);
        return new PipeMessage { Type = MessageTypes.ActivityResponse, RequestId = message.RequestId,
            Payload = Serialize(new Dictionary<string, string> { ["initialized"] = "true", ["workspacePath"] = workspacePath }) };
    }

    private async Task<PipeMessage> HandleGetActivityConfig(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var workspacePath = req["workspacePath"].GetString()!;
        var config = await _activityLogger.GetConfigAsync(workspacePath, ct);
        return new PipeMessage { Type = MessageTypes.ActivityResponse, RequestId = message.RequestId,
            Payload = config != null ? Serialize(config) : null };
    }

    private async Task<PipeMessage> HandleUpdateActivityConfig(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var config = JsonSerializer.Deserialize<ActivityLogConfig>(req["config"].GetRawText(), JsonOptions)!;
        await _activityLogger.UpdateConfigAsync(config, ct);
        return new PipeMessage { Type = MessageTypes.ActivityResponse, RequestId = message.RequestId,
            Payload = Serialize(new Dictionary<string, string> { ["updated"] = "true" }) };
    }

    private async Task<PipeMessage> HandleGetActivityHours(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var workspacePath = req["workspacePath"].GetString()!;
        var limit = req.TryGetValue("limit", out var limitEl) ? limitEl.GetInt32() : 100;
        var hourBuckets = await _activityLogger.GetHourBucketsAsync(workspacePath, limit, ct);
        return new PipeMessage { Type = MessageTypes.ActivityResponse, RequestId = message.RequestId, Payload = Serialize(hourBuckets) };
    }

    private async Task<PipeMessage> HandleGetActivityByHour(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var workspacePath = req["workspacePath"].GetString()!;
        var hourBucket = req["hourBucket"].GetString()!;
        var activities = await _activityLogger.GetActivitiesByHourAsync(workspacePath, hourBucket, ct);
        return new PipeMessage { Type = MessageTypes.ActivityResponse, RequestId = message.RequestId, Payload = Serialize(activities) };
    }

    private async Task<PipeMessage> HandleSyncGitHistory(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var workspacePath = req["workspacePath"].GetString()!;
        DateTime? since = req.TryGetValue("sinceDays", out var dEl) ? DateTime.UtcNow.AddDays(-dEl.GetInt32()) : null;
        await _gitIntegration.SyncFromGitHistoryAsync(workspacePath, since, ct);
        return new PipeMessage { Type = MessageTypes.ActivityResponse, RequestId = message.RequestId,
            Payload = Serialize(new Dictionary<string, string> { ["synced"] = "true" }) };
    }

    private async Task<PipeMessage> HandleGenerateCommitMessage(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var workspacePath = req["workspacePath"].GetString()!;
        DateTime? since = req.TryGetValue("sinceDays", out var dEl) ? DateTime.UtcNow.AddDays(-dEl.GetInt32()) : null;
        var msg = await _gitIntegration.GenerateCommitMessageAsync(workspacePath, since, ct);
        // Wrap in an object so the client receives { message: string } rather than a bare JSON string.
        return new PipeMessage { Type = MessageTypes.ActivityResponse, RequestId = message.RequestId,
            Payload = Serialize(new Dictionary<string, string> { ["message"] = msg ?? string.Empty }) };
    }

    // ── Workflow handlers ──────────────────────────────────────────────────────

    private async Task<PipeMessage> HandleStartWorkflow(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<StartWorkflowRequest>(message.Payload!);
        var instance = await _workflowEngine.StartAsync(req, ct);
        return new PipeMessage
        {
            Type = MessageTypes.WorkflowUpdate, RequestId = message.RequestId,
            Payload = Serialize(instance)
        };
    }

    private PipeMessage HandleGetWorkflows(PipeMessage message)
    {
        string? workspacePath = null;
        if (message.Payload is { Length: > 0 })
        {
            var req = Deserialize<GetWorkflowsRequest>(message.Payload);
            workspacePath = req.WorkspacePath;
        }
        var defs = _workflowEngine.GetAvailableDefinitions(workspacePath);
        return new PipeMessage
        {
            Type = MessageTypes.WorkflowUpdate, RequestId = message.RequestId,
            Payload = Serialize(defs)
        };
    }

    private PipeMessage HandleGetWorkflowInstances(PipeMessage message)
    {
        var instances = _workflowEngine.GetAllInstances();
        return new PipeMessage
        {
            Type = MessageTypes.WorkflowUpdate, RequestId = message.RequestId,
            Payload = Serialize(instances)
        };
    }

    private async Task<PipeMessage> HandleCancelWorkflow(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<CancelWorkflowRequest>(message.Payload!);
        await _workflowEngine.CancelAsync(req.InstanceId, ct);
        var instance = _workflowEngine.GetInstance(req.InstanceId);
        return new PipeMessage
        {
            Type = MessageTypes.WorkflowUpdate, RequestId = message.RequestId,
            Payload = instance is not null ? Serialize(instance) : null
        };
    }

    private async Task<PipeMessage> HandlePauseWorkflow(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var instanceId = req["instanceId"].GetString()!;
        await _workflowEngine.PauseAsync(instanceId, ct);
        var instance = _workflowEngine.GetInstance(instanceId);
        return new PipeMessage
        {
            Type = MessageTypes.WorkflowUpdate, RequestId = message.RequestId,
            Payload = instance is not null ? Serialize(instance) : null
        };
    }

    private async Task<PipeMessage> HandleResumeWorkflow(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var instanceId = req["instanceId"].GetString()!;
        await _workflowEngine.ResumeAsync(instanceId, ct);
        var instance = _workflowEngine.GetInstance(instanceId);
        return new PipeMessage
        {
            Type = MessageTypes.WorkflowUpdate, RequestId = message.RequestId,
            Payload = instance is not null ? Serialize(instance) : null
        };
    }

    private async Task<PipeMessage> HandleUpdateWorkflowContext(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<Dictionary<string, JsonElement>>(message.Payload!);
        var instanceId = req["instanceId"].GetString()!;
        var updates    = JsonSerializer.Deserialize<Dictionary<string, string>>(
            req["updates"].GetRawText(), JsonOptions) ?? [];
        await _workflowEngine.UpdateContextAsync(instanceId, updates, ct);
        var instance = _workflowEngine.GetInstance(instanceId);
        return new PipeMessage
        {
            Type = MessageTypes.WorkflowUpdate, RequestId = message.RequestId,
            Payload = instance is not null ? Serialize(instance) : null
        };
    }

    private async Task<PipeMessage> HandleApproveWorkflowStep(PipeMessage message, CancellationToken ct)
    {
        var req = Deserialize<ApproveWorkflowStepRequest>(message.Payload!);
        await _workflowEngine.ApproveWorkflowStepAsync(
            req.InstanceId, req.StepId, req.Approved, req.Comment, ct);
        var instance = _workflowEngine.GetInstance(req.InstanceId);
        return new PipeMessage
        {
            Type = MessageTypes.WorkflowUpdate, RequestId = message.RequestId,
            Payload = instance is not null ? Serialize(instance) : null
        };
    }

    // ── GetModels — returns configured model list + affinities from appsettings ─

    private PipeMessage HandleGetModels(PipeMessage message)
    {
        // Build Ollama model options from SAGIDE:Ollama:Servers config
        var models = new List<object>();
        var serversSection = _configuration.GetSection("SAGIDE:Ollama:Servers");
        foreach (var server in serversSection.GetChildren())
        {
            var name    = server["Name"]    ?? "ollama";
            var baseUrl = server["BaseUrl"] ?? "";
            foreach (var modelEntry in server.GetSection("Models").GetChildren())
            {
                var modelId = modelEntry.Value ?? "";
                if (string.IsNullOrEmpty(modelId)) continue;
                var key = $"ollama-{name}-{modelId}";
                models.Add(new
                {
                    key,
                    label       = $"{name} / {modelId}  [Local]",
                    provider    = "ollama",
                    modelId,
                    endpoint    = baseUrl,
                    description = $"Ollama on {name}",
                });
            }
        }

        // Build OpenAI-compatible model options from SAGIDE:OpenAICompatible:Servers config
        var openAiCompatibleSection = _configuration.GetSection("SAGIDE:OpenAICompatible:Servers");
        foreach (var server in openAiCompatibleSection.GetChildren())
        {
            var name    = server["Name"]    ?? "local";
            var baseUrl = server["BaseUrl"] ?? "";
            foreach (var modelEntry in server.GetSection("Models").GetChildren())
            {
                var modelId = modelEntry.Value ?? "";
                if (string.IsNullOrEmpty(modelId)) continue;
                var key = $"codex-{name}-{modelId}";
                models.Add(new
                {
                    key,
                    label       = $"{name} / {modelId}  [Local]",
                    provider    = "codex",
                    modelId,
                    endpoint    = baseUrl,
                    description = $"OpenAI-compatible on {name}",
                });
            }
        }

        // Build cloud model options — always include so users can see and select them;
        // mark unconfigured ones so users know they need to set an API key.
        var anthropicKey = _configuration["SAGIDE:ApiKeys:Anthropic"] ?? "";
        var openaiKey    = _configuration["SAGIDE:ApiKeys:OpenAI"]    ?? "";
        var googleKey    = _configuration["SAGIDE:ApiKeys:Google"]    ?? "";

        static string ProviderHuman(string provider) => provider.ToUpperInvariant() switch
        {
            "CLAUDE" => "Anthropic",
            "CODEX"  => "OpenAI",
            "GEMINI" => "Google",
            _        => provider,
        };
        bool HasKey(string provider) => provider.ToUpperInvariant() switch
        {
            "CLAUDE" => !string.IsNullOrEmpty(anthropicKey),
            "CODEX"  => !string.IsNullOrEmpty(openaiKey),
            "GEMINI" => !string.IsNullOrEmpty(googleKey),
            _        => false,
        };
        string ProviderShort(string provider) => provider.ToLowerInvariant() switch
        {
            "claude" => "claude",
            "codex"  => "codex",
            "gemini" => "gemini",
            _        => provider.ToLowerInvariant(),
        };
        // appsettings key name to set for each cloud provider
        static string ApiKeySettingName(string provider) => provider.ToUpperInvariant() switch
        {
            "CLAUDE" => "SAGIDE:ApiKeys:Anthropic",
            "CODEX"  => "SAGIDE:ApiKeys:OpenAI",
            "GEMINI" => "SAGIDE:ApiKeys:Google",
            _        => $"SAGIDE:ApiKeys:{provider}",
        };

        var cloudSeen = new HashSet<string>();
        foreach (var (_, entry) in _taskAffinities.Affinities)
        {
            if (string.IsNullOrEmpty(entry.CloudModel) || string.IsNullOrEmpty(entry.CloudProvider))
                continue;

            var cKey = $"{ProviderShort(entry.CloudProvider)}-{entry.CloudModel}";
            if (!cloudSeen.Add(cKey)) continue;

            var hasKey  = HasKey(entry.CloudProvider);
            var descr   = hasKey
                ? $"{ProviderHuman(entry.CloudProvider)} cloud model"
                : $"{ProviderHuman(entry.CloudProvider)} — set {ApiKeySettingName(entry.CloudProvider)} in appsettings.json";

            models.Add(new
            {
                key         = cKey,
                label       = hasKey
                    ? $"{entry.CloudModel}  [Cloud / {ProviderHuman(entry.CloudProvider)}]"
                    : $"{entry.CloudModel}  [Cloud / {ProviderHuman(entry.CloudProvider)} — no key]",
                provider    = ProviderShort(entry.CloudProvider),
                modelId     = entry.CloudModel,
                endpoint    = (string?)null,
                description = descr,
            });
        }

        // Build affinities: agentType → recommended model key
        // Prefer cloud if key is configured; fall back to the first Ollama model matching LocalModel
        var allLocalOptions = models
            .OfType<object>()
            .Select(m =>
            {
                // Use reflection-free anonymous type access via JSON round-trip
                var j = JsonDocument.Parse(JsonSerializer.Serialize(m, JsonOptions));
                return (
                    key:     j.RootElement.GetProperty("key").GetString() ?? "",
                    modelId: j.RootElement.GetProperty("modelId").GetString() ?? "",
                    provider:j.RootElement.GetProperty("provider").GetString() ?? ""
                );
            })
            .Where(m => m.provider is "ollama" or "codex")
            .ToList();

        var affinities = new Dictionary<string, string>();
        foreach (var (agentType, entry) in _taskAffinities.Affinities)
        {
            var cloudKey = string.IsNullOrEmpty(entry.CloudModel) || string.IsNullOrEmpty(entry.CloudProvider)
                ? null
                : $"{ProviderShort(entry.CloudProvider)}-{entry.CloudModel}";

            if (cloudKey is not null && HasKey(entry.CloudProvider))
            {
                affinities[agentType] = cloudKey;
                continue;
            }

            // Fall back to local: find first local option (Ollama or OpenAI-compatible) matching LocalModel
            if (!string.IsNullOrEmpty(entry.LocalModel))
            {
                var match = allLocalOptions.FirstOrDefault(m => m.modelId == entry.LocalModel);
                if (match.key != "") { affinities[agentType] = match.key; continue; }
            }
        }

        return new PipeMessage
        {
            Type      = MessageTypes.GetModels,
            RequestId = message.RequestId,
            Payload   = Serialize(new { models, affinities }),
        };
    }

    // ── Validation limits ────────────────────────────────────────────────────
    // These guard against obviously malformed requests reaching the orchestrator.
    // They are intentionally conservative; real size limits on file content are
    // enforced in AgentOrchestrator via MaxFileSizeChars from config.
    private const int MaxDescriptionLength = 10_000;
    private const int MaxFilePaths         = 100;

    private static PipeMessage CreateError(string? requestId, string error) => new()
    {
        Type = MessageTypes.Error, RequestId = requestId,
        Payload = JsonSerializer.SerializeToUtf8Bytes(error, JsonOptions)
    };
}
