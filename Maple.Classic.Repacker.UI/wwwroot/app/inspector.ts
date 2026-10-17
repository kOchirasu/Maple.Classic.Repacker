namespace MapleRepacker {
    /** Property kinds whose value can't be edited in the inspector form. */
    const NON_EDITABLE_KINDS: ReadonlySet<RepackerPropertyKind> = new Set([
        RepackerPropertyKind.Object,
        RepackerPropertyKind.Convex,
        RepackerPropertyKind.Sound,
        RepackerPropertyKind.RawData,
    ]);

    /** Right-hand property inspector: node metadata, node actions and the value editor form. */
    export class PropertyInspector {
        private readonly typePill = byId('inspectorTypePill');
        private readonly metaGrid = byId('inspectorMetaGrid');
        private readonly renameBtn = byId('btnRenameNode');
        private readonly deleteBtn = byId('btnDeleteNode');
        /** Actions that need a live, non-root node. */
        private readonly nodeActionButtons = ['btnInspAddChild', 'btnInspDuplicate', 'btnInspCopy'].map((id) => byId<HTMLButtonElement>(id));
        private readonly editorCard = byId('valueEditorCard');
        private readonly kindSelect = byId<HTMLSelectElement>('editPropKind');
        private readonly scalarInput = byId<HTMLInputElement>('editScalarValue');
        private readonly vecXInput = byId<HTMLInputElement>('editVecX');
        private readonly vecYInput = byId<HTMLInputElement>('editVecY');
        private readonly scalarGroup = byId('scalarInputGroup');
        private readonly scalarLabel = byId('scalarValueLabel');
        private readonly vectorGroup = byId('vectorInputGroup');

        constructor(private readonly app: RepackerStudioApp) {
            this.kindSelect.addEventListener('change', () => this.syncEditorFieldVisibility(this.kindSelect.value as RepackerPropertyKind));
            byId<HTMLFormElement>('propertyEditForm').addEventListener('submit', (ev) => {
                ev.preventDefault();
                void this.app.nodes.setSelectedValue(
                    this.kindSelect.value as RepackerPropertyKind,
                    this.scalarInput.value,
                    Number(this.vecXInput.value) || 0,
                    Number(this.vecYInput.value) || 0
                );
            });

            const { nodes, files, layout } = this.app;
            this.renameBtn.addEventListener('click', () => nodes.openRenameModal());
            this.deleteBtn.addEventListener('click', () => {
                if (this.app.currentInspection?.changeState === RepackerChangeState.Deleted) {
                    void nodes.restoreSelected();
                } else {
                    void nodes.deleteSelected();
                }
            });
            byId('btnToggleInspectorCollapse').addEventListener('click', () => {
                layout.toggleInspector();
                this.app.canvas.redraw();
            });
            byId('inspectorCollapsedStrip').addEventListener('click', () => {
                layout.toggleInspector(false);
                this.app.canvas.redraw();
            });
            byId('btnInspAddChild').addEventListener('click', () => nodes.openAddPropertyModal());
            byId('btnInspDuplicate').addEventListener('click', () => void nodes.duplicateSelected());
            byId('btnInspCopy').addEventListener('click', () => void nodes.copySelected());
            byId('btnInspPaste').addEventListener('click', () => void nodes.pasteIntoSelected());
            byId('btnInspExportJson').addEventListener('click', () => files.exportSelectedNode(RepackerExportFormat.Json));
            byId('btnInspExportAuto').addEventListener('click', () => files.exportSelectedNode(RepackerExportFormat.Auto));
        }

        clear(): void {
            this.typePill.textContent = 'NONE';
            this.metaGrid.innerHTML = '';
            this.editorCard.classList.add('hidden');
        }

        render(dto: NodeInspectionDto): void {
            this.typePill.textContent = typeBadge(dto.nodeKind, dto.propertyKind);

            const isArchiveRoot = !dto.path || dto.nodeKind === RepackerNodeKind.Archive;
            const isDeleted = dto.changeState === RepackerChangeState.Deleted;
            this.renameBtn.classList.toggle('hidden', isArchiveRoot || isDeleted);
            this.deleteBtn.textContent = isArchiveRoot ? 'Close' : isDeleted ? 'Restore' : 'Delete';
            this.deleteBtn.title = isArchiveRoot ? 'Close WZ archive (Del)' : isDeleted ? 'Restore deleted node' : 'Delete selected node (Del)';
            this.deleteBtn.classList.toggle('danger', !isDeleted);
            for (const btn of this.nodeActionButtons) {
                btn.disabled = isArchiveRoot || isDeleted;
            }

            const kv = (key: string, value: string) => `<div class="kv-key">${key}</div><div class="kv-val">${value}</div>`;
            this.metaGrid.innerHTML = [
                kv('Archive', escapeHtml(dto.archiveName)),
                kv('Path', escapeHtml(dto.path || '(Root)')),
                kv('Node Kind', escapeHtml(dto.nodeKind)),
                kv('Property Type', escapeHtml(dto.propertyKind)),
                kv('Value', escapeHtml(dto.displayValue)),
                kv('Size', formatByteSize(dto.sizeBytes)),
            ].join('');

            const isEditable = !isDeleted && dto.nodeKind === RepackerNodeKind.Property && !NON_EDITABLE_KINDS.has(dto.propertyKind);
            this.editorCard.classList.toggle('hidden', !isEditable);
            if (!isEditable) return;

            this.kindSelect.value = dto.propertyKind;
            if (dto.propertyKind === RepackerPropertyKind.Vector && dto.vectorValue) {
                this.vecXInput.value = String(dto.vectorValue.x);
                this.vecYInput.value = String(dto.vectorValue.y);
            } else if (dto.propertyKind === RepackerPropertyKind.Canvas && dto.preview.canvas) {
                this.vecXInput.value = String(dto.preview.canvas.origin.x);
                this.vecYInput.value = String(dto.preview.canvas.origin.y);
                this.scalarInput.value = String(dto.preview.canvas.delayMs);
            } else {
                this.scalarInput.value = dto.rawStringValue ?? dto.displayValue;
            }
            this.syncEditorFieldVisibility(dto.propertyKind);
        }

        private syncEditorFieldVisibility(kind: RepackerPropertyKind): void {
            const isVector = kind === RepackerPropertyKind.Vector;
            const isCanvas = kind === RepackerPropertyKind.Canvas;
            const isNull = kind === RepackerPropertyKind.Null;

            // Canvas edits its origin (vector) and frame delay (scalar)
            this.scalarGroup.classList.toggle('hidden', isVector || isNull);
            this.vectorGroup.classList.toggle('hidden', !isVector && !isCanvas);
            this.scalarLabel.textContent = isCanvas ? 'Frame Delay (ms)' : kind === RepackerPropertyKind.Uol ? 'Relative UOL Target Path' : 'Value';
        }
    }
}
