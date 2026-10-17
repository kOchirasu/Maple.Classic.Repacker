import { BrowserWindow, Menu, MenuItemConstructorOptions } from 'electron';
import { HostWebMessage, HostWebMessageType, IpcChannel, TreeContextMenuCommand } from './protocol';

interface MenuEntry {
    label: string;
    command: TreeContextMenuCommand;
    accelerator?: string;
    /** Hidden when the target is the archive root or a deleted node, mirroring WindowsDesktopShell. */
    when?: (ctx: MenuContext) => boolean;
}

interface MenuContext {
    isArchiveRoot: boolean;
    isDeleted: boolean;
}

const editable = (ctx: MenuContext): boolean => !ctx.isArchiveRoot && !ctx.isDeleted;

const MENU_LAYOUT: Array<MenuEntry | 'separator'> = [
    { label: 'Inspect Node', command: TreeContextMenuCommand.Inspect },
    'separator',
    { label: '+ Add Child Property...', command: TreeContextMenuCommand.AddProp, when: editable },
    { label: '+ Add .img Image...', command: TreeContextMenuCommand.AddImg },
    { label: '+ Add Directory...', command: TreeContextMenuCommand.AddDir },
    'separator',
    { label: 'Copy', command: TreeContextMenuCommand.Copy, accelerator: 'CmdOrCtrl+C', when: editable },
    { label: 'Paste', command: TreeContextMenuCommand.Paste, accelerator: 'CmdOrCtrl+V' },
    { label: 'Duplicate', command: TreeContextMenuCommand.Duplicate, accelerator: 'CmdOrCtrl+D', when: editable },
    { label: 'Rename', command: TreeContextMenuCommand.Rename, accelerator: 'F2', when: editable },
    'separator',
    { label: 'Export Node...', command: TreeContextMenuCommand.ExportAuto },
    'separator',
    { label: 'Restore Node', command: TreeContextMenuCommand.Restore, when: (c) => !c.isArchiveRoot && c.isDeleted },
    { label: 'Delete Node', command: TreeContextMenuCommand.Delete, accelerator: 'Delete', when: editable },
    { label: 'Close Archive', command: TreeContextMenuCommand.CloseArchive, accelerator: 'Delete', when: (c) => c.isArchiveRoot },
];

/** Native tree context menu, equivalent to the one in WindowsDesktopShell.cs. */
export class TreeContextMenu {
    private openMenu: Menu | null = null;

    constructor(private readonly window: BrowserWindow) {}

    show(message: HostWebMessage): void {
        const archiveId = String(message.archiveId ?? '');
        const path = String(message.path ?? '');
        const ctx: MenuContext = {
            isArchiveRoot: path.length === 0,
            isDeleted: message.isDeleted === true || message.changeState === 'Deleted',
        };

        const template: MenuItemConstructorOptions[] = MENU_LAYOUT.map((entry) =>
            entry === 'separator'
                ? { type: 'separator' }
                : {
                      label: entry.command === TreeContextMenuCommand.ExportAuto && ctx.isArchiveRoot ? 'Export .wz Archive...' : entry.label,
                      accelerator: entry.accelerator,
                      registerAccelerator: false,
                      visible: entry.when ? entry.when(ctx) : true,
                      click: () => this.sendCommand(entry.command, archiveId, path),
                  }
        );

        this.openMenu = Menu.buildFromTemplate(template);
        this.openMenu.popup({ window: this.window, callback: () => (this.openMenu = null) });
    }

    close(): void {
        this.openMenu?.closePopup(this.window);
        this.openMenu = null;
    }

    private sendCommand(command: TreeContextMenuCommand, archiveId: string, path: string): void {
        const reply: HostWebMessage = { type: HostWebMessageType.TreeContextMenuCommand, cmd: command, archiveId, path };
        this.window.webContents.send(IpcChannel.HostMessage, reply);
    }
}
