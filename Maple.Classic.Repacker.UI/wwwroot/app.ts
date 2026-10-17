interface Cm6EditorBridge {
    getValue(): string;
    setValue(content: string): void;
    focus(): void;
    refresh(): void;
    on(event: 'change', handler: () => void): void;
}

interface Window {
    MapleCM6?: {
        createEditor(hostElement: HTMLElement, options?: { initialValue?: string; onSave?: () => void }): Cm6EditorBridge;
    };
    chrome?: {
        webview?: {
            postMessage(message: unknown): void;
            addEventListener(type: 'message', listener: (ev: MessageEvent) => void): void;
        };
    };
    mapleHost?: MapleRepacker.ElectronHostApi;
}

namespace MapleRepacker {
    const TOAST_DURATION_MS = 3400;

    /**
     * Owns the shared selection/workspace state and wires the feature modules together:
     * tree, context menu, stage (canvas, sound, string, child table), inspector, workbench, search,
     * file operations, node operations and modals. Also binds the menu bar, toolbar and keyboard shortcuts.
     */
    export class RepackerStudioApp {
        archives: ArchiveSummaryDto[] = [];
        selectedArchiveId = '';
        selectedPath = '';
        currentInspection: NodeInspectionDto | null = null;
        backendSupportsNativeDialogs = false;
        private appliedInitialUiState = false;

        private readonly toastContainer = byId('toastContainer');
        private readonly archiveCountBadge = byId('archiveCountBadge');
        private readonly clipboardPill = byId('clipboardPill');
        private readonly undoBtn = byId<HTMLButtonElement>('btnUndo');
        private readonly redoBtn = byId<HTMLButtonElement>('btnRedo');

        readonly layout: LayoutController;
        readonly modal: ModalController;
        readonly tree: TreeView;
        readonly contextMenu: TreeContextMenu;
        readonly canvas: CanvasStage;
        readonly stage: StagePanel;
        readonly inspector: PropertyInspector;
        readonly workbench: Workbench;
        readonly search: SearchPanel;
        readonly files: FileOperations;
        readonly nodes: NodeOperations;

        constructor() {
            this.layout = new LayoutController({
                onLayoutChanged: (layoutDto) => {
                    void rpcCall(RpcMethod.UiSaveState, { uiState: { layout: layoutDto } });
                },
            });
            this.modal = new ModalController();
            // Operations first: the UI modules bind their buttons to them
            this.files = new FileOperations(this);
            this.nodes = new NodeOperations(this);
            this.tree = new TreeView(this);
            this.contextMenu = new TreeContextMenu(this);
            this.canvas = new CanvasStage(this);
            this.stage = new StagePanel(this);
            this.inspector = new PropertyInspector(this);
            this.workbench = new Workbench(this);
            this.search = new SearchPanel(this);

            this.bindMenuBar();
            this.bindKeyboardShortcuts();
            this.bindDesktopHost();

            onRpcStatus((status) => void this.handleWorkspaceStatus(status));
            ensureRpcSocket();
        }

        get selectedArchive(): ArchiveSummaryDto | undefined {
            return this.archives.find((a) => a.archiveId === this.selectedArchiveId);
        }

        showToast(message: string, kind: ToastKind = ToastKind.Info): void {
            const item = document.createElement('div');
            item.className = `toast-item toast-${kind}`;
            item.textContent = message;
            this.toastContainer.appendChild(item);
            setTimeout(() => item.remove(), TOAST_DURATION_MS);
        }

        /** Error toast with the server's message, or `fallback` when it didn't send one. */
        showRpcError(data: { error?: string | null } | null | undefined, fallback: string): void {
            this.showToast(data?.error || fallback, ToastKind.Error);
        }

        // ---------------------------------------------------------------------
        // Workspace status & selection
        // ---------------------------------------------------------------------

        async handleWorkspaceStatus(status: WorkspaceStatusPayloadDto): Promise<void> {
            this.archives = status.archives || [];
            this.backendSupportsNativeDialogs = Boolean(status.supportsNativeDialogs);

            if (!this.appliedInitialUiState && status.uiState) {
                this.appliedInitialUiState = true;
                const uiState = status.uiState;
                if (uiState.layout) {
                    this.layout.applySavedLayout(uiState.layout);
                }
                this.canvas.applySavedUiState(uiState);
                if (uiState.activeWorkbenchTab) {
                    this.workbench.activateTab(uiState.activeWorkbenchTab, false);
                }
            }

            this.updateUndoRedoButtons();
            this.archiveCountBadge.textContent = pluralize(this.archives.length, 'archive');
            this.search.renderArchiveScope(this.archives);
            this.renderClipboardPill(status.clipboard);
            this.workbench.renderLogs(status.logs || []);

            const nextArchiveId = status.selectedArchiveId || (this.archives[0]?.archiveId ?? '');
            const archiveChanged = nextArchiveId !== this.selectedArchiveId;
            this.selectedArchiveId = nextArchiveId;

            if (!this.selectedArchiveId) {
                this.tree.render();
                this.clearSelection();
                return;
            }

            await this.tree.ensureArchiveRoot(this.selectedArchiveId, archiveChanged);
            this.tree.render();
            if (!this.currentInspection || archiveChanged) {
                await this.selectAndInspectNode(this.selectedArchiveId, status.selectedNodePath ?? '', true);
            }
        }

        private renderClipboardPill(clip?: ClipboardStateDto | null): void {
            const hasData = Boolean(clip?.hasData);
            this.clipboardPill.classList.toggle('hidden', !hasData);
            if (clip && hasData) {
                this.clipboardPill.textContent = `Clipboard: ${clip.nodeName} (${clip.propertyKind})`;
            }
        }

        async selectAndInspectNode(archiveId: string, path: string, expandAncestors = true): Promise<void> {
            this.selectedArchiveId = archiveId;
            this.selectedPath = path;

            if (expandAncestors) {
                await this.tree.revealPath(archiveId, path);
            } else {
                this.tree.render();
            }

            // Success returns the inspection; failure returns the standard error response
            const res = await rpcCall<NodeInspectionDto | RpcOperationResponse>(RpcMethod.NodeInspect, { archiveId, path });
            if (!res.ok) {
                this.showRpcError(res.data as RpcOperationResponse, 'Failed to inspect node');
                return;
            }

            const inspection = res.data as NodeInspectionDto;
            this.currentInspection = inspection;
            this.updateUndoRedoButtons();
            await this.stage.render(inspection);
            this.inspector.render(inspection);
            this.workbench.render(inspection);
        }

        /**
         * After an edit: refreshes the tree branch around `path` in the selected archive, then selects and
         * re-inspects it. Defaults to the current selection.
         */
        async refreshSelection(path = this.selectedPath, expandAncestors = false): Promise<void> {
            const archiveId = this.selectedArchiveId;
            await this.tree.refreshBranch(archiveId, path);
            await this.selectAndInspectNode(archiveId, path, expandAncestors);
        }

        private clearSelection(): void {
            this.selectedPath = '';
            this.currentInspection = null;
            this.stage.clear();
            this.inspector.clear();
            this.workbench.clear();
            this.undoBtn.disabled = true;
            this.redoBtn.disabled = true;
        }

        /** Undo/redo is archive-wide, so the toolbar follows the selected archive's summary. */
        private updateUndoRedoButtons(): void {
            const archive = this.selectedArchive;
            this.undoBtn.disabled = !archive?.canUndo;
            this.redoBtn.disabled = !archive?.canRedo;
        }

        // ---------------------------------------------------------------------
        // Menu bar, toolbar, keyboard & desktop host
        // ---------------------------------------------------------------------

        private bindMenuBar(): void {
            const menuBar = byId('mainMenuBar');
            const items = Array.from(menuBar.querySelectorAll<HTMLElement>('.menu-item'));
            const closeMenus = () => items.forEach((item) => item.classList.remove('open'));

            for (const item of items) {
                item.querySelector('.menu-trigger')!.addEventListener('click', (ev) => {
                    ev.stopPropagation();
                    this.contextMenu.close();
                    const wasOpen = item.classList.contains('open');
                    closeMenus();
                    item.classList.toggle('open', !wasOpen);
                });
            }

            document.addEventListener('click', () => {
                closeMenus();
                this.contextMenu.close();
            });

            menuBar.querySelectorAll<HTMLElement>('[data-action]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    closeMenus();
                    this.dispatchMenuAction((btn.dataset.action || '') as MenuBarAction);
                });
            });

            this.undoBtn.addEventListener('click', () => void this.nodes.undo());
            this.redoBtn.addEventListener('click', () => void this.nodes.redo());
        }

        private dispatchMenuAction(action: MenuBarAction): void {
            const { files, nodes, canvas, layout } = this;
            switch (action) {
                case MenuBarAction.OpenFileModal:
                    files.triggerOpenWzFile();
                    break;
                case MenuBarAction.OpenDirModal:
                    files.triggerOpenDirectory();
                    break;
                case MenuBarAction.NewArchiveModal:
                    files.openNewArchiveModal();
                    break;
                case MenuBarAction.SaveArchiveDisk:
                    files.openSaveRepackModal();
                    break;
                case MenuBarAction.DownloadArchive:
                    void files.downloadSelectedArchive();
                    break;
                case MenuBarAction.OpenBulkExportModal:
                    files.openBulkExportModal();
                    break;
                case MenuBarAction.OpenInlineOutlinksModal:
                    files.openInlineOutlinksModal();
                    break;
                case MenuBarAction.UndoNode:
                    void nodes.undo();
                    break;
                case MenuBarAction.RedoNode:
                    void nodes.redo();
                    break;
                case MenuBarAction.AddPropertyModal:
                    nodes.openAddPropertyModal();
                    break;
                case MenuBarAction.AddImageModal:
                    nodes.openAddImageModal();
                    break;
                case MenuBarAction.AddDirectoryModal:
                    nodes.openAddDirectoryModal();
                    break;
                case MenuBarAction.CopyNode:
                    void nodes.copySelected();
                    break;
                case MenuBarAction.PasteNode:
                    void nodes.pasteIntoSelected();
                    break;
                case MenuBarAction.DuplicateNode:
                    void nodes.duplicateSelected();
                    break;
                case MenuBarAction.RenameNodePrompt:
                    nodes.openRenameModal();
                    break;
                case MenuBarAction.DeleteNode:
                    void nodes.deleteSelected();
                    break;
                case MenuBarAction.FocusGlobalSearch:
                    this.search.focus();
                    break;
                case MenuBarAction.ExportSelectedJson:
                    files.exportSelectedNode(RepackerExportFormat.Json);
                    break;
                case MenuBarAction.ExportSelectedImg:
                    files.exportSelectedNode(RepackerExportFormat.Img);
                    break;
                case MenuBarAction.ToggleCrosshair:
                    canvas.toggleOverlay(CanvasOverlay.Origin);
                    break;
                case MenuBarAction.ToggleVectors:
                    canvas.toggleOverlay(CanvasOverlay.Vectors);
                    break;
                case MenuBarAction.TogglePixelGrid:
                    canvas.toggleOverlay(CanvasOverlay.PixelGrid);
                    break;
                case MenuBarAction.ToggleInspector:
                    layout.toggleInspector();
                    canvas.redraw();
                    break;
                case MenuBarAction.ToggleWorkbench:
                    layout.toggleWorkbench();
                    canvas.redraw();
                    break;
                case MenuBarAction.ResetLayout:
                    layout.resetLayout();
                    this.stage.resetLayout();
                    canvas.redraw();
                    break;
            }
        }

        private bindKeyboardShortcuts(): void {
            window.addEventListener('keydown', (ev: KeyboardEvent) => {
                if (ev.key === 'Escape') {
                    this.contextMenu.close();
                    this.modal.dismiss();
                    return;
                }
                const target = ev.target as HTMLElement;
                const isTyping =
                    target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.closest('.cm-editor') !== null;
                const action = this.getShortcutAction(ev, isTyping);
                if (action) {
                    ev.preventDefault();
                    action();
                }
            });
        }

        private getShortcutAction(ev: KeyboardEvent, isTyping: boolean): (() => void) | null {
            const ctrl = ev.ctrlKey || ev.metaKey;
            const key = ev.key.toLowerCase();

            // Global shortcuts, also active while typing
            if (ctrl && key === 'f') return () => this.search.focus();
            if (ctrl && key === 'o') return () => this.files.triggerOpenWzFile();
            if (ctrl && ev.shiftKey && key === 's') return () => this.files.openSaveRepackModal();
            if (isTyping) return null;

            const { nodes } = this;
            if (ctrl) {
                switch (key) {
                    case 'z':
                        return () => void nodes.undo();
                    case 'y':
                        return () => void nodes.redo();
                    case 'c':
                        return () => void nodes.copySelected();
                    case 'v':
                        return () => void nodes.pasteIntoSelected();
                    case 'd':
                        return () => void nodes.duplicateSelected();
                }
            }
            switch (ev.key) {
                case 'F2':
                    return () => nodes.openRenameModal();
                case 'Delete':
                    return () => void nodes.deleteSelected();
                case 'Insert':
                    return () => nodes.openAddPropertyModal();
                default:
                    return null;
            }
        }

        private bindDesktopHost(): void {
            desktopHost.onMessage((data) => {
                if (data.type === HostWebMessageType.TreeContextMenuCommand) {
                    void this.contextMenu.execute(
                        String(data.cmd || '') as TreeContextMenuCommand,
                        String(data.archiveId || ''),
                        String(data.path || '')
                    );
                } else if (data.type === HostWebMessageType.RequestAppClose) {
                    this.files.handleAppCloseRequest();
                }
            });

            // Desktop shells route window close through RequestAppClose; only a plain browser tab needs beforeunload.
            window.addEventListener('beforeunload', (ev: BeforeUnloadEvent) => {
                if (!desktopHost.isDesktop && this.files.getDirtyArchives().length > 0) {
                    ev.preventDefault();
                    ev.returnValue = '';
                }
            });
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        new RepackerStudioApp();
    });
}
