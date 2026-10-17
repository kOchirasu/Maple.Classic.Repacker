namespace MapleRepacker {
    const CHANGE_STATE_DOTS: Partial<Record<RepackerChangeState, string>> = {
        [RepackerChangeState.Deleted]: '<span class="tree-del-dot" title="Deleted"></span>',
        [RepackerChangeState.Added]: '<span class="tree-add-dot" title="Added"></span>',
        [RepackerChangeState.Modified]: '<span class="tree-mod-dot" title="Modified"></span>',
    };

    function twistyHtml(expanded: boolean): string {
        return expanded ? '&#9662;' : '&#9656;';
    }

    /** Lazily loaded archive tree: caches children per parent, tracks expansion and the text filter. */
    export class TreeView {
        private readonly container = byId('treeContainer');
        private readonly filterInput = byId<HTMLInputElement>('treeFilterInput');
        // key: `${archiveId}::${parentPath}`
        private readonly childrenByParent = new Map<string, TreeNodeDto[]>();
        private readonly expandedKeys = new Set<string>();
        private filterText = '';

        constructor(private readonly app: RepackerStudioApp) {
            byId('btnCollapseAll').addEventListener('click', () => {
                this.expandedKeys.clear();
                this.render();
            });
            this.filterInput.addEventListener('input', () => {
                this.filterText = this.filterInput.value;
                this.render();
            });
            byId('btnClearTreeFilter').addEventListener('click', () => {
                this.clearFilter();
                this.render();
            });
            this.container.addEventListener('scroll', () => this.app.contextMenu.close());
        }

        private static key(archiveId: string, path: string): string {
            return `${archiveId}::${path}`;
        }

        getCachedChildren(archiveId: string, parentPath: string): TreeNodeDto[] {
            return this.childrenByParent.get(TreeView.key(archiveId, parentPath)) || [];
        }

        async fetchChildren(archiveId: string, parentPath: string): Promise<TreeNodeDto[]> {
            const res = await rpcCall<TreeChildrenResponse>(RpcMethod.TreeChildren, {
                archiveId,
                path: parentPath,
            });
            const nodes = res.ok && res.data?.nodes ? res.data.nodes : [];
            this.childrenByParent.set(TreeView.key(archiveId, parentPath), nodes);
            return nodes;
        }

        private async fetchIfMissing(archiveId: string, path: string): Promise<void> {
            if (!this.childrenByParent.has(TreeView.key(archiveId, path))) {
                await this.fetchChildren(archiveId, path);
            }
        }

        /** Makes sure the archive root is expanded and loaded; `refetch` reloads it even when cached. */
        async ensureArchiveRoot(archiveId: string, refetch: boolean): Promise<void> {
            const rootKey = TreeView.key(archiveId, '');
            if (!this.childrenByParent.has(rootKey)) {
                this.expandedKeys.add(rootKey);
                await this.fetchChildren(archiveId, '');
            } else if (refetch) {
                await this.fetchChildren(archiveId, '');
            }
        }

        /** Refetches the root, the ancestor chain of `path`, `path` itself and its expanded descendants, then re-renders. */
        async refreshBranch(archiveId: string, path: string): Promise<void> {
            await this.fetchChildren(archiveId, '');
            const parts = path.split('/').filter((p) => p.length > 0);
            let accum = '';
            for (let i = 0; i < parts.length; i++) {
                accum = accum ? `${accum}/${parts[i]}` : parts[i];
                const isAncestor = i < parts.length - 1;
                if (isAncestor || this.expandedKeys.has(TreeView.key(archiveId, accum)) || accum === path) {
                    await this.fetchChildren(archiveId, accum);
                }
            }
            if (path) {
                const archivePrefix = TreeView.key(archiveId, '');
                const descPrefix = TreeView.key(archiveId, `${path}/`);
                for (const expKey of Array.from(this.expandedKeys)) {
                    if (expKey.startsWith(descPrefix)) {
                        await this.fetchChildren(archiveId, expKey.slice(archivePrefix.length));
                    }
                }
            }
            this.render();
        }

        /** Drops the cached children of one parent so the next refresh reloads it. */
        invalidate(archiveId: string, parentPath: string): void {
            this.childrenByParent.delete(TreeView.key(archiveId, parentPath));
        }

        /** Drops every cached child list (expansion state is kept). */
        invalidateAll(): void {
            this.childrenByParent.clear();
        }

        /** Forgets all cached children and expansion state of an archive; `keepRootExpanded` re-expands its root. */
        forgetArchive(archiveId: string, keepRootExpanded = false): void {
            const prefix = TreeView.key(archiveId, '');
            for (const key of Array.from(this.childrenByParent.keys())) {
                if (key.startsWith(prefix)) {
                    this.childrenByParent.delete(key);
                }
            }
            for (const key of Array.from(this.expandedKeys)) {
                if (key.startsWith(prefix)) {
                    this.expandedKeys.delete(key);
                }
            }
            if (keepRootExpanded) {
                this.expandedKeys.add(prefix);
            }
        }

        /** Expands and loads every ancestor of `targetPath`, renders, and scrolls the selected row into view. */
        async revealPath(archiveId: string, targetPath: string): Promise<void> {
            this.expandedKeys.add(TreeView.key(archiveId, ''));
            await this.fetchIfMissing(archiveId, '');

            const parts = targetPath.split('/').filter((p) => p.length > 0);
            let accum = '';
            for (let i = 0; i < parts.length - 1; i++) {
                accum = accum ? `${accum}/${parts[i]}` : parts[i];
                this.expandedKeys.add(TreeView.key(archiveId, accum));
                await this.fetchIfMissing(archiveId, accum);
            }

            this.render();

            // A filter that hides the selected node would make the reveal pointless, so drop it
            if (this.filterText.trim().length > 0 && !this.container.querySelector('.tree-row.selected')) {
                this.clearFilter();
                this.render();
            }

            this.scrollSelectedRowIntoView();
        }

        private clearFilter(): void {
            this.filterText = '';
            this.filterInput.value = '';
        }

        private scrollSelectedRowIntoView(): void {
            const selectedRow = this.container.querySelector<HTMLElement>('.tree-row.selected');
            if (!selectedRow) return;

            const containerRect = this.container.getBoundingClientRect();
            const rowRect = selectedRow.getBoundingClientRect();
            if (containerRect.height <= 0) return;

            if (rowRect.top < containerRect.top + 24 || rowRect.bottom > containerRect.bottom - 24) {
                const rowOffsetInContainer = rowRect.top - containerRect.top + this.container.scrollTop;
                const targetScrollTop = rowOffsetInContainer - containerRect.height / 2 + rowRect.height / 2;
                this.container.scrollTop = Math.max(0, Math.round(targetScrollTop));
            }
        }

        render(): void {
            this.container.innerHTML = '';

            if (this.app.archives.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'tree-empty';
                empty.textContent = 'No WZ archives open. Use the File menu (Ctrl+O) to open a .wz archive or data directory.';
                this.container.appendChild(empty);
                return;
            }

            const filterLower = this.filterText.trim().toLowerCase();

            for (const ar of this.app.archives) {
                const rootKey = TreeView.key(ar.archiveId, '');
                const isRootExpanded = this.expandedKeys.has(rootKey);
                const isRootSelected = ar.archiveId === this.app.selectedArchiveId && this.app.selectedPath === '';

                const rootRow = this.createRow(ar.archiveId, '', 0, isRootSelected, false);
                rootRow.innerHTML = `
                    <span class="tree-twisty">${twistyHtml(isRootExpanded)}</span>
                    ${typeBadgeHtml(RepackerNodeKind.Archive)}
                    <span class="tree-name" title="${escapeHtml(ar.name)}">${escapeHtml(ar.name)}</span>
                    <span class="tree-val-preview" title="v${ar.version} • ${ar.imageCount} img">v${ar.version} &bull; ${ar.imageCount} img</span>
                    ${CHANGE_STATE_DOTS[ar.changeState] ?? ''}
                `;
                this.bindRowEvents(rootRow, ar.archiveId, '', true, ar.changeState);
                this.container.appendChild(rootRow);

                if (isRootExpanded) {
                    this.renderNodes(ar.archiveId, this.childrenByParent.get(rootKey) || [], 1, filterLower);
                }
            }
        }

        private renderNodes(archiveId: string, nodes: TreeNodeDto[], depth: number, filterLower: string): void {
            for (const node of nodes) {
                const nodeKey = TreeView.key(archiveId, node.path);
                const isExpanded = this.expandedKeys.has(nodeKey);
                const cachedKids = this.childrenByParent.get(nodeKey) || [];

                if (filterLower && !this.nodeMatchesFilter(node, filterLower) && !this.hasMatchingDescendant(archiveId, cachedKids, filterLower)) {
                    continue;
                }

                const isSelected = archiveId === this.app.selectedArchiveId && node.path === this.app.selectedPath;
                const isDeleted = node.changeState === RepackerChangeState.Deleted;
                const row = this.createRow(archiveId, node.path, depth, isSelected, isDeleted);

                const twisty = hasTreeChildren(node) ? twistyHtml(isExpanded) : '';
                const valPreview = node.displayValue
                    ? `<span class="tree-val-preview" title="${escapeHtml(node.displayValue)}">${escapeHtml(node.displayValue)}</span>`
                    : '';
                row.innerHTML = `
                    <span class="tree-twisty">${twisty}</span>
                    ${typeBadgeHtml(node.nodeKind, node.propertyKind)}
                    <span class="tree-name" title="${escapeHtml(node.name)}">${escapeHtml(node.name)}</span>
                    ${valPreview}
                    ${CHANGE_STATE_DOTS[node.changeState] ?? ''}
                `;
                this.bindRowEvents(row, archiveId, node.path, canExpandTreeNode(node), node.changeState);
                this.container.appendChild(row);

                if (isExpanded && cachedKids.length > 0) {
                    this.renderNodes(archiveId, cachedKids, depth + 1, filterLower);
                }
            }
        }

        private createRow(archiveId: string, path: string, depth: number, isSelected: boolean, isDeleted: boolean): HTMLDivElement {
            const row = document.createElement('div');
            row.className = `tree-row${isSelected ? ' selected' : ''}${isDeleted ? ' deleted' : ''}`;
            row.style.paddingLeft = `${6 + depth * 15}px`;
            row.dataset.archiveId = archiveId;
            row.dataset.path = path;
            return row;
        }

        private bindRowEvents(row: HTMLElement, archiveId: string, path: string, canExpand: boolean, changeState: RepackerChangeState): void {
            row.addEventListener('click', (ev) => {
                this.app.contextMenu.close();
                const clickedTwisty = (ev.target as HTMLElement).classList.contains('tree-twisty');
                void this.toggleOrSelect(archiveId, path, canExpand, clickedTwisty);
            });
            row.addEventListener('dblclick', () => {
                this.app.contextMenu.close();
                if (canExpand) {
                    void this.toggleExpansion(archiveId, path);
                }
            });
            row.addEventListener('contextmenu', (ev) => {
                ev.preventDefault();
                ev.stopPropagation();
                this.app.contextMenu.open(ev.clientX, ev.clientY, archiveId, path, changeState);
            });
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

        private nodeMatchesFilter(node: TreeNodeDto, filterLower: string): boolean {
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
                if (this.nodeMatchesFilter(n, filterLower)) {
                    return true;
                }
                const kids = this.childrenByParent.get(TreeView.key(archiveId, n.path));
                if (kids && this.hasMatchingDescendant(archiveId, kids, filterLower)) {
                    return true;
                }
            }
            return false;
        }

        private async setExpanded(archiveId: string, path: string, expanded: boolean): Promise<void> {
            const key = TreeView.key(archiveId, path);
            if (expanded) {
                this.expandedKeys.add(key);
                await this.fetchIfMissing(archiveId, path);
            } else {
                this.expandedKeys.delete(key);
            }
        }

        private async toggleExpansion(archiveId: string, path: string): Promise<void> {
            await this.setExpanded(archiveId, path, !this.expandedKeys.has(TreeView.key(archiveId, path)));
            this.render();
        }

        /** Clicking a row selects it and expands it; clicking the twisty or an already selected row toggles it. */
        private async toggleOrSelect(archiveId: string, path: string, canExpand: boolean, clickedTwisty: boolean): Promise<void> {
            if (canExpand) {
                const isExpanded = this.expandedKeys.has(TreeView.key(archiveId, path));
                const alreadySelected = this.app.selectedArchiveId === archiveId && this.app.selectedPath === path;
                const toggle = clickedTwisty || alreadySelected;
                await this.setExpanded(archiveId, path, toggle ? !isExpanded : true);
            }
            await this.app.selectAndInspectNode(archiveId, path, false);
        }
    }
}
