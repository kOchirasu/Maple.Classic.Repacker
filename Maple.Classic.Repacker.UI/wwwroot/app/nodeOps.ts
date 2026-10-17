namespace MapleRepacker {
    const PROPERTY_KIND_OPTIONS: ReadonlyArray<readonly [RepackerPropertyKind, string]> = [
        [RepackerPropertyKind.Int, 'Int (Compressed Int32)'],
        [RepackerPropertyKind.Short, 'Short (Int16)'],
        [RepackerPropertyKind.Long, 'Long (Int64)'],
        [RepackerPropertyKind.Float, 'Float (Single)'],
        [RepackerPropertyKind.Double, 'Double (Float64)'],
        [RepackerPropertyKind.String, 'String'],
        [RepackerPropertyKind.Vector, 'Vector2D (X, Y)'],
        [RepackerPropertyKind.Uol, 'UOL (Relative Path Link)'],
        [RepackerPropertyKind.Object, 'SubProperty (Object Container)'],
        [RepackerPropertyKind.Canvas, 'Canvas (2D Sprite / PNG)'],
        [RepackerPropertyKind.Sound, 'Sound_DX8 (Audio Effect / BGM)'],
        [RepackerPropertyKind.Null, 'Null'],
    ];

    const CANVAS_FORMAT_OPTIONS: ReadonlyArray<readonly [WzCanvasFormat, string]> = [
        [WzCanvasFormat.Bgra8888, 'BGRA8888 (32-bit)'],
        [WzCanvasFormat.Bgra4444, 'BGRA4444 (16-bit)'],
        [WzCanvasFormat.Bgr565, 'BGR565 (16-bit)'],
        [WzCanvasFormat.Bgr565Block16, 'BGR565 Block16'],
        [WzCanvasFormat.Dxt3, 'DXT3 / BC2'],
        [WzCanvasFormat.Dxt5, 'DXT5 / BC3'],
    ];

    /** Property kinds that take a single scalar value in the Add Property dialog. */
    const NO_SCALAR_KINDS: ReadonlySet<RepackerPropertyKind> = new Set([
        RepackerPropertyKind.Vector,
        RepackerPropertyKind.Object,
        RepackerPropertyKind.Canvas,
        RepackerPropertyKind.Sound,
        RepackerPropertyKind.Null,
    ]);

    /** Edits to the selected node: values, JSON, clipboard, history, add/rename/delete and media replacement. */
    export class NodeOperations {
        private readonly pngInput = byId<HTMLInputElement>('hiddenPngFileInput');
        private readonly audioInput = byId<HTMLInputElement>('hiddenAudioFileInput');

        constructor(private readonly app: RepackerStudioApp) {
            this.bindReplacementInput(this.pngInput, (archiveId, path, bytes, file) =>
                this.mutate(
                    RpcMethod.NodeReplaceCanvas,
                    { archiveId, path, pngBytes: bytes, format: this.app.canvas.selectedFormat },
                    `Replaced Canvas with ${file.name}`,
                    'Failed to import PNG'
                )
            );
            this.bindReplacementInput(this.audioInput, (archiveId, path, bytes, file) =>
                this.mutate(
                    RpcMethod.NodeReplaceSound,
                    { archiveId, path, audioBytes: bytes },
                    `Replaced Sound_DX8 with ${file.name}`,
                    'Failed to import audio'
                )
            );
        }

        /** Selected archive and node path, or null when no (non-root) node is selected and `requirePath` is set. */
        private selection(requirePath: boolean): { archiveId: string; path: string } | null {
            const { selectedArchiveId: archiveId, selectedPath: path } = this.app;
            if (!archiveId || (requirePath && !path)) return null;
            return { archiveId, path };
        }

        /**
         * Runs an RPC that edits the tree and toasts the outcome. On success, returns the response so the caller
         * can refresh; on failure, shows the server error (or `failureMessage`) and returns null.
         */
        private async runEdit(
            method: RpcMethod,
            params: Record<string, unknown>,
            successMessage: string | ((data: RpcOperationResponse) => string),
            failureMessage: string,
            successKind = ToastKind.Success
        ): Promise<RpcOperationResponse | null> {
            const res = await rpcCall<RpcOperationResponse>(method, params);
            if (!res.ok) {
                this.app.showRpcError(res.data, failureMessage);
                return null;
            }
            this.app.showToast(typeof successMessage === 'string' ? successMessage : successMessage(res.data), successKind);
            return res.data;
        }

        /** Edit of the selected node itself, followed by a tree + inspector refresh. */
        private async mutate(method: RpcMethod, params: Record<string, unknown>, successMessage: string, failureMessage: string): Promise<void> {
            if (await this.runEdit(method, params, successMessage, failureMessage)) {
                await this.app.refreshSelection();
            }
        }

        private bindReplacementInput(
            input: HTMLInputElement,
            replace: (archiveId: string, path: string, bytes: Uint8Array, file: File) => Promise<void>
        ): void {
            input.addEventListener('change', async () => {
                const file = input.files?.[0];
                input.value = '';
                const sel = this.selection(true);
                if (!file || !sel) return;
                await replace(sel.archiveId, sel.path, new Uint8Array(await file.arrayBuffer()), file);
            });
        }

        pickCanvasReplacement(): void {
            this.pngInput.click();
        }

        pickSoundReplacement(): void {
            this.audioInput.click();
        }

        async convertSelectedCanvasFormat(format: WzCanvasFormat): Promise<void> {
            const sel = this.selection(true);
            if (!sel) return;
            if (await this.runEdit(RpcMethod.NodeChangeCanvasFormat, { ...sel, format }, `Converted Canvas to ${format}`, 'Format conversion failed')) {
                await this.app.selectAndInspectNode(sel.archiveId, sel.path, false);
            }
        }

        async setSelectedValue(kind: RepackerPropertyKind, rawValue: string, vecX: number, vecY: number): Promise<void> {
            const sel = this.selection(true);
            if (!sel) return;
            await this.mutate(RpcMethod.NodeSetValue, { ...sel, kind, rawValue, vecX, vecY }, 'Updated property value', 'Failed to update value');
        }

        async applyJsonToSelected(jsonText: string): Promise<void> {
            const sel = this.selection(true);
            if (!sel) return;
            await this.mutate(RpcMethod.NodeApplyJson, { ...sel, jsonText }, 'Applied JSON changes to node', 'Failed to apply JSON');
        }

        // ---------------------------------------------------------------------
        // History (archive-wide undo / redo)
        // ---------------------------------------------------------------------

        undo(): Promise<void> {
            return this.stepHistory(RpcMethod.NodeUndo, (p) => (p ? `Undid last change in ${p}` : 'Undid last change'), 'Nothing to undo');
        }

        redo(): Promise<void> {
            return this.stepHistory(RpcMethod.NodeRedo, (p) => (p ? `Redid change in ${p}` : 'Redid change'), 'Nothing to redo');
        }

        private async stepHistory(method: RpcMethod, describe: (affectedPath: string) => string, failureMessage: string): Promise<void> {
            const sel = this.selection(false);
            if (!sel) return;
            const data = await this.runEdit(method, sel, (d) => describe(d.path || sel.path), failureMessage, ToastKind.Info);
            if (!data) return;
            // The undone change may live elsewhere in the archive; refresh that branch too
            const affectedPath = data.path || sel.path;
            if (affectedPath !== sel.path) {
                await this.app.tree.refreshBranch(sel.archiveId, affectedPath);
            }
            await this.app.refreshSelection();
        }

        // ---------------------------------------------------------------------
        // Clipboard, duplicate, delete, restore, inline links
        // ---------------------------------------------------------------------

        async copySelected(): Promise<void> {
            const sel = this.selection(true);
            if (sel) {
                await this.runEdit(RpcMethod.NodeCopy, sel, `Copied "${sel.path}"`, 'Copy failed', ToastKind.Info);
            }
        }

        async pasteIntoSelected(): Promise<void> {
            const sel = this.selection(false);
            if (!sel) return;
            await this.createAndSelect(
                RpcMethod.NodePaste,
                { archiveId: sel.archiveId, targetPath: sel.path },
                (path) => `Pasted "${path}"`,
                'Paste failed'
            );
        }

        async duplicateSelected(): Promise<void> {
            const sel = this.selection(true);
            if (sel) {
                await this.createAndSelect(RpcMethod.NodeDuplicate, sel, (path) => `Duplicated -> "${path}"`, 'Duplicate failed');
            }
        }

        /**
         * Runs an RPC that creates a node and returns its path, then reveals and selects the new node.
         * Returns true on success.
         */
        private async createAndSelect(
            method: RpcMethod,
            params: Record<string, unknown>,
            successMessage: (newPath: string) => string,
            failureMessage: string
        ): Promise<boolean> {
            const res = await rpcCall<RpcOperationResponse>(method, params);
            const newPath = res.ok ? res.data.path : null;
            if (!newPath) {
                this.app.showRpcError(res.data, failureMessage);
                return false;
            }
            this.app.showToast(successMessage(newPath), ToastKind.Success);
            await this.app.refreshSelection(newPath, true);
            return true;
        }

        /** Deletes the selected node; on an archive root this closes the archive instead. */
        async deleteSelected(): Promise<void> {
            const sel = this.selection(false);
            if (!sel) return;
            if (!sel.path) {
                await this.app.files.requestCloseArchive(sel.archiveId);
                return;
            }
            const data = await this.runEdit(RpcMethod.NodeDelete, sel, 'Deleted node', 'Delete failed', ToastKind.Info);
            if (!data) return;

            // Nodes from the original file stay in the tree marked as deleted; newly added ones disappear
            const parentPath = data.path ?? '';
            const { tree } = this.app;
            tree.invalidate(sel.archiveId, parentPath);
            await tree.refreshBranch(sel.archiveId, sel.path);
            const stillInTree = tree.getCachedChildren(sel.archiveId, parentPath).some((n) => n.path === sel.path);
            await this.app.selectAndInspectNode(sel.archiveId, stillInTree ? sel.path : parentPath, false);
        }

        async restoreSelected(): Promise<void> {
            const sel = this.selection(true);
            if (!sel) return;
            const data = await this.runEdit(RpcMethod.NodeRestore, sel, (d) => `Restored "${d.path || sel.path}"`, 'Restore failed');
            if (data) {
                await this.app.refreshSelection(data.path || sel.path);
            }
        }

        async inlineLinksInSelection(): Promise<void> {
            const sel = this.selection(false);
            if (!sel) return;
            const data = await this.runEdit(
                RpcMethod.NodeInlineOutlinks,
                sel,
                (d) => `Inlined ${d.count ?? 0} linked sprite(s)`,
                'Inlining failed'
            );
            if (data) {
                await this.app.selectAndInspectNode(sel.archiveId, sel.path, false);
            }
        }

        // ---------------------------------------------------------------------
        // Add / rename dialogs
        // ---------------------------------------------------------------------

        openAddPropertyModal(): void {
            const sel = this.selection(false);
            if (!sel) return;

            const modal = this.app.modal.open({
                title: 'Add WZ Property',
                body: [
                    ModalHtml.row('Property Name', ModalHtml.input('modalPropName', { placeholder: 'e.g. incSTR, origin, stand1, icon' })),
                    ModalHtml.row('Property Type', ModalHtml.select('modalPropKind', PROPERTY_KIND_OPTIONS)),
                    ModalHtml.row('Initial Value', ModalHtml.input('modalPropValue', { value: '0' }), { id: 'modalScalarRow' }),
                    `<div class="form-row two-col hidden" id="modalVectorRow">
                        <div><label id="modalVecXLabel">Vector X</label>${ModalHtml.input('modalVecX', { type: 'number', value: '0' })}</div>
                        <div><label id="modalVecYLabel">Vector Y</label>${ModalHtml.input('modalVecY', { type: 'number', value: '0' })}</div>
                    </div>`,
                    ModalHtml.row('Source File (optional)', ModalHtml.input('modalPropFile', { type: 'file' }), {
                        id: 'modalPropFileRow',
                        hidden: true,
                        labelId: 'modalPropFileLabel',
                        hintId: 'modalPropFileHint',
                    }),
                    ModalHtml.row('Pixel Format', ModalHtml.select('modalCanvasFormat', CANVAS_FORMAT_OPTIONS), {
                        id: 'modalCanvasFormatRow',
                        hidden: true,
                    }),
                ].join(''),
                actions: [
                    {
                        label: 'Add Property',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (m) => {
                            const name = m.field('modalPropName').value.trim();
                            const kind = kindSelect.value as RepackerPropertyKind;
                            const isCanvas = kind === RepackerPropertyKind.Canvas;
                            // Blank Canvas origin inputs mean "let the server pick a default origin"
                            const readCoord = (input: HTMLInputElement): number | undefined =>
                                input.value.trim() === '' ? (isCanvas ? undefined : 0) : Number(input.value) || 0;
                            const sourceFile = fileInput.files?.[0];
                            const binaryPayload =
                                sourceFile && (isCanvas || kind === RepackerPropertyKind.Sound) ? new Uint8Array(await sourceFile.arrayBuffer()) : undefined;

                            const created = await this.createAndSelect(
                                RpcMethod.NodeAddProperty,
                                {
                                    archiveId: sel.archiveId,
                                    parentPath: sel.path,
                                    name,
                                    kind,
                                    rawValue: m.field('modalPropValue').value,
                                    vecX: readCoord(vecXInput),
                                    vecY: readCoord(vecYInput),
                                    binaryPayload,
                                    canvasFormat: isCanvas ? (m.field<HTMLSelectElement>('modalCanvasFormat').value as WzCanvasFormat) : undefined,
                                },
                                () => `Added ${kind} "${name}"`,
                                'Failed to add property'
                            );
                            if (created) m.close();
                        },
                    },
                ],
            });

            const kindSelect = modal.field<HTMLSelectElement>('modalPropKind');
            const vecXInput = modal.field('modalVecX');
            const vecYInput = modal.field('modalVecY');
            const fileInput = modal.field('modalPropFile');
            kindSelect.addEventListener('change', () => {
                const kind = kindSelect.value as RepackerPropertyKind;
                const isCanvas = kind === RepackerPropertyKind.Canvas;
                const isSound = kind === RepackerPropertyKind.Sound;
                modal.field('modalVectorRow').classList.toggle('hidden', kind !== RepackerPropertyKind.Vector && !isCanvas);
                modal.field('modalScalarRow').classList.toggle('hidden', NO_SCALAR_KINDS.has(kind));

                // Canvas / Sound can be created from a file; the vector inputs become the Canvas origin
                modal.field('modalPropFileRow').classList.toggle('hidden', !isCanvas && !isSound);
                modal.field('modalCanvasFormatRow').classList.toggle('hidden', !isCanvas);
                fileInput.value = '';
                fileInput.accept = isCanvas ? '.png,image/png' : '.mp3,.wav,audio/*';
                modal.field('modalPropFileLabel').textContent = isCanvas ? 'Source Image (.png, optional)' : 'Source Audio (.mp3 / .wav, optional)';
                modal.field('modalPropFileHint').textContent = isCanvas
                    ? 'Leave empty for a blank 32x32 sprite. Leave the origin empty to center it at the bottom.'
                    : 'Leave empty for an empty Sound_DX8 property.';
                modal.field('modalVecXLabel').textContent = isCanvas ? 'Origin X' : 'Vector X';
                modal.field('modalVecYLabel').textContent = isCanvas ? 'Origin Y' : 'Vector Y';
                for (const input of [vecXInput, vecYInput]) {
                    input.value = isCanvas ? '' : '0';
                    input.placeholder = isCanvas ? 'auto' : '';
                }
            });
        }

        openAddImageModal(): void {
            const sel = this.selection(false);
            if (!sel) return;

            const modal = this.app.modal.open({
                title: 'Add WZ Image (.img)',
                body:
                    ModalHtml.row('Image Name', ModalHtml.input('modalImgName', { placeholder: 'e.g. 01040001.img' })) +
                    ModalHtml.row('Import From File (optional)', ModalHtml.input('modalImgFile', { type: 'file', accept: '.img,.json' }), {
                        hint: 'A raw .img or an exported .json subtree. Leave empty for an empty image.',
                    }),
                actions: [
                    {
                        label: 'Create .img',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (m) => {
                            const sourceFile = fileInput.files?.[0];
                            const isJson = sourceFile?.name.toLowerCase().endsWith('.json') ?? false;
                            const created = await this.createAndSelect(
                                RpcMethod.NodeAddImage,
                                {
                                    archiveId: sel.archiveId,
                                    parentPath: sel.path,
                                    imageName: nameInput.value.trim(),
                                    rawImgBytes: sourceFile && !isJson ? new Uint8Array(await sourceFile.arrayBuffer()) : undefined,
                                    jsonContent: sourceFile && isJson ? await sourceFile.text() : undefined,
                                },
                                (path) => `${sourceFile ? 'Imported' : 'Created'} image "${path}"`,
                                'Failed to add image'
                            );
                            if (created) m.close();
                        },
                    },
                ],
            });

            const nameInput = modal.field('modalImgName');
            const fileInput = modal.field('modalImgFile');
            fileInput.addEventListener('change', () => {
                // Suggest the file's name (as .img) unless the user already typed one
                const file = fileInput.files?.[0];
                if (file && !nameInput.value.trim()) {
                    nameInput.value = file.name.replace(/\.json$/i, '.img');
                }
            });
        }

        openAddDirectoryModal(): void {
            const sel = this.selection(false);
            if (!sel) return;

            this.app.modal.open({
                title: 'Add WZ Directory',
                body: ModalHtml.row('Directory Name', ModalHtml.input('modalDirName', { placeholder: 'e.g. Cap, Cape, Glove' })),
                actions: [
                    {
                        label: 'Create Directory',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (m) => {
                            const created = await this.createAndSelect(
                                RpcMethod.NodeAddDirectory,
                                { archiveId: sel.archiveId, parentPath: sel.path, dirName: m.field('modalDirName').value.trim() },
                                (path) => `Created directory "${path}"`,
                                'Failed to create directory'
                            );
                            if (created) m.close();
                        },
                    },
                ],
            });
        }

        openRenameModal(): void {
            const sel = this.selection(false);
            const node = this.app.currentInspection;
            if (!sel || !node) return;

            this.app.modal.open({
                title: `Rename "${node.name}"`,
                body: ModalHtml.row('New Name', ModalHtml.input('modalRenameInput', { value: node.name })),
                actions: [
                    {
                        label: 'Rename',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (m) => {
                            const newName = m.field('modalRenameInput').value.trim();
                            const data = await this.runEdit(RpcMethod.NodeRename, { ...sel, newName }, `Renamed to "${newName}"`, 'Rename failed');
                            if (!data) return;
                            m.close();
                            // Renaming changes the paths of the whole subtree, so drop every cached branch
                            this.app.tree.invalidateAll();
                            await this.app.selectAndInspectNode(sel.archiveId, data.path ?? '', true);
                        },
                    },
                ],
            });
        }
    }
}
