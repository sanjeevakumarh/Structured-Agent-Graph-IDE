import * as vscode from 'vscode';
import * as http from 'http';

// ── Data types ──────────────────────────────────────────────────────────────────

interface SkillNode {
    id: string;
    label: string;
    type: 'primitive' | 'skill' | 'object' | 'subtask';
    skillRef: string | null;
    skillDomain: string | null;
    stepType: string | null;
    outputVar: string | null;
}

interface SkillEdge {
    from: string;
    to: string;
    dataFlow: string;
}

interface SkillGraphResponse {
    nodes: SkillNode[];
    edges: SkillEdge[];
    promptName: string;
    promptDomain: string;
}

// ── Panel registry ──────────────────────────────────────────────────────────────

// One panel per prompt key ("domain/name")
const openPanels = new Map<string, SkillComposerPanel>();

export class SkillComposerPanel {
    private readonly panel: vscode.WebviewPanel;

    private constructor(
        context: vscode.ExtensionContext,
        promptKey: string,
        graph: SkillGraphResponse
    ) {
        this.panel = vscode.window.createWebviewPanel(
            'sagIDE.skillComposer',
            `Skills: ${graph.promptDomain}/${graph.promptName}`,
            vscode.ViewColumn.Beside,
            {
                enableScripts: true,
                retainContextWhenHidden: true,
            }
        );

        this.panel.webview.html = this.buildHtml(graph);

        // Click on a skill node → open its YAML file in the editor
        this.panel.webview.onDidReceiveMessage(async msg => {
            if (msg.command === 'openSkill' && msg.skillRef) {
                const skillRef: string = msg.skillRef;
                const fileName = skillRef.split('/').pop() ?? skillRef;
                const files = await vscode.workspace.findFiles(
                    `**/skills/**/${fileName}.yaml`, undefined, 5
                );
                if (files.length > 0) {
                    vscode.window.showTextDocument(files[0]);
                } else {
                    vscode.window.showInformationMessage(
                        `Skill file '${skillRef}.yaml' not found in workspace.`
                    );
                }
            }
        }, undefined, context.subscriptions);

        this.panel.onDidDispose(() => {
            openPanels.delete(promptKey);
        }, undefined, context.subscriptions);
    }

    // ── Public static API ────────────────────────────────────────────────────────

    static async show(
        context: vscode.ExtensionContext,
        promptDomain: string,
        promptName: string,
        restBaseUrl: string
    ): Promise<void> {
        const key = `${promptDomain}/${promptName}`;
        const existing = openPanels.get(key);
        if (existing) {
            existing.panel.reveal(vscode.ViewColumn.Beside);
            return;
        }

        let graph: SkillGraphResponse;
        try {
            graph = await fetchJson<SkillGraphResponse>(
                restBaseUrl,
                `/api/skills/graph?prompt=${encodeURIComponent(key)}`
            );
        } catch (err) {
            vscode.window.showErrorMessage(
                `Failed to load skill graph for '${key}': ${err}`
            );
            return;
        }

        const panel = new SkillComposerPanel(context, key, graph);
        openPanels.set(key, panel);
    }

    // ── HTML generation ──────────────────────────────────────────────────────────

    private buildHtml(graph: SkillGraphResponse): string {
        const layers   = computeLayers(graph.nodes, graph.edges);
        const nodesJson = JSON.stringify(graph.nodes);
        const edgesJson = JSON.stringify(graph.edges);
        const layersJson = JSON.stringify(layers);
        const title   = escapeHtml(`${graph.promptDomain}/${graph.promptName}`);

        return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline';">
<title>Skill Composer</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size);
         background: var(--vscode-editor-background); color: var(--vscode-foreground);
         overflow-x: auto; }
  #toolbar { display: flex; align-items: center; gap: 12px; padding: 8px 16px;
             background: var(--vscode-sideBar-background);
             border-bottom: 1px solid var(--vscode-panel-border); }
  #toolbar h2 { font-size: 13px; font-weight: 600; flex: 1; }
  .legend { display: flex; gap: 12px; align-items: center; font-size: 11px;
            color: var(--vscode-descriptionForeground); }
  .legend-item { display: flex; align-items: center; gap: 4px; }
  .legend-dot { width: 10px; height: 10px; border-radius: 2px; }
  .dot-skill     { background: rgba(91,169,224,0.2);  border: 1px solid #5ba9e0; }
  .dot-object    { background: rgba(92,184,92,0.2);   border: 1px solid #5cb85c; }
  .dot-subtask   { background: rgba(160,91,224,0.2);  border: 1px solid #a05be0; }
  .dot-primitive { background: rgba(212,176,74,0.2);  border: 1px solid #d4b04a; }

  #graph-container { position: relative; padding: 20px; min-height: 300px; }
  .layer { display: flex; justify-content: center; gap: 20px; margin-bottom: 60px;
           position: relative; z-index: 1; }

  .node-card { width: 180px; border: 1px solid var(--vscode-panel-border);
               border-radius: 6px; padding: 10px 12px; transition: border-color 0.15s; }
  .node-card.clickable { cursor: pointer; }
  .node-card.clickable:hover { border-color: var(--vscode-focusBorder); }
  .node-card.type-skill     { border-color: #5ba9e0; background: rgba(91,169,224,0.05); }
  .node-card.type-object    { border-color: #5cb85c; background: rgba(92,184,92,0.05); }
  .node-card.type-subtask   { border-color: #a05be0; background: rgba(160,91,224,0.05); }
  .node-card.type-primitive { border-color: #d4b04a; background: rgba(212,176,74,0.05); }

  .node-header { display: flex; align-items: center; gap: 6px; margin-bottom: 4px; }
  .node-icon   { font-size: 14px; }
  .node-label  { font-size: 12px; font-weight: 600; flex: 1; overflow: hidden;
                 text-overflow: ellipsis; white-space: nowrap; }
  .node-meta   { font-size: 10px; color: var(--vscode-descriptionForeground);
                 overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .node-output { font-size: 10px; color: #d4b04a; margin-top: 2px; }

  svg#arrows { position: absolute; top: 0; left: 0; width: 100%; height: 100%;
               pointer-events: none; z-index: 0; overflow: visible; }
  svg#arrows line { stroke: var(--vscode-panel-border); stroke-width: 1.5;
                    marker-end: url(#arrowhead); }
  svg#arrows .edge-label { fill: var(--vscode-descriptionForeground); font-size: 9px; }
</style>
</head>
<body>

<div id="toolbar">
  <h2>${title}</h2>
  <div class="legend">
    <span class="legend-item"><span class="legend-dot dot-skill"></span>skill step</span>
    <span class="legend-item"><span class="legend-dot dot-object"></span>object</span>
    <span class="legend-item"><span class="legend-dot dot-subtask"></span>subtask</span>
    <span class="legend-item"><span class="legend-dot dot-primitive"></span>primitive</span>
  </div>
</div>

<div id="graph-container">
  <svg id="arrows">
    <defs>
      <marker id="arrowhead" markerWidth="10" markerHeight="7" refX="10" refY="3.5" orient="auto">
        <polygon points="0 0, 10 3.5, 0 7" fill="var(--vscode-panel-border)"/>
      </marker>
    </defs>
  </svg>
  <div id="layers"></div>
</div>

<script>
  const vscode   = acquireVsCodeApi();
  const allNodes = ${nodesJson};
  const allEdges = ${edgesJson};
  const layers   = ${layersJson};

  // ── Render layers ──────────────────────────────────────────────────────────

  function render() {
    const layersEl = document.getElementById('layers');
    layersEl.innerHTML = '';
    layers.forEach(layer => {
      const rowEl = document.createElement('div');
      rowEl.className = 'layer';
      layer.forEach(nodeId => {
        const node = allNodes.find(n => n.id === nodeId);
        if (!node) return;
        const card = buildCard(node);
        card.id = safeId(nodeId);
        rowEl.appendChild(card);
      });
      layersEl.appendChild(rowEl);
    });
    requestAnimationFrame(drawArrows);
  }

  function buildCard(node) {
    const div = document.createElement('div');
    div.className = 'node-card type-' + node.type + (node.skillRef ? ' clickable' : '');
    div.title = node.id;
    if (node.skillRef) {
      div.onclick = () => vscode.postMessage({ command: 'openSkill', skillRef: node.skillRef });
    }
    const icon = nodeIcon(node.type);
    const meta = node.skillRef || node.stepType || '';
    const outputHtml = node.outputVar
      ? '<div class="node-output">\u2192 ' + escHtml(node.outputVar) + '</div>'
      : '';
    div.innerHTML = \`
      <div class="node-header">
        <span class="node-icon">\${icon}</span>
        <span class="node-label" title="\${escHtml(node.label)}">\${escHtml(node.label)}</span>
      </div>
      <div class="node-meta" title="\${escHtml(meta)}">\${escHtml(meta)}</div>
      \${outputHtml}
    \`;
    return div;
  }

  function nodeIcon(type) {
    if (type === 'skill')     return '\u2699';   // ⚙
    if (type === 'object')    return '\u25c8';   // ◈
    if (type === 'subtask')   return '\u2b21';   // ⬡
    return '\u25a2';                             // ▢ primitive
  }

  function escHtml(s) {
    if (!s) return '';
    return String(s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;')
      .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  function safeId(id) {
    return 'node-' + id.replace(/[^a-zA-Z0-9_-]/g, '_');
  }

  // ── Draw SVG arrows ────────────────────────────────────────────────────────

  function drawArrows() {
    const svg = document.getElementById('arrows');
    Array.from(svg.querySelectorAll('line, text')).forEach(el => el.remove());
    const container = document.getElementById('graph-container');
    const containerRect = container.getBoundingClientRect();

    allEdges.forEach(edge => {
      const fromEl = document.getElementById(safeId(edge.from));
      const toEl   = document.getElementById(safeId(edge.to));
      if (!fromEl || !toEl) return;
      const fromRect = fromEl.getBoundingClientRect();
      const toRect   = toEl.getBoundingClientRect();
      const fromX = fromRect.left - containerRect.left + fromRect.width / 2;
      const fromY = fromRect.top  - containerRect.top  + fromRect.height;
      const toX   = toRect.left   - containerRect.left + toRect.width  / 2;
      const toY   = toRect.top    - containerRect.top;

      const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
      line.setAttribute('x1', fromX);
      line.setAttribute('y1', fromY);
      line.setAttribute('x2', toX);
      line.setAttribute('y2', toY - 10);
      svg.appendChild(line);

      if (edge.dataFlow) {
        const mx = (fromX + toX) / 2;
        const my = (fromY + toY) / 2;
        const label = String(edge.dataFlow);
        const text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
        text.setAttribute('x', mx);
        text.setAttribute('y', my - 2);
        text.setAttribute('text-anchor', 'middle');
        text.setAttribute('class', 'edge-label');
        text.textContent = label.length > 22 ? label.substring(0, 20) + '\u2026' : label;
        svg.appendChild(text);
      }
    });
  }

  window.addEventListener('resize', drawArrows);
  render();
</script>
</body>
</html>`;
    }
}

// ── Topological layering ────────────────────────────────────────────────────────

function computeLayers(nodes: SkillNode[], edges: SkillEdge[]): string[][] {
    const nodeIds = nodes.map(n => n.id);
    const inEdges = new Map<string, string[]>();
    for (const n of nodeIds) { inEdges.set(n, []); }
    for (const e of edges)   { inEdges.get(e.to)?.push(e.from); }

    const layers: string[][] = [];
    const placed = new Set<string>();

    while (placed.size < nodeIds.length) {
        const layer = nodeIds
            .filter(id => !placed.has(id))
            .filter(id => (inEdges.get(id) ?? []).every(dep => placed.has(dep)));
        if (layer.length === 0) {
            // Cycle or unresolvable — place all remaining in one final layer
            nodeIds.filter(id => !placed.has(id)).forEach(id => layer.push(id));
        }
        layer.forEach(id => placed.add(id));
        layers.push(layer);
        if (layer.length === 0) { break; }
    }

    return layers;
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

function escapeHtml(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
