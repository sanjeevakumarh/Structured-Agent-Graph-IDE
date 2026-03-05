import * as vscode from 'vscode';
import * as http from 'http';

// ── Data types ─────────────────────────────────────────────────────────────────

export interface PromptSummary {
    name: string;
    domain: string;
    version: number;
    schedule?: string;
    sourceTag?: string;
    description?: string;
    hasSubtasks: boolean;
}

// ── Tree item classes ──────────────────────────────────────────────────────────

export class PromptDomainItem extends vscode.TreeItem {
    constructor(public readonly domain: string, promptCount: number) {
        super(domain, vscode.TreeItemCollapsibleState.Expanded);
        this.description  = `${promptCount} prompt${promptCount === 1 ? '' : 's'}`;
        this.contextValue = 'promptDomain';
        this.iconPath     = new vscode.ThemeIcon('folder');
    }
}

export class PromptItem extends vscode.TreeItem {
    constructor(public readonly prompt: PromptSummary) {
        super(prompt.name, vscode.TreeItemCollapsibleState.None);

        // Description: show cron if scheduled, otherwise truncate description
        this.description = prompt.schedule
            ? `$(clock) ${prompt.schedule}`
            : (prompt.description?.substring(0, 50) ?? '');

        const lines: string[] = [];
        if (prompt.description) lines.push(prompt.description);
        if (prompt.schedule)    lines.push(`Schedule: ${prompt.schedule}`);
        if (prompt.hasSubtasks) lines.push('Multi-model (has subtasks)');
        if (prompt.sourceTag)   lines.push(`Tag: ${prompt.sourceTag}`);
        this.tooltip = new vscode.MarkdownString(lines.join('\n\n'));

        // Icon: graph for multi-model, clock for scheduled, symbol-method for simple
        this.iconPath     = new vscode.ThemeIcon(
            prompt.hasSubtasks ? 'graph' : (prompt.schedule ? 'clock' : 'symbol-method'));
        this.contextValue = 'promptItem';

        // Double-click opens the YAML file
        this.command = {
            command: 'sagIDE.editPrompt',
            title:   'Open Prompt YAML',
            arguments: [prompt.domain, prompt.name],
        };
    }
}

// ── Provider ───────────────────────────────────────────────────────────────────

/**
 * Tree view provider for the "Prompt Library" sidebar panel.
 *
 * Fetches prompts from GET /api/prompts on the SAGIDE REST backend and groups
 * them by domain. Auto-refreshes when *.yaml files change under any prompts/
 * directory in the workspace.
 */
export class PromptLibraryProvider
    implements vscode.TreeDataProvider<vscode.TreeItem>, vscode.Disposable
{
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData: vscode.Event<void> = this._onDidChangeTreeData.event;

    private _prompts: PromptSummary[] = [];
    private readonly _watcher: vscode.FileSystemWatcher;
    private _refreshTimeout?: ReturnType<typeof setTimeout>;

    constructor(private readonly _restBaseUrl: string) {
        // Hot-reload: rebuild tree whenever a prompt YAML changes on disk
        this._watcher = vscode.workspace.createFileSystemWatcher('**/prompts/*.yaml');
        const scheduleRefresh = () => {
            clearTimeout(this._refreshTimeout);
            // Debounce 500ms to avoid rapid consecutive refreshes on save
            this._refreshTimeout = setTimeout(() => this.refresh(), 500);
        };
        this._watcher.onDidChange(scheduleRefresh);
        this._watcher.onDidCreate(scheduleRefresh);
        this._watcher.onDidDelete(scheduleRefresh);
    }

    // ── TreeDataProvider ───────────────────────────────────────────────────────

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: vscode.TreeItem): vscode.ProviderResult<vscode.TreeItem[]> {
        if (!element) {
            // Root level: one item per domain
            const domains = [...new Set(this._prompts.map(p => p.domain))].sort();
            return domains.map(domain => {
                const count = this._prompts.filter(p => p.domain === domain).length;
                return new PromptDomainItem(domain, count);
            });
        }
        if (element instanceof PromptDomainItem) {
            return this._prompts
                .filter(p => p.domain === element.domain)
                .sort((a, b) => a.name.localeCompare(b.name))
                .map(p => new PromptItem(p));
        }
        return [];
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /** Fetches the latest prompts from the backend and fires a tree-data change. */
    async refresh(): Promise<void> {
        try {
            this._prompts = await fetchJson<PromptSummary[]>(this._restBaseUrl, '/api/prompts');
        } catch {
            // Service may not be running — show empty tree silently
            this._prompts = [];
        }
        this._onDidChangeTreeData.fire();
    }

    /** Returns all currently loaded prompts. */
    getAll(): PromptSummary[] { return this._prompts; }

    /** Finds a specific prompt by domain + name. */
    find(domain: string, name: string): PromptSummary | undefined {
        return this._prompts.find(
            p => p.domain.toLowerCase() === domain.toLowerCase()
              && p.name.toLowerCase()   === name.toLowerCase());
    }

    dispose(): void {
        this._watcher.dispose();
        this._onDidChangeTreeData.dispose();
        clearTimeout(this._refreshTimeout);
    }
}

// ── HTTP helper ────────────────────────────────────────────────────────────────

/** Minimal GET helper that returns parsed JSON. Throws on non-2xx or timeout. */
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

/** POST helper for running prompts. Returns parsed JSON response. */
export function postJson<T>(
    baseUrl: string,
    path: string,
    body: unknown
): Promise<T> {
    return new Promise((resolve, reject) => {
        const payload = JSON.stringify(body);
        const url     = new URL(`${baseUrl.replace(/\/$/, '')}${path}`);
        const options: http.RequestOptions = {
            hostname: url.hostname,
            port:     url.port || 5100,
            path:     url.pathname + url.search,
            method:   'POST',
            headers:  {
                'Content-Type':   'application/json',
                'Content-Length': Buffer.byteLength(payload),
            },
            timeout: 10_000,
        };
        const req = http.request(options, (res) => {
            let data = '';
            res.on('data', (chunk: string) => data += chunk);
            res.on('end', () => {
                try { resolve(JSON.parse(data) as T); }
                catch { resolve(data as unknown as T); }
            });
        });
        req.on('error', reject);
        req.on('timeout', () => { req.destroy(); reject(new Error('Request timed out')); });
        req.write(payload);
        req.end();
    });
}
