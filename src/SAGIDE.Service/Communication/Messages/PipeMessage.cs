namespace SAGIDE.Service.Communication.Messages;

// Wire protocol uses System.Text.Json (camelCase). byte[] Payload is base64 in JSON,
// matching the TypeScript client which base64-encodes the payload buffer.
public class PipeMessage
{
    public string Type { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public byte[]? Payload { get; set; }
}

public static class MessageTypes
{
    public const string SubmitTask = "submit_task";
    public const string CancelTask = "cancel_task";
    public const string GetTaskStatus = "get_task_status";
    public const string GetAllTasks = "get_all_tasks";
    public const string ApproveTask = "approve_task";
    public const string TaskUpdate = "task_update";
    public const string Error = "error";
    public const string GetDlq = "get_dlq";
    public const string RetryDlq = "retry_dlq";
    public const string DiscardDlq = "discard_dlq";
    public const string DlqResponse = "dlq_response";
    public const string Ping = "ping";
    public const string Pong = "pong";

    // Activity logging message types
    public const string InitializeActivityLog = "initialize_activity_log";
    public const string GetActivityConfig = "get_activity_config";
    public const string UpdateActivityConfig = "update_activity_config";
    public const string GetActivityHours = "get_activity_hours";
    public const string GetActivityByHour = "get_activity_by_hour";
    public const string SyncGitHistory = "sync_git_history";
    public const string GenerateCommitMessage = "generate_commit_message";
    public const string ActivityResponse = "activity_response";
    public const string ToggleGitAutoCommit = "toggle_git_auto_commit";
    public const string StreamingOutput = "streaming_output";

    // Workflow orchestration message types
    public const string StartWorkflow          = "start_workflow";
    public const string GetWorkflows           = "get_workflows";
    public const string GetWorkflowInstances   = "get_workflow_instances";
    public const string CancelWorkflow         = "cancel_workflow";
    public const string WorkflowUpdate         = "workflow_update";   // push event (no request ID)

    // Workflow intervention: pause / resume / context update
    public const string PauseWorkflow          = "pause_workflow";
    public const string ResumeWorkflow         = "resume_workflow";
    public const string UpdateWorkflowContext  = "update_workflow_context";

    // Human approval gate
    /// <summary>Server → client push: a human_approval step is waiting for user input.</summary>
    public const string WorkflowApprovalNeeded = "workflow_approval_needed";
    /// <summary>Client → server: user approved or rejected the approval gate.</summary>
    public const string ApproveWorkflowStep    = "approve_workflow_step";

    /// <summary>Client → server: request the configured model list and task affinities.</summary>
    public const string GetModels = "get_models";

    // ── Pipe authentication ───────────────────────────────────────────────────
    /// <summary>Client → server: shared-secret handshake (payload = UTF-8 secret).</summary>
    public const string PipeAuth   = "pipe_auth";
    /// <summary>Server → client: handshake accepted; normal messages may now be sent.</summary>
    public const string PipeAuthOk = "pipe_auth_ok";
}
