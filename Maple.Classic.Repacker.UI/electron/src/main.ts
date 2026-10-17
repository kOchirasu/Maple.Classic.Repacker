import { app, BrowserWindow, dialog, ipcMain, IpcMainEvent, shell } from 'electron';
import path from 'path';
import { BackendProcess } from './backend';
import { HostWebMessage, HostWebMessageType, IpcChannel } from './protocol';
import { TreeContextMenu } from './treeContextMenu';
import { WindowStateStore } from './windowState';

const APP_TITLE = 'Maple.Classic.Repacker // WZ & IMG Studio';
const BACKGROUND = '#181a1f';
const WZ_FILTERS = [
    { name: 'MapleStory WZ Archive (.wz)', extensions: ['wz'] },
    { name: 'All Files', extensions: ['*'] },
];

const backend = new BackendProcess({ isPackaged: app.isPackaged, userDataDir: app.getPath('userData') });
let mainWindow: BrowserWindow | null = null;
let contextMenu: TreeContextMenu | null = null;
/** Set once the page confirms there are no unsaved archives (or the user chose to discard/save them). */
let closeConfirmed = false;
let appLoaded = false;

if (!app.requestSingleInstanceLock()) {
    app.quit();
} else {
    app.on('second-instance', () => {
        if (mainWindow) {
            if (mainWindow.isMinimized()) mainWindow.restore();
            mainWindow.focus();
        }
    });
    app.whenReady().then(createMainWindow);
    app.on('window-all-closed', () => app.quit());
    app.on('will-quit', () => backend.stop());
    registerIpcHandlers();
}

function createMainWindow(): void {
    const windowState = new WindowStateStore(app.getPath('userData'));
    const { bounds, maximized } = windowState.load();

    mainWindow = new BrowserWindow({
        ...bounds,
        minWidth: 1120,
        minHeight: 700,
        backgroundColor: BACKGROUND,
        title: APP_TITLE,
        autoHideMenuBar: true,
        show: false,
        webPreferences: {
            preload: path.join(__dirname, 'preload.js'),
            contextIsolation: true,
            nodeIntegration: false,
            sandbox: true,
        },
    });
    if (maximized) mainWindow.maximize();
    windowState.track(mainWindow);
    contextMenu = new TreeContextMenu(mainWindow);

    mainWindow.once('ready-to-show', () => mainWindow?.show());
    mainWindow.on('close', onWindowClose);
    mainWindow.on('closed', () => {
        mainWindow = null;
        contextMenu = null;
    });
    mainWindow.webContents.on('render-process-gone', () => {
        // A crashed page can no longer answer RequestAppClose, so don't block closing on it.
        closeConfirmed = true;
    });
    configureDownloads(mainWindow);
    configureExternalLinks(mainWindow);

    void mainWindow.loadURL(statusPageUrl('Starting Maple.Classic.Repacker...', 'Waiting for the local backend', true));
    void startBackendAndLoad(mainWindow);
}

async function startBackendAndLoad(window: BrowserWindow): Promise<void> {
    try {
        const url = await backend.start();
        if (!window.isDestroyed()) {
            await window.loadURL(url);
            appLoaded = true;
        }
    } catch (err) {
        if (!window.isDestroyed()) {
            const message = err instanceof Error ? err.message : String(err);
            await window.loadURL(statusPageUrl('The backend failed to start', message, false));
        }
    }
}

/**
 * Closing is negotiated with the page, exactly like the WebView2 shell: ask it via RequestAppClose, and it replies
 * ConfirmAppClose once unsaved archives are handled (immediately if there are none).
 */
function onWindowClose(event: Electron.Event): void {
    if (closeConfirmed || !appLoaded || !mainWindow) {
        return;
    }
    event.preventDefault();
    contextMenu?.close();
    mainWindow.webContents.send(IpcChannel.HostMessage, { type: HostWebMessageType.RequestAppClose } satisfies HostWebMessage);
}

function registerIpcHandlers(): void {
    ipcMain.on(IpcChannel.HostMessage, (event: IpcMainEvent, message: HostWebMessage) => {
        if (!mainWindow || event.sender !== mainWindow.webContents || typeof message?.type !== 'string') {
            return;
        }
        switch (message.type) {
            case HostWebMessageType.OpenTreeContextMenu:
                contextMenu?.show(message);
                break;
            case HostWebMessageType.CloseTreeContextMenu:
                contextMenu?.close();
                break;
            case HostWebMessageType.ConfirmAppClose:
                closeConfirmed = true;
                mainWindow.close();
                break;
        }
    });

    ipcMain.handle(IpcChannel.PickOpenWzFiles, async () => {
        if (!mainWindow) return [];
        const result = await dialog.showOpenDialog(mainWindow, {
            title: 'Open WZ Archive(s)',
            filters: WZ_FILTERS,
            properties: ['openFile', 'multiSelections'],
        });
        return result.canceled ? [] : result.filePaths;
    });

    ipcMain.handle(IpcChannel.PickDirectory, async () => {
        if (!mainWindow) return null;
        const result = await dialog.showOpenDialog(mainWindow, {
            title: 'Select Directory',
            properties: ['openDirectory', 'createDirectory'],
        });
        return result.canceled ? null : (result.filePaths[0] ?? null);
    });

    ipcMain.handle(IpcChannel.PickSaveWzFile, async (_event, defaultFileName: unknown) => {
        if (!mainWindow) return null;
        const result = await dialog.showSaveDialog(mainWindow, {
            title: 'Save WZ Archive',
            defaultPath: typeof defaultFileName === 'string' ? defaultFileName : undefined,
            filters: WZ_FILTERS,
        });
        return result.canceled ? null : (result.filePath ?? null);
    });
}

/** Browser-style downloads (Download Repacked .wz, node exports) get a native save dialog with sensible filters. */
function configureDownloads(window: BrowserWindow): void {
    window.webContents.session.on('will-download', (_event, item) => {
        const filename = item.getFilename();
        const lower = filename.toLowerCase();
        const filters = lower.endsWith('.wz')
            ? WZ_FILTERS
            : lower.endsWith('.img')
              ? [{ name: 'MapleStory WZ Image (.img)', extensions: ['img'] }, { name: 'All Files', extensions: ['*'] }]
              : undefined;
        item.setSaveDialogOptions({ title: `Save ${filename}`, defaultPath: filename, filters });
    });
}

/** Keep the app window on the local backend; open anything else in the user's browser. */
function configureExternalLinks(window: BrowserWindow): void {
    window.webContents.setWindowOpenHandler(({ url }) => {
        if (!url.startsWith(backend.url)) {
            void shell.openExternal(url);
        }
        return { action: 'deny' };
    });
}

function statusPageUrl(title: string, detail: string, spinning: boolean): string {
    const escape = (s: string): string => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    const html = `<!DOCTYPE html>
<html><head><meta charset="utf-8" /><style>
  body { margin: 0; height: 100vh; display: flex; flex-direction: column; align-items: center; justify-content: center;
         background: ${BACKGROUND}; color: #d4d4d4; font-family: 'Segoe UI', system-ui, sans-serif; user-select: none; }
  .spinner { width: 36px; height: 36px; border: 3px solid #2c313a; border-top-color: #d19a66; border-radius: 50%;
             animation: spin 0.8s linear infinite; margin-bottom: 18px; }
  @keyframes spin { to { transform: rotate(360deg); } }
  h2 { margin: 0 0 8px 0; font-size: 18px; font-weight: 600; }
  pre { margin: 0; max-width: 80vw; max-height: 60vh; overflow: auto; font-size: 12px; color: #858585; white-space: pre-wrap; user-select: text; }
</style></head><body>
  ${spinning ? '<div class="spinner"></div>' : ''}
  <h2>${escape(title)}</h2>
  <pre>${escape(detail)}</pre>
</body></html>`;
    return `data:text/html;charset=utf-8,${encodeURIComponent(html)}`;
}
