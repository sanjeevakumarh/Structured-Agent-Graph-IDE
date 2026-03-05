import * as vscode from 'vscode';
import * as http from 'http';

// ── Data types ──────────────────────────────────────────────────────────────────

export interface SkillSummary {
    name: string;
    domain: string;
    version: number;
    description?: string;
    protocolImplements: string[];
    capabilitySlots: string[];
    implementationSteps: number;
}

// ── Tree item classes ───────────────────────────────────────────────────────────

export class SkillDomainItem extends vscode.TreeItem {
    constructor(public readonly domain: string, skillCount: number) {
        super(domain, vscode.TreeItemCollapsibleState.Expanded);
        this.description  = `${skillCount} skill${skillCount === 1 ? '' : 's'}`;
        this.contextValue = 'skillDomain';
        this.iconPath     = new vscode.ThemeIcon('folder');
    }
}

export class SkillItem extends vscode.TreeItem {
    constructor(public readonly skill: SkillSummary) {
        super(skill.name, vscode.TreeItemCollapsibleState.None);

        // Description: protocols if declared, otherwise version
        this.description = skill.protocolImplements.length > 0
            ? `[${skill.protocolImplements.join(', ')}]`
            : `v${skill.version}`;

        const lines: string[] = [];
        if (skill.description)              lines.push(skill.description);
        if (skill.protocolImplements.length) lines.push(`Protocols: ${skill.protocolImplements.join(', ')}`);
        if (skill.capabilitySlots.length)    lines.push(`Capabilities: ${skill.capabilitySlots.join(', ')}`);
        lines.push(`${skill.implementationSteps} implementation step${skill.implementationSteps === 1 ? '' : 's'}`);
        this.tooltip = new vscode.MarkdownString(lines.join('\n\n'));

        this.iconPath     = new vscode.ThemeIcon('symbol-module');
        this.contextValue = 'skillItem';

        // Click opens the skill composer panel for this skill's domain
        this.command = {
            command:   'sagIDE.openSkillComposer',
            title:     'Open Skill Composer',
            arguments: [skill.domain, skill.name],
        };
    }
}

// ── Provider ────────────────────────────────────────────────────────────────────

/**
 * Tree view provider for the "Skill Library" sidebar panel.
 *
 * Fetches skills from GET /api/skills on the SAGIDE REST backend and groups
 * them by domain. Auto-refreshes when *.yaml files change under any skills/
 * directory in the workspace.
 */
export class SkillLibraryProvider
    implements vscode.TreeDataProvider<vscode.TreeItem>, vscode.Disposable
{
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData: vscode.Event<void> = this._onDidChangeTreeData.event;

    private _skills: SkillSummary[] = [];
    private readonly _watcher: vscode.FileSystemWatcher;
    private _refreshTimeout?: ReturnType<typeof setTimeout>;

    constructor(private readonly _restBaseUrl: string) {
        // Hot-reload: rebuild tree whenever a skill YAML changes on disk
        this._watcher = vscode.workspace.createFileSystemWatcher('**/skills/*.yaml');
        const scheduleRefresh = () => {
            clearTimeout(this._refreshTimeout);
            // Debounce 500ms to avoid rapid consecutive refreshes on save
            this._refreshTimeout = setTimeout(() => this.refresh(), 500);
        };
        this._watcher.onDidChange(scheduleRefresh);
        this._watcher.onDidCreate(scheduleRefresh);
        this._watcher.onDidDelete(scheduleRefresh);
    }

    // ── TreeDataProvider ─────────────────────────────────────────────────────────

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: vscode.TreeItem): vscode.ProviderResult<vscode.TreeItem[]> {
        if (!element) {
            // Root level: one item per domain
            const domains = [...new Set(this._skills.map(s => s.domain))].sort();
            return domains.map(domain => {
                const count = this._skills.filter(s => s.domain === domain).length;
                return new SkillDomainItem(domain, count);
            });
        }
        if (element instanceof SkillDomainItem) {
            return this._skills
                .filter(s => s.domain === element.domain)
                .sort((a, b) => a.name.localeCompare(b.name))
                .map(s => new SkillItem(s));
        }
        return [];
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    /** Fetches the latest skills from the backend and fires a tree-data change. */
    async refresh(): Promise<void> {
        try {
            this._skills = await fetchJson<SkillSummary[]>(this._restBaseUrl, '/api/skills');
        } catch {
            // Service may not be running — show empty tree silently
            this._skills = [];
        }
        this._onDidChangeTreeData.fire();
    }

    dispose(): void {
        this._watcher.dispose();
        this._onDidChangeTreeData.dispose();
        clearTimeout(this._refreshTimeout);
    }
}

// ── HTTP helper ─────────────────────────────────────────────────────────────────

function fetchJson<T>(baseUrl: string, path: string): Promise<T> {
    return new Promise((resolve, reject) => {
        const url = `${baseUrl.replace(/\/$/, '')}${path}`;
        const req = http.get(url, { timeout: 5_000 }, (res) => {
            let body = '';
            res.on('data', (chunk: string) => body += chunk);
            res.on('end', () => {
                if ((res.statusCode ?? 0) >= 400) {
                    reject(new Error(`HTTP ${res.statusCode}`));
                } else {
                    try { resolve(JSON.parse(body) as T); }
                    catch { reject(new Error('Invalid JSON')); }
                }
            });
        });
        req.on('error', reject);
        req.on('timeout', () => { req.destroy(); reject(new Error('Request timed out')); });
    });
}
