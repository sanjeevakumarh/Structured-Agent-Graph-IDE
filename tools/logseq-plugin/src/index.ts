import '@logseq/libs';

// ── Types ─────────────────────────────────────────────────────────────────────

interface PromptSummary {
    name: string;
    domain: string;
    schedule?: string;
    description?: string;
    hasSubtasks: boolean;
}

interface TaskStatus {
    taskId: string;
    status: 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled' | 'WaitingForApproval';
    progress: number;
    statusMessage?: string;
    result?: { output?: string; errorMessage?: string; success: boolean };
}

interface RunResponse {
    taskId?: string;
    mode?: string;
    instanceId?: string;
}

// ── Settings schema ───────────────────────────────────────────────────────────

const settingsSchema: Parameters<typeof logseq.useSettingsSchema>[0] = [
    {
        key: 'baseUrl',
        title: 'SAGIDE service URL',
        description: 'Base URL of the SAGIDE backend service',
        type: 'string',
        default: 'http://localhost:5100',
    },
    {
        key: 'outputPagePrefix',
        title: 'Output page prefix',
        description: 'Logseq page name prefix for auto-inserted results (e.g. "SAGIDE/")',
        type: 'string',
        default: 'SAGIDE/',
    },
    {
        key: 'pollIntervalMs',
        title: 'Poll interval (ms)',
        description: 'How often to check task status while waiting for completion',
        type: 'number',
        default: 2000,
    },
    {
        key: 'insertMode',
        title: 'Insert mode',
        description: 'Where to put the result when a task completes',
        type: 'enum',
        enumChoices: ['current-block', 'new-page', 'notification-only'],
        default: 'current-block',
    },
];

// ── API helpers ───────────────────────────────────────────────────────────────

function baseUrl(): string {
    return ((logseq.settings?.baseUrl as string) ?? 'http://localhost:5100').replace(/\/$/, '');
}

async function apiFetch<T>(path: string, opts?: RequestInit): Promise<T> {
    const r = await fetch(`${baseUrl()}${path}`, {
        headers: { 'Content-Type': 'application/json' },
        ...opts,
    });
    if (!r.ok) throw new Error(`SAGIDE API ${r.status}: ${await r.text()}`);
    return r.json() as Promise<T>;
}

async function getPrompts(): Promise<PromptSummary[]> {
    return apiFetch<PromptSummary[]>('/api/prompts');
}

async function runPrompt(
    domain: string,
    name: string,
    variables: Record<string, string> = {}
): Promise<RunResponse> {
    return apiFetch<RunResponse>(`/api/prompts/${domain}/${name}/run`, {
        method: 'POST',
        body: JSON.stringify(variables),
    });
}

async function pollUntilDone(
    taskId: string,
    onProgress: (pct: number, msg: string) => void,
    timeoutMs = 2 * 60 * 60 * 1000
): Promise<TaskStatus> {
    const interval = (logseq.settings?.pollIntervalMs as number) ?? 2000;
    const deadline = Date.now() + timeoutMs;

    while (Date.now() < deadline) {
        const status = await apiFetch<TaskStatus>(`/api/tasks/${taskId}`);
        onProgress(status.progress, status.statusMessage ?? status.status);

        if (['Completed', 'Failed', 'Cancelled'].includes(status.status)) {
            return status;
        }
        await sleep(interval);
    }
    throw new Error(`Task ${taskId} timed out`);
}

// ── Prompt picker (text-input fallback since Logseq has no native picker) ────

async function pickPrompt(): Promise<{ domain: string; name: string; variables: Record<string, string> } | null> {
    let prompts: PromptSummary[] = [];
    try {
        prompts = await getPrompts();
    } catch (e) {
        await logseq.UI.showMsg(`SAGIDE: Cannot reach service at ${baseUrl()}`, 'error');
        return null;
    }

    if (!prompts.length) {
        await logseq.UI.showMsg('SAGIDE: No prompts found in registry', 'warning');
        return null;
    }

    // Build prompt list string for display in input dialog
    const list = prompts
        .map((p, i) => `${i + 1}. ${p.domain}/${p.name}${p.hasSubtasks ? ' [multi]' : ''}${p.schedule ? ' ⏰' : ''}`)
        .join('\n');

    const input = await logseq.UI.showMsg(
        `SAGIDE — Available prompts:\n\n${list}\n\nType "domain/name" or number to run:`,
        'info',
        { timeout: false }
    );

    // Logseq doesn't support input dialogs via the standard API, so we use
    // the block-level input approach instead.
    const blockInput = await logseq.Editor.getEditingBlockContent();

    // Alternative: parse whatever the user typed in the current block
    return resolvePromptInput(blockInput ?? '', prompts);
}

function resolvePromptInput(
    raw: string,
    prompts: PromptSummary[]
): { domain: string; name: string; variables: Record<string, string> } | null {
    const trimmed = raw.trim();
    if (!trimmed) return null;

    // Try "domain/name [key=val, ...]" format
    const [spec, ...varParts] = trimmed.split(/\s+(?=\w+=)/);
    const variables: Record<string, string> = {};
    varParts.join(' ').split(',').forEach(pair => {
        const [k, ...rest] = pair.trim().split('=');
        if (k && rest.length) variables[k.trim()] = rest.join('=').trim();
    });

    if (spec.includes('/')) {
        const [domain, name] = spec.split('/', 2);
        return { domain, name, variables };
    }

    // Try numeric index
    const idx = parseInt(spec, 10) - 1;
    if (!isNaN(idx) && idx >= 0 && idx < prompts.length) {
        const p = prompts[idx];
        return { domain: p.domain, name: p.name, variables };
    }

    return null;
}

// ── Insert result into Logseq ─────────────────────────────────────────────────

async function insertResult(
    output: string,
    label: string,
    anchorBlock: { uuid: string } | null
) {
    const mode = (logseq.settings?.insertMode as string) ?? 'current-block';
    const timestamp = new Date().toISOString().substring(0, 16).replace('T', ' ');

    if (mode === 'new-page') {
        const prefix = (logseq.settings?.outputPagePrefix as string) ?? 'SAGIDE/';
        const pageName = `${prefix}${label}-${timestamp}`;
        await logseq.Editor.createPage(pageName, {}, { redirect: true });
        const page = await logseq.Editor.getPage(pageName);
        if (page) {
            await logseq.Editor.appendBlockInPage(pageName, `## ${label}\n${output}`);
        }
        return;
    }

    if (mode === 'current-block' && anchorBlock) {
        // Insert as children of the anchor block
        await logseq.Editor.insertBlock(anchorBlock.uuid, `**${label}** (${timestamp})`, { sibling: false });
        // Split output into blocks at double-newlines; cap at 50 blocks
        const chunks = output.split(/\n{2,}/).slice(0, 50);
        let prev = anchorBlock;
        for (const chunk of chunks) {
            const b = await logseq.Editor.insertBlock(prev.uuid, chunk.trim(), { sibling: true });
            if (b) prev = b;
        }
        return;
    }

    // notification-only or fallback
    await logseq.UI.showMsg(`SAGIDE: ${label} completed`, 'success', { timeout: 5000 });
}

// ── Slash commands ────────────────────────────────────────────────────────────

/** /sag run — submit a prompt and optionally wait for result */
async function cmdRun() {
    const block = await logseq.Editor.getCurrentBlock();
    if (!block) {
        await logseq.UI.showMsg('SAGIDE: Place cursor in a block first', 'warning');
        return;
    }

    // Read prompt spec from the block content (user types "domain/name key=val")
    const content = (await logseq.Editor.getEditingBlockContent()) ?? block.content ?? '';
    const trimmed = content.replace('/sag run', '').trim();

    let domain: string;
    let name: string;
    let variables: Record<string, string> = {};

    if (trimmed.includes('/')) {
        const result = resolvePromptInput(trimmed, []);
        if (!result) {
            await logseq.UI.showMsg('SAGIDE: Format: /sag run domain/name [key=val ...]', 'warning');
            return;
        }
        ({ domain, name, variables } = result);
    } else {
        // No prompt spec: list available and bail with a hint
        let prompts: PromptSummary[] = [];
        try { prompts = await getPrompts(); } catch { /* ignore */ }

        const list = prompts.map(p => `${p.domain}/${p.name}`).join(', ');
        await logseq.UI.showMsg(
            `SAGIDE: Available prompts:\n${list || '(none)'}\n\nUsage: /sag run domain/name`,
            'info', { timeout: 8000 }
        );
        return;
    }

    await logseq.UI.showMsg(`SAGIDE: Submitting ${domain}/${name}…`, 'info', { timeout: 3000 });

    let resp: RunResponse;
    try {
        resp = await runPrompt(domain, name, variables);
    } catch (e) {
        await logseq.UI.showMsg(`SAGIDE error: ${(e as Error).message}`, 'error');
        return;
    }

    const label = `${domain}/${name}`;

    if (!resp.taskId) {
        // Coordinator / subtask mode — no single taskId to poll
        await logseq.UI.showMsg(
            `SAGIDE: ${label} accepted (multi-model coordinator). Check reports when done.`,
            'success', { timeout: 6000 }
        );
        return;
    }

    const taskId = resp.taskId;
    await logseq.UI.showMsg(`SAGIDE: Task ${taskId.substring(0, 8)} queued…`, 'info', { timeout: 3000 });

    // Poll in background
    try {
        const status = await pollUntilDone(
            taskId,
            (pct, msg) => logseq.UI.showMsg(`SAGIDE: ${label} — ${pct}% ${msg}`, 'info', { timeout: 2000 }),
        );

        if (status.status === 'Completed' && status.result?.output) {
            await insertResult(status.result.output, label, block);
            await logseq.UI.showMsg(`SAGIDE: ${label} done`, 'success', { timeout: 4000 });
        } else {
            await logseq.UI.showMsg(
                `SAGIDE: ${label} ${status.status} — ${status.result?.errorMessage ?? ''}`,
                status.status === 'Failed' ? 'error' : 'warning',
                { timeout: 6000 }
            );
        }
    } catch (e) {
        await logseq.UI.showMsg(`SAGIDE poll error: ${(e as Error).message}`, 'error');
    }
}

/** /sag status — show status of a task ID (typed in block) */
async function cmdStatus() {
    const block = await logseq.Editor.getCurrentBlock();
    const content = (await logseq.Editor.getEditingBlockContent()) ?? '';
    const taskId = content.replace('/sag status', '').trim();

    if (!taskId) {
        await logseq.UI.showMsg('SAGIDE: Usage: /sag status <task-id>', 'warning');
        return;
    }

    try {
        const status = await apiFetch<TaskStatus>(`/api/tasks/${taskId}`);
        const msg = `Task ${taskId.substring(0, 8)}: ${status.status} (${status.progress}%) ${status.statusMessage ?? ''}`;
        await logseq.UI.showMsg(`SAGIDE: ${msg}`, 'info', { timeout: 5000 });

        if (block && status.status === 'Completed' && status.result?.output) {
            await insertResult(status.result.output, taskId.substring(0, 8), block);
        }
    } catch (e) {
        await logseq.UI.showMsg(`SAGIDE: ${(e as Error).message}`, 'error');
    }
}

/** /sag prompts — list available prompts as blocks under cursor */
async function cmdPrompts() {
    const block = await logseq.Editor.getCurrentBlock();
    if (!block) return;

    let prompts: PromptSummary[] = [];
    try {
        prompts = await getPrompts();
    } catch (e) {
        await logseq.UI.showMsg(`SAGIDE: ${(e as Error).message}`, 'error');
        return;
    }

    if (!prompts.length) {
        await logseq.UI.showMsg('SAGIDE: No prompts found', 'warning');
        return;
    }

    // Group by domain
    const byDomain = Map.groupBy(prompts, p => p.domain);
    let prev: { uuid: string } = block;

    for (const [domain, items] of byDomain) {
        const domBlock = await logseq.Editor.insertBlock(prev.uuid, `**${domain}**`, { sibling: true });
        if (!domBlock) continue;
        for (const p of items) {
            const meta = [
                p.schedule ? `schedule: \`${p.schedule}\`` : null,
                p.hasSubtasks ? 'multi-model' : null,
                p.description ? p.description.substring(0, 80) : null,
            ].filter(Boolean).join(' · ');
            await logseq.Editor.insertBlock(domBlock.uuid, `${p.name} — ${meta}`, { sibling: false });
        }
        prev = domBlock;
    }
}

// ── Utility ───────────────────────────────────────────────────────────────────

function sleep(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

// ── Plugin entry point ────────────────────────────────────────────────────────

async function main() {
    logseq.useSettingsSchema(settingsSchema);

    // Slash commands
    logseq.Editor.registerSlashCommand('sag run', cmdRun);
    logseq.Editor.registerSlashCommand('sag status', cmdStatus);
    logseq.Editor.registerSlashCommand('sag prompts', cmdPrompts);

    // Command palette entries (for keybinding)
    logseq.App.registerCommandPalette(
        { key: 'sagide-run', label: 'SAGIDE: Run prompt' },
        cmdRun
    );
    logseq.App.registerCommandPalette(
        { key: 'sagide-prompts', label: 'SAGIDE: List prompts' },
        cmdPrompts
    );

    console.log('SAGIDE plugin loaded — service:', baseUrl());
    logseq.UI.showMsg('SAGIDE plugin ready', 'success', { timeout: 2000 });
}

logseq.ready(main).catch(console.error);
