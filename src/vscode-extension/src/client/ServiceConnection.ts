import * as vscode from 'vscode';
import * as os from 'os';
import * as path from 'path';
import { NamedPipeClient } from './NamedPipeClient';
import { logError } from '../utils/Logger';
import {
    PipeMessage,
    MessageTypes,
    SubmitTaskRequest,
    TaskStatusResponse,
    StreamingOutputMessage,
    DeadLetterEntry,
    ActivityEntry,
    ActivityLogConfig,
    InitializeActivityLogRequest,
    GetActivityConfigRequest,
    UpdateActivityConfigRequest,
    GetActivityHoursRequest,
    GetActivityByHourRequest,
    SyncGitHistoryRequest,
    GenerateCommitMessageRequest,
    WorkflowDefinition,
    WorkflowInstance,
    StartWorkflowRequest,
    WorkflowApprovalNeededPayload,
    GetModelsResponse,
} from './MessageProtocol';

export class ServiceConnection implements vscode.Disposable {
    private client: NamedPipeClient;
    private statusBarItem: vscode.StatusBarItem;
    private _onTaskUpdate = new vscode.EventEmitter<TaskStatusResponse>();
    public readonly onTaskUpdate = this._onTaskUpdate.event;
    private _onStreamingOutput = new vscode.EventEmitter<StreamingOutputMessage>();
    public readonly onStreamingOutput = this._onStreamingOutput.event;
    private _onWorkflowUpdate = new vscode.EventEmitter<WorkflowInstance>();
    public readonly onWorkflowUpdate = this._onWorkflowUpdate.event;
    private _onApprovalNeeded = new vscode.EventEmitter<WorkflowApprovalNeededPayload>();
    public readonly onApprovalNeeded = this._onApprovalNeeded.event;

    constructor(pipeName: string, sharedSecret?: string) {
        const fullPipeName = process.platform === 'win32'
            ? `\\\\.\\pipe\\${pipeName}`
            : path.join(os.tmpdir(), `CoreFxPipe_${pipeName}`);

        this.client = new NamedPipeClient(fullPipeName, sharedSecret);
        this.statusBarItem = vscode.window.createStatusBarItem(
            vscode.StatusBarAlignment.Left, 100
        );
        this.statusBarItem.text = '$(plug) SAG: Connecting...';
        this.statusBarItem.show();

        this.client.on('connected', () => {
            this.statusBarItem.text = '$(check) SAG: Connected';
            this.statusBarItem.tooltip = 'SAG IDE service connected';
        });

        this.client.on('disconnected', () => {
            this.statusBarItem.text = '$(warning) SAG: Disconnected';
            this.statusBarItem.tooltip = 'SAG IDE service disconnected — reconnecting...';
        });

        this.client.on('message', (msg: PipeMessage) => {
            try {
                if (msg.type === MessageTypes.TaskUpdate && msg.payload) {
                    const status = JSON.parse(msg.payload.toString()) as TaskStatusResponse;
                    this._onTaskUpdate.fire(status);
                } else if (msg.type === MessageTypes.StreamingOutput && msg.payload) {
                    const streamMsg = JSON.parse(msg.payload.toString()) as StreamingOutputMessage;
                    this._onStreamingOutput.fire(streamMsg);
                } else if (msg.type === MessageTypes.WorkflowUpdate && msg.payload) {
                    const instance = JSON.parse(msg.payload.toString()) as WorkflowInstance;
                    this._onWorkflowUpdate.fire(instance);
                } else if (msg.type === MessageTypes.WorkflowApprovalNeeded && msg.payload) {
                    const payload = JSON.parse(msg.payload.toString()) as WorkflowApprovalNeededPayload;
                    this._onApprovalNeeded.fire(payload);
                }
            } catch (err) {
                // Malformed payload — skip this message rather than crashing the handler,
                // but surface the error so protocol issues can be diagnosed.
                logError('[ServiceConnection] Failed to process pipe message', err);
            }
        });
    }

    async connect(): Promise<void> {
        try {
            await this.client.connect();
        } catch {
            this.statusBarItem.text = '$(error) SAG: Service not running';
            this.statusBarItem.tooltip = 'Start the SAG IDE service first';
        }
    }

    async submitTask(request: SubmitTaskRequest): Promise<TaskStatusResponse | null> {
        const response = await this.client.send({
            type: MessageTypes.SubmitTask,
            payload: Buffer.from(JSON.stringify(request)),
        });
        if (response.payload) {
            // The server already broadcasts a Queued update to all clients via BroadcastAsync.
            // Firing _onTaskUpdate here would produce a duplicate event for the submitting window.
            return JSON.parse(response.payload.toString()) as TaskStatusResponse;
        }
        return null;
    }

    async cancelTask(taskId: string): Promise<void> {
        await this.client.send({
            type: MessageTypes.CancelTask,
            payload: Buffer.from(JSON.stringify(taskId)),
        });
    }

    async getTaskStatus(taskId: string): Promise<TaskStatusResponse | null> {
        const response = await this.client.send({
            type: MessageTypes.GetTaskStatus,
            payload: Buffer.from(JSON.stringify(taskId)),
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as TaskStatusResponse;
        }
        return null;
    }

    async getAllTasks(): Promise<TaskStatusResponse[]> {
        const response = await this.client.send({
            type: MessageTypes.GetAllTasks,
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as TaskStatusResponse[];
        }
        return [];
    }

    async getDlqEntries(): Promise<DeadLetterEntry[]> {
        const response = await this.client.send({
            type: MessageTypes.GetDlq,
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as DeadLetterEntry[];
        }
        return [];
    }

    async retryDlq(dlqId: string): Promise<{ retried: boolean; newTaskId?: string }> {
        const response = await this.client.send({
            type: MessageTypes.RetryDlq,
            payload: Buffer.from(JSON.stringify(dlqId)),
        });
        if (response.payload) {
            const result = JSON.parse(response.payload.toString());
            return {
                retried: result.retried === 'true',
                newTaskId: result.newTaskId,
            };
        }
        return { retried: false };
    }

    async discardDlq(dlqId: string): Promise<boolean> {
        const response = await this.client.send({
            type: MessageTypes.DiscardDlq,
            payload: Buffer.from(JSON.stringify(dlqId)),
        });
        if (response.payload) {
            const result = JSON.parse(response.payload.toString());
            return result.discarded === 'true';
        }
        return false;
    }

    async approveTask(taskId: string, approved: boolean): Promise<void> {
        await this.client.send({
            type: MessageTypes.ApproveTask,
            payload: Buffer.from(JSON.stringify({ taskId, approved })),
        });
    }

    async ping(): Promise<boolean> {
        try {
            const response = await this.client.send(
                { type: MessageTypes.Ping },
                5000
            );
            return response.type === MessageTypes.Pong;
        } catch {
            return false;
        }
    }

    get isConnected(): boolean {
        return this.client.isConnected;
    }

    // Activity logging methods
    async initializeActivityLog(workspacePath: string): Promise<void> {
        const request: InitializeActivityLogRequest = { workspacePath };
        await this.client.send({
            type: MessageTypes.InitializeActivityLog,
            payload: Buffer.from(JSON.stringify(request)),
        });
    }

    async getActivityConfig(workspacePath: string): Promise<ActivityLogConfig | null> {
        const request: GetActivityConfigRequest = { workspacePath };
        const response = await this.client.send({
            type: MessageTypes.GetActivityConfig,
            payload: Buffer.from(JSON.stringify(request)),
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as ActivityLogConfig;
        }
        return null;
    }

    async updateActivityConfig(workspacePath: string, config: ActivityLogConfig): Promise<void> {
        const request: UpdateActivityConfigRequest = { workspacePath, config };
        await this.client.send({
            type: MessageTypes.UpdateActivityConfig,
            payload: Buffer.from(JSON.stringify(request)),
        });
    }

    async getActivityHours(workspacePath: string, limit: number = 100): Promise<string[]> {
        const request: GetActivityHoursRequest = { workspacePath, limit };
        const response = await this.client.send({
            type: MessageTypes.GetActivityHours,
            payload: Buffer.from(JSON.stringify(request)),
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as string[];
        }
        return [];
    }

    async getActivityByHour(workspacePath: string, hourBucket: string): Promise<ActivityEntry[]> {
        const request: GetActivityByHourRequest = { workspacePath, hourBucket };
        const response = await this.client.send({
            type: MessageTypes.GetActivityByHour,
            payload: Buffer.from(JSON.stringify(request)),
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as ActivityEntry[];
        }
        return [];
    }

    async syncGitHistory(workspacePath: string, sinceDays: number = 7): Promise<void> {
        const request: SyncGitHistoryRequest = { workspacePath, sinceDays };
        await this.client.send({
            type: MessageTypes.SyncGitHistory,
            payload: Buffer.from(JSON.stringify(request)),
        });
    }

    async generateCommitMessage(workspacePath: string, sinceDays: number = 1): Promise<string> {
        const request: GenerateCommitMessageRequest = { workspacePath, sinceDays };
        const response = await this.client.send({
            type: MessageTypes.GenerateCommitMessage,
            payload: Buffer.from(JSON.stringify(request)),
        });
        if (response.payload) {
            const result = JSON.parse(response.payload.toString());
            return result.message || '';
        }
        return '';
    }

    async toggleGitAutoCommit(enabled: boolean): Promise<void> {
        await this.client.send({
            type: MessageTypes.ToggleGitAutoCommit,
            payload: Buffer.from(JSON.stringify({ enabled })),
        });
    }

    // Workflow orchestration methods
    async startWorkflow(request: StartWorkflowRequest): Promise<{ instanceId: string }> {
        const response = await this.client.send({
            type: MessageTypes.StartWorkflow,
            payload: Buffer.from(JSON.stringify(request)),
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString());
        }
        throw new Error('No response from StartWorkflow');
    }

    async getWorkflows(workspacePath?: string): Promise<WorkflowDefinition[]> {
        const response = await this.client.send({
            type: MessageTypes.GetWorkflows,
            payload: workspacePath ? Buffer.from(JSON.stringify({ workspacePath })) : undefined,
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as WorkflowDefinition[];
        }
        return [];
    }

    async getWorkflowInstances(): Promise<WorkflowInstance[]> {
        const response = await this.client.send({
            type: MessageTypes.GetWorkflowInstances,
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as WorkflowInstance[];
        }
        return [];
    }

    async cancelWorkflow(instanceId: string): Promise<void> {
        await this.client.send({
            type: MessageTypes.CancelWorkflow,
            payload: Buffer.from(JSON.stringify({ instanceId })),
        });
    }

    /** Pause a running workflow — in-flight tasks complete but no new steps are submitted. */
    async pauseWorkflow(instanceId: string): Promise<WorkflowInstance | undefined> {
        const response = await this.client.send({
            type: MessageTypes.PauseWorkflow,
            payload: Buffer.from(JSON.stringify({ instanceId })),
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as WorkflowInstance;
        }
        return undefined;
    }

    /** Resume a previously paused workflow — re-evaluates all pending steps. */
    async resumeWorkflow(instanceId: string): Promise<WorkflowInstance | undefined> {
        const response = await this.client.send({
            type: MessageTypes.ResumeWorkflow,
            payload: Buffer.from(JSON.stringify({ instanceId })),
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as WorkflowInstance;
        }
        return undefined;
    }

    /**
     * Update workflow context variables while the workflow is running.
     * Changed values are immediately available for {{variable}} substitution in
     * subsequently-submitted steps.
     */
    async updateWorkflowContext(instanceId: string, updates: Record<string, string>): Promise<WorkflowInstance | undefined> {
        const response = await this.client.send({
            type: MessageTypes.UpdateWorkflowContext,
            payload: Buffer.from(JSON.stringify({ instanceId, updates })),
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as WorkflowInstance;
        }
        return undefined;
    }

    /** Returns the model list and affinities as configured in the service's appsettings.json. */
    async getModels(): Promise<GetModelsResponse | null> {
        const response = await this.client.send({
            type: MessageTypes.GetModels,
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as GetModelsResponse;
        }
        return null;
    }

    /** Approve or reject a human_approval gate step. */
    async approveWorkflowStep(
        instanceId: string, stepId: string, approved: boolean, comment?: string
    ): Promise<WorkflowInstance | undefined> {
        const response = await this.client.send({
            type: MessageTypes.ApproveWorkflowStep,
            payload: Buffer.from(JSON.stringify({ instanceId, stepId, approved, comment })),
        });
        if (response.payload) {
            return JSON.parse(response.payload.toString()) as WorkflowInstance;
        }
        return undefined;
    }

    dispose(): void {
        this.client.disconnect();
        this.statusBarItem.dispose();
        this._onTaskUpdate.dispose();
        this._onStreamingOutput.dispose();
        this._onWorkflowUpdate.dispose();
        this._onApprovalNeeded.dispose();
    }
}
