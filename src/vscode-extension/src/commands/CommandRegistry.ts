import * as vscode from 'vscode';
import * as path from 'path';
import { ServiceConnection } from '../client/ServiceConnection';
import { TaskTreeProvider } from '../views/TaskTreeProvider';
import { HistoryTreeProvider } from '../views/HistoryTreeProvider';
import { DlqTreeProvider } from '../views/DlqTreeProvider';
import { DiagnosticsManager } from '../views/DiagnosticsManager';
import { WorkflowExplorerProvider, WorkflowRunningItem } from '../views/WorkflowExplorerProvider';
import { WorkflowGraphPanel } from '../views/WorkflowGraphPanel';
import { PromptLibraryProvider, PromptItem, postJson } from '../views/PromptLibraryProvider';
import { submitTaskCommand, submitTaskOnFilesCommand, getAllModels } from './SubmitTaskCommand';
import { compareModelsCommand } from './CompareModelsCommand';
import { runWorkflowCommand } from './RunWorkflowCommand';
import { ComparisonTracker } from '../utils/ComparisonTracker';
import { openTaskResult } from '../utils/ResultViewer';
import { log } from '../utils/Logger';
import { WorkflowDefinition, WorkflowInstance } from '../client/MessageProtocol';
import { pickContext } from '../utils/ContextPicker';

/**
 * Tree-view inline commands receive the clicked TreeItem as their first argument,
 * while programmatic executeCommand calls pass the instanceId string directly.
 * This helper normalizes both cases.
 */
function resolveInstanceId(arg?: string | WorkflowRunningItem): string | undefined {
    if (!arg) { return undefined; }
    if (typeof arg === 'string') { return arg; }
    return arg.instance?.instanceId;
}

export function registerCommands(
    context: vscode.ExtensionContext,
    connection: ServiceConnection,
    taskTree: TaskTreeProvider,
    historyTree: HistoryTreeProvider,
    dlqTree: DlqTreeProvider,
    diagnostics: DiagnosticsManager,
    comparisonTracker: ComparisonTracker,
    workflowExplorer: WorkflowExplorerProvider,
    promptLibrary: PromptLibraryProvider,
    restBaseUrl: string
): void {
    // Submit new task (with model picker)
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.submitTask', () => {
            submitTaskCommand(connection);
        })
    );

    // Submit task on files selected in the Explorer (right-click → SAG: Submit Task on Selected Files)
    // VSCode passes (clickedUri, allSelectedUris) for multi-select context menu commands.
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.submitTaskOnFiles',
            (clickedUri: vscode.Uri, allUris: vscode.Uri[]) => {
                submitTaskOnFilesCommand(connection, clickedUri, allUris);
            }
        )
    );

    // Cancel running task
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.cancelTask', async (item?: any) => {
            let taskId: string | undefined;

            if (item?.task?.taskId) {
                taskId = item.task.taskId;
            } else {
                const input = await vscode.window.showInputBox({
                    prompt: 'Enter task ID to cancel',
                    placeHolder: 'Task ID (first 8 chars)',
                });
                taskId = input || undefined;
            }

            if (taskId) {
                try {
                    await connection.cancelTask(taskId);
                    log(`Task ${taskId} cancelled`);
                    vscode.window.showInformationMessage(`Task ${taskId.substring(0, 8)} cancelled`);
                } catch (err) {
                    vscode.window.showErrorMessage(`Failed to cancel task: ${err}`);
                }
            }
        })
    );

    // Switch default model
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.switchModel', async () => {
            const models = ['claude', 'codex', 'gemini', 'ollama'];
            const pick = await vscode.window.showQuickPick(models, {
                placeHolder: 'Select default AI model',
            });
            if (pick) {
                await vscode.workspace.getConfiguration('sagIDE')
                    .update('defaultModel', pick, vscode.ConfigurationTarget.Global);
                vscode.window.showInformationMessage(`Default model set to ${pick}`);
            }
        })
    );

    // Review current file (quick action — CodeReview pre-selected, context picker for scope)
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.reviewFile', async () => {
            if (!connection.isConnected) {
                vscode.window.showErrorMessage('SAG IDE service is not running');
                return;
            }

            const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
            const workspacePath = workspaceFolder?.uri.fsPath || '';

            // Resolve model: use CodeReview affinity from service, fall back to first available
            const allModels = await getAllModels(connection);
            if (allModels.length === 0) {
                vscode.window.showWarningMessage('No models available. Check service connection.');
                return;
            }
            let model = allModels[0];
            try {
                const resp = await connection.getModels();
                const affinityKey = resp?.affinities['CodeReview'];
                const preferred = affinityKey ? allModels.find(m => m.key === affinityKey) : undefined;
                if (preferred) { model = preferred; }
            } catch { /* keep first model */ }

            // Pick context (scope) — lets user choose active file, open editors, folder, etc.
            const ctx = await pickContext(workspacePath || undefined);
            if (!ctx) { return; }

            const defaultDesc = ctx.filePaths.length === 1
                ? `Review ${path.basename(ctx.filePaths[0])}`
                : `Review ${ctx.label}`;
            const description = await vscode.window.showInputBox({
                prompt: 'Describe what to review',
                value: defaultDesc,
            });
            if (!description) { return; }

            try {
                const result = await connection.submitTask({
                    agentType: 'CodeReview',
                    modelProvider: model.provider,
                    modelId: model.modelId,
                    description,
                    filePaths: ctx.filePaths,
                    priority: 1,
                    metadata: workspacePath ? { workspacePath } : undefined,
                    modelEndpoint: model.endpoint,
                });
                if (result) {
                    vscode.window.showInformationMessage(
                        `Code review started on ${model.modelId.split(':')[0]} — Task ${result.taskId.substring(0, 8)}`
                    );
                }
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to start review: ${err}`);
            }
        })
    );

    // Show task history
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.showTaskHistory', () => {
            vscode.commands.executeCommand('sagIDE.taskHistory.focus');
        })
    );

    // Show DLQ (refresh entries)
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.showDlq', async () => {
            try {
                const entries = await connection.getDlqEntries();
                dlqTree.refresh(entries);
                log(`DLQ refreshed: ${entries.length} entries`);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to load DLQ: ${err}`);
            }
        })
    );

    // Retry from DLQ
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.retryDlq', async (item?: any) => {
            const dlqId = item?.entry?.id;
            if (!dlqId) {
                vscode.window.showWarningMessage('Select a DLQ entry to retry');
                return;
            }

            try {
                const result = await connection.retryDlq(dlqId);
                if (result.retried) {
                    vscode.window.showInformationMessage(
                        `Retrying as new task ${result.newTaskId?.substring(0, 8)}`
                    );
                    vscode.commands.executeCommand('sagIDE.showDlq');
                } else {
                    vscode.window.showWarningMessage('DLQ entry not found or already retried');
                }
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to retry: ${err}`);
            }
        })
    );

    // Discard from DLQ
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.discardDlq', async (item?: any) => {
            const dlqId = item?.entry?.id;
            if (!dlqId) {
                vscode.window.showWarningMessage('Select a DLQ entry to discard');
                return;
            }

            const confirm = await vscode.window.showWarningMessage(
                `Discard failed task ${dlqId.substring(0, 8)}? This cannot be undone.`,
                { modal: true },
                'Discard'
            );

            if (confirm === 'Discard') {
                try {
                    await connection.discardDlq(dlqId);
                    vscode.window.showInformationMessage(`DLQ entry ${dlqId.substring(0, 8)} discarded`);
                    vscode.commands.executeCommand('sagIDE.showDlq');
                } catch (err) {
                    vscode.window.showErrorMessage(`Failed to discard: ${err}`);
                }
            }
        })
    );

    // Clear diagnostics
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.clearDiagnostics', () => {
            diagnostics.clearAll();
            vscode.window.showInformationMessage('SAG IDE diagnostics cleared');
        })
    );

    // Approve/Reject task (A005 approval workflow)
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.approveTask', async (item?: any) => {
            const taskId = item?.task?.taskId;
            if (!taskId) {
                vscode.window.showWarningMessage('Select a task to approve');
                return;
            }

            const choice = await vscode.window.showQuickPick(
                [
                    { label: '$(check) Approve', description: 'Apply the proposed changes', value: true },
                    { label: '$(x) Reject', description: 'Discard the proposed changes', value: false },
                ],
                { placeHolder: `Approve or reject task ${taskId.substring(0, 8)}?` }
            );

            if (choice !== undefined) {
                try {
                    await connection.approveTask(taskId, choice.value);
                    vscode.window.showInformationMessage(
                        choice.value
                            ? `Task ${taskId.substring(0, 8)} approved`
                            : `Task ${taskId.substring(0, 8)} rejected`
                    );
                } catch (err) {
                    vscode.window.showErrorMessage(`Failed to approve/reject: ${err}`);
                }
            }
        })
    );

    // Show result for a completed task (double-click in history)
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.showDiff', async (item?: any) => {
            const task = item?.task;
            if (!task) {
                vscode.window.showWarningMessage('Select a completed task to view its result');
                return;
            }

            try {
                // If result is already cached on the item use it; otherwise fetch from service
                let status = task;
                if (!status.result?.output) {
                    status = await connection.getTaskStatus(task.taskId) ?? task;
                }
                await openTaskResult(status);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to show result: ${err}`);
            }
        })
    );

    // Activity Logging Commands

    // Initialize activity log for workspace
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.initActivityLog', async () => {
            const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
            if (!workspaceFolder) {
                vscode.window.showWarningMessage('No workspace folder open');
                return;
            }

            const workspacePath = workspaceFolder.uri.fsPath;

            try {
                await connection.initializeActivityLog(workspacePath);
                vscode.window.showInformationMessage('Activity logging initialized for workspace');

                // Ask if user wants to sync existing git history
                const syncGit = await vscode.window.showQuickPick(
                    ['Yes', 'No'],
                    { placeHolder: 'Sync existing git history to activity log?' }
                );

                if (syncGit === 'Yes') {
                    const days = await vscode.window.showInputBox({
                        prompt: 'Number of days of git history to sync',
                        value: '7',
                        validateInput: (value) => {
                            const num = parseInt(value, 10);
                            if (isNaN(num) || num <= 0) {
                                return 'Enter a positive number';
                            }
                            return undefined;
                        }
                    });

                    if (days) {
                        await vscode.window.withProgress(
                            {
                                location: vscode.ProgressLocation.Notification,
                                title: `Syncing ${days} days of git history...`,
                                cancellable: false,
                            },
                            async () => {
                                await connection.syncGitHistory(workspacePath, parseInt(days, 10));
                            }
                        );
                        vscode.window.showInformationMessage(`Git history synced (${days} days)`);
                    }
                }
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to initialize activity log: ${err}`);
            }
        })
    );

    // Configure activity log settings
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.configureActivityLog', async () => {
            const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
            if (!workspaceFolder) {
                vscode.window.showWarningMessage('No workspace folder open');
                return;
            }

            const workspacePath = workspaceFolder.uri.fsPath;

            try {
                const config = await connection.getActivityConfig(workspacePath);
                if (!config) {
                    vscode.window.showWarningMessage('Activity logging not initialized. Run "Initialize Activity Log" first.');
                    return;
                }

                // Toggle enabled/disabled
                const enabledChoice = await vscode.window.showQuickPick(
                    [
                        { label: '$(check) Enabled', value: true },
                        { label: '$(x) Disabled', value: false }
                    ],
                    { placeHolder: 'Enable or disable activity logging' }
                );

                if (enabledChoice === undefined) {
                    return;
                }

                config.enabled = enabledChoice.value;

                // Git integration mode
                const gitMode = await vscode.window.showQuickPick(
                    [
                        { label: 'Disabled', description: 'No git integration', value: 'Disabled' },
                        { label: 'Log Commits', description: 'Parse and log existing commits', value: 'LogCommits' },
                        { label: 'Generate Messages', description: 'Generate commit messages from activities', value: 'GenerateMessages' },
                        { label: 'Bidirectional', description: 'Both - log commits AND generate messages', value: 'Bidirectional' },
                    ],
                    { placeHolder: 'Select git integration mode' }
                );

                if (gitMode) {
                    config.gitIntegrationMode = gitMode.value as any;
                }

                config.updatedAt = new Date().toISOString();

                await connection.updateActivityConfig(workspacePath, config);
                vscode.window.showInformationMessage('Activity log configuration updated');
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to configure activity log: ${err}`);
            }
        })
    );

    // View activity log (open README.md)
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.viewActivityLog', async () => {
            const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
            if (!workspaceFolder) {
                vscode.window.showWarningMessage('No workspace folder open');
                return;
            }

            const readmePath = vscode.Uri.joinPath(workspaceFolder.uri, '.sag-activity', 'README.md');

            try {
                const doc = await vscode.workspace.openTextDocument(readmePath);
                await vscode.window.showTextDocument(doc, { preview: false });
            } catch (err) {
                vscode.window.showWarningMessage('Activity log README not found. Run "Initialize Activity Log" first.');
            }
        })
    );

    // Sync git history
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.syncGitHistory', async () => {
            const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
            if (!workspaceFolder) {
                vscode.window.showWarningMessage('No workspace folder open');
                return;
            }

            const workspacePath = workspaceFolder.uri.fsPath;

            const days = await vscode.window.showInputBox({
                prompt: 'Number of days of git history to sync',
                value: '7',
                validateInput: (value) => {
                    const num = parseInt(value, 10);
                    if (isNaN(num) || num <= 0) {
                        return 'Enter a positive number';
                    }
                    return undefined;
                }
            });

            if (!days) {
                return;
            }

            try {
                await vscode.window.withProgress(
                    {
                        location: vscode.ProgressLocation.Notification,
                        title: `Syncing ${days} days of git history...`,
                        cancellable: false,
                    },
                    async () => {
                        await connection.syncGitHistory(workspacePath, parseInt(days, 10));
                    }
                );
                vscode.window.showInformationMessage(`Git history synced (${days} days)`);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to sync git history: ${err}`);
            }
        })
    );

    // Compare models — same task on N models side-by-side
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.compareModels', () => {
            compareModelsCommand(connection, comparisonTracker);
        })
    );

    // Toggle git auto-commit of task results
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.toggleGitAutoCommit', async () => {
            const config = vscode.workspace.getConfiguration('sagIDE');
            const current = config.get<boolean>('git.autoCommitResults', false);
            await config.update('git.autoCommitResults', !current, vscode.ConfigurationTarget.Global);

            try {
                await connection.toggleGitAutoCommit(!current);
            } catch {
                // Service may not be running; the config update still persists
            }

            vscode.window.showInformationMessage(
                `Git auto-commit ${!current ? 'enabled' : 'disabled'} — results will ${!current ? '' : 'not '}be committed to sag-agent-log`
            );
        })
    );

    // Generate commit message
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.generateCommitMessage', async () => {
            const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
            if (!workspaceFolder) {
                vscode.window.showWarningMessage('No workspace folder open');
                return;
            }

            const workspacePath = workspaceFolder.uri.fsPath;

            try {
                await vscode.window.withProgress(
                    {
                        location: vscode.ProgressLocation.Notification,
                        title: 'Generating commit message from recent activities...',
                        cancellable: false,
                    },
                    async () => {
                        const message = await connection.generateCommitMessage(workspacePath, 1);

                        if (message) {
                            // Copy to clipboard
                            await vscode.env.clipboard.writeText(message);

                            // Show in modal
                            const action = await vscode.window.showInformationMessage(
                                'Commit message generated and copied to clipboard!',
                                { modal: false },
                                'View Message'
                            );

                            if (action === 'View Message') {
                                const doc = await vscode.workspace.openTextDocument({
                                    content: message,
                                    language: 'markdown',
                                });
                                await vscode.window.showTextDocument(doc, { preview: true });
                            }
                        } else {
                            vscode.window.showWarningMessage('No recent activities to generate commit message from');
                        }
                    }
                );
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to generate commit message: ${err}`);
            }
        })
    );

    // Open task output / result panel for a given taskId (used by workflow graph node click)
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.openTaskOutput', async (taskId?: string) => {
            if (!taskId) {
                vscode.window.showWarningMessage('No task ID provided');
                return;
            }
            try {
                const status = await connection.getTaskStatus(taskId);
                if (!status) {
                    vscode.window.showWarningMessage(`Task ${taskId.substring(0, 8)} not found`);
                    return;
                }
                await openTaskResult(status);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to open task output: ${err}`);
            }
        })
    );

    // ── Workflow Orchestration Commands ──────────────────────────────────────

    // Run a workflow (command palette or tree click)
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.runWorkflow', (preselectedDef?: WorkflowDefinition) => {
            runWorkflowCommand(context, connection, preselectedDef);
        })
    );

    // Open workflow graph panel (tree item click or notification button)
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.openWorkflowGraph', async (instanceOrId: WorkflowInstance | string) => {
            try {
                let instance: WorkflowInstance | undefined;
                if (typeof instanceOrId === 'string') {
                    const all = await connection.getWorkflowInstances();
                    instance = all.find(i => i.instanceId === instanceOrId);
                } else {
                    instance = instanceOrId;
                }
                if (!instance) {
                    vscode.window.showWarningMessage('Workflow instance not found');
                    return;
                }
                // Get the definition for graph layout
                const workspacePath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
                const defs = await connection.getWorkflows(workspacePath);
                const def = defs.find(d => d.id === instance!.definitionId);
                if (!def) {
                    vscode.window.showWarningMessage('Workflow definition not found');
                    return;
                }
                WorkflowGraphPanel.show(context, instance, def);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to open workflow graph: ${err}`);
            }
        })
    );

    // Cancel a running workflow instance
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.cancelWorkflowInstance', async (arg?: string | WorkflowRunningItem) => {
            const instanceId = resolveInstanceId(arg);
            if (!instanceId) {
                vscode.window.showWarningMessage('No workflow instance to cancel');
                return;
            }
            try {
                await connection.cancelWorkflow(instanceId);
                vscode.window.showInformationMessage(`Workflow ${instanceId.substring(0, 8)} cancelled`);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to cancel workflow: ${err}`);
            }
        })
    );

    // Refresh workflow explorer
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.refreshWorkflows', async () => {
            try {
                const workspacePath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
                const [defs, instances] = await Promise.all([
                    connection.getWorkflows(workspacePath),
                    connection.getWorkflowInstances(),
                ]);
                workflowExplorer.refreshDefinitions(defs);
                workflowExplorer.refreshInstances(instances);
                log(`Workflows refreshed: ${defs.length} definitions, ${instances.length} instances`);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to refresh workflows: ${err}`);
            }
        })
    );

    // Pause a running workflow instance
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.pauseWorkflowInstance', async (arg?: string | WorkflowRunningItem) => {
            const instanceId = resolveInstanceId(arg);
            if (!instanceId) {
                vscode.window.showWarningMessage('No workflow instance selected');
                return;
            }
            try {
                await connection.pauseWorkflow(instanceId);
                vscode.window.showInformationMessage(`Workflow paused. Running steps will complete; no new steps will start.`);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to pause workflow: ${err}`);
            }
        })
    );

    // Resume a paused workflow instance
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.resumeWorkflowInstance', async (arg?: string | WorkflowRunningItem) => {
            const instanceId = resolveInstanceId(arg);
            if (!instanceId) {
                vscode.window.showWarningMessage('No workflow instance selected');
                return;
            }
            try {
                await connection.resumeWorkflow(instanceId);
                vscode.window.showInformationMessage(`Workflow resumed.`);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to resume workflow: ${err}`);
            }
        })
    );

    // Update a workflow context variable while running
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.updateWorkflowContext', async (arg?: string | WorkflowRunningItem) => {
            const instanceId = resolveInstanceId(arg);
            if (!instanceId) {
                vscode.window.showWarningMessage('No workflow instance selected');
                return;
            }
            const key = await vscode.window.showInputBox({
                prompt: 'Variable name (e.g. feature_description)',
                placeHolder: 'context_key',
            });
            if (!key) { return; }
            const value = await vscode.window.showInputBox({
                prompt: `New value for {{${key}}}`,
                placeHolder: 'new value',
            });
            if (value === undefined) { return; }
            try {
                await connection.updateWorkflowContext(instanceId, { [key]: value });
                vscode.window.showInformationMessage(`Context updated: {{${key}}} = "${value}"`);
            } catch (err) {
                vscode.window.showErrorMessage(`Failed to update context: ${err}`);
            }
        })
    );

    // Clear completed/failed/cancelled workflow instances from the explorer
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.clearCompletedWorkflows', () => {
            workflowExplorer.clearCompletedInstances();
            vscode.window.showInformationMessage('Completed workflow instances cleared from view');
        })
    );

    // Approve or reject a human_approval gate step
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.approveWorkflowStep',
            async (instanceId?: string, stepId?: string, approved?: boolean, _comment?: string) => {
                if (!instanceId || !stepId) {
                    vscode.window.showWarningMessage('No approval step specified');
                    return;
                }

                let comment: string | undefined;
                if (!approved) {
                    comment = await vscode.window.showInputBox({
                        title: `Reject workflow step "${stepId}"`,
                        prompt: 'Optional: provide a reason for rejection',
                        placeHolder: 'Rejection reason (optional)',
                    });
                    if (comment === undefined) { return; } // user cancelled the input box
                }

                try {
                    await connection.approveWorkflowStep(instanceId, stepId, approved ?? true, comment);
                    vscode.window.showInformationMessage(
                        approved ? `Step "${stepId}" approved.` : `Step "${stepId}" rejected.`
                    );
                } catch (err) {
                    vscode.window.showErrorMessage(`Failed to ${approved ? 'approve' : 'reject'} step: ${err}`);
                }
            })
    );

    // ── Prompt Library Commands ───────────────────────────────────────────────

    // Refresh the prompt library tree view
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.refreshPromptLibrary', async () => {
            await promptLibrary.refresh();
            log('Prompt library refreshed');
        })
    );

    // Open prompt YAML file in editor
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.editPrompt',
            async (domainOrItem?: string | PromptItem, name?: string) => {
                let domain: string | undefined;
                let promptName: string | undefined;

                if (domainOrItem instanceof PromptItem) {
                    domain     = domainOrItem.prompt.domain;
                    promptName = domainOrItem.prompt.name;
                } else {
                    domain     = domainOrItem;
                    promptName = name;
                }

                if (!domain || !promptName) {
                    // Let the user pick from the library
                    const all  = promptLibrary.getAll();
                    const pick = await vscode.window.showQuickPick(
                        all.map(p => ({ label: `${p.domain}/${p.name}`, description: p.description ?? '', data: p })),
                        { placeHolder: 'Select a prompt to open' }
                    );
                    if (!pick) { return; }
                    domain     = pick.data.domain;
                    promptName = pick.data.name;
                }

                // Look for the file across workspace folders and common locations
                const candidates: vscode.Uri[] = [];
                for (const wf of vscode.workspace.workspaceFolders ?? []) {
                    candidates.push(
                        vscode.Uri.joinPath(wf.uri, 'prompts', domain, `${promptName}.yaml`),
                        vscode.Uri.joinPath(wf.uri, '..', 'prompts', domain, `${promptName}.yaml`),
                    );
                }
                for (const uri of candidates) {
                    try {
                        const doc = await vscode.workspace.openTextDocument(uri);
                        await vscode.window.showTextDocument(doc, { preview: false });
                        return;
                    } catch { /* try next */ }
                }
                vscode.window.showWarningMessage(
                    `Prompt file not found: prompts/${domain}/${promptName}.yaml`);
            })
    );

    // Run a prompt now via the REST API
    context.subscriptions.push(
        vscode.commands.registerCommand('sagIDE.runPrompt',
            async (itemOrDomain?: PromptItem | string, name?: string) => {
                let domain: string | undefined;
                let promptName: string | undefined;

                if (itemOrDomain instanceof PromptItem) {
                    domain     = itemOrDomain.prompt.domain;
                    promptName = itemOrDomain.prompt.name;
                } else {
                    domain     = itemOrDomain;
                    promptName = name;
                }

                if (!domain || !promptName) {
                    const all  = promptLibrary.getAll();
                    const pick = await vscode.window.showQuickPick(
                        all.map(p => ({
                            label:       `${p.domain}/${p.name}`,
                            description: p.description ?? '',
                            detail:      p.hasSubtasks ? '$(graph) Multi-model' : (p.schedule ? `$(clock) ${p.schedule}` : ''),
                            data:        p,
                        })),
                        { placeHolder: 'Select a prompt to run', matchOnDescription: true }
                    );
                    if (!pick) { return; }
                    domain     = pick.data.domain;
                    promptName = pick.data.name;
                }

                // Optionally collect variable overrides
                const overrideInput = await vscode.window.showInputBox({
                    prompt: 'Optional: variable overrides as key=value pairs (comma-separated)',
                    placeHolder: 'e.g. drop_threshold=3, max_stocks=5 (leave blank for defaults)',
                });
                const variables: Record<string, string> = {};
                if (overrideInput?.trim()) {
                    for (const part of overrideInput.split(',')) {
                        const eq = part.indexOf('=');
                        if (eq > 0) {
                            variables[part.slice(0, eq).trim()] = part.slice(eq + 1).trim();
                        }
                    }
                }

                try {
                    const resp = await postJson<Record<string, unknown>>(
                        restBaseUrl,
                        `/api/prompts/${domain}/${promptName}/run`,
                        Object.keys(variables).length > 0 ? variables : {}
                    );

                    const mode    = resp['mode'] as string | undefined;
                    const taskId  = resp['taskId'] as string | undefined;
                    const instId  = resp['instanceId'] as string | undefined;

                    if (mode === 'subtask_coordinator') {
                        vscode.window.showInformationMessage(
                            `$(graph) Prompt "${domain}/${promptName}" dispatched to SubtaskCoordinator`);
                    } else if (taskId) {
                        vscode.window.showInformationMessage(
                            `$(play) Prompt "${domain}/${promptName}" submitted — Task ${taskId.substring(0, 8)}`);
                    } else {
                        vscode.window.showInformationMessage(
                            `$(check) Prompt "${domain}/${promptName}" accepted by service`);
                    }

                    log(`Prompt run: ${domain}/${promptName} — ${JSON.stringify(resp)}`);
                } catch (err) {
                    vscode.window.showErrorMessage(`Failed to run prompt: ${err}`);
                }
            })
    );
}
