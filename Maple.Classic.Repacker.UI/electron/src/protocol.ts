/**
 * Message protocol shared with the web front end (`HostWebMessageType` / `TreeContextMenuCommand` in wwwroot/rpc.ts)
 * and the Windows WebView2 shell (Rpc/HostWebMessages.cs). Keep the string values in sync.
 */
export enum HostWebMessageType {
    OpenTreeContextMenu = 'openTreeContextMenu',
    CloseTreeContextMenu = 'closeTreeContextMenu',
    TreeContextMenuCommand = 'treeContextMenuCommand',
    RequestAppClose = 'requestAppClose',
    ConfirmAppClose = 'confirmAppClose',
}

export enum TreeContextMenuCommand {
    Inspect = 'inspect',
    AddProp = 'addProp',
    AddImg = 'addImg',
    AddDir = 'addDir',
    Copy = 'copy',
    Paste = 'paste',
    Duplicate = 'duplicate',
    Rename = 'rename',
    ExportAuto = 'exportAuto',
    Restore = 'restore',
    Delete = 'delete',
    CloseArchive = 'closeArchive',
}

export interface HostWebMessage {
    type: HostWebMessageType;
    [key: string]: unknown;
}

/** IPC channel names between the preload script and the main process. */
export enum IpcChannel {
    /** Renderer -> main (send) and main -> renderer (webContents.send): HostWebMessage payloads. */
    HostMessage = 'maple-host:message',
    PickOpenWzFiles = 'maple-host:pick-open-wz-files',
    PickDirectory = 'maple-host:pick-directory',
    PickSaveWzFile = 'maple-host:pick-save-wz-file',
}
