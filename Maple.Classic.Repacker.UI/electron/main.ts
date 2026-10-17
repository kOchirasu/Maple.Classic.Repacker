interface ElectronBrowserWindowOptions {
    width?: number;
    height?: number;
    minWidth?: number;
    minHeight?: number;
    backgroundColor?: string;
    title?: string;
    autoHideMenuBar?: boolean;
    webPreferences?: {
        contextIsolation?: boolean;
        nodeIntegration?: boolean;
        sandbox?: boolean;
    };
}

interface ElectronDownloadItem {
    getFilename(): string;

    setSaveDialogOptions(options: {
        title: string;
        defaultPath: string;
        filters: Array<{ name: string; extensions: string[] }>;
    }): void;
}

interface ElectronBrowserWindowInstance {
    loadURL(url: string): Promise<void>;

    on(event: 'closed', listener: () => void): void;

    webContents: {
        setWindowOpenHandler(
            handler: (details: { url: string }) => {
                action: 'allow' | 'deny';
                overrideBrowserWindowOptions?: ElectronBrowserWindowOptions;
            }
        ): void;
        session: {
            on(event: 'will-download', listener: (event: unknown, item: ElectronDownloadItem) => void): void;
        };
    };
}

interface ElectronModule {
    app: {
        whenReady(): Promise<void>;
        on(event: 'window-all-closed', listener: () => void): void;
        quit(): void;
    };
    BrowserWindow: new (options: ElectronBrowserWindowOptions) => ElectronBrowserWindowInstance;
}

interface ChildProcessInstance {
    on(event: 'error', listener: (err: Error) => void): void;

    kill(): void;
}

interface HttpIncomingMessage {
    statusCode?: number;

    resume(): void;
}

interface HttpClientRequest {
    on(event: 'error' | 'timeout', listener: () => void): void;

    destroy(): void;
}

declare function require(moduleName: 'electron'): ElectronModule;
declare function require(moduleName: 'child_process'): {
    spawn(
        command: string,
        args: string[],
        options: {
            cwd?: string;
            env?: Record<string, string | undefined>;
            stdio?: string;
            windowsHide?: boolean;
        }
    ): ChildProcessInstance;
};
declare function require(moduleName: 'http'): {
    get(
        url: string,
        options: { timeout: number },
        callback: (res: HttpIncomingMessage) => void
    ): HttpClientRequest;
};
declare function require(moduleName: 'fs'): {
    existsSync(path: string): boolean;
};
declare function require(moduleName: 'path'): {
    resolve(...paths: string[]): string;
    join(...paths: string[]): string;
};

declare const __dirname: string;
declare const process: {
    env: Record<string, string | undefined>;
};

(() => {
    const { app, BrowserWindow } = require('electron');
    const { spawn } = require('child_process');
    const fs = require('fs');
    const http = require('http');
    const path = require('path');

    let mainWindow: ElectronBrowserWindowInstance | null = null;
    let backendProcess: ChildProcessInstance | null = null;
    const PORT: string = process.env.MAPLE_REPACKER_PORT || '5189';
    const BACKEND_URL = `http://127.0.0.1:${PORT}`;
    const STATUS_URL = `${BACKEND_URL}/index.html`;

    function checkBackendReady(): Promise<boolean> {
        return new Promise<boolean>((resolve) => {
            const req = http.get(STATUS_URL, { timeout: 1000 }, (res) => {
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

    function startBackendIfNeeded(): void {
        const parentDir = path.resolve(__dirname, '..');
        const rootFromObjDir = path.resolve(__dirname, '..', '..', '..');
        const uiDir = fs.existsSync(path.join(parentDir, 'Maple.Classic.Repacker.UI.csproj'))
            ? parentDir
            : rootFromObjDir;
        const projectPath = path.join(uiDir, 'Maple.Classic.Repacker.UI.csproj');

        const env: Record<string, string | undefined> = {
            ...process.env,
            ASPNETCORE_URLS: BACKEND_URL,
            MAPLE_REPACKER_PORT: PORT,
            MAPLE_REPACKER_HEADLESS: '1',
        };

        const args = ['run', '--project', projectPath, '--', '--headless', '--urls', BACKEND_URL];

        backendProcess = spawn('dotnet', args, {
            cwd: uiDir,
            env,
            stdio: 'inherit',
            windowsHide: true,
        });

        backendProcess.on('error', (err: Error) => {
            console.error('[Maple.Repacker Electron] Failed to spawn dotnet backend:', err);
        });
    }

    function buildLoadingDataUrl(): string {
        const html = `<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8" />
<style>
  body {
    margin: 0;
    height: 100vh;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    background: #181a1f;
    color: #d4d4d4;
    font-family: 'Segoe UI', system-ui, sans-serif;
    user-select: none;
  }
  .spinner {
    width: 36px;
    height: 36px;
    border: 3px solid #2c313a;
    border-top-color: #d19a66;
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
    margin-bottom: 18px;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
  h2 { margin: 0 0 8px 0; font-size: 18px; font-weight: 600; letter-spacing: 0.4px; }
  p { margin: 0; font-size: 13px; color: #858585; font-family: Consolas, monospace; }
</style>
</head>
<body>
  <div class="spinner"></div>
  <h2>Starting Maple.Classic.Repacker...</h2>
  <p>Waiting for local backend on ${BACKEND_URL}</p>
</body>
</html>`;
        return `data:text/html;charset=utf-8,${encodeURIComponent(html)}`;
    }

    async function waitForBackendAndLoad(): Promise<void> {
        const alreadyUp = await checkBackendReady();
        if (!alreadyUp) {
            startBackendIfNeeded();
        }

        for (let attempt = 0; attempt < 240; attempt++) {
            if (!mainWindow) {
                return;
            }
            const ready = await checkBackendReady();
            if (ready && mainWindow) {
                await mainWindow.loadURL(BACKEND_URL);
                return;
            }
            await new Promise<void>((r) => setTimeout(r, 250));
        }
    }

    function createWindow(): void {
        mainWindow = new BrowserWindow({
            width: 1580,
            height: 940,
            minWidth: 1120,
            minHeight: 700,
            backgroundColor: '#181a1f',
            title: 'Maple.Classic.Repacker // WZ & IMG Studio',
            autoHideMenuBar: true,
            webPreferences: {
                contextIsolation: true,
                nodeIntegration: false,
                sandbox: true,
            },
        });

        mainWindow.webContents.setWindowOpenHandler(() => ({
            action: 'allow',
            overrideBrowserWindowOptions: {
                width: 980,
                height: 680,
                minWidth: 560,
                minHeight: 380,
                backgroundColor: '#181a1f',
                autoHideMenuBar: true,
                webPreferences: {
                    contextIsolation: true,
                    nodeIntegration: false,
                    sandbox: true,
                },
            },
        }));

        mainWindow.webContents.session.on('will-download', (_event, item) => {
            const filename = item.getFilename();
            if (!filename) return;
            const lower = filename.toLowerCase();
            if (lower.endsWith('.wz')) {
                item.setSaveDialogOptions({
                    title: 'Save Repacked WZ Archive (.wz)',
                    defaultPath: filename,
                    filters: [
                        { name: 'MapleStory WZ Archive (.wz)', extensions: ['wz'] },
                        { name: 'All Files', extensions: ['*'] },
                    ],
                });
            } else if (lower.endsWith('.img')) {
                item.setSaveDialogOptions({
                    title: 'Export WZ Image (.img)',
                    defaultPath: filename,
                    filters: [
                        { name: 'MapleStory WZ Image (.img)', extensions: ['img'] },
                        { name: 'All Files', extensions: ['*'] },
                    ],
                });
            }
        });

        void mainWindow.loadURL(buildLoadingDataUrl());
        void waitForBackendAndLoad();

        mainWindow.on('closed', () => {
            mainWindow = null;
        });
    }

    void app.whenReady().then(() => {
        createWindow();
    });

    app.on('window-all-closed', () => {
        if (backendProcess) {
            backendProcess.kill();
        }
        app.quit();
    });
})();
