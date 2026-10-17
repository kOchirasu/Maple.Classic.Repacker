import { ChildProcess, spawn, spawnSync } from 'child_process';
import fs from 'fs';
import http from 'http';
import net from 'net';
import path from 'path';

const BACKEND_EXE_BASENAME = 'Maple.Classic.Repacker.UI';

export interface BackendLaunchOptions {
    /** True when running from a packaged app (electron-builder), false in development. */
    isPackaged: boolean;
    /** Electron's per-user data directory; backend settings are stored here. */
    userDataDir: string;
}

interface BackendCommand {
    command: string;
    args: string[];
    cwd: string;
    description: string;
}

/**
 * Owns the headless .NET backend process: picks a free loopback port, starts the process, waits for it to serve the
 * UI and stops it (including child processes) on shutdown.
 */
export class BackendProcess {
    private child: ChildProcess | null = null;
    private recentOutput: string[] = [];
    private exitedEarly = false;

    url = '';

    constructor(private readonly options: BackendLaunchOptions) {}

    /** Starts the backend and resolves once it serves index.html. Rejects with a readable error otherwise. */
    async start(timeoutMs = 60_000): Promise<string> {
        const port = await findFreeLoopbackPort();
        this.url = `http://127.0.0.1:${port}`;

        const { command, args, cwd, description } = this.resolveCommand();
        const env: NodeJS.ProcessEnv = {
            ...process.env,
            MAPLE_REPACKER_HEADLESS: '1',
            MAPLE_REPACKER_PARENT_PID: String(process.pid),
            MAPLE_REPACKER_SETTINGS_PATH: path.join(this.options.userDataDir, 'repacker-settings.json'),
        };

        this.child = spawn(command, [...args, '--headless', '--urls', this.url], {
            cwd,
            env,
            windowsHide: true,
            // Own process group on POSIX so `dotnet run` and the app it launches can be stopped together.
            detached: process.platform !== 'win32',
            stdio: ['ignore', 'pipe', 'pipe'],
        });
        this.child.stdout?.on('data', (chunk: Buffer) => this.captureOutput(chunk));
        this.child.stderr?.on('data', (chunk: Buffer) => this.captureOutput(chunk));
        this.child.on('exit', () => {
            this.exitedEarly = true;
        });

        const spawnError = new Promise<never>((_, reject) => {
            this.child?.on('error', (err) => reject(new Error(`Could not start ${description}: ${err.message}`)));
        });

        await Promise.race([this.waitUntilReady(timeoutMs, description), spawnError]);
        return this.url;
    }

    stop(): void {
        const child = this.child;
        this.child = null;
        if (!child?.pid || child.exitCode !== null) {
            return;
        }
        try {
            if (process.platform === 'win32') {
                spawnSync('taskkill', ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true });
            } else {
                process.kill(-child.pid, 'SIGTERM');
            }
        } catch {
            child.kill();
        }
    }

    /**
     * Resolution order:
     *   1. MAPLE_REPACKER_BACKEND: explicit path to a backend executable.
     *   2. Packaged app: the self-contained backend bundled under resources/backend (see scripts/publish-backend.mjs).
     *   3. Development: `dotnet run` against the cross-platform (net10.0) target of the UI project.
     */
    private resolveCommand(): BackendCommand {
        const explicit = process.env.MAPLE_REPACKER_BACKEND;
        if (explicit) {
            return { command: explicit, args: [], cwd: path.dirname(explicit), description: explicit };
        }

        if (this.options.isPackaged) {
            const exe = path.join(
                process.resourcesPath,
                'backend',
                process.platform === 'win32' ? `${BACKEND_EXE_BASENAME}.exe` : BACKEND_EXE_BASENAME
            );
            return { command: exe, args: [], cwd: path.dirname(exe), description: 'the bundled backend' };
        }

        // dist/ -> electron/ -> Maple.Classic.Repacker.UI/
        const uiProjectDir = path.resolve(__dirname, '..', '..');
        const projectPath = path.join(uiProjectDir, `${BACKEND_EXE_BASENAME}.csproj`);
        if (!fs.existsSync(projectPath)) {
            throw new Error(`Backend project not found at ${projectPath}. Set MAPLE_REPACKER_BACKEND to a backend executable.`);
        }
        return {
            command: 'dotnet',
            args: ['run', '--project', projectPath, '--framework', 'net10.0', '--'],
            cwd: uiProjectDir,
            description: '`dotnet run` (is the .NET 10 SDK installed?)',
        };
    }

    private async waitUntilReady(timeoutMs: number, description: string): Promise<void> {
        const deadline = Date.now() + timeoutMs;
        while (Date.now() < deadline) {
            if (this.exitedEarly) {
                throw new Error(`${description} exited before it was ready.\n\n${this.recentOutput.join('')}`);
            }
            if (await isServing(`${this.url}/index.html`)) {
                return;
            }
            await new Promise((resolve) => setTimeout(resolve, 250));
        }
        throw new Error(`Timed out waiting for ${description} on ${this.url}.\n\n${this.recentOutput.join('')}`);
    }

    private captureOutput(chunk: Buffer): void {
        const text = chunk.toString();
        process.stdout.write(text);
        this.recentOutput.push(text);
        if (this.recentOutput.length > 40) {
            this.recentOutput.shift();
        }
    }
}

function findFreeLoopbackPort(): Promise<number> {
    return new Promise((resolve, reject) => {
        const server = net.createServer();
        server.unref();
        server.on('error', reject);
        server.listen(0, '127.0.0.1', () => {
            const address = server.address();
            const port = typeof address === 'object' && address ? address.port : 0;
            server.close(() => resolve(port));
        });
    });
}

function isServing(url: string): Promise<boolean> {
    return new Promise((resolve) => {
        const req = http.get(url, { timeout: 1000 }, (res) => {
            res.resume();
            resolve(res.statusCode === 200);
        });
        req.on('error', () => resolve(false));
        req.on('timeout', () => {
            req.destroy();
            resolve(false);
        });
    });
}
