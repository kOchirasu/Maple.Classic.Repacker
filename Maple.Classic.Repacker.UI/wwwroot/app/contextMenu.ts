namespace MapleRepacker {
    /** Right-click menu for tree rows and child-table rows; desktop shells render it natively. */
    export class TreeContextMenu {
        private readonly menu = byId('treeContextMenu');
        private readonly exportBtn = byId('ctxExportBtn');
        private readonly restoreBtn = byId('ctxRestoreBtn');
        private readonly deleteBtn = byId('ctxDeleteBtn');
        private readonly closeArchiveBtn = byId('ctxCloseArchiveBtn');
        /** Menu items that only apply to a live (non-root, non-deleted) node. */
        private readonly nodeOnlyItems: HTMLElement[];
        private targetArchiveId = '';
        private targetPath = '';

        constructor(private readonly app: RepackerStudioApp) {
            this.nodeOnlyItems = [
                TreeContextMenuCommand.AddProp,
                TreeContextMenuCommand.Copy,
                TreeContextMenuCommand.Duplicate,
                TreeContextMenuCommand.Rename,
            ].map((cmd) => this.menu.querySelector<HTMLElement>(`[data-ctx="${cmd}"]`)!);

            this.menu.querySelectorAll<HTMLElement>('[data-ctx]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const cmd = (btn.dataset.ctx || '') as TreeContextMenuCommand;
                    const archiveId = this.targetArchiveId;
                    const path = this.targetPath;
                    this.close();
                    void this.execute(cmd, archiveId, path);
                });
            });

            document.addEventListener('contextmenu', (ev) => {
                const target = ev.target as HTMLElement | null;
                if (!target?.closest('.tree-row') && !target?.closest('#childrenTableBody tr') && !target?.closest('#treeContextMenu')) {
                    this.close();
                }
            });
        }

        close(): void {
            this.menu.classList.add('hidden');
            if (desktopHost.isDesktop) {
                desktopHost.postMessage({ type: HostWebMessageType.CloseTreeContextMenu });
            }
        }

        open(x: number, y: number, archiveId: string, path: string, changeState: RepackerChangeState = RepackerChangeState.Unchanged): void {
            this.targetArchiveId = archiveId;
            this.targetPath = path;
            const isDeleted = changeState === RepackerChangeState.Deleted;

            if (desktopHost.isDesktop) {
                desktopHost.postMessage({
                    type: HostWebMessageType.OpenTreeContextMenu,
                    archiveId,
                    path,
                    changeState,
                    isDeleted,
                });
                return;
            }

            const isArchiveRoot = !path;
            for (const item of this.nodeOnlyItems) {
                item.classList.toggle('hidden', isArchiveRoot || isDeleted);
            }
            this.exportBtn.textContent = isArchiveRoot ? 'Export .wz Archive...' : 'Export Node...';
            this.restoreBtn.classList.toggle('hidden', isArchiveRoot || !isDeleted);
            this.deleteBtn.classList.toggle('hidden', isArchiveRoot || isDeleted);
            this.closeArchiveBtn.classList.toggle('hidden', !isArchiveRoot);

            this.menu.classList.remove('hidden');
            const rect = this.menu.getBoundingClientRect();
            const margin = 8;
            const menuW = rect.width || 220;
            const menuH = rect.height || 360;

            let left = x;
            if (left + menuW + margin > window.innerWidth) {
                left = x - menuW >= margin ? x - menuW : window.innerWidth - menuW - margin;
            }
            left = Math.max(margin, left);

            let top = y;
            if (top + menuH + margin > window.innerHeight) {
                top = y - menuH >= margin ? y - menuH : window.innerHeight - menuH - margin;
            }
            top = Math.max(margin, top);

            this.menu.style.left = `${Math.round(left)}px`;
            this.menu.style.top = `${Math.round(top)}px`;
        }

        /** Runs a menu command against its target node (also used for commands from the native desktop menu). */
        async execute(cmd: TreeContextMenuCommand, archiveId: string, path: string): Promise<void> {
            const { nodes, files } = this.app;
            if (cmd === TreeContextMenuCommand.CloseArchive) {
                if (archiveId) {
                    await files.requestCloseArchive(archiveId);
                }
                return;
            }

            if (archiveId) {
                await this.app.selectAndInspectNode(archiveId, path, false);
            }
            switch (cmd) {
                case TreeContextMenuCommand.AddProp:
                    nodes.openAddPropertyModal();
                    break;
                case TreeContextMenuCommand.AddImg:
                    nodes.openAddImageModal();
                    break;
                case TreeContextMenuCommand.AddDir:
                    nodes.openAddDirectoryModal();
                    break;
                case TreeContextMenuCommand.Copy:
                    await nodes.copySelected();
                    break;
                case TreeContextMenuCommand.Paste:
                    await nodes.pasteIntoSelected();
                    break;
                case TreeContextMenuCommand.Duplicate:
                    await nodes.duplicateSelected();
                    break;
                case TreeContextMenuCommand.Rename:
                    nodes.openRenameModal();
                    break;
                case TreeContextMenuCommand.ExportAuto:
                    if (!path) {
                        await files.downloadSelectedArchive();
                    } else {
                        files.exportSelectedNode(RepackerExportFormat.Auto);
                    }
                    break;
                case TreeContextMenuCommand.Delete:
                    await nodes.deleteSelected();
                    break;
                case TreeContextMenuCommand.Restore:
                    await nodes.restoreSelected();
                    break;
            }
        }
    }
}
