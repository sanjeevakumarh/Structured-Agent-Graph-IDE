import * as net from 'net';
import { EventEmitter } from 'events';
import { PipeMessage, MessageTypes } from './MessageProtocol';

export class NamedPipeClient extends EventEmitter {
    private socket: net.Socket | null = null;
    private pipeName: string;
    private sharedSecret: string | undefined;
    private connected = false;
    private pendingRequests = new Map<string, {
        resolve: (msg: PipeMessage) => void;
        reject: (err: Error) => void;
        timer: NodeJS.Timeout;
    }>();
    private receiveBuffer = Buffer.alloc(0);
    private reconnectTimer: NodeJS.Timeout | null = null;
    private requestCounter = 0;

    constructor(pipeName?: string, sharedSecret?: string) {
        super();
        // ServiceConnection always supplies the full platform-resolved path.
        // The default here handles any direct instantiation (e.g. tests).
        this.pipeName = pipeName ?? (
            process.platform === 'win32'
                ? '\\\\.\\pipe\\SAGIDEPipe'
                : '/tmp/CoreFxPipe_SAGIDEPipe'
        );
        this.sharedSecret = sharedSecret;
    }

    async connect(): Promise<void> {
        // Phase 1: establish the OS-level pipe connection.
        await this.connectSocket();
        // Phase 2: if a shared secret is configured, prove identity before normal use.
        if (this.sharedSecret) {
            await this.authenticate();
        }
        this.emit('connected');
    }

    private connectSocket(): Promise<void> {
        return new Promise((resolve, reject) => {
            this.socket = net.createConnection(this.pipeName);

            this.socket.once('connect', () => {
                this.connected = true;
                resolve();
            });

            this.socket.on('data', (data: Buffer) => {
                this.onData(data);
            });

            this.socket.on('close', () => {
                this.connected = false;
                this.emit('disconnected');
                this.scheduleReconnect();
            });

            this.socket.on('error', (err: Error) => {
                if (!this.connected) {
                    reject(err);
                } else {
                    this.emit('error', err);
                }
            });
        });
    }

    private async authenticate(): Promise<void> {
        const response = await this.send(
            { type: MessageTypes.PipeAuth, payload: Buffer.from(this.sharedSecret!, 'utf-8') },
            5_000
        );
        if (response.type !== MessageTypes.PipeAuthOk) {
            await this.disconnect();
            throw new Error('Pipe authentication failed: server rejected the shared secret');
        }
    }

    async send(message: PipeMessage, timeoutMs = 7_200_000): Promise<PipeMessage> {
        if (!this.socket || !this.connected) {
            throw new Error('Not connected to service');
        }

        const requestId = `req_${++this.requestCounter}`;
        message.requestId = requestId;

        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.pendingRequests.delete(requestId);
                reject(new Error(`Request ${requestId} timed out`));
            }, timeoutMs);

            this.pendingRequests.set(requestId, { resolve, reject, timer });

            // Encode payload as base64 string so C# System.Text.Json can decode byte[] correctly
            const wireMessage = {
                type: message.type,
                requestId: message.requestId,
                payload: message.payload ? message.payload.toString('base64') : undefined,
            };
            const json = JSON.stringify(wireMessage);
            const encoded = Buffer.from(json, 'utf-8');
            const lengthBuf = Buffer.alloc(4);
            lengthBuf.writeInt32LE(encoded.length, 0);

            this.socket!.write(Buffer.concat([lengthBuf, encoded]));
        });
    }

    private onData(data: Buffer): void {
        this.receiveBuffer = Buffer.concat([this.receiveBuffer, data]);

        while (this.receiveBuffer.length >= 4) {
            const messageLength = this.receiveBuffer.readInt32LE(0);
            if (this.receiveBuffer.length < 4 + messageLength) {
                break;
            }

            const messageBytes = this.receiveBuffer.subarray(4, 4 + messageLength);
            this.receiveBuffer = this.receiveBuffer.subarray(4 + messageLength);

            try {
                // C# System.Text.Json encodes byte[] as base64; decode it back to Buffer
                const raw = JSON.parse(messageBytes.toString('utf-8'));
                const message: PipeMessage = {
                    type: raw.type,
                    requestId: raw.requestId,
                    payload: raw.payload ? Buffer.from(raw.payload, 'base64') : undefined,
                };
                this.handleMessage(message);
            } catch {
                this.emit('error', new Error('Failed to parse message'));
            }
        }
    }

    private handleMessage(message: PipeMessage): void {
        if (message.requestId && this.pendingRequests.has(message.requestId)) {
            const pending = this.pendingRequests.get(message.requestId)!;
            clearTimeout(pending.timer);
            this.pendingRequests.delete(message.requestId);
            pending.resolve(message);
        } else {
            this.emit('message', message);
        }
    }

    private scheduleReconnect(): void {
        if (this.reconnectTimer) { return; }
        this.reconnectTimer = setTimeout(async () => {
            this.reconnectTimer = null;
            try {
                await this.connect();
            } catch {
                this.scheduleReconnect();
            }
        }, 3000);
    }

    get isConnected(): boolean { return this.connected; }

    async disconnect(): Promise<void> {
        if (this.reconnectTimer) {
            clearTimeout(this.reconnectTimer);
            this.reconnectTimer = null;
        }
        for (const [, pending] of this.pendingRequests) {
            clearTimeout(pending.timer);
            pending.reject(new Error('Disconnected'));
        }
        this.pendingRequests.clear();
        this.socket?.destroy();
        this.socket = null;
        this.connected = false;
    }
}
