import * as http from 'http';

/** Minimal GET helper that returns parsed JSON. Throws on non-2xx or timeout. */
export function fetchJson<T>(baseUrl: string, path: string): Promise<T> {
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
