import { contextBridge, ipcRenderer } from 'electron';

// Sandboxed preloads can only require('electron'), so the channel names are duplicated from protocol.ts here.
const HOST_MESSAGE = 'maple-host:message';
const PICK_OPEN_WZ_FILES = 'maple-host:pick-open-wz-files';
const PICK_DIRECTORY = 'maple-host:pick-directory';
const PICK_SAVE_WZ_FILE = 'maple-host:pick-save-wz-file';

/**
 * Exposed to the page as `window.mapleHost` (see `ElectronHostApi` in wwwroot/rpc.ts). Mirrors what the WebView2 shell
 * provides through `window.chrome.webview`, plus native file dialogs.
 */
contextBridge.exposeInMainWorld('mapleHost', {
    platform: process.platform,
    postMessage: (message: unknown): void => {
        ipcRenderer.send(HOST_MESSAGE, message);
    },
    onMessage: (listener: (message: unknown) => void): void => {
        ipcRenderer.on(HOST_MESSAGE, (_event, message: unknown) => listener(message));
    },
    pickOpenWzFiles: (): Promise<string[]> => ipcRenderer.invoke(PICK_OPEN_WZ_FILES),
    pickDirectory: (): Promise<string | null> => ipcRenderer.invoke(PICK_DIRECTORY),
    pickSaveWzFile: (defaultFileName: string): Promise<string | null> => ipcRenderer.invoke(PICK_SAVE_WZ_FILE, defaultFileName),
});
