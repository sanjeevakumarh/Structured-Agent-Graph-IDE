import * as vscode from 'vscode';
import { ModelProvider, AgentType } from '../client/MessageProtocol';

export class Configuration {
    private static get config(): vscode.WorkspaceConfiguration {
        return vscode.workspace.getConfiguration('sagIDE');
    }

    static get defaultModel(): ModelProvider {
        return this.config.get<ModelProvider>('defaultModel', 'claude');
    }

    static get maxConcurrentTasks(): number {
        return this.config.get<number>('maxConcurrentTasks', 5);
    }

    static get serviceUrl(): string {
        return this.config.get<string>('serviceUrl', 'http://localhost:5100');
    }

    static get pipeName(): string {
        return this.config.get<string>('pipeName', 'SAGIDEPipe');
    }

    /** Returns the shared secret for the pipe handshake, or undefined if not configured. */
    static get pipeSharedSecret(): string | undefined {
        const secret = this.config.get<string>('pipeSharedSecret', '');
        return secret || undefined;
    }

    static getModelForAgent(agentType: AgentType): ModelProvider {
        const key = agentType.charAt(0).toLowerCase() + agentType.slice(1);
        return this.config.get<ModelProvider>(`models.${key}`, this.defaultModel);
    }

    static getApiKey(provider: ModelProvider): string {
        const keyMap: Record<ModelProvider, string> = {
            claude: 'apiKeys.anthropic',
            codex: 'apiKeys.openai',
            gemini: 'apiKeys.google',
            ollama: '',
        };
        return this.config.get<string>(keyMap[provider], '');
    }

    static get hasAnyApiKey(): boolean {
        return ['claude', 'codex', 'gemini'].some(
            p => this.getApiKey(p as ModelProvider).length > 0
        );
    }
}
