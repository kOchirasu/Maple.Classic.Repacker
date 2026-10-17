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
}

namespace MapleRepacker {
    interface TreeCacheMap {
        // key: `${archiveId}::${parentPath}`
        childrenByParent: Map<string, TreeNodeDto[]>;
        expandedKeys: Set<string>;
    }

    interface DecodedSpriteFrame {
        img: HTMLImageElement;
        url: string;
        width: number;
        height: number;
        origin: VectorPointDto;
        lt?: VectorPointDto | null;
        rb?: VectorPointDto | null;
        head?: VectorPointDto | null;
        delayMs: number;
        alphaStart: number;
        alphaEnd: number;
        frameName: string;
        framePath: string;
    }

    export class RepackerStudioApp {
        private readonly layout: LayoutController;
        private readonly treeState: TreeCacheMap = {
            childrenByParent: new Map<string, TreeNodeDto[]>(),
            expandedKeys: new Set<string>(),
        };

        private archives: ArchiveSummaryDto[] = [];
        private selectedArchiveId = '';
        private selectedPath = '';
        private currentInspection: NodeInspectionDto | null = null;
        private recentPaths: string[] = [];
        private supportsNativeDialogs = false;
        private treeFilterText = '';
        private appliedInitialUiState = false;

        // CodeMirror 6 JSON Editor
        private jsonEditor: Cm6EditorBridge | null = null;
        private lastLoadedJsonText = '';

        // Canvas & Animation Viewport State
        private readonly spriteCanvas: HTMLCanvasElement;
        private readonly spriteCtx: CanvasRenderingContext2D;
        private decodedFrames: DecodedSpriteFrame[] = [];
        private activeFrameIdx = 0;
        private animPlaying = false;
        private animTimer: ReturnType<typeof setTimeout> | null = null;
        private zoomScale = 2.0;
        private panX = 0;
        private panY = 0;
        private isDraggingCanvas = false;
        private dragStartX = 0;
        private dragStartY = 0;
        private showOrigin = true;
        private showVectors = true;
        private showPixelGrid = false;

        // Sound Player Object URL
        private activeAudioUrl: string | null = null;

        // Children Table Minimize & Resize State
        private childrenTableMinimized = false;
        private childrenTableHeight = 185;
        private lastInspectedLayerKey = '';

        // Hex Inspector State
        private activeHexBytes: Uint8Array = new Uint8Array(0);
        private selectedHexOffset = 0;

        // Context Menu Target
        private ctxTargetArchiveId = '';
        private ctxTargetPath = '';
        private modalCancelCallback: (() => void) | null = null;
        private lastBulkExportDir = '';
        private lastBulkExportMode: RepackerBatchExportMode = RepackerBatchExportMode.Png;

        constructor() {
            this.spriteCanvas = document.getElementById('spriteCanvas') as HTMLCanvasElement;
            this.spriteCtx = this.spriteCanvas.getContext('2d')!;

            this.layout = new LayoutController({
                onLayoutChanged: (layoutDto) => {
                    void rpcCall(RpcMethod.UiSaveState, { uiState: { layout: layoutDto } });
                },
            });

            this.initJsonEditor();
            this.bindMenuAndToolbar();
            this.bindTreeEvents();
            this.bindCanvasViewport();
            this.bindInspectorEvents();
            this.bindWorkbenchEvents();
            this.bindKeyboardShortcuts();

            onRpcStatus((status) => void this.handleWorkspaceStatus(status));
            ensureRpcSocket();
        }

        private initJsonEditor(): void {
            const host = document.getElementById('jsonEditorHost');
            if (!host || !window.MapleCM6) return;
            this.jsonEditor = window.MapleCM6.createEditor(host, {
                initialValue: '{\n  "//": "Select an .img or property node to inspect & edit JSON"\n}',
                onSave: () => void this.applyJsonFromEditor(),
            });
        }

        private showToast(message: string, kind: ToastKind = ToastKind.Info): void {
            const container = document.getElementById('toastContainer');
            if (!container) return;
            const item = document.createElement('div');
            item.className = `toast-item toast-${kind}`;
            item.textContent = message;
            container.appendChild(item);
            setTimeout(() => {
                item.remove();
            }, 3400);
        }

        private async handleWorkspaceStatus(status: WorkspaceStatusPayloadDto): Promise<void> {
            this.archives = status.archives || [];
            this.recentPaths = status.recentPaths || [];
            this.supportsNativeDialogs = Boolean(status.supportsNativeDialogs);

            if (!this.appliedInitialUiState && status.uiState) {
                this.appliedInitialUiState = true;
                if (status.uiState.layout) {
                    this.layout.applySavedLayout(status.uiState.layout);
                }
                if (status.uiState.canvasBackground) {
                    const bg = status.uiState.canvasBackground;
                    document.querySelectorAll('[data-bg]').forEach((b) => {
                        b.classList.toggle('active', (b as HTMLElement).dataset.bg === bg);
                    });
                    const viewport = document.getElementById('canvasViewport');
                    if (viewport) {
                        viewport.className = `canvas-viewport bg-${bg.toLowerCase()}`;
                    }
                }
                if (status.uiState.activeWorkbenchTab) {
                    this.activateWorkbenchTab(status.uiState.activeWorkbenchTab, false);
                }
                if (typeof status.uiState.showOriginCrosshair === 'boolean') {
                    this.showOrigin = status.uiState.showOriginCrosshair;
                    (document.getElementById('chkShowOrigin') as HTMLInputElement).checked = this.showOrigin;
                }
                if (typeof status.uiState.showVectorBounds === 'boolean') {
                    this.showVectors = status.uiState.showVectorBounds;
                    (document.getElementById('chkShowVectors') as HTMLInputElement).checked = this.showVectors;
                }
                if (typeof status.uiState.showPixelGrid === 'boolean') {
                    this.showPixelGrid = status.uiState.showPixelGrid;
                    (document.getElementById('chkShowPixelGrid') as HTMLInputElement).checked = this.showPixelGrid;
                }
            }

            // Update archive count & search dropdown
            const countBadge = document.getElementById('archiveCountBadge')!;
            countBadge.textContent = `${this.archives.length} archive${this.archives.length === 1 ? '' : 's'}`;
            this.renderSearchArchiveScope();
            this.renderClipboardPill(status.clipboard);
            this.renderConsoleLogs(status.logs || []);

            const nextArchiveId = status.selectedArchiveId || (this.archives[0]?.archiveId ?? '');
            const archiveChanged = nextArchiveId !== this.selectedArchiveId;
            this.selectedArchiveId = nextArchiveId;

            if (this.selectedArchiveId) {
                // Ensure root children of active archive are loaded
                const rootKey = this.makeCacheKey(this.selectedArchiveId, '');
                if (!this.treeState.childrenByParent.has(rootKey)) {
                    this.treeState.expandedKeys.add(rootKey);
                    await this.fetchTreeChildren(this.selectedArchiveId, '');
                } else if (archiveChanged) {
                    await this.fetchTreeChildren(this.selectedArchiveId, '');
                }
                this.renderTree();

                if (!this.currentInspection || archiveChanged) {
                    const initialPath = status.selectedNodePath ?? '';
                    await this.selectAndInspectNode(this.selectedArchiveId, initialPath, true);
                }
            } else {
                this.renderTree();
                this.clearStageAndInspector();
            }
        }

        private clearStageAndInspector(): void {
            this.selectedPath = '';
            this.currentInspection = null;
            this.stopAnimationLoop();
            this.revokeDecodedFrames();

            document.getElementById('canvasStageSection')?.classList.add('hidden');
            document.getElementById('soundStageSection')?.classList.add('hidden');
            document.getElementById('stringStageSection')?.classList.add('hidden');
            this.childrenTableMinimized = false;
            this.lastInspectedLayerKey = '';
            this.applyChildrenTableMinimizedDom();
            document.getElementById('breadcrumbBar')!.innerHTML = '';
            document.getElementById('stageMetaBadges')!.innerHTML = '';
            document.getElementById('childrenTableBody')!.innerHTML = '';
            document.getElementById('inspectorTypePill')!.textContent = 'NONE';
            document.getElementById('inspectorMetaGrid')!.innerHTML = '';
            document.getElementById('valueEditorCard')?.classList.add('hidden');
            (document.getElementById('btnUndo') as HTMLButtonElement).disabled = true;
            (document.getElementById('btnRedo') as HTMLButtonElement).disabled = true;

            this.lastLoadedJsonText = '{}';
            if (this.jsonEditor) {
                this.jsonEditor.setValue('{}');
            }
            this.activeHexBytes = new Uint8Array(0);
            this.selectedHexOffset = 0;
            this.renderHexInspector();
        }

        private renderSearchArchiveScope(): void {
            const sel = document.getElementById('searchArchiveScope') as HTMLSelectElement;
            const prev = sel.value;
            sel.innerHTML = '<option value="">All Open WZ Archives</option>';
            for (const ar of this.archives) {
                const opt = document.createElement('option');
                opt.value = ar.archiveId;
                opt.textContent = `${ar.name} (v${ar.version})`;
                sel.appendChild(opt);
            }
            sel.value = prev;
        }

        private renderClipboardPill(clip?: ClipboardStateDto | null): void {
            const pill = document.getElementById('clipboardPill')!;
            if (!clip || !clip.hasData) {
                pill.classList.add('hidden');
                return;
            }
            pill.classList.remove('hidden');
            pill.textContent = `Clipboard: ${clip.nodeName} (${clip.propertyKind})`;
        }

        private renderConsoleLogs(logs: RepackerLogEntry[]): void {
            const pill = document.getElementById('logCountPill')!;
            pill.textContent = String(logs.length);
            const container = document.getElementById('logsContainer')!;
            container.innerHTML = '';
            for (let i = logs.length - 1; i >= 0; i--) {
                const entry = logs[i];
                const row = document.createElement('div');
                row.className = 'log-row';
                row.innerHTML = `
                    <span class="log-ts">[${this.escapeHtml(entry.timestamp)}]</span>
                    <span class="log-lvl lvl-${this.escapeHtml(entry.level)}">${this.escapeHtml(entry.level.toUpperCase())}</span>
                    <span class="log-msg">${this.escapeHtml(entry.message)}</span>
                `;
                container.appendChild(row);
            }
        }

        // ---------------------------------------------------------------------
        // Lazy Tree Explorer
        // ---------------------------------------------------------------------

        private makeCacheKey(archiveId: string, parentPath: string): string {
            return `${archiveId}::${parentPath}`;
        }

        private async fetchTreeChildren(archiveId: string, parentPath: string): Promise<TreeNodeDto[]> {
            const res = await rpcCall<TreeChildrenResponse>(RpcMethod.TreeChildren, {
                archiveId,
                path: parentPath,
            });
            const nodes = res.ok && res.data?.nodes ? res.data.nodes : [];
            this.treeState.childrenByParent.set(this.makeCacheKey(archiveId, parentPath), nodes);
            return nodes;
        }

        private async invalidateAndRefreshTreeBranch(archiveId: string, path: string): Promise<void> {
            // Refresh root + ancestor chain along path + any expanded descendants
            await this.fetchTreeChildren(archiveId, '');
            const parts = path.split('/').filter((p) => p.length > 0);
            let accum = '';
            for (let i = 0; i < parts.length; i++) {
                const part = parts[i];
                accum = accum ? `${accum}/${part}` : part;
                const key = this.makeCacheKey(archiveId, accum);
                const isAncestor = i < parts.length - 1;
                if (isAncestor || this.treeState.expandedKeys.has(key) || accum === path) {
                    await this.fetchTreeChildren(archiveId, accum);
                }
            }
            if (path) {
                const descPrefix = `${archiveId}::${path}/`;
                for (const expKey of Array.from(this.treeState.expandedKeys)) {
                    if (expKey.startsWith(descPrefix)) {
                        const subPath = expKey.slice(`${archiveId}::`.length);
                        await this.fetchTreeChildren(archiveId, subPath);
                    }
                }
            }
            this.renderTree();
        }

        private async ensurePathExpanded(archiveId: string, targetPath: string): Promise<void> {
            const rootKey = this.makeCacheKey(archiveId, '');
            this.treeState.expandedKeys.add(rootKey);
            if (!this.treeState.childrenByParent.has(rootKey)) {
                await this.fetchTreeChildren(archiveId, '');
            }

            if (targetPath) {
                const parts = targetPath.split('/').filter((p) => p.length > 0);
                let accum = '';
                for (let i = 0; i < parts.length - 1; i++) {
                    accum = accum ? `${accum}/${parts[i]}` : parts[i];
                    const key = this.makeCacheKey(archiveId, accum);
                    this.treeState.expandedKeys.add(key);
                    if (!this.treeState.childrenByParent.has(key)) {
                        await this.fetchTreeChildren(archiveId, accum);
                    }
                }
            }

            this.renderTree();

            if (this.treeFilterText.trim().length > 0) {
                const container = document.getElementById('treeContainer');
                if (container && !container.querySelector('.tree-row.selected')) {
                    this.treeFilterText = '';
                    const filterInput = document.getElementById('treeFilterInput') as HTMLInputElement | null;
                    if (filterInput) {
                        filterInput.value = '';
                    }
                    this.renderTree();
                }
            }

            this.scrollSelectedTreeRowIntoView();
        }

        private scrollSelectedTreeRowIntoView(): void {
            const container = document.getElementById('treeContainer');
            if (!container) return;
            const selectedRow = container.querySelector('.tree-row.selected') as HTMLElement | null;
            if (!selectedRow) return;

            const containerRect = container.getBoundingClientRect();
            const rowRect = selectedRow.getBoundingClientRect();
            if (containerRect.height <= 0) return;

            if (rowRect.top < containerRect.top + 24 || rowRect.bottom > containerRect.bottom - 24) {
                const rowOffsetInContainer = rowRect.top - containerRect.top + container.scrollTop;
                const targetScrollTop = rowOffsetInContainer - containerRect.height / 2 + rowRect.height / 2;
                container.scrollTop = Math.max(0, Math.round(targetScrollTop));
            }
        }

        private renderTree(): void {
            const container = document.getElementById('treeContainer')!;
            container.innerHTML = '';

            if (this.archives.length === 0) {
                const empty = document.createElement('div');
                empty.style.padding = '16px 12px';
                empty.style.color = 'var(--text-muted)';
                empty.textContent = 'No WZ archives open. Use the File menu (Ctrl+O) to open a .wz archive or data directory.';
                container.appendChild(empty);
                return;
            }

            const filterLower = this.treeFilterText.trim().toLowerCase();

            for (const ar of this.archives) {
                const rootKey = this.makeCacheKey(ar.archiveId, '');
                const isRootExpanded = this.treeState.expandedKeys.has(rootKey);

                // Render Archive Root Row
                const rootRow = document.createElement('div');
                const isRootSelected = ar.archiveId === this.selectedArchiveId && this.selectedPath === '';
                rootRow.className = `tree-row${isRootSelected ? ' selected' : ''}`;
                rootRow.style.paddingLeft = '6px';
                rootRow.dataset.archiveId = ar.archiveId;
                rootRow.dataset.path = '';

                const rootStatusDot =
                    ar.changeState === RepackerChangeState.Added
                        ? '<span class="tree-add-dot" title="Added"></span>'
                        : ar.changeState === RepackerChangeState.Modified
                          ? '<span class="tree-mod-dot" title="Modified"></span>'
                          : '';

                rootRow.innerHTML = `
                    <span class="tree-twisty">${isRootExpanded ? '&#9662;' : '&#9656;'}</span>
                    <span class="tree-badge badge-WZ">WZ</span>
                    <span class="tree-name" title="${this.escapeHtml(ar.name)}">${this.escapeHtml(ar.name)}</span>
                    <span class="tree-val-preview" title="v${ar.version} • ${ar.imageCount} img">v${ar.version} &bull; ${ar.imageCount} img</span>
                    ${rootStatusDot}
                `;

                rootRow.addEventListener('click', (ev) => {
                    this.closeContextMenu();
                    const clickedTwisty = (ev.target as HTMLElement).classList.contains('tree-twisty');
                    void this.toggleOrSelectTreeNode(ar.archiveId, '', true, clickedTwisty);
                });
                rootRow.addEventListener('dblclick', () => {
                    this.closeContextMenu();
                    void this.toggleNodeExpansionOnly(ar.archiveId, '');
                });
                rootRow.addEventListener('contextmenu', (ev) => {
                    ev.preventDefault();
                    ev.stopPropagation();
                    this.openContextMenu(ev.clientX, ev.clientY, ar.archiveId, '', ar.changeState);
                });

                container.appendChild(rootRow);

                if (isRootExpanded) {
                    const rootChildren = this.treeState.childrenByParent.get(rootKey) || [];
                    this.renderTreeNodesRecursive(container, ar.archiveId, rootChildren, 1, filterLower);
                }
            }
        }

        private renderTreeNodesRecursive(
            container: HTMLElement,
            archiveId: string,
            nodes: TreeNodeDto[],
            depth: number,
            filterLower: string
        ): void {
            for (const node of nodes) {
                const nodeKey = this.makeCacheKey(archiveId, node.path);
                const isExpanded = this.treeState.expandedKeys.has(nodeKey);
                const cachedKids = this.treeState.childrenByParent.get(nodeKey) || [];

                if (filterLower) {
                    const selfMatches = this.nodeMatchesTreeFilter(node, filterLower);
                    const descendantMatches = this.hasMatchingDescendant(archiveId, cachedKids, filterLower);
                    if (!selfMatches && !descendantMatches) {
                        continue;
                    }
                }

                const isSelected = archiveId === this.selectedArchiveId && node.path === this.selectedPath;
                const isNodeDeleted = node.changeState === RepackerChangeState.Deleted;
                const row = document.createElement('div');
                row.className = `tree-row${isSelected ? ' selected' : ''}${isNodeDeleted ? ' deleted' : ''}`;
                row.style.paddingLeft = `${6 + depth * 15}px`;
                row.dataset.archiveId = archiveId;
                row.dataset.path = node.path;

                const canExpand = node.hasChildren || node.nodeKind === RepackerNodeKind.Directory;
                const twistyChar = node.hasChildren ? (isExpanded ? '&#9662;' : '&#9656;') : '';
                const valPreview = node.displayValue
                    ? `<span class="tree-val-preview" title="${this.escapeHtml(node.displayValue)}">${this.escapeHtml(node.displayValue)}</span>`
                    : '';
                const statusDot =
                    node.changeState === RepackerChangeState.Deleted
                        ? '<span class="tree-del-dot" title="Deleted"></span>'
                        : node.changeState === RepackerChangeState.Added
                          ? '<span class="tree-add-dot" title="Added"></span>'
                          : node.changeState === RepackerChangeState.Modified
                            ? '<span class="tree-mod-dot" title="Modified"></span>'
                            : '';

                row.innerHTML = `
                    <span class="tree-twisty">${twistyChar}</span>
                    <span class="tree-badge badge-${this.escapeHtml(node.typeBadge)}">${this.escapeHtml(node.typeBadge)}</span>
                    <span class="tree-name" title="${this.escapeHtml(node.name)}">${this.escapeHtml(node.name)}</span>
                    ${valPreview}
                    ${statusDot}
                `;

                row.addEventListener('click', (ev) => {
                    this.closeContextMenu();
                    const clickedTwisty = (ev.target as HTMLElement).classList.contains('tree-twisty');
                    void this.toggleOrSelectTreeNode(archiveId, node.path, canExpand, clickedTwisty);
                });

                row.addEventListener('dblclick', () => {
                    this.closeContextMenu();
                    if (canExpand) {
                        void this.toggleNodeExpansionOnly(archiveId, node.path);
                    }
                });

                row.addEventListener('contextmenu', (ev) => {
                    ev.preventDefault();
                    ev.stopPropagation();
                    this.openContextMenu(ev.clientX, ev.clientY, archiveId, node.path, node.changeState);
                });

                container.appendChild(row);

                if (isExpanded && cachedKids.length > 0) {
                    this.renderTreeNodesRecursive(container, archiveId, cachedKids, depth + 1, filterLower);
                }
            }
        }

        private getFilterableNodeValue(node: TreeNodeDto): string {
            if (node.nodeKind !== RepackerNodeKind.Property) {
                return '';
            }
            switch (node.propertyKind) {
                case RepackerPropertyKind.Short:
                case RepackerPropertyKind.Int:
                case RepackerPropertyKind.Long:
                case RepackerPropertyKind.Float:
                case RepackerPropertyKind.Double:
                case RepackerPropertyKind.String:
                case RepackerPropertyKind.Vector:
                    return node.displayValue || '';
                case RepackerPropertyKind.Uol:
                    return (node.displayValue || '').replace(/^->\s*/, '');
                default:
                    return '';
            }
        }

        private nodeMatchesTreeFilter(node: TreeNodeDto, filterLower: string): boolean {
            if (node.name.toLowerCase().includes(filterLower)) {
                return true;
            }
            if (filterLower.includes('/') && node.path.toLowerCase().includes(filterLower)) {
                return true;
            }
            const val = this.getFilterableNodeValue(node);
            return val.length > 0 && val.toLowerCase().includes(filterLower);
        }

        private hasMatchingDescendant(archiveId: string, nodes: TreeNodeDto[], filterLower: string): boolean {
            for (const n of nodes) {
                if (this.nodeMatchesTreeFilter(n, filterLower)) {
                    return true;
                }
                const kids = this.treeState.childrenByParent.get(this.makeCacheKey(archiveId, n.path));
                if (kids && this.hasMatchingDescendant(archiveId, kids, filterLower)) {
                    return true;
                }
            }
            return false;
        }

        private async toggleNodeExpansionOnly(archiveId: string, path: string): Promise<void> {
            const key = this.makeCacheKey(archiveId, path);
            if (this.treeState.expandedKeys.has(key)) {
                this.treeState.expandedKeys.delete(key);
            } else {
                this.treeState.expandedKeys.add(key);
                if (!this.treeState.childrenByParent.has(key)) {
                    await this.fetchTreeChildren(archiveId, path);
                }
            }
            this.renderTree();
        }

        private async toggleOrSelectTreeNode(
            archiveId: string,
            path: string,
            hasChildren: boolean,
            clickedTwisty = false
        ): Promise<void> {
            const key = this.makeCacheKey(archiveId, path);
            const alreadySelected = this.selectedArchiveId === archiveId && this.selectedPath === path;

            if (hasChildren) {
                if (clickedTwisty || alreadySelected) {
                    if (this.treeState.expandedKeys.has(key)) {
                        this.treeState.expandedKeys.delete(key);
                    } else {
                        this.treeState.expandedKeys.add(key);
                        if (!this.treeState.childrenByParent.has(key)) {
                            await this.fetchTreeChildren(archiveId, path);
                        }
                    }
                } else if (!this.treeState.expandedKeys.has(key)) {
                    this.treeState.expandedKeys.add(key);
                    if (!this.treeState.childrenByParent.has(key)) {
                        await this.fetchTreeChildren(archiveId, path);
                    }
                }
            }

            await this.selectAndInspectNode(archiveId, path, false);
        }

        public async selectAndInspectNode(
            archiveId: string,
            path: string,
            expandAncestors = true
        ): Promise<void> {
            this.selectedArchiveId = archiveId;
            this.selectedPath = path;

            if (expandAncestors) {
                await this.ensurePathExpanded(archiveId, path);
            } else {
                this.renderTree();
            }

            const res = await rpcCall<NodeInspectionDto>(RpcMethod.NodeInspect, {
                archiveId,
                path,
            });
            if (!res.ok || !res.data) {
                this.showToast(res.data?.error || 'Failed to inspect node', ToastKind.Error);
                return;
            }

            this.currentInspection = res.data;
            await this.renderInspection(res.data);
        }

        // ---------------------------------------------------------------------
        // Stage & Inspector Rendering
        // ---------------------------------------------------------------------

        private async renderInspection(dto: NodeInspectionDto): Promise<void> {
            // Update Undo / Redo toolbar buttons
            (document.getElementById('btnUndo') as HTMLButtonElement).disabled = !dto.canUndo;
            (document.getElementById('btnRedo') as HTMLButtonElement).disabled = !dto.canRedo;

            // 1. Breadcrumbs & Meta Chips
            this.renderBreadcrumbs(dto);

            // Sync Child Nodes Table minimize & height state before measuring Canvas viewport
            this.syncChildrenTableMinimizedState(dto);

            // 2. Sound_DX8 Audio Studio
            this.renderSoundStage(dto);

            // 3. String Stage Preview
            this.renderStringStage(dto);

            // 4. Canvas / Animation Viewport
            await this.renderCanvasOrAnimationStage(dto);

            // 5. Child Nodes Table
            this.renderChildrenTable(dto);

            // 5. Right Property Inspector
            this.renderPropertyInspector(dto);

            // 6. CodeMirror 6 JSON Subtree Editor
            this.lastLoadedJsonText = dto.jsonPreview || '{}';
            if (this.jsonEditor) {
                this.jsonEditor.setValue(this.lastLoadedJsonText);
            }

            // 7. Hex & Binary Inspector
            this.activeHexBytes = toUint8Array(dto.rawBinarySlice);
            this.selectedHexOffset = 0;
            this.renderHexInspector();
        }

        private renderBreadcrumbs(dto: NodeInspectionDto): void {
            const bar = document.getElementById('breadcrumbBar')!;
            bar.innerHTML = '';

            const rootBtn = document.createElement('button');
            rootBtn.type = 'button';
            rootBtn.className = `crumb-btn${!dto.path ? ' active' : ''}`;
            rootBtn.textContent = dto.archiveName;
            rootBtn.addEventListener('click', () => {
                void this.selectAndInspectNode(dto.archiveId, '', true);
            });
            bar.appendChild(rootBtn);

            const parts = dto.path ? dto.path.split('/').filter((p) => p.length > 0) : [];
            let accum = '';
            parts.forEach((part, idx) => {
                const sep = document.createElement('span');
                sep.className = 'crumb-sep';
                sep.textContent = '/';
                bar.appendChild(sep);

                accum = accum ? `${accum}/${part}` : part;
                const targetPath = accum;
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = `crumb-btn${idx === parts.length - 1 ? ' active' : ''}`;
                btn.textContent = part;
                btn.addEventListener('click', () => {
                    void this.selectAndInspectNode(dto.archiveId, targetPath, true);
                });
                bar.appendChild(btn);
            });

            const metaBadges = document.getElementById('stageMetaBadges')!;
            const changeChip =
                dto.changeState === RepackerChangeState.Deleted
                    ? '<span class="meta-chip" style="color:var(--accent-danger); border-color:var(--accent-danger-border); background:var(--accent-danger-bg)">DELETED</span>'
                    : dto.changeState === RepackerChangeState.Added
                      ? '<span class="meta-chip" style="color:var(--accent-green); border-color:var(--accent-green-border); background:var(--accent-green-bg)">ADDED</span>'
                      : dto.changeState === RepackerChangeState.Modified
                        ? '<span class="meta-chip" style="color:var(--accent-warning); border-color:var(--accent-warning-border); background:var(--accent-warning-bg)">MODIFIED</span>'
                        : '';
            metaBadges.innerHTML = `
                <span class="meta-chip">${this.escapeHtml(dto.typeBadge)}</span>
                <span class="meta-chip">${formatByteSize(dto.sizeBytes)}</span>
                ${changeChip}
            `;
        }

        private async renderCanvasOrAnimationStage(dto: NodeInspectionDto): Promise<void> {
            this.stopAnimationLoop();
            this.revokeDecodedFrames();

            const section = document.getElementById('canvasStageSection')!;
            const hasSingleCanvas = Boolean(dto.canvas && dto.canvas.pngBytes);
            const hasAnimFrames = Array.isArray(dto.animationFrames) && dto.animationFrames.length > 0;

            if (!hasSingleCanvas && !hasAnimFrames) {
                section.classList.add('hidden');
                return;
            }
            section.classList.remove('hidden');

            // Outlink / Inlink / UOL resolution banner
            const linkBanner = document.getElementById('linkResolutionBanner')!;
            const linkText = document.getElementById('linkBannerText')!;
            if (dto.canvas && (dto.canvas.outlink || dto.canvas.inlink)) {
                linkBanner.classList.remove('hidden');
                const linkType = dto.canvas.outlink ? '_outlink' : '_inlink';
                const rawLink = dto.canvas.outlink || dto.canvas.inlink || '';
                const resolved = dto.canvas.resolvedLinkPath
                    ? ` -> Resolved from ${dto.canvas.resolvedLinkPath}`
                    : ' (Unresolved external archive)';
                linkText.textContent = `${linkType}: "${rawLink}"${resolved}`;
            } else if (dto.resolvedUolTargetPath) {
                linkBanner.classList.remove('hidden');
                linkText.textContent = `UOL Link -> "${dto.resolvedUolTargetPath}"`;
            } else {
                linkBanner.classList.add('hidden');
            }

            if (dto.canvas) {
                const fmtSelect = document.getElementById('canvasFormatSelect') as HTMLSelectElement;
                fmtSelect.value = dto.canvas.format || WzCanvasFormat.Bgra8888;
            }

            // Decode frames into HTMLImageElements
            if (hasAnimFrames && dto.animationFrames.length > 1) {
                for (const f of dto.animationFrames) {
                    const decoded = await this.decodePngBytesToFrame(
                        toUint8Array(f.pngBytes),
                        f.width,
                        f.height,
                        f.origin,
                        f.lt,
                        f.rb,
                        f.head,
                        f.delayMs,
                        f.alphaStart,
                        f.alphaEnd,
                        f.frameName,
                        f.framePath
                    );
                    if (decoded) {
                        this.decodedFrames.push(decoded);
                    }
                }
            } else if (dto.canvas) {
                const c = dto.canvas;
                const single = await this.decodePngBytesToFrame(
                    toUint8Array(c.pngBytes),
                    c.width,
                    c.height,
                    c.origin,
                    c.lt,
                    c.rb,
                    c.head,
                    c.delayMs,
                    255,
                    255,
                    dto.name,
                    dto.path
                );
                if (single) {
                    this.decodedFrames.push(single);
                }
            }

            this.activeFrameIdx = 0;
            this.animPlaying = false;
            this.panX = 0;
            this.panY = 0;
            this.renderAnimationPlayerBar();
            this.drawActiveCanvasFrame();
        }

        private decodePngBytesToFrame(
            pngBytes: Uint8Array,
            width: number,
            height: number,
            origin: VectorPointDto,
            lt: VectorPointDto | null | undefined,
            rb: VectorPointDto | null | undefined,
            head: VectorPointDto | null | undefined,
            delayMs: number,
            alphaStart: number,
            alphaEnd: number,
            frameName: string,
            framePath: string
        ): Promise<DecodedSpriteFrame | null> {
            if (!pngBytes || pngBytes.length === 0) {
                return Promise.resolve(null);
            }
            return new Promise<DecodedSpriteFrame | null>((resolve) => {
                const blob = new Blob([pngBytes as unknown as BlobPart], { type: 'image/png' });
                const url = URL.createObjectURL(blob);
                const img = new Image();
                img.onload = () => {
                    resolve({
                        img,
                        url,
                        width: width || img.width,
                        height: height || img.height,
                        origin: origin || { x: 0, y: 0 },
                        lt,
                        rb,
                        head,
                        delayMs: Math.max(30, delayMs || 120),
                        alphaStart: alphaStart ?? 255,
                        alphaEnd: alphaEnd ?? 255,
                        frameName,
                        framePath,
                    });
                };
                img.onerror = () => {
                    URL.revokeObjectURL(url);
                    resolve(null);
                };
                img.src = url;
            });
        }

        private revokeDecodedFrames(): void {
            for (const f of this.decodedFrames) {
                URL.revokeObjectURL(f.url);
            }
            this.decodedFrames = [];
        }

        private stopAnimationLoop(): void {
            if (this.animTimer !== null) {
                clearTimeout(this.animTimer);
                this.animTimer = null;
            }
        }

        private scheduleNextAnimationFrame(): void {
            this.stopAnimationLoop();
            if (!this.animPlaying || this.decodedFrames.length <= 1) return;
            const current = this.decodedFrames[this.activeFrameIdx];
            const delay = current ? Math.max(30, current.delayMs) : 120;
            this.animTimer = setTimeout(() => {
                this.activeFrameIdx = (this.activeFrameIdx + 1) % this.decodedFrames.length;
                this.updateActiveFrameHighlight();
                this.drawActiveCanvasFrame();
                this.scheduleNextAnimationFrame();
            }, delay);
        }

        private updateAnimPlayPauseButton(): void {
            const playBtn = document.getElementById('btnAnimPlayPause');
            if (!playBtn) return;
            playBtn.classList.toggle('play', !this.animPlaying);
            playBtn.classList.toggle('stop', this.animPlaying);
            playBtn.innerHTML = this.animPlaying
                ? '<span class="anim-btn-icon">&#9632;</span><span>Stop</span>'
                : '<span class="anim-btn-icon">&#9654;</span><span>Play</span>';
        }

        private renderAnimationPlayerBar(): void {
            const bar = document.getElementById('animPlayerBar')!;
            const strip = document.getElementById('animTimelineStrip')!;
            strip.innerHTML = '';

            if (this.decodedFrames.length <= 1) {
                bar.classList.add('hidden');
                return;
            }
            bar.classList.remove('hidden');

            this.updateAnimPlayPauseButton();

            this.decodedFrames.forEach((f, idx) => {
                const pill = document.createElement('button');
                pill.type = 'button';
                pill.className = `anim-frame-pill${idx === this.activeFrameIdx ? ' active' : ''}`;
                pill.innerHTML = `<span>#${this.escapeHtml(f.frameName)}</span><span>${f.delayMs}ms</span>`;
                pill.addEventListener('click', () => {
                    this.activeFrameIdx = idx;
                    this.updateActiveFrameHighlight();
                    this.drawActiveCanvasFrame();
                });
                pill.addEventListener('dblclick', () => {
                    if (this.selectedArchiveId && f.framePath) {
                        void this.selectAndInspectNode(this.selectedArchiveId, f.framePath, true);
                    }
                });
                strip.appendChild(pill);
            });

            this.updateActiveFrameHighlight();
        }

        private updateActiveFrameHighlight(): void {
            const counter = document.getElementById('animFrameCounter')!;
            counter.textContent = `Frame ${this.activeFrameIdx + 1} / ${this.decodedFrames.length}`;
            const strip = document.getElementById('animTimelineStrip')!;
            const pills = strip.querySelectorAll('.anim-frame-pill');
            pills.forEach((el, idx) => {
                el.classList.toggle('active', idx === this.activeFrameIdx);
            });

            const activeFrame = this.decodedFrames[this.activeFrameIdx];
            if (activeFrame?.framePath) {
                const tbody = document.getElementById('childrenTableBody');
                if (tbody) {
                    tbody.querySelectorAll('tr').forEach((tr) => {
                        tr.classList.toggle('active-row', (tr as HTMLElement).dataset.path === activeFrame.framePath);
                    });
                }
            }
        }

        private drawActiveCanvasFrame(): void {
            const viewport = document.getElementById('canvasViewport')!;
            const rect = viewport.getBoundingClientRect();
            const cw = Math.max(300, Math.floor(rect.width));
            const ch = Math.max(180, Math.floor(rect.height));
            if (this.spriteCanvas.width !== cw || this.spriteCanvas.height !== ch) {
                this.spriteCanvas.width = cw;
                this.spriteCanvas.height = ch;
            }

            const ctx = this.spriteCtx;
            ctx.clearRect(0, 0, cw, ch);

            const frame = this.decodedFrames[this.activeFrameIdx];
            if (!frame) return;

            // Center the bounding box of the sprite (or animation sequence) in the viewport + user pan offset
            let minRelX = -frame.origin.x;
            let maxRelX = frame.width - frame.origin.x;
            let minRelY = -frame.origin.y;
            let maxRelY = frame.height - frame.origin.y;
            for (const f of this.decodedFrames) {
                minRelX = Math.min(minRelX, -f.origin.x);
                maxRelX = Math.max(maxRelX, f.width - f.origin.x);
                minRelY = Math.min(minRelY, -f.origin.y);
                maxRelY = Math.max(maxRelY, f.height - f.origin.y);
            }
            const centerRelX = (minRelX + maxRelX) * 0.5;
            const centerRelY = (minRelY + maxRelY) * 0.5;

            const anchorX = Math.round(cw * 0.5 - centerRelX * this.zoomScale + this.panX);
            const anchorY = Math.round(ch * 0.5 - centerRelY * this.zoomScale + this.panY);

            const drawX = Math.round(anchorX - frame.origin.x * this.zoomScale);
            const drawY = Math.round(anchorY - frame.origin.y * this.zoomScale);
            const drawW = Math.round(frame.width * this.zoomScale);
            const drawH = Math.round(frame.height * this.zoomScale);

            ctx.save();
            ctx.imageSmoothingEnabled = false;
            ctx.globalAlpha = Math.max(0.05, Math.min(1.0, frame.alphaStart / 255));
            ctx.drawImage(frame.img, drawX, drawY, drawW, drawH);
            ctx.restore();

            // Optional pixel grid when zoomed >= 3x
            if (this.showPixelGrid && this.zoomScale >= 3) {
                ctx.save();
                ctx.strokeStyle = 'rgba(255, 255, 255, 0.12)';
                ctx.lineWidth = 1;
                for (let x = 0; x <= frame.width; x++) {
                    const px = drawX + Math.floor(x * this.zoomScale) + 0.5;
                    ctx.beginPath();
                    ctx.moveTo(px, drawY);
                    ctx.lineTo(px, drawY + drawH);
                    ctx.stroke();
                }
                for (let y = 0; y <= frame.height; y++) {
                    const py = drawY + Math.floor(y * this.zoomScale) + 0.5;
                    ctx.beginPath();
                    ctx.moveTo(drawX, py);
                    ctx.lineTo(drawX + drawW, py);
                    ctx.stroke();
                }
                ctx.restore();
            }

            // Optional lt / rb bounding box & head marker
            if (this.showVectors) {
                if (frame.lt && frame.rb) {
                    const boxX = anchorX + frame.lt.x * this.zoomScale;
                    const boxY = anchorY + frame.lt.y * this.zoomScale;
                    const boxW = (frame.rb.x - frame.lt.x) * this.zoomScale;
                    const boxH = (frame.rb.y - frame.lt.y) * this.zoomScale;
                    ctx.save();
                    ctx.strokeStyle = '#3794ff';
                    ctx.lineWidth = 1.5;
                    ctx.setLineDash([4, 3]);
                    ctx.strokeRect(boxX, boxY, boxW, boxH);
                    ctx.restore();
                }
                if (frame.head) {
                    const hx = anchorX + frame.head.x * this.zoomScale;
                    const hy = anchorY + frame.head.y * this.zoomScale;
                    ctx.save();
                    ctx.fillStyle = '#4cd964';
                    ctx.beginPath();
                    ctx.arc(hx, hy, 4, 0, Math.PI * 2);
                    ctx.fill();
                    ctx.restore();
                }
            }

            // Optional Origin Crosshair
            if (this.showOrigin) {
                ctx.save();
                ctx.strokeStyle = 'rgba(251, 191, 36, 0.85)';
                ctx.lineWidth = 1;
                ctx.beginPath();
                ctx.moveTo(anchorX - 14, anchorY + 0.5);
                ctx.lineTo(anchorX + 14, anchorY + 0.5);
                ctx.moveTo(anchorX + 0.5, anchorY - 14);
                ctx.lineTo(anchorX + 0.5, anchorY + 14);
                ctx.stroke();
                ctx.restore();
            }

            // HUD Overlay
            const hud = document.getElementById('canvasHudOverlay')!;
            const fmtInfo = this.currentInspection?.canvas?.formatName || 'Canvas';
            hud.innerHTML = `
                <span>${frame.width}&times;${frame.height} px</span>
                <span>Origin: (${frame.origin.x}, ${frame.origin.y})</span>
                <span>Delay: ${frame.delayMs} ms</span>
                <span>${this.escapeHtml(fmtInfo)}</span>
            `;
        }

        private renderSoundStage(dto: NodeInspectionDto): void {
            const section = document.getElementById('soundStageSection')!;
            const audioEl = document.getElementById('audioPlayerEl') as HTMLAudioElement;

            if (this.activeAudioUrl) {
                URL.revokeObjectURL(this.activeAudioUrl);
                this.activeAudioUrl = null;
            }

            if (!dto.sound) {
                section.classList.add('hidden');
                audioEl.removeAttribute('src');
                return;
            }

            section.classList.remove('hidden');
            const s = dto.sound;
            const audioBytes = toUint8Array(s.audioBytes);

            const firstSoundChild =
                dto.propertyKind !== RepackerPropertyKind.Sound
                    ? (dto.children || []).find((c) => c.propertyKind === RepackerPropertyKind.Sound)
                    : null;
            const soundName = firstSoundChild ? firstSoundChild.name : dto.name;
            const soundPath = firstSoundChild ? firstSoundChild.path : dto.path;
            document.getElementById('soundTitle')!.textContent = `${soundName} (${s.formatName})`;
            document.getElementById('soundSubtitle')!.textContent = `${dto.archiveName} \u2022 ${soundPath}`;

            const blob = new Blob([audioBytes as unknown as BlobPart], { type: s.mimeType || 'audio/wav' });
            this.activeAudioUrl = URL.createObjectURL(blob);
            audioEl.src = this.activeAudioUrl;

            const metricsGrid = document.getElementById('soundMetricsGrid')!;
            metricsGrid.innerHTML = `
                <div class="metric-box"><div class="metric-label">Duration</div><div class="metric-val">${s.durationMs} ms</div></div>
                <div class="metric-box"><div class="metric-label">Sample Rate</div><div class="metric-val">${s.sampleRate} Hz</div></div>
                <div class="metric-box"><div class="metric-label">Channels</div><div class="metric-val">${s.channels === 1 ? 'Mono (1ch)' : `Stereo (${s.channels}ch)`}</div></div>
                <div class="metric-box"><div class="metric-label">Stream Size</div><div class="metric-val">${formatByteSize(s.dataLength)}</div></div>
            `;

            this.drawSoundWaveform(audioBytes);
        }

        private drawSoundWaveform(audioBytes: Uint8Array): void {
            const canvas = document.getElementById('waveformCanvas') as HTMLCanvasElement;
            const ctx = canvas.getContext('2d')!;
            const w = canvas.width;
            const h = canvas.height;
            ctx.clearRect(0, 0, w, h);

            ctx.strokeStyle = 'rgba(255, 255, 255, 0.08)';
            ctx.beginPath();
            ctx.moveTo(0, h / 2);
            ctx.lineTo(w, h / 2);
            ctx.stroke();

            if (audioBytes.length <= 44) return;
            const startOffset = 44;
            const sampleBytes = audioBytes.length - startOffset;
            const totalSamples = Math.floor(sampleBytes / 2);
            if (totalSamples <= 0) return;

            ctx.strokeStyle = '#2dd4bf';
            ctx.lineWidth = 1.25;
            ctx.beginPath();

            const view = new DataView(audioBytes.buffer, audioBytes.byteOffset + startOffset, sampleBytes);
            for (let x = 0; x < w; x++) {
                const sampleIdx = Math.min(totalSamples - 1, Math.floor((x / w) * totalSamples));
                const sampleVal = view.getInt16(sampleIdx * 2, true) / 32768.0;
                const y = (0.5 - sampleVal * 0.44) * h;
                if (x === 0) ctx.moveTo(x, y);
                else ctx.lineTo(x, y);
            }
            ctx.stroke();
        }

        private getActiveStringPreviewTarget(dto: NodeInspectionDto): {
            name: string;
            path: string;
            text: string;
            sizeBytes: number;
        } | null {
            if (dto.propertyKind === RepackerPropertyKind.String) {
                return {
                    name: dto.name,
                    path: dto.path,
                    text: dto.rawStringValue ?? dto.displayValue ?? '',
                    sizeBytes: dto.sizeBytes,
                };
            }
            if (!dto.canvas && !dto.sound && (!dto.animationFrames || dto.animationFrames.length === 0)) {
                const firstStrChild = (dto.children || []).find(
                    (c) => c.propertyKind === RepackerPropertyKind.String
                );
                if (firstStrChild) {
                    const text = firstStrChild.displayValue ?? '';
                    return {
                        name: firstStrChild.name,
                        path: firstStrChild.path,
                        text,
                        sizeBytes: firstStrChild.sizeBytes || new TextEncoder().encode(text).length,
                    };
                }
            }
            return null;
        }

        private renderStringStage(dto: NodeInspectionDto): void {
            const section = document.getElementById('stringStageSection')!;
            const target = this.getActiveStringPreviewTarget(dto);
            if (!target) {
                section.classList.add('hidden');
                return;
            }

            section.classList.remove('hidden');
            const text = target.text;
            document.getElementById('stringTitle')!.textContent = target.name;
            document.getElementById('stringSubtitle')!.textContent = `${dto.archiveName} \u2022 ${target.path}`;

            const lineCount = text.length === 0 ? 0 : text.split(/\r\n|\r|\n|\\n/).length;
            const lineLabel = lineCount > 1 ? ` \u2022 ${lineCount} lines` : '';
            document.getElementById('stringLengthMeta')!.textContent = `${text.length} chars${lineLabel} \u2022 ${formatByteSize(target.sizeBytes)}`;

            const viewer = document.getElementById('stringViewerContent')!;
            if (text.length === 0) {
                viewer.textContent = '(Empty string)';
                viewer.classList.add('empty');
            } else {
                const formatted = text.includes('\n') ? text : text.replace(/\\r\\n/g, '\n').replace(/\\n/g, '\n');
                viewer.textContent = formatted;
                viewer.classList.remove('empty');
            }
        }

        private getHighlightedChildRowPath(dto: NodeInspectionDto): string {
            if (dto.childCount === 0) {
                return dto.path;
            }
            if (dto.animationFrames && dto.animationFrames.length > 0) {
                return dto.animationFrames[0].framePath;
            }
            if (dto.canvas && dto.propertyKind !== RepackerPropertyKind.Canvas) {
                const firstCanvas = (dto.children || []).find((c) => c.propertyKind === RepackerPropertyKind.Canvas);
                if (firstCanvas) return firstCanvas.path;
            }
            if (dto.sound && dto.propertyKind !== RepackerPropertyKind.Sound) {
                const firstSound = (dto.children || []).find((c) => c.propertyKind === RepackerPropertyKind.Sound);
                if (firstSound) return firstSound.path;
            }
            const strTarget = this.getActiveStringPreviewTarget(dto);
            if (strTarget && strTarget.path !== dto.path) {
                return strTarget.path;
            }
            return dto.path;
        }

        private getNodeLayerKey(dto: NodeInspectionDto): string {
            const parts = dto.path ? dto.path.split('/').filter((p) => p.length > 0) : [];
            const parentPath = parts.length > 1 ? parts.slice(0, -1).join('/') : '';
            return `${dto.archiveId}::${parentPath}::${parts.length}`;
        }

        private hasStagePreview(dto: NodeInspectionDto | null): boolean {
            if (!dto) return false;
            const hasCanvas =
                Boolean(dto.canvas && dto.canvas.pngBytes) ||
                (Array.isArray(dto.animationFrames) && dto.animationFrames.length > 0);
            const hasSound = Boolean(dto.sound && dto.sound.audioBytes);
            const hasString = Boolean(this.getActiveStringPreviewTarget(dto));
            return hasCanvas || hasSound || hasString;
        }

        private syncChildrenTableMinimizedState(dto: NodeInspectionDto): void {
            const layerKey = this.getNodeLayerKey(dto);

            if (this.childrenTableMinimized && layerKey !== this.lastInspectedLayerKey) {
                this.childrenTableMinimized = false;
            }

            this.lastInspectedLayerKey = layerKey;
            this.applyChildrenTableMinimizedDom(this.hasStagePreview(dto));
        }

        private toggleChildrenTableMinimized(): void {
            this.childrenTableMinimized = !this.childrenTableMinimized;
            this.applyChildrenTableMinimizedDom();
            this.drawActiveCanvasFrame();
        }

        private applyChildrenTableMinimizedDom(hasPreviewOverride?: boolean): void {
            const section = document.getElementById('childrenTableSection');
            const splitter = document.getElementById('childrenTableSplitter');
            const btn = document.getElementById('btnToggleChildrenTable');
            const hasPreview =
                typeof hasPreviewOverride === 'boolean'
                    ? hasPreviewOverride
                    : this.hasStagePreview(this.currentInspection);

            if (splitter) {
                splitter.classList.toggle('hidden', !hasPreview);
            }
            if (section) {
                section.classList.toggle('minimized', this.childrenTableMinimized);
                if (hasPreview && !this.childrenTableMinimized) {
                    section.style.flex = '0 0 auto';
                    section.style.height = `${Math.round(this.childrenTableHeight)}px`;
                } else {
                    section.style.flex = '';
                    section.style.height = '';
                }
            }
            if (btn) {
                btn.innerHTML = this.childrenTableMinimized ? '&#9652;' : '&#9662;';
                btn.title = this.childrenTableMinimized
                    ? 'Expand child nodes table'
                    : 'Minimize child nodes table';
            }
        }

        private renderChildrenTable(dto: NodeInspectionDto): void {
            const titleEl = document.getElementById('childrenTableTitle')!;
            const metaEl = document.getElementById('childrenTableMeta')!;
            const tbody = document.getElementById('childrenTableBody')!;
            tbody.innerHTML = '';

            const children = dto.children || [];
            const isParentChildren = dto.childCount === 0 && children.length > 0;
            if (isParentChildren) {
                const lastSlash = dto.path.lastIndexOf('/');
                const parentPath = lastSlash >= 0 ? dto.path.slice(0, lastSlash) : '';
                const parentName = parentPath.includes('/')
                    ? parentPath.slice(parentPath.lastIndexOf('/') + 1)
                    : parentPath || dto.archiveName;
                titleEl.textContent = `Parent Child Nodes (${children.length})`;
                metaEl.textContent = `Children of ${parentName} \u2022 Click a row to inspect`;
            } else {
                titleEl.textContent = `Child Nodes (${children.length})`;
                metaEl.textContent = children.length > 0 ? 'Click a row to inspect' : 'No child nodes';
            }

            const highlightedPath = this.getHighlightedChildRowPath(dto);
            let activeRowEl: HTMLTableRowElement | null = null;
            for (const c of children) {
                const tr = document.createElement('tr');
                tr.dataset.path = c.path;
                const rowClasses: string[] = [];
                if (c.path === highlightedPath) {
                    rowClasses.push('active-row');
                    activeRowEl = tr;
                }
                if (c.changeState === RepackerChangeState.Deleted) {
                    rowClasses.push('deleted-row');
                }
                if (rowClasses.length > 0) {
                    tr.className = rowClasses.join(' ');
                }
                const sizeOrItems =
                    c.sizeBytes > 0
                        ? formatByteSize(c.sizeBytes)
                        : c.childCount > 0
                          ? `${c.childCount} items`
                          : '-';

                tr.innerHTML = `
                    <td>${this.escapeHtml(c.name)}</td>
                    <td><span class="tree-badge badge-${this.escapeHtml(c.typeBadge)}">${this.escapeHtml(c.typeBadge)}</span></td>
                    <td>${this.escapeHtml(c.displayValue)}</td>
                    <td style="text-align: right; color: var(--text-muted)">${this.escapeHtml(sizeOrItems)}</td>
                `;

                tr.addEventListener('click', () => {
                    this.closeContextMenu();
                    void this.selectAndInspectNode(dto.archiveId, c.path, true);
                });
                tr.addEventListener('contextmenu', (ev) => {
                    ev.preventDefault();
                    ev.stopPropagation();
                    this.openContextMenu(ev.clientX, ev.clientY, dto.archiveId, c.path, c.changeState);
                });
                tbody.appendChild(tr);
            }

            const scrollWrap = tbody.closest('.table-scroll-wrap') as HTMLElement | null;
            if (scrollWrap) {
                if (!isParentChildren || !activeRowEl) {
                    scrollWrap.scrollTop = 0;
                } else {
                    const rowTop = activeRowEl.offsetTop;
                    const rowBottom = rowTop + activeRowEl.offsetHeight;
                    const headerHeight = 28;
                    if (rowTop - headerHeight < scrollWrap.scrollTop) {
                        scrollWrap.scrollTop = Math.max(0, rowTop - headerHeight);
                    } else if (rowBottom > scrollWrap.scrollTop + scrollWrap.clientHeight) {
                        scrollWrap.scrollTop = rowBottom - scrollWrap.clientHeight;
                    }
                }
            }
        }

        private renderPropertyInspector(dto: NodeInspectionDto): void {
            document.getElementById('inspectorTypePill')!.textContent = dto.typeBadge;

            const isArchiveRoot = !dto.path || dto.nodeKind === RepackerNodeKind.Archive;
            const isDeleted = dto.changeState === RepackerChangeState.Deleted;
            document.getElementById('btnRenameNode')!.classList.toggle('hidden', isArchiveRoot || isDeleted);
            const deleteBtn = document.getElementById('btnDeleteNode')!;
            deleteBtn.textContent = isArchiveRoot ? 'Close' : isDeleted ? 'Restore' : 'Delete';
            deleteBtn.title = isArchiveRoot
                ? 'Close WZ archive (Del)'
                : isDeleted
                  ? 'Restore deleted node'
                  : 'Delete selected node (Del)';
            deleteBtn.classList.toggle('danger', !isDeleted);

            (document.getElementById('btnInspAddChild') as HTMLButtonElement).disabled = isArchiveRoot || isDeleted;
            (document.getElementById('btnInspDuplicate') as HTMLButtonElement).disabled = isArchiveRoot || isDeleted;
            (document.getElementById('btnInspCopy') as HTMLButtonElement).disabled = isArchiveRoot || isDeleted;

            const metaGrid = document.getElementById('inspectorMetaGrid')!;
            metaGrid.innerHTML = `
                <div class="kv-key">Archive</div><div class="kv-val">${this.escapeHtml(dto.archiveName)}</div>
                <div class="kv-key">Path</div><div class="kv-val">${this.escapeHtml(dto.path || '(Root)')}</div>
                <div class="kv-key">Node Kind</div><div class="kv-val">${this.escapeHtml(dto.nodeKind)}</div>
                <div class="kv-key">Property Type</div><div class="kv-val">${this.escapeHtml(dto.propertyKind)}</div>
                <div class="kv-key">Value</div><div class="kv-val">${this.escapeHtml(dto.displayValue)}</div>
                <div class="kv-key">Size</div><div class="kv-val">${formatByteSize(dto.sizeBytes)}</div>
            `;

            const editorCard = document.getElementById('valueEditorCard')!;
            const isEditableProperty =
                !isDeleted &&
                dto.nodeKind === RepackerNodeKind.Property &&
                dto.propertyKind !== RepackerPropertyKind.Object &&
                dto.propertyKind !== RepackerPropertyKind.Convex &&
                dto.propertyKind !== RepackerPropertyKind.Sound &&
                dto.propertyKind !== RepackerPropertyKind.RawData;

            if (!isEditableProperty) {
                editorCard.classList.add('hidden');
                return;
            }
            editorCard.classList.remove('hidden');

            const kindSelect = document.getElementById('editPropKind') as HTMLSelectElement;
            const scalarInput = document.getElementById('editScalarValue') as HTMLInputElement;
            const vecXInput = document.getElementById('editVecX') as HTMLInputElement;
            const vecYInput = document.getElementById('editVecY') as HTMLInputElement;

            kindSelect.value = dto.propertyKind;
            if (dto.propertyKind === RepackerPropertyKind.Vector && dto.vectorValue) {
                vecXInput.value = String(dto.vectorValue.x);
                vecYInput.value = String(dto.vectorValue.y);
            } else if (dto.propertyKind === RepackerPropertyKind.Canvas && dto.canvas) {
                vecXInput.value = String(dto.canvas.origin.x);
                vecYInput.value = String(dto.canvas.origin.y);
                scalarInput.value = String(dto.canvas.delayMs);
            } else {
                scalarInput.value = dto.rawStringValue ?? dto.displayValue;
            }

            this.syncPropertyEditorFieldVisibility(kindSelect.value);
        }

        private syncPropertyEditorFieldVisibility(kind: string): void {
            const scalarGroup = document.getElementById('scalarInputGroup')!;
            const scalarLabel = document.getElementById('scalarValueLabel')!;
            const vectorGroup = document.getElementById('vectorInputGroup')!;

            if (kind === RepackerPropertyKind.Vector) {
                scalarGroup.classList.add('hidden');
                vectorGroup.classList.remove('hidden');
            } else if (kind === RepackerPropertyKind.Canvas) {
                scalarGroup.classList.remove('hidden');
                scalarLabel.textContent = 'Frame Delay (ms)';
                vectorGroup.classList.remove('hidden');
            } else if (kind === RepackerPropertyKind.Null) {
                scalarGroup.classList.add('hidden');
                vectorGroup.classList.add('hidden');
            } else {
                scalarGroup.classList.remove('hidden');
                scalarLabel.textContent = kind === RepackerPropertyKind.Uol ? 'Relative UOL Target Path' : 'Value';
                vectorGroup.classList.add('hidden');
            }
        }

        // ---------------------------------------------------------------------
        // Hex & Binary Data Inspector
        // ---------------------------------------------------------------------

        private renderHexInspector(): void {
            const dumpContainer = document.getElementById('hexDumpContainer')!;
            dumpContainer.innerHTML = '';

            const bytes = this.activeHexBytes;
            if (!bytes || bytes.length === 0) {
                dumpContainer.textContent = 'No binary slice available for this node.';
                this.renderHexDataSidebar();
                return;
            }

            const maxRenderBytes = Math.min(bytes.length, 4096);
            for (let offset = 0; offset < maxRenderBytes; offset += 16) {
                const rowLen = Math.min(16, maxRenderBytes - offset);
                const line = document.createElement('div');
                line.className = 'hex-line';

                const offSpan = document.createElement('span');
                offSpan.className = 'hex-offset';
                offSpan.textContent = offset.toString(16).toUpperCase().padStart(8, '0');
                line.appendChild(offSpan);

                const hexBytesSpan = document.createElement('span');
                hexBytesSpan.className = 'hex-bytes';
                let asciiStr = '';

                for (let i = 0; i < 16; i++) {
                    if (i < rowLen) {
                        const byteIndex = offset + i;
                        const b = bytes[byteIndex];
                        const bSpan = document.createElement('span');
                        bSpan.className = `hex-byte${byteIndex === this.selectedHexOffset ? ' active' : ''}`;
                        bSpan.textContent = b.toString(16).toUpperCase().padStart(2, '0');
                        bSpan.addEventListener('click', () => {
                            this.selectedHexOffset = byteIndex;
                            dumpContainer.querySelectorAll('.hex-byte.active').forEach((el) => el.classList.remove('active'));
                            bSpan.classList.add('active');
                            this.renderHexDataSidebar();
                        });
                        hexBytesSpan.appendChild(bSpan);
                        if (i < 15) {
                            hexBytesSpan.appendChild(document.createTextNode(i === 7 ? '  ' : ' '));
                        }
                        asciiStr += b >= 32 && b <= 126 ? String.fromCharCode(b) : '.';
                    } else {
                        hexBytesSpan.appendChild(document.createTextNode(i === 7 ? '    ' : '   '));
                        asciiStr += ' ';
                    }
                }
                line.appendChild(hexBytesSpan);

                const asciiSpan = document.createElement('span');
                asciiSpan.className = 'hex-ascii';
                asciiSpan.textContent = `|${asciiStr}|`;
                line.appendChild(asciiSpan);

                dumpContainer.appendChild(line);
            }

            this.renderHexDataSidebar();
        }

        private renderHexDataSidebar(): void {
            const sidebar = document.getElementById('hexDataInspector')!;
            const bytes = this.activeHexBytes;
            const off = this.selectedHexOffset;

            if (!bytes || bytes.length === 0 || off >= bytes.length) {
                sidebar.innerHTML = '<div class="card-heading">Data Inspector</div><div style="color:var(--text-muted)">No byte selected</div>';
                return;
            }

            const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
            const rem = bytes.length - off;

            const u8 = dv.getUint8(off);
            const i8 = dv.getInt8(off);
            const i16 = rem >= 2 ? String(dv.getInt16(off, true)) : '-';
            const u16 = rem >= 2 ? String(dv.getUint16(off, true)) : '-';
            const i32 = rem >= 4 ? String(dv.getInt32(off, true)) : '-';
            const u32 = rem >= 4 ? String(dv.getUint32(off, true)) : '-';
            const f32 = rem >= 4 ? dv.getFloat32(off, true).toPrecision(6) : '-';
            const f64 = rem >= 8 ? dv.getFloat64(off, true).toPrecision(8) : '-';

            sidebar.innerHTML = `
                <div class="card-heading">Byte Inspector @ 0x${off.toString(16).toUpperCase()}</div>
                <div class="kv-grid">
                    <div class="kv-key">Offset</div><div class="kv-val">${off} (0x${off.toString(16).toUpperCase()})</div>
                    <div class="kv-key">UInt8 / Byte</div><div class="kv-val">${u8} (0x${u8.toString(16).toUpperCase().padStart(2, '0')})</div>
                    <div class="kv-key">Int8 / SByte</div><div class="kv-val">${i8}</div>
                    <div class="kv-key">Int16 (LE)</div><div class="kv-val">${i16}</div>
                    <div class="kv-key">UInt16 (LE)</div><div class="kv-val">${u16}</div>
                    <div class="kv-key">Int32 (LE)</div><div class="kv-val">${i32}</div>
                    <div class="kv-key">UInt32 (LE)</div><div class="kv-val">${u32}</div>
                    <div class="kv-key">Float32</div><div class="kv-val">${f32}</div>
                    <div class="kv-key">Float64</div><div class="kv-val">${f64}</div>
                </div>
            `;
        }

        // ---------------------------------------------------------------------
        // Event Bindings & Actions
        // ---------------------------------------------------------------------

        private bindMenuAndToolbar(): void {
            // Dropdown menus
            const menuBar = document.getElementById('mainMenuBar')!;
            const items = menuBar.querySelectorAll('.menu-item');
            items.forEach((item) => {
                const trigger = item.querySelector('.menu-trigger')!;
                trigger.addEventListener('click', (ev) => {
                    ev.stopPropagation();
                    this.closeContextMenu();
                    const wasOpen = item.classList.contains('open');
                    items.forEach((other) => other.classList.remove('open'));
                    if (!wasOpen) item.classList.add('open');
                });
            });

            document.addEventListener('click', () => {
                items.forEach((other) => other.classList.remove('open'));
                this.closeContextMenu();
            });

            document.addEventListener('contextmenu', (ev) => {
                const target = ev.target as HTMLElement | null;
                if (
                    !target?.closest('.tree-row') &&
                    !target?.closest('#childrenTableBody tr') &&
                    !target?.closest('#treeContextMenu')
                ) {
                    this.closeContextMenu();
                }
            });

            menuBar.querySelectorAll('[data-action]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const action = ((btn as HTMLElement).dataset.action || '') as MenuBarAction;
                    items.forEach((other) => other.classList.remove('open'));
                    this.dispatchMenuAction(action);
                });
            });

            // Toolbar buttons
            document.getElementById('btnUndo')!.addEventListener('click', () => void this.undoSelectedImage());
            document.getElementById('btnRedo')!.addEventListener('click', () => void this.redoSelectedImage());

            // Hidden browser file input handlers
            const wzInput = document.getElementById('hiddenWzFileInput') as HTMLInputElement;
            wzInput.addEventListener('change', async () => {
                const files = Array.from(wzInput.files || []);
                wzInput.value = '';
                if (files.length === 0) return;
                let lastArchiveId = '';
                for (const file of files) {
                    const nativePath = (file as unknown as { path?: string }).path || '';
                    const existing = this.findExistingOpenArchive(nativePath || file.name);
                    if (existing) {
                        const confirmed = await this.confirmReloadArchive(existing, file.name);
                        if (!confirmed) {
                            continue;
                        }
                    }
                    const bytes = new Uint8Array(await file.arrayBuffer());
                    const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceOpenBytes, {
                        fileName: file.name,
                        filePath: nativePath || undefined,
                        data: bytes,
                    });
                    if (res.ok && res.data.archiveId) {
                        this.clearArchiveTreeCache(res.data.archiveId);
                        lastArchiveId = res.data.archiveId;
                        this.showToast(`Loaded ${file.name}`, ToastKind.Success);
                        await this.fetchTreeChildren(res.data.archiveId, '');
                    } else {
                        this.showToast(res.data?.error || `Failed to open ${file.name}`, ToastKind.Error);
                    }
                }
                if (lastArchiveId) {
                    await this.selectAndInspectNode(lastArchiveId, '', true);
                }
            });

            const pngInput = document.getElementById('hiddenPngFileInput') as HTMLInputElement;
            pngInput.addEventListener('change', async () => {
                const file = pngInput.files?.[0];
                pngInput.value = '';
                if (!file || !this.selectedArchiveId || !this.selectedPath) return;
                const bytes = new Uint8Array(await file.arrayBuffer());
                const fmt =
                    ((document.getElementById('canvasFormatSelect') as HTMLSelectElement).value as WzCanvasFormat) ||
                    WzCanvasFormat.Bgra8888;
                const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeReplaceCanvas, {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    pngBytes: bytes,
                    format: fmt,
                });
                if (res.ok) {
                    this.showToast(`Replaced Canvas with ${file.name}`, ToastKind.Success);
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                    await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
                } else {
                    this.showToast(res.data?.error || 'Failed to import PNG', ToastKind.Error);
                }
            });

            const audioInput = document.getElementById('hiddenAudioFileInput') as HTMLInputElement;
            audioInput.addEventListener('change', async () => {
                const file = audioInput.files?.[0];
                audioInput.value = '';
                if (!file || !this.selectedArchiveId || !this.selectedPath) return;
                const bytes = new Uint8Array(await file.arrayBuffer());
                const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeReplaceSound, {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    audioBytes: bytes,
                });
                if (res.ok) {
                    this.showToast(`Replaced Sound_DX8 with ${file.name}`, ToastKind.Success);
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                    await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
                } else {
                    this.showToast(res.data?.error || 'Failed to import audio', ToastKind.Error);
                }
            });

            document.getElementById('btnCloseModal')!.addEventListener('click', () => this.closeModal());
            document.getElementById('modalBackdrop')!.addEventListener('click', (ev) => {
                if (ev.target === document.getElementById('modalBackdrop')) {
                    this.closeModal();
                }
            });
        }

        private dispatchMenuAction(action: MenuBarAction): void {
            switch (action) {
                case MenuBarAction.OpenFileModal:
                    this.triggerOpenWzFile();
                    break;
                case MenuBarAction.OpenDirModal:
                    this.triggerOpenDirectory();
                    break;
                case MenuBarAction.NewArchiveModal:
                    this.openNewArchiveModal();
                    break;
                case MenuBarAction.SaveArchiveDisk:
                    this.openSaveRepackModal();
                    break;
                case MenuBarAction.DownloadArchive:
                    void this.downloadCurrentArchive();
                    break;
                case MenuBarAction.OpenBulkExportModal:
                    this.openBulkExportModal();
                    break;
                case MenuBarAction.OpenInlineOutlinksModal:
                    this.openInlineOutlinksModal();
                    break;
                case MenuBarAction.UndoNode:
                    void this.undoSelectedImage();
                    break;
                case MenuBarAction.RedoNode:
                    void this.redoSelectedImage();
                    break;
                case MenuBarAction.AddPropertyModal:
                    this.openAddPropertyModal();
                    break;
                case MenuBarAction.AddImageModal:
                    this.openAddImageModal();
                    break;
                case MenuBarAction.AddDirectoryModal:
                    this.openAddDirectoryModal();
                    break;
                case MenuBarAction.CopyNode:
                    void this.copySelectedNode();
                    break;
                case MenuBarAction.PasteNode:
                    void this.pasteIntoSelectedNode();
                    break;
                case MenuBarAction.DuplicateNode:
                    void this.duplicateSelectedNode();
                    break;
                case MenuBarAction.RenameNodePrompt:
                    this.openRenameModal();
                    break;
                case MenuBarAction.DeleteNode:
                    void this.deleteSelectedNode();
                    break;
                case MenuBarAction.FocusGlobalSearch:
                    this.focusGlobalSearch();
                    break;
                case MenuBarAction.ExportSelectedJson:
                    void this.exportCurrentNode(RepackerExportFormat.Json);
                    break;
                case MenuBarAction.ExportSelectedImg:
                    void this.exportCurrentNode(RepackerExportFormat.Img);
                    break;
                case MenuBarAction.ToggleCrosshair: {
                    const chk = document.getElementById('chkShowOrigin') as HTMLInputElement;
                    chk.checked = !chk.checked;
                    this.showOrigin = chk.checked;
                    this.drawActiveCanvasFrame();
                    break;
                }
                case MenuBarAction.ToggleVectors: {
                    const chk = document.getElementById('chkShowVectors') as HTMLInputElement;
                    chk.checked = !chk.checked;
                    this.showVectors = chk.checked;
                    this.drawActiveCanvasFrame();
                    break;
                }
                case MenuBarAction.TogglePixelGrid: {
                    const chk = document.getElementById('chkShowPixelGrid') as HTMLInputElement;
                    chk.checked = !chk.checked;
                    this.showPixelGrid = chk.checked;
                    this.drawActiveCanvasFrame();
                    break;
                }
                case MenuBarAction.ToggleInspector:
                    this.layout.toggleInspector();
                    this.drawActiveCanvasFrame();
                    break;
                case MenuBarAction.ToggleWorkbench:
                    this.layout.toggleWorkbench();
                    this.drawActiveCanvasFrame();
                    break;
                case MenuBarAction.ResetLayout:
                    this.layout.resetLayout();
                    this.childrenTableHeight = 185;
                    this.childrenTableMinimized = false;
                    this.applyChildrenTableMinimizedDom();
                    this.drawActiveCanvasFrame();
                    break;
            }
        }

        private bindTreeEvents(): void {
            document.getElementById('btnCollapseAll')!.addEventListener('click', () => {
                this.treeState.expandedKeys.clear();
                this.renderTree();
            });

            const filterInput = document.getElementById('treeFilterInput') as HTMLInputElement;
            filterInput.addEventListener('input', () => {
                this.treeFilterText = filterInput.value;
                this.renderTree();
            });
            document.getElementById('btnClearTreeFilter')!.addEventListener('click', () => {
                filterInput.value = '';
                this.treeFilterText = '';
                this.renderTree();
            });

            document.getElementById('treeContainer')!.addEventListener('scroll', () => {
                this.closeContextMenu();
            });

            // Context menu actions
            const ctxMenu = document.getElementById('treeContextMenu')!;
            ctxMenu.querySelectorAll('[data-ctx]').forEach((btn) => {
                btn.addEventListener('click', async () => {
                    const cmd = ((btn as HTMLElement).dataset.ctx || '') as TreeContextMenuCommand;
                    const targetArchiveId = this.ctxTargetArchiveId;
                    const targetPath = this.ctxTargetPath;
                    this.closeContextMenu();
                    await this.executeContextMenuCommand(cmd, targetArchiveId, targetPath);
                });
            });

            window.chrome?.webview?.addEventListener('message', (ev: MessageEvent) => {
                try {
                    const data = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data;
                    if (!data || typeof data.type !== 'string') {
                        return;
                    }
                    if (data.type === HostWebMessageType.TreeContextMenuCommand) {
                        void this.executeContextMenuCommand(
                            String(data.cmd || '') as TreeContextMenuCommand,
                            String(data.archiveId || ''),
                            String(data.path || '')
                        );
                    } else if (data.type === HostWebMessageType.RequestAppClose) {
                        this.handleAppCloseRequest();
                    }
                } catch {
                    // Ignore malformed host messages
                }
            });

            window.addEventListener('beforeunload', (ev: BeforeUnloadEvent) => {
                if (!window.chrome?.webview && this.getDirtyArchives().length > 0) {
                    ev.preventDefault();
                    ev.returnValue = '';
                }
            });
        }

        private async executeContextMenuCommand(
            cmd: TreeContextMenuCommand,
            targetArchiveId: string,
            targetPath: string
        ): Promise<void> {
            if (cmd === TreeContextMenuCommand.CloseArchive) {
                if (targetArchiveId) {
                    await this.requestCloseArchive(targetArchiveId);
                }
                return;
            }

            if (targetArchiveId) {
                await this.selectAndInspectNode(targetArchiveId, targetPath, false);
            }
            switch (cmd) {
                case TreeContextMenuCommand.AddProp:
                    this.openAddPropertyModal();
                    break;
                case TreeContextMenuCommand.AddImg:
                    this.openAddImageModal();
                    break;
                case TreeContextMenuCommand.AddDir:
                    this.openAddDirectoryModal();
                    break;
                case TreeContextMenuCommand.Copy:
                    await this.copySelectedNode();
                    break;
                case TreeContextMenuCommand.Paste:
                    await this.pasteIntoSelectedNode();
                    break;
                case TreeContextMenuCommand.Duplicate:
                    await this.duplicateSelectedNode();
                    break;
                case TreeContextMenuCommand.Rename:
                    this.openRenameModal();
                    break;
                case TreeContextMenuCommand.ExportAuto:
                    if (!targetPath) {
                        await this.downloadCurrentArchive();
                    } else {
                        await this.exportCurrentNode(RepackerExportFormat.Auto);
                    }
                    break;
                case TreeContextMenuCommand.Delete:
                    await this.deleteSelectedNode();
                    break;
                case TreeContextMenuCommand.Restore:
                    await this.restoreSelectedNode();
                    break;
            }
        }

        private closeContextMenu(): void {
            document.getElementById('treeContextMenu')?.classList.add('hidden');
            if (window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage(JSON.stringify({ type: HostWebMessageType.CloseTreeContextMenu }));
            }
        }

        private openContextMenu(
            x: number,
            y: number,
            archiveId: string,
            path: string,
            changeState: RepackerChangeState = RepackerChangeState.Unchanged
        ): void {
            this.ctxTargetArchiveId = archiveId;
            this.ctxTargetPath = path;
            const isDeleted = changeState === RepackerChangeState.Deleted;

            if (window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage(
                    JSON.stringify({
                        type: HostWebMessageType.OpenTreeContextMenu,
                        archiveId,
                        path,
                        changeState,
                        isDeleted,
                    })
                );
                return;
            }

            const isArchiveRoot = !path;
            const menu = document.getElementById('treeContextMenu')!;

            menu.querySelector(`[data-ctx="${TreeContextMenuCommand.AddProp}"]`)?.classList.toggle('hidden', isArchiveRoot || isDeleted);
            menu.querySelector(`[data-ctx="${TreeContextMenuCommand.Copy}"]`)?.classList.toggle('hidden', isArchiveRoot || isDeleted);
            menu.querySelector(`[data-ctx="${TreeContextMenuCommand.Duplicate}"]`)?.classList.toggle('hidden', isArchiveRoot || isDeleted);
            menu.querySelector(`[data-ctx="${TreeContextMenuCommand.Rename}"]`)?.classList.toggle('hidden', isArchiveRoot || isDeleted);

            const exportBtn = document.getElementById('ctxExportBtn');
            if (exportBtn) {
                exportBtn.textContent = isArchiveRoot ? 'Export .wz Archive...' : 'Export Node...';
            }

            document.getElementById('ctxRestoreBtn')?.classList.toggle('hidden', isArchiveRoot || !isDeleted);
            document.getElementById('ctxDeleteBtn')?.classList.toggle('hidden', isArchiveRoot || isDeleted);
            document.getElementById('ctxCloseArchiveBtn')?.classList.toggle('hidden', !isArchiveRoot);

            menu.classList.remove('hidden');
            const rect = menu.getBoundingClientRect();
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

            menu.style.left = `${Math.round(left)}px`;
            menu.style.top = `${Math.round(top)}px`;
        }

        private bindCanvasViewport(): void {
            const viewport = document.getElementById('canvasViewport')!;

            // Background switchers
            document.querySelectorAll('[data-bg]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const bg =
                        ((btn as HTMLElement).dataset.bg as RepackerCanvasBackground) ||
                        RepackerCanvasBackground.Checker;
                    document.querySelectorAll('[data-bg]').forEach((b) => b.classList.remove('active'));
                    btn.classList.add('active');
                    viewport.className = `canvas-viewport bg-${bg.toLowerCase()}`;
                    void rpcCall(RpcMethod.UiSaveState, { uiState: { canvasBackground: bg } });
                });
            });

            const updateZoom = (newZoom: number) => {
                this.zoomScale = Math.max(0.25, Math.min(12.0, newZoom));
                document.getElementById('zoomReadout')!.textContent = `${Math.round(this.zoomScale * 100)}%`;
                this.drawActiveCanvasFrame();
            };

            document.getElementById('btnZoomIn')!.addEventListener('click', () => updateZoom(this.zoomScale + 0.5));
            document.getElementById('btnZoomOut')!.addEventListener('click', () => updateZoom(this.zoomScale - 0.5));
            document.getElementById('btnZoom1x')!.addEventListener('click', () => updateZoom(1.0));
            document.getElementById('btnZoomReset')!.addEventListener('click', () => {
                this.panX = 0;
                this.panY = 0;
                updateZoom(2.0);
            });

            viewport.addEventListener('wheel', (ev: WheelEvent) => {
                ev.preventDefault();
                const delta = ev.deltaY < 0 ? 0.25 : -0.25;
                updateZoom(this.zoomScale + delta);
            });

            viewport.addEventListener('mousedown', (ev: MouseEvent) => {
                this.isDraggingCanvas = true;
                this.dragStartX = ev.clientX - this.panX;
                this.dragStartY = ev.clientY - this.panY;
            });

            window.addEventListener('mousemove', (ev: MouseEvent) => {
                if (!this.isDraggingCanvas) return;
                this.panX = ev.clientX - this.dragStartX;
                this.panY = ev.clientY - this.dragStartY;
                this.drawActiveCanvasFrame();
            });

            window.addEventListener('mouseup', () => {
                this.isDraggingCanvas = false;
            });

            window.addEventListener('resize', () => {
                this.drawActiveCanvasFrame();
            });

            document.getElementById('chkShowOrigin')!.addEventListener('change', (ev) => {
                this.showOrigin = (ev.target as HTMLInputElement).checked;
                this.drawActiveCanvasFrame();
                void rpcCall(RpcMethod.UiSaveState, { uiState: { showOriginCrosshair: this.showOrigin } });
            });

            document.getElementById('chkShowVectors')!.addEventListener('change', (ev) => {
                this.showVectors = (ev.target as HTMLInputElement).checked;
                this.drawActiveCanvasFrame();
                void rpcCall(RpcMethod.UiSaveState, { uiState: { showVectorBounds: this.showVectors } });
            });

            document.getElementById('chkShowPixelGrid')!.addEventListener('change', (ev) => {
                this.showPixelGrid = (ev.target as HTMLInputElement).checked;
                this.drawActiveCanvasFrame();
                void rpcCall(RpcMethod.UiSaveState, { uiState: { showPixelGrid: this.showPixelGrid } });
            });

            // Animation controls
            document.getElementById('btnAnimPlayPause')!.addEventListener('click', () => {
                if (this.decodedFrames.length <= 1) return;
                this.animPlaying = !this.animPlaying;
                this.updateAnimPlayPauseButton();
                if (this.animPlaying) {
                    this.scheduleNextAnimationFrame();
                } else {
                    this.stopAnimationLoop();
                    this.activeFrameIdx = 0;
                    this.updateActiveFrameHighlight();
                    this.drawActiveCanvasFrame();
                }
            });

            document.getElementById('btnAnimPrev')!.addEventListener('click', () => {
                if (this.decodedFrames.length === 0) return;
                this.animPlaying = false;
                this.updateAnimPlayPauseButton();
                this.stopAnimationLoop();
                this.activeFrameIdx = (this.activeFrameIdx - 1 + this.decodedFrames.length) % this.decodedFrames.length;
                this.updateActiveFrameHighlight();
                this.drawActiveCanvasFrame();
            });

            document.getElementById('btnAnimNext')!.addEventListener('click', () => {
                if (this.decodedFrames.length === 0) return;
                this.animPlaying = false;
                this.updateAnimPlayPauseButton();
                this.stopAnimationLoop();
                this.activeFrameIdx = (this.activeFrameIdx + 1) % this.decodedFrames.length;
                this.updateActiveFrameHighlight();
                this.drawActiveCanvasFrame();
            });

            // Canvas format conversion & PNG import/export
            document.getElementById('btnConvertCanvasFormat')!.addEventListener('click', async () => {
                if (!this.selectedArchiveId || !this.selectedPath) return;
                const fmt =
                    ((document.getElementById('canvasFormatSelect') as HTMLSelectElement).value as WzCanvasFormat) ||
                    WzCanvasFormat.Bgra8888;
                const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeChangeCanvasFormat, {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    format: fmt,
                });
                if (res.ok) {
                    this.showToast(`Converted Canvas to ${fmt}`, ToastKind.Success);
                    await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
                } else {
                    this.showToast(res.data?.error || 'Format conversion failed', ToastKind.Error);
                }
            });

            document.getElementById('btnReplacePng')!.addEventListener('click', () => {
                (document.getElementById('hiddenPngFileInput') as HTMLInputElement).click();
            });

            document.getElementById('btnExportPng')!.addEventListener('click', () => {
                void this.exportCurrentNode(RepackerExportFormat.Png);
            });

            document.getElementById('btnJumpToLinkedTarget')!.addEventListener('click', () => {
                const dto = this.currentInspection;
                if (!dto) return;
                if (dto.resolvedUolTargetPath) {
                    void this.selectAndInspectNode(dto.archiveId, dto.resolvedUolTargetPath, true);
                } else if (dto.canvas?.resolvedLinkPath) {
                    const resolved = dto.canvas.resolvedLinkPath;
                    const colonIdx = resolved.indexOf(':');
                    if (colonIdx > 0) {
                        const arName = resolved.slice(0, colonIdx);
                        const subPath = resolved.slice(colonIdx + 1);
                        const targetAr = this.archives.find((a) => a.name.toLowerCase() === arName.toLowerCase());
                        if (targetAr) {
                            void this.selectAndInspectNode(targetAr.archiveId, subPath, true);
                        }
                    } else {
                        void this.selectAndInspectNode(dto.archiveId, resolved, true);
                    }
                }
            });

            // Sound buttons
            document.getElementById('btnReplaceAudio')!.addEventListener('click', () => {
                (document.getElementById('hiddenAudioFileInput') as HTMLInputElement).click();
            });
            document.getElementById('btnExportAudio')!.addEventListener('click', () => {
                void this.exportCurrentNode(RepackerExportFormat.Audio);
            });

            // String stage copy button
            document.getElementById('btnCopyStringStage')!.addEventListener('click', async () => {
                const dto = this.currentInspection;
                if (!dto) return;
                const target = this.getActiveStringPreviewTarget(dto);
                if (!target) return;
                try {
                    await navigator.clipboard.writeText(target.text);
                    this.showToast('Copied string to clipboard', ToastKind.Success);
                } catch {
                    this.showToast('Failed to copy string', ToastKind.Error);
                }
            });

            // Child nodes table minimize/expand toggle & vertical resize splitter
            document.getElementById('btnToggleChildrenTable')!.addEventListener('click', () => {
                this.toggleChildrenTableMinimized();
            });

            const childrenSplitter = document.getElementById('childrenTableSplitter')!;
            childrenSplitter.addEventListener('mousedown', (ev: MouseEvent) => {
                ev.preventDefault();
                const stageContent = document.getElementById('stageContent');
                const section = document.getElementById('childrenTableSection');
                if (!stageContent || !section) return;

                const startY = ev.clientY;
                const startH = this.childrenTableMinimized
                    ? 29
                    : section.getBoundingClientRect().height || this.childrenTableHeight;
                if (this.childrenTableMinimized) {
                    this.childrenTableMinimized = false;
                }

                childrenSplitter.classList.add('dragging');
                document.body.classList.add('resizing-row');

                const onMove = (moveEv: MouseEvent) => {
                    const delta = startY - moveEv.clientY;
                    const maxH = Math.max(120, stageContent.clientHeight - 130);
                    this.childrenTableHeight = Math.max(85, Math.min(maxH, startH + delta));
                    this.applyChildrenTableMinimizedDom();
                    this.drawActiveCanvasFrame();
                };

                const onUp = () => {
                    childrenSplitter.classList.remove('dragging');
                    document.body.classList.remove('resizing-row');
                    window.removeEventListener('mousemove', onMove);
                    window.removeEventListener('mouseup', onUp);
                };

                window.addEventListener('mousemove', onMove);
                window.addEventListener('mouseup', onUp);
            });
        }

        private bindInspectorEvents(): void {
            const kindSelect = document.getElementById('editPropKind') as HTMLSelectElement;
            kindSelect.addEventListener('change', () => {
                this.syncPropertyEditorFieldVisibility(kindSelect.value);
            });

            const form = document.getElementById('propertyEditForm') as HTMLFormElement;
            form.addEventListener('submit', async (ev) => {
                ev.preventDefault();
                if (!this.selectedArchiveId || !this.selectedPath) return;
                const kind = kindSelect.value as RepackerPropertyKind;
                const rawValue = (document.getElementById('editScalarValue') as HTMLInputElement).value;
                const vecX = Number((document.getElementById('editVecX') as HTMLInputElement).value) || 0;
                const vecY = Number((document.getElementById('editVecY') as HTMLInputElement).value) || 0;

                const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeSetValue, {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    kind,
                    rawValue,
                    vecX,
                    vecY,
                });
                if (res.ok) {
                    this.showToast('Updated property value', ToastKind.Success);
                    const parentPath = this.selectedPath.includes('/')
                        ? this.selectedPath.slice(0, this.selectedPath.lastIndexOf('/'))
                        : '';
                    await this.fetchTreeChildren(this.selectedArchiveId, parentPath);
                    this.renderTree();
                    await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
                } else {
                    this.showToast(res.data?.error || 'Failed to update value', ToastKind.Error);
                }
            });

            document.getElementById('btnRenameNode')!.addEventListener('click', () => this.openRenameModal());
            document.getElementById('btnDeleteNode')!.addEventListener('click', () => {
                if (this.currentInspection?.changeState === RepackerChangeState.Deleted) {
                    void this.restoreSelectedNode();
                } else {
                    void this.deleteSelectedNode();
                }
            });
            document.getElementById('btnToggleInspectorCollapse')!.addEventListener('click', () => {
                this.layout.toggleInspector();
                this.drawActiveCanvasFrame();
            });
            document.getElementById('inspectorCollapsedStrip')!.addEventListener('click', () => {
                this.layout.toggleInspector(false);
                this.drawActiveCanvasFrame();
            });
            document.getElementById('btnInspAddChild')!.addEventListener('click', () => this.openAddPropertyModal());
            document.getElementById('btnInspDuplicate')!.addEventListener('click', () => void this.duplicateSelectedNode());
            document.getElementById('btnInspCopy')!.addEventListener('click', () => void this.copySelectedNode());
            document.getElementById('btnInspPaste')!.addEventListener('click', () => void this.pasteIntoSelectedNode());
            document.getElementById('btnInspExportJson')!.addEventListener('click', () => void this.exportCurrentNode(RepackerExportFormat.Json));
            document.getElementById('btnInspExportAuto')!.addEventListener('click', () => void this.exportCurrentNode(RepackerExportFormat.Auto));
        }

        private bindWorkbenchEvents(): void {
            const tabs = document.querySelectorAll('#workbenchTabs .wb-tab');
            tabs.forEach((tab) => {
                tab.addEventListener('click', () => {
                    const tabId =
                        ((tab as HTMLElement).dataset.tab as RepackerWorkbenchTab) ||
                        RepackerWorkbenchTab.Json;
                    this.activateWorkbenchTab(tabId);
                });
            });

            document.getElementById('btnToggleWorkbenchCollapse')!.addEventListener('click', () => {
                this.layout.toggleWorkbench();
                this.drawActiveCanvasFrame();
            });

            // JSON Editor Actions
            document.getElementById('btnApplyJsonSubtree')!.addEventListener('click', () => void this.applyJsonFromEditor());
            document.getElementById('btnFormatJson')!.addEventListener('click', () => {
                if (!this.jsonEditor) return;
                try {
                    const parsed = JSON.parse(this.jsonEditor.getValue());
                    this.jsonEditor.setValue(JSON.stringify(parsed, null, 2));
                } catch (err) {
                    this.showToast(`JSON Syntax Error: ${(err as Error).message}`, ToastKind.Error);
                }
            });
            document.getElementById('btnResetJson')!.addEventListener('click', () => {
                if (this.jsonEditor) {
                    this.jsonEditor.setValue(this.lastLoadedJsonText);
                }
            });

            // Global Search Actions
            const runSearch = async () => {
                const archiveId = (document.getElementById('searchArchiveScope') as HTMLSelectElement).value;
                const query = (document.getElementById('searchQueryInput') as HTMLInputElement).value;
                const valueQuery = (document.getElementById('searchValueInput') as HTMLInputElement).value;
                const kindStr = (document.getElementById('searchKindSelect') as HTMLSelectElement).value;
                const exactMatch = (document.getElementById('chkSearchExact') as HTMLInputElement).checked;

                const res = await rpcCall<SearchResponseDto>(RpcMethod.SearchQuery, {
                    archiveId: archiveId || undefined,
                    query,
                    valueQuery,
                    propertyKindFilter: kindStr ? (kindStr as RepackerPropertyKind) : undefined,
                    exactMatch,
                    maxResults: 300,
                });

                const hits = res.ok && res.data?.results ? res.data.results : [];
                document.getElementById('searchCountPill')!.textContent = String(hits.length);
                const tbody = document.getElementById('searchResultsBody')!;
                tbody.innerHTML = '';

                for (const h of hits) {
                    const tr = document.createElement('tr');
                    tr.innerHTML = `
                        <td>${this.escapeHtml(h.archiveName)}</td>
                        <td>${this.escapeHtml(h.path)}</td>
                        <td><span class="tree-badge badge-${this.escapeHtml(h.typeBadge)}">${this.escapeHtml(h.typeBadge)}</span></td>
                        <td>${this.escapeHtml(h.displayValue)}</td>
                    `;
                    tr.addEventListener('click', () => {
                        void this.selectAndInspectNode(h.archiveId, h.path, true);
                    });
                    tbody.appendChild(tr);
                }
            };

            document.getElementById('btnRunSearch')!.addEventListener('click', () => void runSearch());
            document.getElementById('searchQueryInput')!.addEventListener('keydown', (ev) => {
                if (ev.key === 'Enter') void runSearch();
            });
            document.getElementById('searchValueInput')!.addEventListener('keydown', (ev) => {
                if (ev.key === 'Enter') void runSearch();
            });

            // Clear logs
            document.getElementById('btnClearLogs')!.addEventListener('click', () => {
                void rpcCall(RpcMethod.LogsClear, {});
            });
        }

        private activateWorkbenchTab(tabId: RepackerWorkbenchTab, expandIfCollapsed = true): void {
            if (tabId === RepackerWorkbenchTab.Batch) {
                tabId = RepackerWorkbenchTab.Json;
            }
            if (expandIfCollapsed && this.layout.isWorkbenchCollapsed()) {
                this.layout.toggleWorkbench(false);
            }
            document.querySelectorAll('#workbenchTabs .wb-tab').forEach((btn) => {
                btn.classList.toggle('active', (btn as HTMLElement).dataset.tab === tabId);
            });

            const panelMap: Partial<Record<RepackerWorkbenchTab, string>> = {
                [RepackerWorkbenchTab.Json]: 'wbPanelJson',
                [RepackerWorkbenchTab.Search]: 'wbPanelSearch',
                [RepackerWorkbenchTab.Hex]: 'wbPanelHex',
                [RepackerWorkbenchTab.Logs]: 'wbPanelLogs',
            };
            Object.entries(panelMap).forEach(([k, id]) => {
                if (id) {
                    document.getElementById(id)?.classList.toggle('active', k === tabId);
                }
            });

            document.getElementById('wbJsonActions')!.classList.toggle('hidden', tabId !== RepackerWorkbenchTab.Json);
            if (tabId === RepackerWorkbenchTab.Json && this.jsonEditor) {
                this.jsonEditor.refresh();
            }
            void rpcCall(RpcMethod.UiSaveState, { uiState: { activeWorkbenchTab: tabId } });
        }

        private focusGlobalSearch(): void {
            this.activateWorkbenchTab(RepackerWorkbenchTab.Search);
            const input = document.getElementById('searchQueryInput') as HTMLInputElement;
            input.focus();
            input.select();
        }

        private bindKeyboardShortcuts(): void {
            window.addEventListener('keydown', (ev: KeyboardEvent) => {
                const target = ev.target as HTMLElement;
                const isInput =
                    target.tagName === 'INPUT' ||
                    target.tagName === 'TEXTAREA' ||
                    target.tagName === 'SELECT' ||
                    target.closest('.cm-editor') !== null;

                if (ev.key === 'Escape') {
                    this.closeContextMenu();
                    this.closeModal();
                    return;
                }

                if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'f') {
                    ev.preventDefault();
                    this.focusGlobalSearch();
                    return;
                }
                if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'o') {
                    ev.preventDefault();
                    this.triggerOpenWzFile();
                    return;
                }
                if ((ev.ctrlKey || ev.metaKey) && ev.shiftKey && ev.key.toLowerCase() === 's') {
                    ev.preventDefault();
                    this.openSaveRepackModal();
                    return;
                }

                if (isInput) return;

                if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'z') {
                    ev.preventDefault();
                    void this.undoSelectedImage();
                } else if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'y') {
                    ev.preventDefault();
                    void this.redoSelectedImage();
                } else if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'c') {
                    ev.preventDefault();
                    void this.copySelectedNode();
                } else if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'v') {
                    ev.preventDefault();
                    void this.pasteIntoSelectedNode();
                } else if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'd') {
                    ev.preventDefault();
                    void this.duplicateSelectedNode();
                } else if (ev.key === 'F2') {
                    ev.preventDefault();
                    this.openRenameModal();
                } else if (ev.key === 'Delete') {
                    ev.preventDefault();
                    void this.deleteSelectedNode();
                } else if (ev.key === 'Insert') {
                    ev.preventDefault();
                    this.openAddPropertyModal();
                }
            });
        }

        // ---------------------------------------------------------------------
        // Workspace & Node Operations
        // ---------------------------------------------------------------------

        private async applyJsonFromEditor(): Promise<void> {
            if (!this.jsonEditor || !this.selectedArchiveId || !this.selectedPath) return;
            const jsonText = this.jsonEditor.getValue();
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeApplyJson, {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
                jsonText,
            });
            if (res.ok) {
                this.showToast('Applied JSON changes to node', ToastKind.Success);
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
            } else {
                this.showToast(res.data?.error || 'Failed to apply JSON', ToastKind.Error);
            }
        }

        private async undoSelectedImage(): Promise<void> {
            if (!this.selectedArchiveId) return;
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeUndo, {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok) {
                const affectedPath = res.data.path || this.selectedPath;
                this.showToast(affectedPath ? `Undid last change in ${affectedPath}` : 'Undid last change', ToastKind.Info);
                if (affectedPath && affectedPath !== this.selectedPath) {
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, affectedPath);
                }
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
            } else {
                this.showToast(res.data?.error || 'Nothing to undo', ToastKind.Error);
            }
        }

        private async redoSelectedImage(): Promise<void> {
            if (!this.selectedArchiveId) return;
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeRedo, {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok) {
                const affectedPath = res.data.path || this.selectedPath;
                this.showToast(affectedPath ? `Redid change in ${affectedPath}` : 'Redid change', ToastKind.Info);
                if (affectedPath && affectedPath !== this.selectedPath) {
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, affectedPath);
                }
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
            } else {
                this.showToast(res.data?.error || 'Nothing to redo', ToastKind.Error);
            }
        }

        private async copySelectedNode(): Promise<void> {
            if (!this.selectedArchiveId || !this.selectedPath) return;
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeCopy, {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok) {
                this.showToast(`Copied "${this.selectedPath}"`, ToastKind.Info);
            } else {
                this.showToast(res.data?.error || 'Copy failed', ToastKind.Error);
            }
        }

        private async pasteIntoSelectedNode(): Promise<void> {
            if (!this.selectedArchiveId) return;
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodePaste, {
                archiveId: this.selectedArchiveId,
                targetPath: this.selectedPath,
            });
            if (res.ok && res.data.path) {
                this.showToast(`Pasted "${res.data.path}"`, ToastKind.Success);
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
            } else {
                this.showToast(res.data?.error || 'Paste failed', ToastKind.Error);
            }
        }

        private async duplicateSelectedNode(): Promise<void> {
            if (!this.selectedArchiveId || !this.selectedPath) return;
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeDuplicate, {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok && res.data.path) {
                this.showToast(`Duplicated -> "${res.data.path}"`, ToastKind.Success);
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
            } else {
                this.showToast(res.data?.error || 'Duplicate failed', ToastKind.Error);
            }
        }

        private async deleteSelectedNode(): Promise<void> {
            if (!this.selectedArchiveId) return;
            if (!this.selectedPath) {
                await this.requestCloseArchive(this.selectedArchiveId);
                return;
            }
            const targetPath = this.selectedPath;
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeDelete, {
                archiveId: this.selectedArchiveId,
                path: targetPath,
            });
            if (res.ok) {
                const parentPath = res.data.path ?? '';
                this.showToast('Deleted node', ToastKind.Info);
                this.treeState.childrenByParent.delete(this.makeCacheKey(this.selectedArchiveId, parentPath));
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, targetPath);
                const stillInTree = (this.treeState.childrenByParent.get(this.makeCacheKey(this.selectedArchiveId, parentPath)) || []).some(
                    (n) => n.path === targetPath
                );
                await this.selectAndInspectNode(this.selectedArchiveId, stillInTree ? targetPath : parentPath, false);
            } else {
                this.showToast(res.data?.error || 'Delete failed', ToastKind.Error);
            }
        }

        private async restoreSelectedNode(): Promise<void> {
            if (!this.selectedArchiveId || !this.selectedPath) return;
            const targetPath = this.selectedPath;
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeRestore, {
                archiveId: this.selectedArchiveId,
                path: targetPath,
            });
            if (res.ok) {
                const restoredPath = res.data.path || targetPath;
                this.showToast(`Restored "${restoredPath}"`, ToastKind.Success);
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, restoredPath);
                await this.selectAndInspectNode(this.selectedArchiveId, restoredPath, false);
            } else {
                this.showToast(res.data?.error || 'Restore failed', ToastKind.Error);
            }
        }

        private async inlineLinksInCurrentScope(): Promise<void> {
            if (!this.selectedArchiveId) return;
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeInlineOutlinks, {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok) {
                this.showToast(`Inlined ${res.data.count ?? 0} linked sprite(s)`, ToastKind.Success);
                await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
            } else {
                this.showToast(res.data?.error || 'Inlining failed', ToastKind.Error);
            }
        }

        private async exportCurrentNode(format: RepackerExportFormat): Promise<void> {
            if (!this.selectedArchiveId) return;
            const url = `/api/export?archiveId=${encodeURIComponent(this.selectedArchiveId)}&path=${encodeURIComponent(this.selectedPath)}&format=${encodeURIComponent(format)}`;
            const a = document.createElement('a');
            a.href = url;
            a.download = '';
            document.body.appendChild(a);
            a.click();
            a.remove();
        }

        private async downloadCurrentArchive(): Promise<void> {
            if (!this.selectedArchiveId) return;
            await this.exportAndDownloadArchiveById(this.selectedArchiveId);
        }

        private async exportAndDownloadArchiveById(archiveId: string, version?: number): Promise<boolean> {
            const res = await rpcCall<BinaryExportResponseDto>(RpcMethod.WorkspaceExportArchive, {
                archiveId,
                version,
            });
            if (!res.ok || !res.data?.data) {
                this.showToast(res.data?.error || 'Failed to export WZ archive', ToastKind.Error);
                return false;
            }
            const bytes = toUint8Array(res.data.data);
            const blob = new Blob([bytes as unknown as BlobPart], {
                type: res.data.mimeType || 'application/octet-stream',
            });
            const blobUrl = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = blobUrl;
            a.download = res.data.fileName || 'Archive.wz';
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(() => URL.revokeObjectURL(blobUrl), 10000);
            return true;
        }

        private async requestCloseArchive(archiveId: string): Promise<void> {
            const ar = this.archives.find((a) => a.archiveId === archiveId);
            if (!ar) return;

            if (
                ar.changeState !== RepackerChangeState.Modified &&
                ar.changeState !== RepackerChangeState.Added
            ) {
                await this.performCloseArchive(ar.archiveId);
                return;
            }

            this.openCloseArchivePromptModal(ar);
        }

        private async performCloseArchive(archiveId: string): Promise<void> {
            const arName = this.archives.find((a) => a.archiveId === archiveId)?.name || 'archive';
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceCloseArchive, {
                archiveId,
            });
            if (res.ok) {
                const prefix = `${archiveId}::`;
                for (const key of Array.from(this.treeState.childrenByParent.keys())) {
                    if (key.startsWith(prefix)) {
                        this.treeState.childrenByParent.delete(key);
                    }
                }
                for (const key of Array.from(this.treeState.expandedKeys.values())) {
                    if (key.startsWith(prefix)) {
                        this.treeState.expandedKeys.delete(key);
                    }
                }
                this.showToast(`Closed "${arName}"`, ToastKind.Info);
                this.currentInspection = null;
                if (res.data?.status) {
                    await this.handleWorkspaceStatus(res.data.status);
                }
            } else {
                this.showToast(res.data?.error || 'Failed to close archive', ToastKind.Error);
            }
        }

        private getDirtyArchives(): ArchiveSummaryDto[] {
            return this.archives.filter(
                (a) =>
                    a.changeState === RepackerChangeState.Modified ||
                    a.changeState === RepackerChangeState.Added
            );
        }

        private async saveOrExportSingleArchive(ar: ArchiveSummaryDto): Promise<boolean> {
            let targetFilePath = (ar.filePath || '').trim();
            if (!targetFilePath && this.supportsNativeDialogs) {
                const browseRes = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceBrowseSaveFile, {
                    defaultFileName: ar.name,
                });
                if (browseRes.data?.cancelled || !browseRes.ok || !browseRes.data?.path) {
                    return false;
                }
                targetFilePath = browseRes.data.path;
            }

            if (targetFilePath) {
                const saveRes = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceSaveArchive, {
                    archiveId: ar.archiveId,
                    targetFilePath,
                    version: ar.version,
                });
                if (!saveRes.ok) {
                    this.showToast(saveRes.data?.error || `Failed to save ${ar.name}`, ToastKind.Error);
                    return false;
                }
                this.showToast(`Saved ${ar.name}`, ToastKind.Success);
                if (saveRes.data?.status) {
                    await this.handleWorkspaceStatus(saveRes.data.status);
                }
                return true;
            }

            return this.exportAndDownloadArchiveById(ar.archiveId, ar.version);
        }

        private openCloseArchivePromptModal(ar: ArchiveSummaryDto): void {
            const modText =
                ar.modifiedImageCount > 0
                    ? ` (${ar.modifiedImageCount} modified image${ar.modifiedImageCount === 1 ? '' : 's'})`
                    : '';
            const canSaveDisk = Boolean(ar.filePath) || this.supportsNativeDialogs;
            const saveLabel = canSaveDisk ? 'Save &amp; Close' : 'Export .wz &amp; Close';
            const discardLabel = canSaveDisk ? 'Close Without Saving' : 'Close Without Exporting';

            this.showModal(
                `Unsaved Changes in "${ar.name}"`,
                `
                <p style="margin: 0; color: var(--text-primary); line-height: 1.5;">
                    <strong>${this.escapeHtml(ar.name)}</strong> has unsaved changes${modText}.
                    Do you want to save your changes before closing this archive?
                </p>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn danger" id="btnModalDiscardClose">${discardLabel}</button>
                <button type="button" class="cmd-btn primary" id="btnModalExportClose">${saveLabel}</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalDiscardClose')!.addEventListener('click', async () => {
                this.closeModal();
                await this.performCloseArchive(ar.archiveId);
            });
            document.getElementById('btnModalExportClose')!.addEventListener('click', async () => {
                const saved = await this.saveOrExportSingleArchive(ar);
                if (saved) {
                    this.closeModal();
                    await this.performCloseArchive(ar.archiveId);
                }
            });
        }

        private confirmHostAppClose(): void {
            if (window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage(JSON.stringify({ type: HostWebMessageType.ConfirmAppClose }));
            }
        }

        private handleAppCloseRequest(): void {
            const dirtyArchives = this.getDirtyArchives();
            if (dirtyArchives.length === 0) {
                this.confirmHostAppClose();
                return;
            }
            this.openAppClosePromptModal(dirtyArchives);
        }

        private openAppClosePromptModal(dirtyArchives: ArchiveSummaryDto[]): void {
            let bodyHtml = '';
            if (dirtyArchives.length === 1) {
                const ar = dirtyArchives[0];
                const modText =
                    ar.modifiedImageCount > 0
                        ? ` (${ar.modifiedImageCount} modified image${ar.modifiedImageCount === 1 ? '' : 's'})`
                        : '';
                bodyHtml = `
                    <p style="margin: 0; color: var(--text-primary); line-height: 1.5;">
                        <strong>${this.escapeHtml(ar.name)}</strong> has unsaved changes${modText}.
                        Do you want to save your changes before closing?
                    </p>
                `;
            } else {
                const itemsHtml = dirtyArchives
                    .map((ar) => {
                        const modText =
                            ar.modifiedImageCount > 0
                                ? `${ar.modifiedImageCount} modified image${ar.modifiedImageCount === 1 ? '' : 's'}`
                                : ar.changeState === RepackerChangeState.Added
                                  ? 'New archive'
                                  : 'Modified';
                        return `<li style="margin: 4px 0;"><strong>${this.escapeHtml(ar.name)}</strong> <span style="color: var(--text-muted);">(${this.escapeHtml(modText)})</span></li>`;
                    })
                    .join('');
                bodyHtml = `
                    <p style="margin: 0 0 8px; color: var(--text-primary); line-height: 1.5;">
                        The following <strong>${dirtyArchives.length}</strong> archives have unsaved changes:
                    </p>
                    <ul style="margin: 0 0 10px; padding-left: 20px; color: var(--text-primary); font-size: 12.5px;">
                        ${itemsHtml}
                    </ul>
                    <p style="margin: 0; color: var(--text-primary); line-height: 1.5;">
                        Do you want to save your changes before closing?
                    </p>
                `;
            }

            const saveBtnText = dirtyArchives.length > 1 ? 'Save All &amp; Close' : 'Save &amp; Close';
            this.showModal(
                'Unsaved Changes',
                bodyHtml,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn danger" id="btnModalDiscardAppClose">Close Without Saving</button>
                <button type="button" class="cmd-btn primary" id="btnModalSaveAppClose">${saveBtnText}</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalDiscardAppClose')!.addEventListener('click', () => {
                this.closeModal();
                this.confirmHostAppClose();
            });
            document.getElementById('btnModalSaveAppClose')!.addEventListener('click', async () => {
                const saveBtn = document.getElementById('btnModalSaveAppClose') as HTMLButtonElement | null;
                const discardBtn = document.getElementById('btnModalDiscardAppClose') as HTMLButtonElement | null;
                if (saveBtn) saveBtn.disabled = true;
                if (discardBtn) discardBtn.disabled = true;

                for (const ar of dirtyArchives) {
                    const saved = await this.saveOrExportSingleArchive(ar);
                    if (!saved) {
                        if (saveBtn) saveBtn.disabled = false;
                        if (discardBtn) discardBtn.disabled = false;
                        return;
                    }
                }

                this.closeModal();
                this.confirmHostAppClose();
            });
        }

        // ---------------------------------------------------------------------
        // Modals
        // ---------------------------------------------------------------------

        private showModal(title: string, bodyHtml: string, footerHtml: string): void {
            this.modalCancelCallback = null;
            document.getElementById('modalTitle')!.textContent = title;
            document.getElementById('modalBody')!.innerHTML = bodyHtml;
            document.getElementById('modalFooter')!.innerHTML = footerHtml;
            document.getElementById('modalBackdrop')!.classList.remove('hidden');
        }

        private closeModal(): void {
            document.getElementById('modalBackdrop')!.classList.add('hidden');
            if (this.modalCancelCallback) {
                const cb = this.modalCancelCallback;
                this.modalCancelCallback = null;
                cb();
            }
        }

        private stripNumericShardSuffix(stem: string): string {
            const lastUnderscore = stem.lastIndexOf('_');
            if (lastUnderscore <= 0 || lastUnderscore === stem.length - 1) {
                return stem;
            }
            const suffix = stem.slice(lastUnderscore + 1);
            if (!/^\d+$/.test(suffix)) {
                return stem;
            }
            return stem.slice(0, lastUnderscore);
        }

        private isSameOrCanvasStem(stemA: string, stemB: string): boolean {
            const baseA = this.stripNumericShardSuffix(stemA.toLowerCase());
            const baseB = this.stripNumericShardSuffix(stemB.toLowerCase());
            return (
                baseA === baseB ||
                baseA === `${baseB}_canvas` ||
                baseA === `${baseB}__canvas` ||
                baseB === `${baseA}_canvas` ||
                baseB === `${baseA}__canvas`
            );
        }

        private normalizePathForCompare(p: string): string {
            return p.trim().replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
        }

        private getDirPart(normPath: string): string {
            const lastSlash = normPath.lastIndexOf('/');
            return lastSlash >= 0 ? normPath.slice(0, lastSlash) : '';
        }

        private findExistingOpenArchive(filePathOrName: string): ArchiveSummaryDto | undefined {
            const rawTrimmed = filePathOrName.trim();
            if (!rawTrimmed) return undefined;

            const normTarget = this.normalizePathForCompare(rawTrimmed);
            const targetDir = this.getDirPart(normTarget);
            const basePart = normTarget.split('/').pop() || normTarget;
            const cleanName = basePart.endsWith('.wz') ? basePart : `${basePart}.wz`;
            const targetStem = cleanName.slice(0, -3);
            const targetBaseStem = this.stripNumericShardSuffix(targetStem);

            for (const a of this.archives) {
                const aLowerName = a.name.toLowerCase();
                const aStem = aLowerName.endsWith('.wz') ? aLowerName.slice(0, -3) : aLowerName;
                const aNormPath = a.filePath ? this.normalizePathForCompare(a.filePath) : '';
                const aDir = aNormPath ? this.getDirPart(aNormPath) : '';

                if ((aNormPath && aNormPath === normTarget) || aLowerName === cleanName) {
                    return a;
                }

                // Same-directory (or pathless) shard / _Canvas stem match (e.g. Item_000.wz when Item.wz is open)
                if ((!targetDir || !aDir || targetDir === aDir) && this.isSameOrCanvasStem(targetStem, aStem)) {
                    return a;
                }

                // Check all linked file paths belonging to this open archive
                const linkedPaths = a.linkedFilePaths || [];
                for (const lp of linkedPaths) {
                    const normLp = this.normalizePathForCompare(lp);
                    if (normLp === normTarget) {
                        return a;
                    }
                    const lpFile = normLp.split('/').pop() || '';
                    if (!targetDir && lpFile === cleanName) {
                        return a;
                    }
                    const lpDir = this.getDirPart(normLp);
                    const lpStem = lpFile.endsWith('.wz') ? lpFile.slice(0, -3) : lpFile;
                    if (targetDir && lpDir === targetDir && this.isSameOrCanvasStem(targetStem, lpStem)) {
                        return a;
                    }
                }

                // Parent / subdirectory containment (e.g. Cash_000.wz inside Data/Item when Data/Item/Item.wz is open, or vice versa)
                if (aDir && targetDir) {
                    if (targetDir.startsWith(`${aDir}/`)) {
                        return a;
                    }
                    if (aDir.startsWith(`${targetDir}/`)) {
                        const targetFolderName = targetDir.split('/').pop() || '';
                        if (targetFolderName === targetBaseStem) {
                            return a;
                        }
                    }
                }
            }

            return undefined;
        }

        private clearArchiveTreeCache(archiveId: string): void {
            const prefix = `${archiveId}::`;
            for (const key of Array.from(this.treeState.childrenByParent.keys())) {
                if (key.startsWith(prefix)) {
                    this.treeState.childrenByParent.delete(key);
                }
            }
            for (const key of Array.from(this.treeState.expandedKeys.values())) {
                if (key.startsWith(prefix)) {
                    this.treeState.expandedKeys.delete(key);
                }
            }
            this.treeState.expandedKeys.add(this.makeCacheKey(archiveId, ''));
        }

        private confirmReloadArchive(existing: ArchiveSummaryDto, requestedPathOrName?: string): Promise<boolean> {
            return new Promise<boolean>((resolve) => {
                let settled = false;
                const finish = (confirmed: boolean) => {
                    if (settled) return;
                    settled = true;
                    this.modalCancelCallback = null;
                    this.closeModal();
                    resolve(confirmed);
                };

                const reqBase = (requestedPathOrName || '').trim().split(/[\\/]/).pop() || existing.name;
                const isSameFile = reqBase.toLowerCase() === existing.name.toLowerCase();
                const openDescription = isSameFile
                    ? `<strong>${this.escapeHtml(existing.name)}</strong> is already open in the workspace.`
                    : `<strong>${this.escapeHtml(reqBase)}</strong> is already open in the workspace (linked with <strong>${this.escapeHtml(existing.name)}</strong>).`;

                const hasUnsaved =
                    existing.changeState === RepackerChangeState.Modified ||
                    existing.changeState === RepackerChangeState.Added;
                const unsavedNote = hasUnsaved
                    ? `<p style="margin: 8px 0 0; color: var(--amber); font-size: 12px;">Warning: This archive has unsaved changes that will be discarded if you reload it.</p>`
                    : '';

                this.showModal(
                    `Archive Already Open`,
                    `
                    <p style="margin: 0; color: var(--text-primary); line-height: 1.5;">
                        ${openDescription}
                        Do you want to reload it and replace the currently open archive?
                    </p>
                    ${unsavedNote}
                    `,
                    `
                    <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                    <button type="button" class="cmd-btn ${hasUnsaved ? 'danger' : 'primary'}" id="btnModalConfirmReload">Reload Archive</button>
                    `
                );

                this.modalCancelCallback = () => finish(false);
                document.getElementById('btnModalCancel')!.addEventListener('click', () => finish(false));
                document.getElementById('btnModalConfirmReload')!.addEventListener('click', () => finish(true));
            });
        }

        private triggerOpenWzFile(): void {
            if (this.supportsNativeDialogs) {
                void this.browseNativeWzFile();
            } else {
                (document.getElementById('hiddenWzFileInput') as HTMLInputElement).click();
            }
        }

        private async browseNativeWzFile(): Promise<void> {
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceBrowseFiles, {});
            if (res.data?.cancelled) {
                return;
            }
            if (!res.ok) {
                this.showToast(res.data?.error || 'Could not browse files', ToastKind.Error);
                return;
            }

            const filePaths = res.data.filePaths || [];
            if (filePaths.length === 0) {
                return;
            }

            const openedNames: string[] = [];
            let lastArchiveId = '';

            for (const filePath of filePaths) {
                const existing = this.findExistingOpenArchive(filePath);
                if (existing) {
                    const confirmed = await this.confirmReloadArchive(existing, filePath);
                    if (!confirmed) {
                        continue;
                    }
                }

                const openRes = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceOpenFile, { filePath });
                if (openRes.ok && openRes.data.archiveId) {
                    this.clearArchiveTreeCache(openRes.data.archiveId);
                    lastArchiveId = openRes.data.archiveId;
                    const name = openRes.data.archive?.name || filePath.split(/[\\/]/).pop() || filePath;
                    openedNames.push(name);
                    await this.fetchTreeChildren(openRes.data.archiveId, '');
                } else {
                    this.showToast(openRes.data?.error || `Could not open ${filePath}`, ToastKind.Error);
                }
            }

            if (lastArchiveId) {
                const label =
                    openedNames.length === 1
                        ? openedNames[0]
                        : `${openedNames.length} WZ archives`;
                this.showToast(`Opened ${label}`, ToastKind.Success);
                await this.selectAndInspectNode(lastArchiveId, '', true);
            }
        }

        private triggerOpenDirectory(): void {
            if (this.supportsNativeDialogs) {
                void this.browseNativeDirectory();
            } else {
                this.openDirectoryModal();
            }
        }

        private async browseNativeDirectory(): Promise<void> {
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceBrowseAndOpenDirectory, {});
            if (res.data?.cancelled) {
                return;
            }
            if (res.ok) {
                this.treeState.childrenByParent.clear();
                const count = res.data.archives?.length ?? 0;
                this.showToast(`Loaded ${count} WZ archive${count === 1 ? '' : 's'}`, ToastKind.Success);
                if (res.data.archiveId) {
                    await this.fetchTreeChildren(res.data.archiveId, '');
                    await this.selectAndInspectNode(res.data.archiveId, '', true);
                }
            } else {
                this.showToast(res.data?.error || 'Failed to open directory', ToastKind.Error);
            }
        }

        private openDirectoryModal(): void {
            this.showModal(
                'Open MapleStory Data Directory',
                `
                <div class="form-row">
                    <label>Directory Path (Scans recursively for .wz archives)</label>
                    <input type="text" id="modalDirPathInput" class="input-full" placeholder="C:\\Nexon\\MapleStory\\Data" />
                </div>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn primary" id="btnModalConfirm">Load All .wz Archives</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalConfirm')!.addEventListener('click', async () => {
                const directoryPath = (document.getElementById('modalDirPathInput') as HTMLInputElement).value.trim();
                if (!directoryPath) return;
                const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceOpenDirectory, { directoryPath });
                if (res.ok) {
                    this.closeModal();
                    this.treeState.childrenByParent.clear();
                    this.showToast(`Loaded WZ archives from ${directoryPath}`, ToastKind.Success);
                    if (res.data.archiveId) {
                        await this.fetchTreeChildren(res.data.archiveId, '');
                        await this.selectAndInspectNode(res.data.archiveId, '', true);
                    }
                } else {
                    this.showToast(res.data?.error || 'Failed to open directory', ToastKind.Error);
                }
            });
        }

        private openNewArchiveModal(): void {
            this.showModal(
                'Create New WZ Archive',
                `
                <div class="form-row">
                    <label>Archive Name</label>
                    <input type="text" id="modalNewArchiveName" class="input-full" value="Custom_000.wz" />
                </div>
                <div class="form-row">
                    <label>MapleStory WZ Version</label>
                    <input type="number" id="modalNewArchiveVer" class="input-full" value="779" />
                </div>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn primary" id="btnModalConfirm">Create Archive</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalConfirm')!.addEventListener('click', async () => {
                const name = (document.getElementById('modalNewArchiveName') as HTMLInputElement).value.trim();
                const version = Number((document.getElementById('modalNewArchiveVer') as HTMLInputElement).value) || 779;
                const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceCreateArchive, { name, version });
                if (res.ok && res.data.archiveId) {
                    this.closeModal();
                    this.showToast(`Created archive ${name}`, ToastKind.Success);
                    await this.fetchTreeChildren(res.data.archiveId, '');
                    await this.selectAndInspectNode(res.data.archiveId, '', true);
                }
            });
        }

        private openSaveRepackModal(): void {
            const currentAr = this.archives.find((a) => a.archiveId === this.selectedArchiveId);
            if (!currentAr) return;

            this.showModal(
                `Repack & Save "${currentAr.name}"`,
                `
                <div class="form-row">
                    <label>Target Disk File Path (Optional for direct disk write)</label>
                    <input type="text" id="modalSaveFilePath" class="input-full" value="${this.escapeHtml(currentAr.filePath)}" placeholder="/path/to/${this.escapeHtml(currentAr.name)}" />
                </div>
                <div class="form-row">
                    <label>Target WZ Version</label>
                    <input type="number" id="modalSaveWzVer" class="input-full" value="${currentAr.version}" />
                </div>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn accent" id="btnModalDownloadWz">Download .wz File</button>
                <button type="button" class="cmd-btn primary" id="btnModalSaveDisk">Save to Disk Path</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalDownloadWz')!.addEventListener('click', () => {
                this.closeModal();
                void this.downloadCurrentArchive();
            });
            document.getElementById('btnModalSaveDisk')!.addEventListener('click', async () => {
                const targetFilePath = (document.getElementById('modalSaveFilePath') as HTMLInputElement).value.trim();
                const version = Number((document.getElementById('modalSaveWzVer') as HTMLInputElement).value) || currentAr.version;
                const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceSaveArchive, {
                    archiveId: currentAr.archiveId,
                    targetFilePath,
                    version,
                });
                if (res.ok) {
                    this.closeModal();
                    this.showToast(`Repacked and saved ${currentAr.name}`, ToastKind.Success);
                } else {
                    this.showToast(res.data?.error || 'Failed to save archive to disk', ToastKind.Error);
                }
            });
        }

        private openBulkExportModal(): void {
            const currentAr = this.archives.find((a) => a.archiveId === this.selectedArchiveId);
            if (!currentAr) {
                this.showToast('No WZ archive is currently selected', ToastKind.Error);
                return;
            }

            const scopeLabel = this.selectedPath
                ? `${currentAr.name} / ${this.selectedPath}`
                : `${currentAr.name} (Entire Archive)`;
            const browseBtnHtml = this.supportsNativeDialogs
                ? `<button type="button" class="cmd-btn" id="btnModalBrowseExportDir" style="flex-shrink: 0;">Browse...</button>`
                : '';

            this.showModal(
                'Bulk Asset Export to Directory',
                `
                <div class="form-row">
                    <label>Export Scope (Selected Tree Node)</label>
                    <div style="padding: 6px 10px; background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: 4px; font-family: var(--font-mono); font-size: 11.5px; color: var(--text-primary); word-break: break-all;">
                        ${this.escapeHtml(scopeLabel)}
                    </div>
                </div>
                <div class="form-row">
                    <label>Export Format</label>
                    <select id="modalBulkExportMode" class="select-full">
                        <option value="${RepackerBatchExportMode.Png}">All Canvas Sprites (.png)</option>
                        <option value="${RepackerBatchExportMode.Sound}">All Sound_DX8 Audio (.wav / .mp3)</option>
                        <option value="${RepackerBatchExportMode.Json}">All Subtrees / Images (.json)</option>
                        <option value="${RepackerBatchExportMode.Img}">All WZ Images (.img)</option>
                        <option value="${RepackerBatchExportMode.All}">Everything (.png + audio + .json)</option>
                    </select>
                </div>
                <div class="form-row">
                    <label>Target Output Directory</label>
                    <div style="display: flex; gap: 6px; align-items: center;">
                        <input type="text" id="modalBulkExportDir" class="input-full" value="${this.escapeHtml(this.lastBulkExportDir)}" placeholder="C:\\Exports\\MapleAssets" />
                        ${browseBtnHtml}
                    </div>
                </div>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn primary" id="btnModalConfirmBulkExport">Run Bulk Export</button>
                `
            );

            const modeSel = document.getElementById('modalBulkExportMode') as HTMLSelectElement;
            modeSel.value = this.lastBulkExportMode;

            const dirInput = document.getElementById('modalBulkExportDir') as HTMLInputElement;
            const browseBtn = document.getElementById('btnModalBrowseExportDir');
            if (browseBtn) {
                browseBtn.addEventListener('click', async () => {
                    const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceBrowseDirectory, {});
                    if (res.ok && res.data?.path) {
                        dirInput.value = res.data.path;
                    }
                });
            }

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalConfirmBulkExport')!.addEventListener('click', async () => {
                const outDir = dirInput.value.trim();
                const mode = (modeSel.value as RepackerBatchExportMode) || RepackerBatchExportMode.Png;
                if (!outDir) {
                    this.showToast('Enter a target output directory path', ToastKind.Error);
                    return;
                }

                this.lastBulkExportDir = outDir;
                this.lastBulkExportMode = mode;

                const confirmBtn = document.getElementById('btnModalConfirmBulkExport') as HTMLButtonElement;
                confirmBtn.disabled = true;

                const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeBatchExport, {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    outputDirectory: outDir,
                    mode,
                });
                confirmBtn.disabled = false;

                if (res.ok) {
                    this.closeModal();
                    this.showToast(`Batch-exported ${res.data.count ?? 0} file(s) to ${outDir}`, ToastKind.Success);
                } else {
                    this.showToast(res.data?.error || 'Batch export failed', ToastKind.Error);
                }
            });
        }

        private openInlineOutlinksModal(): void {
            const currentAr = this.archives.find((a) => a.archiveId === this.selectedArchiveId);
            if (!currentAr) {
                this.showToast('No WZ archive is currently selected', ToastKind.Error);
                return;
            }

            const scopeLabel = this.selectedPath
                ? `${currentAr.name} / ${this.selectedPath}`
                : `${currentAr.name} (Entire Archive)`;

            this.showModal(
                'Export / Inline _outlink & _inlink Sprites',
                `
                <p style="margin: 0 0 10px; color: var(--text-secondary); font-size: 12px; line-height: 1.5;">
                    Resolves external <code>_outlink</code> and internal <code>_inlink</code> sprite references within the selected scope by baking their target pixel data directly into the Canvas nodes, making the archive or image self-contained.
                </p>
                <div class="form-row">
                    <label>Target Scope</label>
                    <div style="padding: 6px 10px; background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: 4px; font-family: var(--font-mono); font-size: 11.5px; color: var(--text-primary); word-break: break-all;">
                        ${this.escapeHtml(scopeLabel)}
                    </div>
                </div>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn" id="btnModalInlineOnly">Inline in Workspace</button>
                <button type="button" class="cmd-btn primary" id="btnModalInlineAndExport">Inline &amp; Export .wz</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalInlineOnly')!.addEventListener('click', async () => {
                this.closeModal();
                await this.inlineLinksInCurrentScope();
            });
            document.getElementById('btnModalInlineAndExport')!.addEventListener('click', async () => {
                const inlineBtn = document.getElementById('btnModalInlineOnly') as HTMLButtonElement;
                const exportBtn = document.getElementById('btnModalInlineAndExport') as HTMLButtonElement;
                inlineBtn.disabled = true;
                exportBtn.disabled = true;

                await this.inlineLinksInCurrentScope();
                this.closeModal();
                await this.downloadCurrentArchive();
            });
        }

        private openAddPropertyModal(): void {
            if (!this.selectedArchiveId) return;
            this.showModal(
                'Add WZ Property',
                `
                <div class="form-row">
                    <label>Property Name</label>
                    <input type="text" id="modalPropName" class="input-full" placeholder="e.g. incSTR, origin, stand1, icon" />
                </div>
                <div class="form-row">
                    <label>Property Type</label>
                    <select id="modalPropKind" class="select-full">
                        <option value="Int">Int (Compressed Int32)</option>
                        <option value="Short">Short (Int16)</option>
                        <option value="Long">Long (Int64)</option>
                        <option value="Float">Float (Single)</option>
                        <option value="Double">Double (Float64)</option>
                        <option value="String">String</option>
                        <option value="Vector">Vector2D (X, Y)</option>
                        <option value="Uol">UOL (Relative Path Link)</option>
                        <option value="Object">SubProperty (Object Container)</option>
                        <option value="Canvas">Canvas (2D Sprite / PNG)</option>
                        <option value="Sound">Sound_DX8 (Audio Effect / BGM)</option>
                        <option value="Null">Null</option>
                    </select>
                </div>
                <div class="form-row" id="modalScalarRow">
                    <label>Initial Value</label>
                    <input type="text" id="modalPropValue" class="input-full" value="0" />
                </div>
                <div class="form-row two-col hidden" id="modalVectorRow">
                    <div>
                        <label>Vector X</label>
                        <input type="number" id="modalVecX" class="input-full" value="0" />
                    </div>
                    <div>
                        <label>Vector Y</label>
                        <input type="number" id="modalVecY" class="input-full" value="0" />
                    </div>
                </div>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn primary" id="btnModalConfirm">Add Property</button>
                `
            );

            const kindSel = document.getElementById('modalPropKind') as HTMLSelectElement;
            kindSel.addEventListener('change', () => {
                const k = kindSel.value as RepackerPropertyKind;
                document
                    .getElementById('modalVectorRow')!
                    .classList.toggle('hidden', k !== RepackerPropertyKind.Vector && k !== RepackerPropertyKind.Canvas);
                document.getElementById('modalScalarRow')!.classList.toggle(
                    'hidden',
                    k === RepackerPropertyKind.Vector ||
                        k === RepackerPropertyKind.Object ||
                        k === RepackerPropertyKind.Canvas ||
                        k === RepackerPropertyKind.Sound ||
                        k === RepackerPropertyKind.Null
                );
            });

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalConfirm')!.addEventListener('click', async () => {
                const name = (document.getElementById('modalPropName') as HTMLInputElement).value.trim();
                const kind = kindSel.value as RepackerPropertyKind;
                const rawValue = (document.getElementById('modalPropValue') as HTMLInputElement).value;
                const vecX = Number((document.getElementById('modalVecX') as HTMLInputElement).value) || 0;
                const vecY = Number((document.getElementById('modalVecY') as HTMLInputElement).value) || 0;

                const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeAddProperty, {
                    archiveId: this.selectedArchiveId,
                    parentPath: this.selectedPath,
                    name,
                    kind,
                    rawValue,
                    vecX,
                    vecY,
                });
                if (res.ok && res.data.path) {
                    this.closeModal();
                    this.showToast(`Added ${kind} "${name}"`, ToastKind.Success);
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                    await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
                } else {
                    this.showToast(res.data?.error || 'Failed to add property', ToastKind.Error);
                }
            });
        }

        private openAddImageModal(): void {
            if (!this.selectedArchiveId) return;
            this.showModal(
                'Add WZ Image (.img)',
                `
                <div class="form-row">
                    <label>Image Name</label>
                    <input type="text" id="modalImgName" class="input-full" placeholder="e.g. 01040001.img" />
                </div>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn primary" id="btnModalConfirm">Create .img</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalConfirm')!.addEventListener('click', async () => {
                const imageName = (document.getElementById('modalImgName') as HTMLInputElement).value.trim();
                const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeAddImage, {
                    archiveId: this.selectedArchiveId,
                    parentPath: this.selectedPath,
                    imageName,
                });
                if (res.ok && res.data.path) {
                    this.closeModal();
                    this.showToast(`Created image "${res.data.path}"`, ToastKind.Success);
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                    await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
                } else {
                    this.showToast(res.data?.error || 'Failed to add image', ToastKind.Error);
                }
            });
        }

        private openAddDirectoryModal(): void {
            if (!this.selectedArchiveId) return;
            this.showModal(
                'Add WZ Directory',
                `
                <div class="form-row">
                    <label>Directory Name</label>
                    <input type="text" id="modalDirName" class="input-full" placeholder="e.g. Cap, Cape, Glove" />
                </div>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn primary" id="btnModalConfirm">Create Directory</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalConfirm')!.addEventListener('click', async () => {
                const dirName = (document.getElementById('modalDirName') as HTMLInputElement).value.trim();
                const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeAddDirectory, {
                    archiveId: this.selectedArchiveId,
                    parentPath: this.selectedPath,
                    dirName,
                });
                if (res.ok && res.data.path) {
                    this.closeModal();
                    this.showToast(`Created directory "${res.data.path}"`, ToastKind.Success);
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                    await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
                } else {
                    this.showToast(res.data?.error || 'Failed to create directory', ToastKind.Error);
                }
            });
        }

        private openRenameModal(): void {
            if (!this.selectedArchiveId || !this.currentInspection) return;
            this.showModal(
                `Rename "${this.currentInspection.name}"`,
                `
                <div class="form-row">
                    <label>New Name</label>
                    <input type="text" id="modalRenameInput" class="input-full" value="${this.escapeHtml(this.currentInspection.name)}" />
                </div>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn primary" id="btnModalConfirm">Rename</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalConfirm')!.addEventListener('click', async () => {
                const newName = (document.getElementById('modalRenameInput') as HTMLInputElement).value.trim();
                const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeRename, {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    newName,
                });
                if (res.ok) {
                    this.closeModal();
                    const newPath = res.data.path ?? '';
                    this.showToast(`Renamed to "${newName}"`, ToastKind.Success);
                    this.treeState.childrenByParent.clear();
                    await this.selectAndInspectNode(this.selectedArchiveId, newPath, true);
                } else {
                    this.showToast(res.data?.error || 'Rename failed', ToastKind.Error);
                }
            });
        }

        private escapeHtml(str: string | null | undefined): string {
            if (!str) return '';
            return String(str)
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;');
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        new RepackerStudioApp();
    });
}
