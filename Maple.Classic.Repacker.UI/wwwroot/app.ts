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
        private animPlaying = true;
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

        // Hex Inspector State
        private activeHexBytes: Uint8Array = new Uint8Array(0);
        private selectedHexOffset = 0;

        // Context Menu Target
        private ctxTargetArchiveId = '';
        private ctxTargetPath = '';
        private modalCancelCallback: (() => void) | null = null;

        constructor() {
            this.spriteCanvas = document.getElementById('spriteCanvas') as HTMLCanvasElement;
            this.spriteCtx = this.spriteCanvas.getContext('2d')!;

            this.layout = new LayoutController({
                onLayoutChanged: (layoutDto) => {
                    void rpcCall('ui.saveState', { uiState: { layout: layoutDto } });
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

        private showToast(message: string, kind: 'info' | 'success' | 'error' = 'info'): void {
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
            const res = await rpcCall<TreeChildrenResponse>('tree.children', {
                archiveId,
                path: parentPath,
            });
            const nodes = res.ok && res.data?.nodes ? res.data.nodes : [];
            this.treeState.childrenByParent.set(this.makeCacheKey(archiveId, parentPath), nodes);
            return nodes;
        }

        private async invalidateAndRefreshTreeBranch(archiveId: string, path: string): Promise<void> {
            // Refresh root + ancestor chain along path
            await this.fetchTreeChildren(archiveId, '');
            const parts = path.split('/').filter((p) => p.length > 0);
            let accum = '';
            for (const part of parts) {
                accum = accum ? `${accum}/${part}` : part;
                const key = this.makeCacheKey(archiveId, accum);
                if (this.treeState.expandedKeys.has(key) || accum === path) {
                    await this.fetchTreeChildren(archiveId, accum);
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

            if (!targetPath) {
                this.renderTree();
                return;
            }

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
            this.renderTree();
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

                const rootStatusDot = ar.isAdded
                    ? '<span class="tree-add-dot" title="Added"></span>'
                    : ar.isModified
                      ? '<span class="tree-mod-dot" title="Modified"></span>'
                      : '';

                rootRow.innerHTML = `
                    <span class="tree-twisty">${isRootExpanded ? '&#9662;' : '&#9656;'}</span>
                    <span class="tree-badge badge-WZ">WZ</span>
                    <span class="tree-name">${this.escapeHtml(ar.name)}</span>
                    <span class="tree-val-preview">v${ar.version} &bull; ${ar.imageCount} img</span>
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
                    this.openContextMenu(ev.clientX, ev.clientY, ar.archiveId, '');
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
                    const selfMatches =
                        node.name.toLowerCase().includes(filterLower) ||
                        node.displayValue.toLowerCase().includes(filterLower) ||
                        node.path.toLowerCase().includes(filterLower);
                    const descendantMatches = this.hasMatchingDescendant(archiveId, cachedKids, filterLower);
                    if (!selfMatches && !descendantMatches) {
                        continue;
                    }
                }

                const isSelected = archiveId === this.selectedArchiveId && node.path === this.selectedPath;
                const row = document.createElement('div');
                row.className = `tree-row${isSelected ? ' selected' : ''}`;
                row.style.paddingLeft = `${6 + depth * 15}px`;
                row.dataset.archiveId = archiveId;
                row.dataset.path = node.path;

                const twistyChar = node.hasChildren ? (isExpanded ? '&#9662;' : '&#9656;') : '';
                const valPreview = node.displayValue ? `<span class="tree-val-preview">${this.escapeHtml(node.displayValue)}</span>` : '';
                const statusDot = node.isAdded
                    ? '<span class="tree-add-dot" title="Added"></span>'
                    : node.isModified
                      ? '<span class="tree-mod-dot" title="Modified"></span>'
                      : '';

                row.innerHTML = `
                    <span class="tree-twisty">${twistyChar}</span>
                    <span class="tree-badge badge-${this.escapeHtml(node.typeBadge)}">${this.escapeHtml(node.typeBadge)}</span>
                    <span class="tree-name">${this.escapeHtml(node.name)}</span>
                    ${valPreview}
                    ${statusDot}
                `;

                row.addEventListener('click', (ev) => {
                    this.closeContextMenu();
                    const clickedTwisty = (ev.target as HTMLElement).classList.contains('tree-twisty');
                    void this.toggleOrSelectTreeNode(archiveId, node.path, node.hasChildren, clickedTwisty);
                });

                row.addEventListener('dblclick', () => {
                    this.closeContextMenu();
                    if (node.hasChildren) {
                        void this.toggleNodeExpansionOnly(archiveId, node.path);
                    }
                });

                row.addEventListener('contextmenu', (ev) => {
                    ev.preventDefault();
                    ev.stopPropagation();
                    this.openContextMenu(ev.clientX, ev.clientY, archiveId, node.path);
                });

                container.appendChild(row);

                if (isExpanded && cachedKids.length > 0) {
                    this.renderTreeNodesRecursive(container, archiveId, cachedKids, depth + 1, filterLower);
                }
            }
        }

        private hasMatchingDescendant(archiveId: string, nodes: TreeNodeDto[], filterLower: string): boolean {
            for (const n of nodes) {
                if (
                    n.name.toLowerCase().includes(filterLower) ||
                    n.displayValue.toLowerCase().includes(filterLower)
                ) {
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

            const res = await rpcCall<NodeInspectionDto>('node.inspect', {
                archiveId,
                path,
            });
            if (!res.ok || !res.data) {
                this.showToast(res.data?.error || 'Failed to inspect node', 'error');
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

            // 2. Canvas / Animation Viewport
            await this.renderCanvasOrAnimationStage(dto);

            // 3. Sound_DX8 Audio Studio
            this.renderSoundStage(dto);

            // 4. Child Nodes Table
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
            const changeChip = dto.isAdded
                ? '<span class="meta-chip" style="color:var(--accent-green); border-color:var(--accent-green-border); background:var(--accent-green-bg)">ADDED</span>'
                : dto.isModified
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
                fmtSelect.value = String(dto.canvas.format || 2);
            }

            // Decode frames into HTMLImageElements
            if (hasAnimFrames && !hasSingleCanvas) {
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
            this.renderAnimationPlayerBar();
            this.drawActiveCanvasFrame();

            if (this.decodedFrames.length > 1 && this.animPlaying) {
                this.scheduleNextAnimationFrame();
            }
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

        private renderAnimationPlayerBar(): void {
            const bar = document.getElementById('animPlayerBar')!;
            const strip = document.getElementById('animTimelineStrip')!;
            strip.innerHTML = '';

            if (this.decodedFrames.length <= 1) {
                bar.classList.add('hidden');
                return;
            }
            bar.classList.remove('hidden');

            const playBtn = document.getElementById('btnAnimPlayPause')!;
            playBtn.textContent = this.animPlaying ? 'Pause' : 'Play';

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

            // Anchor origin at center of viewport + user pan offset
            const anchorX = Math.floor(cw * 0.5 + this.panX);
            const anchorY = Math.floor(ch * 0.62 + this.panY);

            const drawX = Math.floor(anchorX - frame.origin.x * this.zoomScale);
            const drawY = Math.floor(anchorY - frame.origin.y * this.zoomScale);
            const drawW = Math.floor(frame.width * this.zoomScale);
            const drawH = Math.floor(frame.height * this.zoomScale);

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

            document.getElementById('soundTitle')!.textContent = `${dto.name} (${s.formatName})`;
            document.getElementById('soundSubtitle')!.textContent = `${dto.archiveName} • ${dto.path}`;

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

        private renderChildrenTable(dto: NodeInspectionDto): void {
            const titleEl = document.getElementById('childrenTableTitle')!;
            const metaEl = document.getElementById('childrenTableMeta')!;
            const tbody = document.getElementById('childrenTableBody')!;
            tbody.innerHTML = '';

            const children = dto.children || [];
            titleEl.textContent = `Child Nodes (${children.length})`;
            metaEl.textContent = children.length > 0 ? 'Click a row to inspect' : 'Leaf property node';

            for (const c of children) {
                const tr = document.createElement('tr');
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
                    void this.selectAndInspectNode(dto.archiveId, c.path, true);
                });
                tbody.appendChild(tr);
            }
        }

        private renderPropertyInspector(dto: NodeInspectionDto): void {
            document.getElementById('inspectorTypePill')!.textContent = dto.typeBadge;

            const isArchiveRoot = !dto.path || dto.nodeKind === RepackerNodeKind.Archive;
            document.getElementById('btnRenameNode')!.classList.toggle('hidden', isArchiveRoot);
            const deleteBtn = document.getElementById('btnDeleteNode')!;
            deleteBtn.textContent = isArchiveRoot ? 'Close' : 'Delete';
            deleteBtn.title = isArchiveRoot ? 'Close WZ archive (Del)' : 'Delete selected node (Del)';

            (document.getElementById('btnInspAddChild') as HTMLButtonElement).disabled = isArchiveRoot;
            (document.getElementById('btnInspDuplicate') as HTMLButtonElement).disabled = isArchiveRoot;
            (document.getElementById('btnInspCopy') as HTMLButtonElement).disabled = isArchiveRoot;

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
                if (!target?.closest('.tree-row') && !target?.closest('#treeContextMenu')) {
                    this.closeContextMenu();
                }
            });

            menuBar.querySelectorAll('[data-action]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const action = (btn as HTMLElement).dataset.action || '';
                    items.forEach((other) => other.classList.remove('open'));
                    this.dispatchMenuAction(action);
                });
            });

            // Toolbar buttons
            document.getElementById('btnUndo')!.addEventListener('click', () => void this.undoSelectedImage());
            document.getElementById('btnRedo')!.addEventListener('click', () => void this.redoSelectedImage());
            document.getElementById('btnInlineLinks')!.addEventListener('click', () => void this.inlineLinksInCurrentScope());

            // Hidden browser file input handlers
            const wzInput = document.getElementById('hiddenWzFileInput') as HTMLInputElement;
            wzInput.addEventListener('change', async () => {
                const files = Array.from(wzInput.files || []);
                wzInput.value = '';
                if (files.length === 0) return;
                let lastArchiveId = '';
                for (const file of files) {
                    const existing = this.findExistingOpenArchive(file.name);
                    if (existing) {
                        const confirmed = await this.confirmReloadArchive(existing);
                        if (!confirmed) {
                            continue;
                        }
                    }
                    const bytes = new Uint8Array(await file.arrayBuffer());
                    const res = await rpcCall<RpcOperationResponse>('workspace.openBytes', {
                        fileName: file.name,
                        data: bytes,
                    });
                    if (res.ok && res.data.archiveId) {
                        this.clearArchiveTreeCache(res.data.archiveId);
                        lastArchiveId = res.data.archiveId;
                        this.showToast(`Loaded ${file.name}`, 'success');
                        await this.fetchTreeChildren(res.data.archiveId, '');
                    } else {
                        this.showToast(res.data?.error || `Failed to open ${file.name}`, 'error');
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
                const fmt = Number((document.getElementById('canvasFormatSelect') as HTMLSelectElement).value) || 2;
                const res = await rpcCall<RpcOperationResponse>('node.replaceCanvas', {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    pngBytes: bytes,
                    format: fmt,
                });
                if (res.ok) {
                    this.showToast(`Replaced Canvas with ${file.name}`, 'success');
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                    await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
                } else {
                    this.showToast(res.data?.error || 'Failed to import PNG', 'error');
                }
            });

            const audioInput = document.getElementById('hiddenAudioFileInput') as HTMLInputElement;
            audioInput.addEventListener('change', async () => {
                const file = audioInput.files?.[0];
                audioInput.value = '';
                if (!file || !this.selectedArchiveId || !this.selectedPath) return;
                const bytes = new Uint8Array(await file.arrayBuffer());
                const res = await rpcCall<RpcOperationResponse>('node.replaceSound', {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    audioBytes: bytes,
                });
                if (res.ok) {
                    this.showToast(`Replaced Sound_DX8 with ${file.name}`, 'success');
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                    await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
                } else {
                    this.showToast(res.data?.error || 'Failed to import audio', 'error');
                }
            });

            document.getElementById('btnCloseModal')!.addEventListener('click', () => this.closeModal());
            document.getElementById('modalBackdrop')!.addEventListener('click', (ev) => {
                if (ev.target === document.getElementById('modalBackdrop')) {
                    this.closeModal();
                }
            });
        }

        private dispatchMenuAction(action: string): void {
            switch (action) {
                case 'openFileModal':
                    this.triggerOpenWzFile();
                    break;
                case 'openDirModal':
                    this.triggerOpenDirectory();
                    break;
                case 'newArchiveModal':
                    this.openNewArchiveModal();
                    break;
                case 'saveArchiveDisk':
                    this.openSaveRepackModal();
                    break;
                case 'downloadArchive':
                    void this.downloadCurrentArchive();
                    break;
                case 'closeActiveArchive':
                    void this.closeActiveArchive();
                    break;
                case 'undoNode':
                    void this.undoSelectedImage();
                    break;
                case 'redoNode':
                    void this.redoSelectedImage();
                    break;
                case 'addPropertyModal':
                    this.openAddPropertyModal();
                    break;
                case 'addImageModal':
                    this.openAddImageModal();
                    break;
                case 'addDirectoryModal':
                    this.openAddDirectoryModal();
                    break;
                case 'copyNode':
                    void this.copySelectedNode();
                    break;
                case 'pasteNode':
                    void this.pasteIntoSelectedNode();
                    break;
                case 'duplicateNode':
                    void this.duplicateSelectedNode();
                    break;
                case 'renameNodePrompt':
                    this.openRenameModal();
                    break;
                case 'deleteNode':
                    void this.deleteSelectedNode();
                    break;
                case 'focusGlobalSearch':
                    this.focusGlobalSearch();
                    break;
                case 'inlineOutlinksNode':
                    void this.inlineLinksInCurrentScope();
                    break;
                case 'openBatchExportTab':
                    this.activateWorkbenchTab('batch');
                    break;
                case 'exportSelectedJson':
                    void this.exportCurrentNode('json');
                    break;
                case 'exportSelectedImg':
                    void this.exportCurrentNode('img');
                    break;
                case 'toggleCrosshair': {
                    const chk = document.getElementById('chkShowOrigin') as HTMLInputElement;
                    chk.checked = !chk.checked;
                    this.showOrigin = chk.checked;
                    this.drawActiveCanvasFrame();
                    break;
                }
                case 'toggleVectors': {
                    const chk = document.getElementById('chkShowVectors') as HTMLInputElement;
                    chk.checked = !chk.checked;
                    this.showVectors = chk.checked;
                    this.drawActiveCanvasFrame();
                    break;
                }
                case 'togglePixelGrid': {
                    const chk = document.getElementById('chkShowPixelGrid') as HTMLInputElement;
                    chk.checked = !chk.checked;
                    this.showPixelGrid = chk.checked;
                    this.drawActiveCanvasFrame();
                    break;
                }
                case 'toggleWorkbench':
                    this.layout.toggleWorkbench();
                    break;
                case 'resetLayout':
                    this.layout.resetLayout();
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
                    const cmd = (btn as HTMLElement).dataset.ctx || '';
                    const targetArchiveId = this.ctxTargetArchiveId;
                    const targetPath = this.ctxTargetPath;
                    this.closeContextMenu();

                    if (cmd === 'closeArchive') {
                        if (targetArchiveId) {
                            await this.requestCloseArchive(targetArchiveId);
                        }
                        return;
                    }

                    if (targetArchiveId) {
                        await this.selectAndInspectNode(targetArchiveId, targetPath, false);
                    }
                    switch (cmd) {
                        case 'addProp':
                            this.openAddPropertyModal();
                            break;
                        case 'addImg':
                            this.openAddImageModal();
                            break;
                        case 'addDir':
                            this.openAddDirectoryModal();
                            break;
                        case 'copy':
                            await this.copySelectedNode();
                            break;
                        case 'paste':
                            await this.pasteIntoSelectedNode();
                            break;
                        case 'duplicate':
                            await this.duplicateSelectedNode();
                            break;
                        case 'rename':
                            this.openRenameModal();
                            break;
                        case 'inlineLinks':
                            await this.inlineLinksInCurrentScope();
                            break;
                        case 'exportAuto':
                            if (!targetPath) {
                                await this.downloadCurrentArchive();
                            } else {
                                await this.exportCurrentNode('auto');
                            }
                            break;
                        case 'delete':
                            await this.deleteSelectedNode();
                            break;
                    }
                });
            });
        }

        private closeContextMenu(): void {
            document.getElementById('treeContextMenu')?.classList.add('hidden');
        }

        private openContextMenu(x: number, y: number, archiveId: string, path: string): void {
            this.ctxTargetArchiveId = archiveId;
            this.ctxTargetPath = path;
            const isArchiveRoot = !path;
            const menu = document.getElementById('treeContextMenu')!;

            menu.querySelector('[data-ctx="addProp"]')?.classList.toggle('hidden', isArchiveRoot);
            menu.querySelector('[data-ctx="copy"]')?.classList.toggle('hidden', isArchiveRoot);
            menu.querySelector('[data-ctx="duplicate"]')?.classList.toggle('hidden', isArchiveRoot);
            menu.querySelector('[data-ctx="rename"]')?.classList.toggle('hidden', isArchiveRoot);

            const exportBtn = document.getElementById('ctxExportBtn');
            if (exportBtn) {
                exportBtn.textContent = isArchiveRoot ? 'Export .wz Archive...' : 'Export Node...';
            }

            document.getElementById('ctxDeleteBtn')?.classList.toggle('hidden', isArchiveRoot);
            document.getElementById('ctxCloseArchiveBtn')?.classList.toggle('hidden', !isArchiveRoot);

            menu.classList.remove('hidden');
            const maxX = window.innerWidth - 220;
            const maxY = window.innerHeight - 310;
            menu.style.left = `${Math.max(8, Math.min(x, maxX))}px`;
            menu.style.top = `${Math.max(8, Math.min(y, maxY))}px`;
        }

        private bindCanvasViewport(): void {
            const viewport = document.getElementById('canvasViewport')!;

            // Background switchers
            document.querySelectorAll('[data-bg]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const bg = (btn as HTMLElement).dataset.bg || 'checker';
                    document.querySelectorAll('[data-bg]').forEach((b) => b.classList.remove('active'));
                    btn.classList.add('active');
                    viewport.className = `canvas-viewport bg-${bg}`;
                    void rpcCall('ui.saveState', { uiState: { canvasBackground: bg } });
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
                void rpcCall('ui.saveState', { uiState: { showOriginCrosshair: this.showOrigin } });
            });

            document.getElementById('chkShowVectors')!.addEventListener('change', (ev) => {
                this.showVectors = (ev.target as HTMLInputElement).checked;
                this.drawActiveCanvasFrame();
                void rpcCall('ui.saveState', { uiState: { showVectorBounds: this.showVectors } });
            });

            document.getElementById('chkShowPixelGrid')!.addEventListener('change', (ev) => {
                this.showPixelGrid = (ev.target as HTMLInputElement).checked;
                this.drawActiveCanvasFrame();
                void rpcCall('ui.saveState', { uiState: { showPixelGrid: this.showPixelGrid } });
            });

            // Animation controls
            document.getElementById('btnAnimPlayPause')!.addEventListener('click', () => {
                this.animPlaying = !this.animPlaying;
                document.getElementById('btnAnimPlayPause')!.textContent = this.animPlaying ? 'Pause' : 'Play';
                if (this.animPlaying) {
                    this.scheduleNextAnimationFrame();
                } else {
                    this.stopAnimationLoop();
                }
            });

            document.getElementById('btnAnimPrev')!.addEventListener('click', () => {
                if (this.decodedFrames.length === 0) return;
                this.animPlaying = false;
                document.getElementById('btnAnimPlayPause')!.textContent = 'Play';
                this.stopAnimationLoop();
                this.activeFrameIdx = (this.activeFrameIdx - 1 + this.decodedFrames.length) % this.decodedFrames.length;
                this.updateActiveFrameHighlight();
                this.drawActiveCanvasFrame();
            });

            document.getElementById('btnAnimNext')!.addEventListener('click', () => {
                if (this.decodedFrames.length === 0) return;
                this.animPlaying = false;
                document.getElementById('btnAnimPlayPause')!.textContent = 'Play';
                this.stopAnimationLoop();
                this.activeFrameIdx = (this.activeFrameIdx + 1) % this.decodedFrames.length;
                this.updateActiveFrameHighlight();
                this.drawActiveCanvasFrame();
            });

            // Canvas format conversion & PNG import/export
            document.getElementById('btnConvertCanvasFormat')!.addEventListener('click', async () => {
                if (!this.selectedArchiveId || !this.selectedPath) return;
                const fmt = Number((document.getElementById('canvasFormatSelect') as HTMLSelectElement).value) || 2;
                const res = await rpcCall<RpcOperationResponse>('node.changeCanvasFormat', {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    format: fmt,
                });
                if (res.ok) {
                    this.showToast(`Converted Canvas to Format ${fmt}`, 'success');
                    await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
                } else {
                    this.showToast(res.data?.error || 'Format conversion failed', 'error');
                }
            });

            document.getElementById('btnReplacePng')!.addEventListener('click', () => {
                (document.getElementById('hiddenPngFileInput') as HTMLInputElement).click();
            });

            document.getElementById('btnExportPng')!.addEventListener('click', () => {
                void this.exportCurrentNode('png');
            });

            document.getElementById('btnInlineCurrentCanvas')!.addEventListener('click', () => {
                void this.inlineLinksInCurrentScope();
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
                void this.exportCurrentNode('audio');
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

                const res = await rpcCall<RpcOperationResponse>('node.setValue', {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    kind,
                    rawValue,
                    vecX,
                    vecY,
                });
                if (res.ok) {
                    this.showToast('Updated property value', 'success');
                    const parentPath = this.selectedPath.includes('/')
                        ? this.selectedPath.slice(0, this.selectedPath.lastIndexOf('/'))
                        : '';
                    await this.fetchTreeChildren(this.selectedArchiveId, parentPath);
                    this.renderTree();
                    await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
                } else {
                    this.showToast(res.data?.error || 'Failed to update value', 'error');
                }
            });

            document.getElementById('btnRenameNode')!.addEventListener('click', () => this.openRenameModal());
            document.getElementById('btnDeleteNode')!.addEventListener('click', () => void this.deleteSelectedNode());
            document.getElementById('btnInspAddChild')!.addEventListener('click', () => this.openAddPropertyModal());
            document.getElementById('btnInspDuplicate')!.addEventListener('click', () => void this.duplicateSelectedNode());
            document.getElementById('btnInspCopy')!.addEventListener('click', () => void this.copySelectedNode());
            document.getElementById('btnInspPaste')!.addEventListener('click', () => void this.pasteIntoSelectedNode());
            document.getElementById('btnInspExportJson')!.addEventListener('click', () => void this.exportCurrentNode('json'));
            document.getElementById('btnInspExportAuto')!.addEventListener('click', () => void this.exportCurrentNode('auto'));
        }

        private bindWorkbenchEvents(): void {
            const tabs = document.querySelectorAll('#workbenchTabs .wb-tab');
            tabs.forEach((tab) => {
                tab.addEventListener('click', () => {
                    const tabId = (tab as HTMLElement).dataset.tab || 'json';
                    this.activateWorkbenchTab(tabId);
                });
            });

            document.getElementById('btnToggleWorkbenchCollapse')!.addEventListener('click', () => {
                this.layout.toggleWorkbench();
            });

            // JSON Editor Actions
            document.getElementById('btnApplyJsonSubtree')!.addEventListener('click', () => void this.applyJsonFromEditor());
            document.getElementById('btnFormatJson')!.addEventListener('click', () => {
                if (!this.jsonEditor) return;
                try {
                    const parsed = JSON.parse(this.jsonEditor.getValue());
                    this.jsonEditor.setValue(JSON.stringify(parsed, null, 2));
                } catch (err) {
                    this.showToast(`JSON Syntax Error: ${(err as Error).message}`, 'error');
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

                const res = await rpcCall<SearchResponseDto>('search.query', {
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

            // Batch Tools
            document.getElementById('btnRunBatchExport')!.addEventListener('click', async () => {
                const outDir = (document.getElementById('batchOutputDirInput') as HTMLInputElement).value.trim();
                const mode = (document.getElementById('batchExportModeSelect') as HTMLSelectElement).value;
                if (!outDir) {
                    this.showToast('Enter a target output directory path', 'error');
                    return;
                }
                const res = await rpcCall<RpcOperationResponse>('node.batchExport', {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    outputDirectory: outDir,
                    mode,
                });
                if (res.ok) {
                    this.showToast(`Batch-exported ${res.data.count ?? 0} file(s) to ${outDir}`, 'success');
                } else {
                    this.showToast(res.data?.error || 'Batch export failed', 'error');
                }
            });

            document.getElementById('btnBatchInlineSelected')!.addEventListener('click', () => {
                void this.inlineLinksInCurrentScope();
            });
            document.getElementById('btnBatchDownloadWz')!.addEventListener('click', () => {
                void this.downloadCurrentArchive();
            });

            // Clear logs
            document.getElementById('btnClearLogs')!.addEventListener('click', () => {
                void rpcCall('logs.clear', {});
            });
        }

        private activateWorkbenchTab(tabId: string): void {
            if (this.layout.isWorkbenchCollapsed()) {
                this.layout.toggleWorkbench(false);
            }
            document.querySelectorAll('#workbenchTabs .wb-tab').forEach((btn) => {
                btn.classList.toggle('active', (btn as HTMLElement).dataset.tab === tabId);
            });

            const panelMap: Record<string, string> = {
                json: 'wbPanelJson',
                search: 'wbPanelSearch',
                hex: 'wbPanelHex',
                batch: 'wbPanelBatch',
                logs: 'wbPanelLogs',
            };
            Object.entries(panelMap).forEach(([k, id]) => {
                document.getElementById(id)?.classList.toggle('active', k === tabId);
            });

            document.getElementById('wbJsonActions')!.classList.toggle('hidden', tabId !== 'json');
            if (tabId === 'json' && this.jsonEditor) {
                this.jsonEditor.refresh();
            }
        }

        private focusGlobalSearch(): void {
            this.activateWorkbenchTab('search');
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
            const res = await rpcCall<RpcOperationResponse>('node.applyJson', {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
                jsonText,
            });
            if (res.ok) {
                this.showToast('Applied JSON changes to node', 'success');
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
            } else {
                this.showToast(res.data?.error || 'Failed to apply JSON', 'error');
            }
        }

        private async undoSelectedImage(): Promise<void> {
            if (!this.selectedArchiveId || !this.selectedPath) return;
            const res = await rpcCall<RpcOperationResponse>('node.undo', {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok) {
                this.showToast('Undid last change in image', 'info');
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
            } else {
                this.showToast(res.data?.error || 'Nothing to undo', 'error');
            }
        }

        private async redoSelectedImage(): Promise<void> {
            if (!this.selectedArchiveId || !this.selectedPath) return;
            const res = await rpcCall<RpcOperationResponse>('node.redo', {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok) {
                this.showToast('Redid change in image', 'info');
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, this.selectedPath);
                await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
            } else {
                this.showToast(res.data?.error || 'Nothing to redo', 'error');
            }
        }

        private async copySelectedNode(): Promise<void> {
            if (!this.selectedArchiveId || !this.selectedPath) return;
            const res = await rpcCall<RpcOperationResponse>('node.copy', {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok) {
                this.showToast(`Copied "${this.selectedPath}"`, 'info');
            } else {
                this.showToast(res.data?.error || 'Copy failed', 'error');
            }
        }

        private async pasteIntoSelectedNode(): Promise<void> {
            if (!this.selectedArchiveId) return;
            const res = await rpcCall<RpcOperationResponse>('node.paste', {
                archiveId: this.selectedArchiveId,
                targetPath: this.selectedPath,
            });
            if (res.ok && res.data.path) {
                this.showToast(`Pasted "${res.data.path}"`, 'success');
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
            } else {
                this.showToast(res.data?.error || 'Paste failed', 'error');
            }
        }

        private async duplicateSelectedNode(): Promise<void> {
            if (!this.selectedArchiveId || !this.selectedPath) return;
            const res = await rpcCall<RpcOperationResponse>('node.duplicate', {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok && res.data.path) {
                this.showToast(`Duplicated -> "${res.data.path}"`, 'success');
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
            } else {
                this.showToast(res.data?.error || 'Duplicate failed', 'error');
            }
        }

        private async deleteSelectedNode(): Promise<void> {
            if (!this.selectedArchiveId) return;
            if (!this.selectedPath) {
                await this.requestCloseArchive(this.selectedArchiveId);
                return;
            }
            const res = await rpcCall<RpcOperationResponse>('node.delete', {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok) {
                const parentPath = res.data.path ?? '';
                this.showToast('Deleted node', 'info');
                this.treeState.childrenByParent.delete(this.makeCacheKey(this.selectedArchiveId, parentPath));
                await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, parentPath);
                await this.selectAndInspectNode(this.selectedArchiveId, parentPath, false);
            } else {
                this.showToast(res.data?.error || 'Delete failed', 'error');
            }
        }

        private async inlineLinksInCurrentScope(): Promise<void> {
            if (!this.selectedArchiveId) return;
            const res = await rpcCall<RpcOperationResponse>('node.inlineOutlinks', {
                archiveId: this.selectedArchiveId,
                path: this.selectedPath,
            });
            if (res.ok) {
                this.showToast(`Inlined ${res.data.count ?? 0} linked sprite(s)`, 'success');
                await this.selectAndInspectNode(this.selectedArchiveId, this.selectedPath, false);
            } else {
                this.showToast(res.data?.error || 'Inlining failed', 'error');
            }
        }

        private async exportCurrentNode(format: string): Promise<void> {
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
            const res = await rpcCall<BinaryExportResponseDto>('workspace.exportArchive', {
                archiveId,
                version,
            });
            if (!res.ok || !res.data?.data) {
                this.showToast(res.data?.error || 'Failed to export WZ archive', 'error');
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

        private async closeActiveArchive(): Promise<void> {
            if (!this.selectedArchiveId) return;
            await this.requestCloseArchive(this.selectedArchiveId);
        }

        private async requestCloseArchive(archiveId: string): Promise<void> {
            const ar = this.archives.find((a) => a.archiveId === archiveId);
            if (!ar) return;

            if (!ar.isModified && !ar.isAdded) {
                await this.performCloseArchive(ar.archiveId);
                return;
            }

            this.openCloseArchivePromptModal(ar);
        }

        private async performCloseArchive(archiveId: string): Promise<void> {
            const arName = this.archives.find((a) => a.archiveId === archiveId)?.name || 'archive';
            const res = await rpcCall<RpcOperationResponse>('workspace.closeArchive', {
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
                this.showToast(`Closed "${arName}"`, 'info');
                this.currentInspection = null;
                if (res.data?.status) {
                    await this.handleWorkspaceStatus(res.data.status);
                }
            } else {
                this.showToast(res.data?.error || 'Failed to close archive', 'error');
            }
        }

        private openCloseArchivePromptModal(ar: ArchiveSummaryDto): void {
            const modText =
                ar.modifiedImageCount > 0
                    ? ` (${ar.modifiedImageCount} modified image${ar.modifiedImageCount === 1 ? '' : 's'})`
                    : '';

            this.showModal(
                `Unsaved Changes in "${ar.name}"`,
                `
                <p style="margin: 0; color: var(--text-primary); line-height: 1.5;">
                    <strong>${this.escapeHtml(ar.name)}</strong> has unsaved changes${modText}.
                    Would you like to export the repacked <code>.wz</code> archive before closing?
                </p>
                `,
                `
                <button type="button" class="cmd-btn" id="btnModalCancel">Cancel</button>
                <button type="button" class="cmd-btn danger" id="btnModalDiscardClose">Close Without Exporting</button>
                <button type="button" class="cmd-btn primary" id="btnModalExportClose">Export .wz &amp; Close</button>
                `
            );

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalDiscardClose')!.addEventListener('click', async () => {
                this.closeModal();
                await this.performCloseArchive(ar.archiveId);
            });
            document.getElementById('btnModalExportClose')!.addEventListener('click', async () => {
                const exported = await this.exportAndDownloadArchiveById(ar.archiveId, ar.version);
                if (exported) {
                    this.closeModal();
                    await this.performCloseArchive(ar.archiveId);
                }
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

        private findExistingOpenArchive(filePathOrName: string): ArchiveSummaryDto | undefined {
            const rawTrimmed = filePathOrName.trim();
            if (!rawTrimmed) return undefined;
            const basePart = rawTrimmed.split(/[\\/]/).pop() || rawTrimmed;
            const cleanName = basePart.toLowerCase().endsWith('.wz') ? basePart : `${basePart}.wz`;
            const lowerCleanName = cleanName.toLowerCase();
            const lowerFullPath = rawTrimmed.toLowerCase();

            return this.archives.find(
                (a) =>
                    (a.filePath && a.filePath.toLowerCase() === lowerFullPath) ||
                    a.name.toLowerCase() === lowerCleanName
            );
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

        private confirmReloadArchive(existing: ArchiveSummaryDto): Promise<boolean> {
            return new Promise<boolean>((resolve) => {
                let settled = false;
                const finish = (confirmed: boolean) => {
                    if (settled) return;
                    settled = true;
                    this.modalCancelCallback = null;
                    this.closeModal();
                    resolve(confirmed);
                };

                const hasUnsaved = Boolean(existing.isModified || existing.isAdded);
                const unsavedNote = hasUnsaved
                    ? `<p style="margin: 8px 0 0; color: var(--amber); font-size: 12px;">Warning: This archive has unsaved changes that will be discarded if you reload it.</p>`
                    : '';

                this.showModal(
                    `Archive Already Open`,
                    `
                    <p style="margin: 0; color: var(--text-primary); line-height: 1.5;">
                        <strong>${this.escapeHtml(existing.name)}</strong> is already open in the workspace.
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
            const res = await rpcCall<RpcOperationResponse>('workspace.browseFiles', {});
            if (res.data?.cancelled) {
                return;
            }
            if (!res.ok) {
                this.showToast(res.data?.error || 'Could not browse files', 'error');
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
                    const confirmed = await this.confirmReloadArchive(existing);
                    if (!confirmed) {
                        continue;
                    }
                }

                const openRes = await rpcCall<RpcOperationResponse>('workspace.openFile', { filePath });
                if (openRes.ok && openRes.data.archiveId) {
                    this.clearArchiveTreeCache(openRes.data.archiveId);
                    lastArchiveId = openRes.data.archiveId;
                    const name = openRes.data.archive?.name || filePath.split(/[\\/]/).pop() || filePath;
                    openedNames.push(name);
                    await this.fetchTreeChildren(openRes.data.archiveId, '');
                } else {
                    this.showToast(openRes.data?.error || `Could not open ${filePath}`, 'error');
                }
            }

            if (lastArchiveId) {
                const label =
                    openedNames.length === 1
                        ? openedNames[0]
                        : `${openedNames.length} WZ archives`;
                this.showToast(`Opened ${label}`, 'success');
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
            const res = await rpcCall<RpcOperationResponse>('workspace.browseAndOpenDirectory', {});
            if (res.data?.cancelled) {
                return;
            }
            if (res.ok) {
                this.treeState.childrenByParent.clear();
                const count = res.data.archives?.length ?? 0;
                this.showToast(`Loaded ${count} WZ archive${count === 1 ? '' : 's'}`, 'success');
                if (res.data.archiveId) {
                    await this.fetchTreeChildren(res.data.archiveId, '');
                    await this.selectAndInspectNode(res.data.archiveId, '', true);
                }
            } else {
                this.showToast(res.data?.error || 'Failed to open directory', 'error');
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
                const res = await rpcCall<RpcOperationResponse>('workspace.openDirectory', { directoryPath });
                if (res.ok) {
                    this.closeModal();
                    this.treeState.childrenByParent.clear();
                    this.showToast(`Loaded WZ archives from ${directoryPath}`, 'success');
                    if (res.data.archiveId) {
                        await this.fetchTreeChildren(res.data.archiveId, '');
                        await this.selectAndInspectNode(res.data.archiveId, '', true);
                    }
                } else {
                    this.showToast(res.data?.error || 'Failed to open directory', 'error');
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
                const res = await rpcCall<RpcOperationResponse>('workspace.createArchive', { name, version });
                if (res.ok && res.data.archiveId) {
                    this.closeModal();
                    this.showToast(`Created archive ${name}`, 'success');
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
                const res = await rpcCall<RpcOperationResponse>('workspace.saveArchive', {
                    archiveId: currentAr.archiveId,
                    targetFilePath,
                    version,
                });
                if (res.ok) {
                    this.closeModal();
                    this.showToast(`Repacked and saved ${currentAr.name}`, 'success');
                } else {
                    this.showToast(res.data?.error || 'Failed to save archive to disk', 'error');
                }
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
                const k = kindSel.value;
                document.getElementById('modalVectorRow')!.classList.toggle('hidden', k !== 'Vector' && k !== 'Canvas');
                document.getElementById('modalScalarRow')!.classList.toggle(
                    'hidden',
                    k === 'Vector' || k === 'Object' || k === 'Canvas' || k === 'Sound' || k === 'Null'
                );
            });

            document.getElementById('btnModalCancel')!.addEventListener('click', () => this.closeModal());
            document.getElementById('btnModalConfirm')!.addEventListener('click', async () => {
                const name = (document.getElementById('modalPropName') as HTMLInputElement).value.trim();
                const kind = kindSel.value as RepackerPropertyKind;
                const rawValue = (document.getElementById('modalPropValue') as HTMLInputElement).value;
                const vecX = Number((document.getElementById('modalVecX') as HTMLInputElement).value) || 0;
                const vecY = Number((document.getElementById('modalVecY') as HTMLInputElement).value) || 0;

                const res = await rpcCall<RpcOperationResponse>('node.addProperty', {
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
                    this.showToast(`Added ${kind} "${name}"`, 'success');
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                    await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
                } else {
                    this.showToast(res.data?.error || 'Failed to add property', 'error');
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
                const res = await rpcCall<RpcOperationResponse>('node.addImage', {
                    archiveId: this.selectedArchiveId,
                    parentPath: this.selectedPath,
                    imageName,
                });
                if (res.ok && res.data.path) {
                    this.closeModal();
                    this.showToast(`Created image "${res.data.path}"`, 'success');
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                    await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
                } else {
                    this.showToast(res.data?.error || 'Failed to add image', 'error');
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
                const res = await rpcCall<RpcOperationResponse>('node.addDirectory', {
                    archiveId: this.selectedArchiveId,
                    parentPath: this.selectedPath,
                    dirName,
                });
                if (res.ok && res.data.path) {
                    this.closeModal();
                    this.showToast(`Created directory "${res.data.path}"`, 'success');
                    await this.invalidateAndRefreshTreeBranch(this.selectedArchiveId, res.data.path);
                    await this.selectAndInspectNode(this.selectedArchiveId, res.data.path, true);
                } else {
                    this.showToast(res.data?.error || 'Failed to create directory', 'error');
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
                const res = await rpcCall<RpcOperationResponse>('node.rename', {
                    archiveId: this.selectedArchiveId,
                    path: this.selectedPath,
                    newName,
                });
                if (res.ok) {
                    this.closeModal();
                    const newPath = res.data.path ?? '';
                    this.showToast(`Renamed to "${newName}"`, 'success');
                    this.treeState.childrenByParent.clear();
                    await this.selectAndInspectNode(this.selectedArchiveId, newPath, true);
                } else {
                    this.showToast(res.data?.error || 'Rename failed', 'error');
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
