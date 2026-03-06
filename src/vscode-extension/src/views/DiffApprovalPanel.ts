import * as vscode from 'vscode';
import * as path from 'path';
import { AgentType, FileChange, TaskStatusResponse } from '../client/MessageProtocol';

/**
 * Diff Approval Panel — shows proposed file changes from a completed agent task.
 * Each change has an Apply/Skip button; there is also an Apply All button.
 * Applying writes the file to disk via vscode.workspace.fs (no C# service round-trip).
 */
export class DiffApprovalPanel {
    private static panels = new Map<string, DiffApprovalPanel>();

    private readonly panel: vscode.WebviewPanel;
    private readonly changes: FileChange[];
    private applied = new Set<number>(); // indices of applied changes

    private constructor(
        context: vscode.ExtensionContext,
        status: TaskStatusResponse,
    ) {
        const changes = status.result?.changes ?? [];
        this.changes = changes;

        this.panel = vscode.window.createWebviewPanel(
            'sagDiffApproval',
            `SAG: Review Changes — ${status.agentType} (${changes.length} file${changes.length === 1 ? '' : 's'})`,
            vscode.ViewColumn.Beside,
            { enableScripts: true, retainContextWhenHidden: true }
        );

        this.panel.webview.html = buildHtml(status, changes);

        this.panel.onDidDispose(() => {
            DiffApprovalPanel.panels.delete(status.taskId);
        }, null, context.subscriptions);

        this.panel.webview.onDidReceiveMessage(async msg => {
            if (msg.command === 'apply') {
                await this.applyChange(msg.index, msg.filePath, msg.newContent);
            } else if (msg.command === 'applyAll') {
                for (let i = 0; i < this.changes.length; i++) {
                    if (!this.applied.has(i)) {
                        await this.applyChange(i, this.changes[i].filePath, this.changes[i].newContent);
                    }
                }
            } else if (msg.command === 'openFile') {
                try {
                    const uri = vscode.Uri.file(msg.filePath);
                    await vscode.window.showTextDocument(uri, { preview: false });
                } catch {
                    vscode.window.showErrorMessage(`Cannot open file: ${msg.filePath}`);
                }
            }
        }, null, context.subscriptions);
    }

    private async applyChange(index: number, filePath: string, newContent: string): Promise<void> {
        if (!filePath) {
            vscode.window.showWarningMessage('No file path for this change — copy the content manually.');
            return;
        }

        // Guard against path traversal — only allow writes within open workspace folders
        const normalizedPath = path.resolve(filePath);
        const inWorkspace = (vscode.workspace.workspaceFolders ?? []).some(
            wf => normalizedPath.startsWith(wf.uri.fsPath));
        if (!inWorkspace) {
            vscode.window.showWarningMessage(
                `Blocked write outside workspace: ${filePath}`);
            return;
        }

        try {
            const uri = vscode.Uri.file(normalizedPath);
            const bytes = Buffer.from(newContent, 'utf8');
            await vscode.workspace.fs.writeFile(uri, bytes);
            this.applied.add(index);

            this.panel.webview.postMessage({
                command: 'applied',
                index,
                appliedCount: this.applied.size,
                totalCount: this.changes.length,
            });

            const fileName = path.basename(filePath);
            vscode.window.showInformationMessage(`SAG: Applied changes to ${fileName}`);
        } catch (err) {
            vscode.window.showErrorMessage(`SAG: Failed to write ${filePath}: ${err}`);
        }
    }

    /** Show (or re-show) the panel for a task that has file changes. */
    static show(context: vscode.ExtensionContext, status: TaskStatusResponse): void {
        const existing = DiffApprovalPanel.panels.get(status.taskId);
        if (existing) {
            existing.panel.reveal(vscode.ViewColumn.Beside);
            return;
        }
        const instance = new DiffApprovalPanel(context, status);
        DiffApprovalPanel.panels.set(status.taskId, instance);
    }
}

// ─── HTML ────────────────────────────────────────────────────────────────────

function escHtml(s: string): string {
    return s
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function buildHtml(status: TaskStatusResponse, changes: FileChange[]): string {
    const changesJson = escHtml(JSON.stringify(changes));

    const cards = changes.map((c, i) => {
        const displayPath = c.filePath || `(unnamed change ${i + 1})`;
        const hasPath = !!c.filePath;
        const lang = detectLang(c.filePath);
        const lines = c.newContent.split('\n');
        const lineCount = lines.length;
        return `
<div class="card" id="card-${i}">
  <div class="card-header">
    <span class="file-path ${hasPath ? 'clickable' : ''}" data-idx="${i}" data-path="${escHtml(c.filePath)}" title="${escHtml(displayPath)}">${escHtml(displayPath)}</span>
    <span class="badge">${lineCount} line${lineCount === 1 ? '' : 's'}</span>
    ${c.description ? `<span class="desc">${escHtml(c.description)}</span>` : ''}
    <div class="card-actions">
      <button class="btn-apply" data-idx="${i}" onclick="applyOne(${i})" ${hasPath ? '' : 'title="No file path — cannot apply automatically" disabled'}>Apply</button>
      <button class="btn-skip" data-idx="${i}" onclick="skipOne(${i})">Skip</button>
    </div>
  </div>
  <pre class="code-block" id="code-${i}"><code class="lang-${lang}">${escHtml(c.newContent)}</code></pre>
</div>`;
    }).join('\n');

    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline';">
<title>SAG Diff Approval</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    background: var(--vscode-editor-background);
    color: var(--vscode-editor-foreground);
    font-family: var(--vscode-font-family, sans-serif);
    font-size: var(--vscode-font-size, 13px);
    display: flex;
    flex-direction: column;
    height: 100vh;
    overflow: hidden;
  }
  #toolbar {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 12px;
    background: var(--vscode-titleBar-activeBackground);
    flex-shrink: 0;
    border-bottom: 1px solid var(--vscode-panel-border);
  }
  #toolbar h2 {
    font-size: 13px;
    font-weight: 600;
    flex: 1;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  #status-bar {
    font-size: 11px;
    color: var(--vscode-descriptionForeground);
    padding: 3px 12px;
    border-bottom: 1px solid var(--vscode-panel-border);
    flex-shrink: 0;
  }
  #scroll-area {
    flex: 1;
    overflow-y: auto;
    padding: 10px 12px;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .card {
    border: 1px solid var(--vscode-panel-border);
    border-radius: 4px;
    overflow: hidden;
  }
  .card.applied {
    opacity: 0.5;
    border-color: var(--vscode-gitDecoration-addedResourceForeground);
  }
  .card.skipped {
    opacity: 0.35;
  }
  .card-header {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 10px;
    background: var(--vscode-sideBarSectionHeader-background, var(--vscode-titleBar-activeBackground));
    flex-wrap: wrap;
  }
  .file-path {
    font-family: var(--vscode-editor-font-family, monospace);
    font-size: 12px;
    font-weight: 600;
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--vscode-foreground);
  }
  .file-path.clickable {
    cursor: pointer;
    color: var(--vscode-textLink-foreground);
    text-decoration: underline;
  }
  .file-path.clickable:hover {
    color: var(--vscode-textLink-activeForeground);
  }
  .badge {
    font-size: 10px;
    padding: 1px 5px;
    background: var(--vscode-badge-background);
    color: var(--vscode-badge-foreground);
    border-radius: 10px;
    white-space: nowrap;
  }
  .desc {
    font-size: 11px;
    color: var(--vscode-descriptionForeground);
    flex: 2;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .card-actions { display: flex; gap: 4px; margin-left: auto; }
  button {
    padding: 3px 10px;
    border: none;
    border-radius: 2px;
    font-size: 12px;
    cursor: pointer;
  }
  button:disabled { opacity: 0.4; cursor: not-allowed; }
  .btn-apply {
    background: var(--vscode-button-background);
    color: var(--vscode-button-foreground);
  }
  .btn-apply:hover:not(:disabled) { background: var(--vscode-button-hoverBackground); }
  .btn-skip {
    background: var(--vscode-button-secondaryBackground, #5a5a5a);
    color: var(--vscode-button-secondaryForeground, #fff);
  }
  .btn-skip:hover { background: var(--vscode-button-secondaryHoverBackground, #6e6e6e); }
  .btn-apply-all {
    background: var(--vscode-gitDecoration-addedResourceForeground, #4ec94e);
    color: #000;
    font-weight: 600;
  }
  .btn-apply-all:hover { opacity: 0.85; }
  .code-block {
    margin: 0;
    padding: 10px 12px;
    background: var(--vscode-textCodeBlock-background, var(--vscode-editor-background));
    font-family: var(--vscode-editor-font-family, 'Consolas', monospace);
    font-size: var(--vscode-editor-font-size, 12px);
    line-height: 1.5;
    overflow-x: auto;
    white-space: pre;
    max-height: 400px;
    overflow-y: auto;
  }
  .applied-label {
    color: var(--vscode-gitDecoration-addedResourceForeground, #4ec94e);
    font-size: 11px;
    font-weight: 600;
  }
  .skipped-label {
    color: var(--vscode-descriptionForeground);
    font-size: 11px;
  }
</style>
</head>
<body>
<div id="toolbar">
  <h2>Proposed Changes · ${escHtml(status.agentType)} · ${escHtml(status.modelId.split(':')[0])}</h2>
  <button class="btn-apply-all" onclick="applyAll()">$(check-all) Apply All</button>
</div>
<div id="status-bar">
  <span id="status-text">${changes.length} change${changes.length === 1 ? '' : 's'} pending review</span>
</div>
<div id="scroll-area">
${cards}
</div>
<script>
  const vscode = acquireVsCodeApi();
  const changesData = JSON.parse(decodeURIComponent('${encodeURIComponent(JSON.stringify(changes))}'));
  let appliedCount = 0;
  const totalCount = changesData.length;

  function applyOne(idx) {
    const c = changesData[idx];
    vscode.postMessage({ command: 'apply', index: idx, filePath: c.filePath, newContent: c.newContent });
  }

  function skipOne(idx) {
    const card = document.getElementById('card-' + idx);
    card.classList.add('skipped');
    const btn = card.querySelector('.btn-apply');
    const btnSkip = card.querySelector('.btn-skip');
    if (btn) btn.disabled = true;
    if (btnSkip) btnSkip.disabled = true;
    const header = card.querySelector('.card-actions');
    if (header) {
      const lbl = document.createElement('span');
      lbl.className = 'skipped-label';
      lbl.textContent = 'Skipped';
      header.appendChild(lbl);
    }
  }

  function applyAll() {
    vscode.postMessage({ command: 'applyAll' });
  }

  // Open file when clicking path
  document.querySelectorAll('.file-path.clickable').forEach(el => {
    el.addEventListener('click', () => {
      vscode.postMessage({ command: 'openFile', filePath: el.getAttribute('data-path') });
    });
  });

  window.addEventListener('message', event => {
    const { command, index, appliedCount: ac, totalCount: tc } = event.data;
    if (command === 'applied') {
      appliedCount = ac;
      const card = document.getElementById('card-' + index);
      card.classList.add('applied');
      const btn = card.querySelector('.btn-apply');
      const btnSkip = card.querySelector('.btn-skip');
      if (btn) btn.disabled = true;
      if (btnSkip) btnSkip.disabled = true;
      const actions = card.querySelector('.card-actions');
      if (actions) {
        const lbl = document.createElement('span');
        lbl.className = 'applied-label';
        lbl.textContent = '✓ Applied';
        actions.appendChild(lbl);
      }
      const statusText = document.getElementById('status-text');
      if (statusText) {
        statusText.textContent = ac === tc
          ? \`All \${tc} change\${tc === 1 ? '' : 's'} applied ✓\`
          : \`\${ac} of \${tc} change\${tc === 1 ? '' : 's'} applied\`;
      }
    }
  });
</script>
</body>
</html>`;
}

function detectLang(filePath: string): string {
    const ext = filePath.split('.').pop()?.toLowerCase() ?? '';
    const map: Record<string, string> = {
        ts: 'typescript', tsx: 'typescript',
        js: 'javascript', jsx: 'javascript',
        cs: 'csharp', py: 'python',
        java: 'java', go: 'go',
        rs: 'rust', cpp: 'cpp', c: 'c',
        json: 'json', yaml: 'yaml', yml: 'yaml',
        md: 'markdown', html: 'html', css: 'css',
        sh: 'bash', ps1: 'powershell',
    };
    return map[ext] ?? 'plaintext';
}
