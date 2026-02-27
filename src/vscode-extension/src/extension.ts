import * as vscode from 'vscode';
import { ServiceConnection } from './client/ServiceConnection';
import { TaskTreeProvider } from './views/TaskTreeProvider';
import { HistoryTreeProvider } from './views/HistoryTreeProvider';
import { DlqTreeProvider } from './views/DlqTreeProvider';
import { DiagnosticsManager } from './views/DiagnosticsManager';
import { registerCommands } from './commands/CommandRegistry';
import { Configuration } from './utils/Configuration';
import { initLogger, log } from './utils/Logger';
import { openTaskResult } from './utils/ResultViewer';
import { ComparisonTracker } from './utils/ComparisonTracker';
import { ComparisonPanel } from './views/ComparisonPanel';
import { StreamingOutputPanel } from './views/StreamingOutputPanel';
import { DiffApprovalPanel } from './views/DiffApprovalPanel';
import { WorkflowExplorerProvider } from './views/WorkflowExplorerProvider';
import { WorkflowGraphPanel } from './views/WorkflowGraphPanel';
import { PromptLibraryProvider } from './views/PromptLibraryProvider';

let connection: ServiceConnection;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    const outputChannel = initLogger();
    context.subscriptions.push(outputChannel);
    log('SAG IDE extension activating...');

    // Initialize tree view providers
    const taskTreeProvider = new TaskTreeProvider();
    const historyTreeProvider = new HistoryTreeProvider();
    const dlqTreeProvider = new DlqTreeProvider();
    const diagnosticsManager = new DiagnosticsManager();
    const workflowExplorer  = new WorkflowExplorerProvider();
    const restBaseUrl       = Configuration.serviceUrl;
    const promptLibrary     = new PromptLibraryProvider(restBaseUrl);

    // Register tree views in sidebar
    context.subscriptions.push(
        vscode.window.registerTreeDataProvider('sagIDE.activeTasks', taskTreeProvider),
        vscode.window.registerTreeDataProvider('sagIDE.taskHistory', historyTreeProvider),
        vscode.window.registerTreeDataProvider('sagIDE.dlq', dlqTreeProvider),
        vscode.window.registerTreeDataProvider('sagIDE.workflowExplorer', workflowExplorer),
        vscode.window.registerTreeDataProvider('sagIDE.promptLibrary', promptLibrary),
        diagnosticsManager
    );

    // Create service connection (assigned to module-level var for deactivate())
    const pipeName = Configuration.pipeName;
    const pipeSecret = Configuration.pipeSharedSecret;
    connection = new ServiceConnection(pipeName, pipeSecret);
    context.subscriptions.push(connection);

    // Comparison group tracker — watches for all tasks in a group to finish
    const comparisonTracker = new ComparisonTracker();

    // Listen for task updates from the service
    connection.onTaskUpdate(status => {
        taskTreeProvider.updateTask(status);

        // When a task completes, push diagnostics to Problems panel
        if (status.status === 'Completed') {
            diagnosticsManager.updateFromTask(status);
        }

        // Stop the streaming panel when cancelled (C# throws OCE and never sends IsLastChunk=true)
        if (status.status === 'Cancelled') {
            StreamingOutputPanel.cancel(status.taskId);
        }

        // Move completed/failed/cancelled tasks to history
        if (status.status === 'Completed' || status.status === 'Failed' || status.status === 'Cancelled') {
            historyTreeProvider.addCompleted(status);
            // Remove from active after a short delay so user can see final status
            setTimeout(() => {
                taskTreeProvider.removeTask(status.taskId);
            }, 3000);

            // Notify user and offer to view result / review changes
            if (status.status === 'Completed' && status.result?.output) {
                const label = `${status.agentType} on ${status.modelId.split(':')[0]}`;
                const hasChanges = (status.result.changes?.length ?? 0) > 0;

                if (hasChanges) {
                    // Auto-open diff approval panel when file changes are present
                    DiffApprovalPanel.show(context, status);
                    const changeCount = status.result.changes!.length;
                    vscode.window.showInformationMessage(
                        `$(check) ${label} — ${changeCount} file change${changeCount === 1 ? '' : 's'} ready for review`,
                        'View Output'
                    ).then(action => {
                        if (action === 'View Output') {
                            openTaskResult(status);
                        }
                    });
                } else {
                    vscode.window.showInformationMessage(
                        `$(check) ${label} — done`,
                        'View Result'
                    ).then(action => {
                        if (action === 'View Result') {
                            openTaskResult(status);
                        }
                    });
                }
            }

            // Auto-refresh DLQ when a task fails
            if (status.status === 'Failed') {
                setTimeout(async () => {
                    try {
                        const entries = await connection.getDlqEntries();
                        dlqTreeProvider.refresh(entries);
                    } catch {
                        // Silently ignore — DLQ refresh is best-effort
                    }
                }, 1000);
            }
        }

        // Check if this update completes a multi-model comparison group
        const completedGroup = comparisonTracker.onTaskUpdate(status);
        if (completedGroup) {
            ComparisonPanel.show(
                context,
                completedGroup.description,
                completedGroup.agentType,
                completedGroup.completedTasks
            );
        }

        log(`Task ${status.taskId.substring(0, 8)}: ${status.status} (${status.progress}%) — ${status.statusMessage || ''}`);
    });

    // Handle live streaming output from running tasks
    connection.onStreamingOutput(streamMsg => {
        // Look up the agentType/modelId from the active task tree so the panel title is informative
        const activeTask = taskTreeProvider.getTask(streamMsg.taskId);
        const agentType = activeTask?.agentType ?? 'CodeReview';
        const modelId = activeTask?.modelId ?? 'model';
        StreamingOutputPanel.update(
            context,
            streamMsg.taskId,
            agentType,
            modelId,
            streamMsg.textChunk,
            streamMsg.tokensGeneratedSoFar,
            streamMsg.isLastChunk
        );
    });

    // Handle live workflow updates
    connection.onWorkflowUpdate(instance => {
        workflowExplorer.onWorkflowUpdate(instance);
        WorkflowGraphPanel.update(instance.instanceId, instance);

        if (instance.status === 'completed') {
            vscode.window.showInformationMessage(
                `$(check) Workflow "${instance.definitionName}" completed`,
                'View Graph'
            ).then(action => {
                if (action === 'View Graph') {
                    vscode.commands.executeCommand('sagIDE.openWorkflowGraph', instance);
                }
            });
        } else if (instance.status === 'failed') {
            vscode.window.showWarningMessage(`$(error) Workflow "${instance.definitionName}" failed`);
        }
    });

    // Handle human approval gate notifications
    connection.onApprovalNeeded(payload => {
        vscode.window.showInformationMessage(
            `$(person) Workflow approval required for step "${payload.stepId}"`,
            'Approve', 'Reject'
        ).then(action => {
            if (action === 'Approve') {
                vscode.commands.executeCommand('sagIDE.approveWorkflowStep', payload.instanceId, payload.stepId, true, undefined);
            } else if (action === 'Reject') {
                vscode.commands.executeCommand('sagIDE.approveWorkflowStep', payload.instanceId, payload.stepId, false, undefined);
            }
        });
    });

    // Register all commands
    registerCommands(context, connection, taskTreeProvider, historyTreeProvider, dlqTreeProvider, diagnosticsManager, comparisonTracker, workflowExplorer, promptLibrary, restBaseUrl);

    // Connect to the C# backend service
    try {
        await connection.connect();
        log('Connected to SAG IDE service');

        // Load workflow definitions into the explorer
        try {
            const workspacePath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
            const defs = await connection.getWorkflows(workspacePath);
            workflowExplorer.refreshDefinitions(defs);
            const instances = await connection.getWorkflowInstances();
            workflowExplorer.refreshInstances(instances);
        } catch {
            // Workflow list is best-effort — service may not be ready yet
        }

        // Initial prompt library load (REST API — best-effort)
        promptLibrary.refresh().catch(() => { /* service may not be ready yet */ });
    } catch {
        log('Service not running — will reconnect when available');
    }

    log('SAG IDE extension activated');
}

export function deactivate(): void {
    connection?.dispose();
    log('SAG IDE extension deactivated');
}
