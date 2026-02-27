import * as vscode from 'vscode';
import { ServiceConnection } from '../client/ServiceConnection';
import { WorkflowDefinition, WorkflowStepDef, StepModelOverride, StartWorkflowRequest } from '../client/MessageProtocol';
import { WorkflowGraphPanel } from '../views/WorkflowGraphPanel';
import { getAllModels } from './SubmitTaskCommand';

export async function runWorkflowCommand(
    context: vscode.ExtensionContext,
    connection: ServiceConnection,
    preselectedDef?: WorkflowDefinition
): Promise<void> {
    if (!connection.isConnected) {
        vscode.window.showErrorMessage('SAG IDE service is not running');
        return;
    }

    // 1. Load available workflow definitions
    const workspacePath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;

    let definitions: WorkflowDefinition[];
    try {
        definitions = await connection.getWorkflows(workspacePath);
    } catch (err) {
        vscode.window.showErrorMessage(`Failed to load workflows: ${err}`);
        return;
    }

    if (definitions.length === 0) {
        vscode.window.showWarningMessage('No workflow definitions found. Check .sagide/workflows/ in your workspace.');
        return;
    }

    // 2. Pick workflow definition (skip if already pre-selected from tree click)
    let definition = preselectedDef;
    if (!definition) {
        const pick = await vscode.window.showQuickPick(
            definitions.map(d => ({
                label: d.name,
                description: d.isBuiltIn ? '$(library) built-in' : '$(file-code) workspace',
                detail: d.description,
                value: d,
            })),
            {
                placeHolder: 'Select a workflow to run',
                title: 'SAG IDE — Run Workflow',
                matchOnDetail: true,
            }
        );
        if (!pick) { return; }
        definition = pick.value;
    }

    // 3. Collect required parameter values
    const inputs: Record<string, string> = {};
    for (const param of definition.parameters ?? []) {
        const value = await vscode.window.showInputBox({
            prompt: `Value for parameter: ${param.name}`,
            placeHolder: param.default ?? `Enter ${param.type} value`,
            value: param.default ?? '',
            title: `SAG Workflow — ${definition.name}`,
        });
        if (value === undefined) { return; } // user cancelled
        inputs[param.name] = value;
    }

    // 4. Load available models + affinity map (needed for both global and per-step picks)
    const allModels = await getAllModels(connection);
    let affinities: Record<string, string> = {};
    try {
        const resp = await connection.getModels();
        affinities = resp?.affinities ?? {};
    } catch { /* ignore — fallback to no pre-selection */ }

    // 5. Select global default model (used for any step without its own override)
    const modelPick = await vscode.window.showQuickPick(
        allModels.map(m => ({
            label: m.label,
            description: m.description,
            value: m,
        })),
        {
            placeHolder: 'Select default model (used for steps without a specific override)',
            title: `SAG Workflow — ${definition.name}`,
        }
    );
    if (!modelPick) { return; }
    const defaultModel = modelPick.value;

    // 6. Per-step model selection for configurable agent steps
    //    A step is "configurable" when it is an agent step and has no model locked in the YAML.
    const stepModelOverrides: Record<string, StepModelOverride> = {};
    const configurableSteps = (definition.steps ?? []).filter(isConfigurableAgentStep);

    if (configurableSteps.length > 0) {
        // Build a lookup: agent type name → recommended model key from affinities
        for (const step of configurableSteps) {
            const agentType  = step.agent ?? step.id;            // e.g. "CodeReview"
            const affinityKey = affinities[agentType] ?? affinities[agentType.toLowerCase()];
            const defaultItem = {
                label: `$(pass) Use workflow default  —  ${defaultModel.label}`,
                description: 'Applies the global model chosen above',
                value: null as null,  // null = no override; use instance default
            };
            const affinityItem = affinityKey
                ? allModels.find(m => m.key === affinityKey)
                : undefined;

            const items = [
                defaultItem,
                // Affinity recommendation (if it differs from the global default)
                ...(affinityItem && affinityItem.key !== defaultModel.key
                    ? [{
                        label: `$(star) ${affinityItem.label}  $(tag) recommended for ${agentType}`,
                        description: affinityItem.description,
                        value: affinityItem,
                      }]
                    : []),
                // All remaining models
                ...allModels
                    .filter(m => m.key !== affinityItem?.key)
                    .map(m => ({
                        label: m.label,
                        description: m.description,
                        value: m,
                    })),
            ];

            const stepPick = await vscode.window.showQuickPick(items, {
                placeHolder: `Model for step "${step.id}" (${agentType})`,
                title: `SAG Workflow — ${definition.name}  |  Step: ${step.id}`,
            });
            if (stepPick === undefined) { return; } // user cancelled the whole flow
            if (stepPick.value !== null) {
                stepModelOverrides[step.id] = {
                    provider: stepPick.value.provider,
                    modelId:  stepPick.value.modelId,
                    endpoint: stepPick.value.endpoint,
                };
            }
            // null → user chose "use workflow default" → no override entry → engine falls through to instance default
        }
    }

    // 7. Determine file paths — use open editor files or workspace root
    let filePaths: string[] = [];
    const activeEditor = vscode.window.activeTextEditor;
    if (activeEditor && !activeEditor.document.isUntitled) {
        filePaths = [activeEditor.document.uri.fsPath];
    }

    // 8. Submit workflow
    try {
        const req: StartWorkflowRequest = {
            definitionId: definition.id,
            inputs,
            filePaths,
            defaultModelId:       defaultModel.modelId,
            defaultModelProvider: defaultModel.provider,
            modelEndpoint:        defaultModel.endpoint,
            workspacePath,
            stepModelOverrides:   Object.keys(stepModelOverrides).length > 0
                                    ? stepModelOverrides
                                    : undefined,
        };

        const { instanceId } = await connection.startWorkflow(req);
        vscode.window.showInformationMessage(`$(run-all) Workflow "${definition.name}" started`);

        // 9. Open graph panel — wait briefly for the engine to register the instance
        setTimeout(async () => {
            try {
                const instances = await connection.getWorkflowInstances();
                const instance = instances.find(i => i.instanceId === instanceId);
                if (instance) {
                    WorkflowGraphPanel.show(context, instance, definition!);
                }
            } catch {
                // Graph panel is optional — don't block the user
            }
        }, 300);

    } catch (err) {
        vscode.window.showErrorMessage(`Failed to start workflow: ${err}`);
    }
}

/**
 * Returns true for steps where the user can meaningfully choose a model at launch time:
 * - type must be "agent" (routers, tools, constraints, approvals have no LLM)
 * - must NOT have a model locked in the YAML (modelId already set → author intent)
 */
function isConfigurableAgentStep(step: WorkflowStepDef): boolean {
    return (step.type === 'agent' || !step.type) && !step.modelId;
}
