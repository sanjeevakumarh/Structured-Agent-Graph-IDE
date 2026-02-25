export interface PipeMessage {
    type: string;
    requestId?: string;
    payload?: Buffer;
}

export const MessageTypes = {
    SubmitTask: 'submit_task',
    CancelTask: 'cancel_task',
    GetTaskStatus: 'get_task_status',
    GetAllTasks: 'get_all_tasks',
    ApproveTask: 'approve_task',
    TaskUpdate: 'task_update',
    Error: 'error',
    GetDlq: 'get_dlq',
    RetryDlq: 'retry_dlq',
    DiscardDlq: 'discard_dlq',
    DlqResponse: 'dlq_response',
    Ping: 'ping',
    Pong: 'pong',

    // Activity logging message types
    InitializeActivityLog: 'initialize_activity_log',
    GetActivityConfig: 'get_activity_config',
    UpdateActivityConfig: 'update_activity_config',
    GetActivityHours: 'get_activity_hours',
    GetActivityByHour: 'get_activity_by_hour',
    SyncGitHistory: 'sync_git_history',
    GenerateCommitMessage: 'generate_commit_message',
    ActivityResponse: 'activity_response',
    ToggleGitAutoCommit: 'toggle_git_auto_commit',
    StreamingOutput: 'streaming_output',
    // Workflow orchestration
    StartWorkflow: 'start_workflow',
    GetWorkflows: 'get_workflows',
    GetWorkflowInstances: 'get_workflow_instances',
    CancelWorkflow: 'cancel_workflow',
    WorkflowUpdate: 'workflow_update',

    // Workflow intervention (pause / resume / context update)
    PauseWorkflow: 'pause_workflow',
    ResumeWorkflow: 'resume_workflow',
    UpdateWorkflowContext: 'update_workflow_context',

    // Human approval gate
    WorkflowApprovalNeeded: 'workflow_approval_needed',
    ApproveWorkflowStep: 'approve_workflow_step',

    // Model discovery
    GetModels: 'get_models',
} as const;

export type ModelProvider = 'claude' | 'codex' | 'gemini' | 'ollama';

export type AgentType =
    | 'CodeReview'
    | 'TestGeneration'
    | 'Refactoring'
    | 'Debug'
    | 'Documentation'
    | 'SecurityReview';

export type TaskStatus =
    | 'Queued'
    | 'Running'
    | 'WaitingApproval'
    | 'Completed'
    | 'Failed'
    | 'Cancelled';

export interface SubmitTaskRequest {
    agentType: AgentType;
    modelProvider: ModelProvider;
    modelId: string;
    description: string;
    filePaths: string[];
    priority: number;
    metadata?: Record<string, string>;
    scheduledFor?: string;        // ISO 8601 UTC — task won't run until this time
    comparisonGroupId?: string;   // shared ID for multi-model comparison tasks
    modelEndpoint?: string;       // explicit Ollama server URL override (e.g. http://localhost:11434)
}

export interface TaskStatusResponse {
    taskId: string;
    status: TaskStatus;
    progress: number;
    statusMessage?: string;
    agentType: AgentType;
    modelProvider: ModelProvider;
    modelId: string;
    createdAt: string;
    startedAt?: string;
    completedAt?: string;
    result?: AgentResult;
    scheduledFor?: string;
    comparisonGroupId?: string;
}

export interface StreamingOutputMessage {
    taskId: string;
    textChunk: string;
    progressPercent?: number;
    tokensGeneratedSoFar: number;
    isLastChunk: boolean;
    error?: string;
    generatedAt: string;
}

export interface FileChange {
    filePath: string;          // absolute path when known; empty if LLM didn't specify
    originalContent: string;   // before (may be empty if not captured)
    newContent: string;        // proposed replacement
    description: string;       // human-readable label from the LLM
}

export interface AgentResult {
    taskId: string;
    success: boolean;
    output: string;
    issues: Issue[];
    changes: FileChange[];     // parsed file modifications ready for approval
    tokensUsed: number;
    estimatedCost: number;
    latencyMs: number;
    errorMessage?: string;
}

export interface DeadLetterEntry {
    id: string;
    originalTaskId: string;
    agentType: string;
    modelProvider: string;
    modelId: string;
    error: string;
    errorCode: string;
    failedAt: string;
    retryCount: string;
}

export interface Issue {
    filePath: string;
    line: number;
    severity: 'Info' | 'Low' | 'Medium' | 'High' | 'Critical';
    message: string;
    suggestedFix?: string;
}

// Activity logging types
export type ActivityType =
    | 'AgentTask'
    | 'HumanAction'
    | 'GitCommit'
    | 'FileModified'
    | 'SystemEvent';

export type GitIntegrationMode =
    | 'Disabled'
    | 'LogCommits'
    | 'GenerateMessages'
    | 'Bidirectional';

export interface ActivityEntry {
    id: string;
    workspacePath: string;
    timestamp: string;
    hourBucket: string;
    activityType: ActivityType;
    actor: string;
    summary: string;
    details?: string;
    taskId?: string;
    filePaths: string[];
    gitCommitHash?: string;
    metadata: Record<string, string>;
}

export interface ActivityLogConfig {
    workspacePath: string;
    enabled: boolean;
    gitIntegrationMode: GitIntegrationMode;
    markdownEnabled: boolean;
    createdAt: string;
    updatedAt: string;
}

export interface InitializeActivityLogRequest {
    workspacePath: string;
}

export interface GetActivityConfigRequest {
    workspacePath: string;
}

export interface UpdateActivityConfigRequest {
    workspacePath: string;
    config: ActivityLogConfig;
}

export interface GetActivityHoursRequest {
    workspacePath: string;
    limit?: number;
}

export interface GetActivityByHourRequest {
    workspacePath: string;
    hourBucket: string;
}

export interface SyncGitHistoryRequest {
    workspacePath: string;
    sinceDays?: number;
}

export interface GenerateCommitMessageRequest {
    workspacePath: string;
    sinceDays?: number;
}

// ── Workflow Orchestration Types ──────────────────────────────────────────────

export interface WorkflowParameter {
    name: string;
    type: string;
    default?: string;
}

export interface RouterBranch {
    condition: string;
    target: string;
}

export interface WorkflowStepDef {
    id: string;
    type: 'agent' | 'router' | 'tool' | 'constraint' | 'human_approval' | 'workspace_provision' | 'workspace_teardown';
    agent?: string;
    dependsOn: string[];
    prompt?: string;
    modelId?: string;
    modelProvider?: string;
    next?: string;
    maxIterations?: number;
    router?: { branches: RouterBranch[] };
    // tool step fields
    command?: string;
    workingDir?: string;
    exitCodePolicy?: string;
    // constraint step fields
    constraintExpr?: string;
    onConstraintFail?: string;
    // human_approval step fields
    slaHours?: number;
    timeoutAction?: string;
    approvalPrompt?: string;
    // shadow workspace step fields ()
    shadowBranch?: string;
    shadowAction?: string;
}

export interface ConvergencePolicy {
    maxIterations: number;
    escalationTarget: 'HUMAN_APPROVAL' | 'DLQ' | 'CANCEL';
    partialRetryScope: 'FAILING_NODES_ONLY' | 'FROM_CODEGEN' | 'FULL_WORKFLOW';
    convergenceHintMemory: boolean;
    timeoutPerIterationSec: number;
}

export interface WorkflowDefinition {
    id: string;
    name: string;
    description: string;
    parameters: WorkflowParameter[];
    steps: WorkflowStepDef[];
    isBuiltIn: boolean;
    convergencePolicy?: ConvergencePolicy;
}

export interface ApproveWorkflowStepRequest {
    instanceId: string;
    stepId: string;
    approved: boolean;
    comment?: string;
}

export interface WorkflowApprovalNeededPayload {
    instanceId: string;
    stepId: string;
    prompt: string;
}

export type WorkflowStepStatus =
    | 'pending'
    | 'running'
    | 'completed'
    | 'failed'
    | 'skipped'
    | 'waitingForApproval'
    | 'rejected';
export type WorkflowStatus = 'running' | 'completed' | 'failed' | 'cancelled' | 'paused';

export interface WorkflowStepExecution {
    stepId: string;
    taskId?: string;
    status: WorkflowStepStatus;
    output?: string;
    issueCount: number;
    iteration: number;
    startedAt?: string;
    completedAt?: string;
    error?: string;
    exitCode?: number;
}

export interface WorkflowInstance {
    instanceId: string;
    definitionId: string;
    definitionName: string;
    status: WorkflowStatus;
    /** True when paused: running tasks complete but no new steps are submitted. */
    isPaused: boolean;
    stepExecutions: Record<string, WorkflowStepExecution>;
    createdAt: string;
    completedAt?: string;
    filePaths: string[];
    workspacePath?: string;
}

/** Per-step model override chosen at workflow launch time (keyed by step ID). */
export interface StepModelOverride {
    provider: string;
    modelId: string;
    /** Ollama server URL; absent for cloud models. */
    endpoint?: string;
}

export interface StartWorkflowRequest {
    definitionId: string;
    inputs: Record<string, string>;
    filePaths: string[];
    defaultModelId: string;
    defaultModelProvider: string;
    modelEndpoint?: string;
    workspacePath?: string;
    /** Per-step model overrides: stepId → chosen model. */
    stepModelOverrides?: Record<string, StepModelOverride>;
}

export interface PauseWorkflowRequest {
    instanceId: string;
}

export interface ResumeWorkflowRequest {
    instanceId: string;
}

export interface UpdateWorkflowContextRequest {
    instanceId: string;
    /** Key/value pairs to merge into the workflow's InputContext. */
    updates: Record<string, string>;
}

export interface CancelWorkflowRequest {
    instanceId: string;
}

// ── Model Discovery Types ─────────────────────────────────────────────────────

/** A single selectable model option returned by the service's get_models handler. */
export interface ModelOption {
    key: string;
    label: string;
    provider: ModelProvider;
    modelId: string;
    description: string;
    endpoint?: string;  // Ollama server URL; absent for cloud models
}

/** Response payload for the get_models message. */
export interface GetModelsResponse {
    models: ModelOption[];
    /** Maps AgentType name → recommended model key from affinities config. */
    affinities: Record<string, string>;
}
