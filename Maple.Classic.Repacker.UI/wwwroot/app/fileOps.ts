namespace MapleRepacker {
    const DEFAULT_WZ_VERSION = 779;

    const BULK_EXPORT_MODES: ReadonlyArray<readonly [RepackerBatchExportMode, string]> = [
        [RepackerBatchExportMode.Png, 'All Canvas Sprites (.png)'],
        [RepackerBatchExportMode.Sound, 'All Sound_DX8 Audio (.wav / .mp3)'],
        [RepackerBatchExportMode.Json, 'All Subtrees / Images (.json)'],
        [RepackerBatchExportMode.Img, 'All WZ Images (.img)'],
        [RepackerBatchExportMode.All, 'Everything (.png + audio + .json)'],
    ];

    function modifiedImagesText(ar: ArchiveSummaryDto): string {
        return pluralize(ar.modifiedImageCount, 'modified image');
    }

    /** Archive-level file operations: open, create, save/export, close (with unsaved-change prompts) and bulk export. */
    export class FileOperations {
        private readonly wzFileInput = byId<HTMLInputElement>('hiddenWzFileInput');
        private lastBulkExportDir = '';
        private lastBulkExportMode = RepackerBatchExportMode.Png;

        constructor(private readonly app: RepackerStudioApp) {
            this.wzFileInput.addEventListener('change', () => {
                const files = Array.from(this.wzFileInput.files || []);
                this.wzFileInput.value = '';
                void this.openArchives(
                    files,
                    (file) => (file as unknown as { path?: string }).path || file.name,
                    async (file) => {
                        const nativePath = (file as unknown as { path?: string }).path || '';
                        return rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceOpenBytes, {
                            fileName: file.name,
                            filePath: nativePath || undefined,
                            data: new Uint8Array(await file.arrayBuffer()),
                        });
                    }
                );
            });
        }

        // ---------------------------------------------------------------------
        // Native dialogs: provided by the Electron shell when present, otherwise
        // by the Windows backend over RPC (WebView2 shell).
        // ---------------------------------------------------------------------

        get hasNativeDialogs(): boolean {
            return desktopHost.kind === DesktopHostKind.Electron || this.app.backendSupportsNativeDialogs;
        }

        private async pickWzFilePaths(): Promise<string[]> {
            if (window.mapleHost) {
                return window.mapleHost.pickOpenWzFiles();
            }
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceBrowseFiles, {});
            if (!res.ok && !res.data?.cancelled) {
                this.app.showRpcError(res.data, 'Could not browse files');
            }
            return res.ok ? res.data.filePaths || [] : [];
        }

        private async pickDirectoryPath(): Promise<string | null> {
            if (window.mapleHost) {
                return window.mapleHost.pickDirectory();
            }
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceBrowseDirectory, {});
            return res.ok && res.data?.path ? res.data.path : null;
        }

        private async pickSaveWzPath(defaultFileName: string): Promise<string | null> {
            if (window.mapleHost) {
                return window.mapleHost.pickSaveWzFile(defaultFileName);
            }
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceBrowseSaveFile, { defaultFileName });
            return res.ok && res.data?.path ? res.data.path : null;
        }

        // ---------------------------------------------------------------------
        // Opening archives
        // ---------------------------------------------------------------------

        triggerOpenWzFile(): void {
            if (this.hasNativeDialogs) {
                void this.browseNativeWzFiles();
            } else {
                this.wzFileInput.click();
            }
        }

        private async browseNativeWzFiles(): Promise<void> {
            const filePaths = await this.pickWzFilePaths();
            await this.openArchives(
                filePaths,
                (filePath) => filePath,
                (filePath) => rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceOpenFile, { filePath })
            );
        }

        /**
         * Opens each file, asking before replacing an archive that is already open, then selects the last one opened.
         * `identify` returns the path (or bare name) used to detect an already open archive.
         */
        private async openArchives<T>(
            files: readonly T[],
            identify: (file: T) => string,
            open: (file: T) => Promise<{ ok: boolean; data: RpcOperationResponse }>
        ): Promise<void> {
            const openedNames: string[] = [];
            let lastArchiveId = '';

            for (const file of files) {
                const pathOrName = identify(file);
                const existing = findExistingOpenArchive(this.app.archives, pathOrName);
                if (existing && !(await this.confirmReloadArchive(existing, pathOrName))) {
                    continue;
                }

                const displayName = pathOrName.split(/[\\/]/).pop() || pathOrName;
                const res = await open(file);
                if (res.ok && res.data.archiveId) {
                    this.app.tree.forgetArchive(res.data.archiveId, true);
                    lastArchiveId = res.data.archiveId;
                    openedNames.push(res.data.archive?.name || displayName);
                    await this.app.tree.fetchChildren(res.data.archiveId, '');
                } else {
                    this.app.showRpcError(res.data, `Could not open ${displayName}`);
                }
            }

            if (lastArchiveId) {
                const label = openedNames.length === 1 ? openedNames[0] : `${openedNames.length} WZ archives`;
                this.app.showToast(`Opened ${label}`, ToastKind.Success);
                await this.app.selectAndInspectNode(lastArchiveId, '', true);
            }
        }

        private confirmReloadArchive(existing: ArchiveSummaryDto, requestedPathOrName: string): Promise<boolean> {
            return new Promise<boolean>((resolve) => {
                const reqBase = requestedPathOrName.trim().split(/[\\/]/).pop() || existing.name;
                const isSameFile = reqBase.toLowerCase() === existing.name.toLowerCase();
                const openDescription = isSameFile
                    ? `<strong>${escapeHtml(existing.name)}</strong> is already open in the workspace.`
                    : `<strong>${escapeHtml(reqBase)}</strong> is already open in the workspace (linked with <strong>${escapeHtml(existing.name)}</strong>).`;
                const hasUnsaved = isArchiveDirty(existing);

                this.app.modal.open({
                    title: 'Archive Already Open',
                    body:
                        ModalHtml.text(`${openDescription} Do you want to reload it and replace the currently open archive?`) +
                        (hasUnsaved ? ModalHtml.text('Warning: This archive has unsaved changes that will be discarded if you reload it.', 'warning') : ''),
                    actions: [
                        {
                            label: 'Reload Archive',
                            variant: hasUnsaved ? ModalButtonVariant.Danger : ModalButtonVariant.Primary,
                            onClick: (modal) => {
                                modal.close();
                                resolve(true);
                            },
                        },
                    ],
                    onDismiss: () => resolve(false),
                });
            });
        }

        triggerOpenDirectory(): void {
            if (this.hasNativeDialogs) {
                void this.browseNativeDirectory();
            } else {
                this.openDirectoryModal();
            }
        }

        private async browseNativeDirectory(): Promise<void> {
            const directoryPath = await this.pickDirectoryPath();
            if (directoryPath) {
                await this.openDataDirectory(directoryPath);
            }
        }

        /** Opens every .wz archive under a data directory. Returns true on success. */
        private async openDataDirectory(directoryPath: string): Promise<boolean> {
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceOpenDirectory, { directoryPath });
            if (!res.ok) {
                this.app.showRpcError(res.data, 'Failed to open directory');
                return false;
            }
            this.app.tree.invalidateAll();
            const count = res.data.archives?.length ?? 0;
            this.app.showToast(`Loaded ${pluralize(count, 'WZ archive')} from ${directoryPath}`, ToastKind.Success);
            if (res.data.archiveId) {
                await this.app.tree.fetchChildren(res.data.archiveId, '');
                await this.app.selectAndInspectNode(res.data.archiveId, '', true);
            }
            return true;
        }

        private openDirectoryModal(): void {
            this.app.modal.open({
                title: 'Open MapleStory Data Directory',
                body: ModalHtml.row(
                    'Directory Path (Scans recursively for .wz archives)',
                    ModalHtml.input('modalDirPathInput', { placeholder: 'C:\\Nexon\\MapleStory\\Data' })
                ),
                actions: [
                    {
                        label: 'Load All .wz Archives',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (modal) => {
                            const directoryPath = modal.field('modalDirPathInput').value.trim();
                            if (directoryPath && (await this.openDataDirectory(directoryPath))) {
                                modal.close();
                            }
                        },
                    },
                ],
            });
        }

        openNewArchiveModal(): void {
            this.app.modal.open({
                title: 'Create New WZ Archive',
                body:
                    ModalHtml.row('Archive Name', ModalHtml.input('modalNewArchiveName', { value: 'Custom_000.wz' })) +
                    ModalHtml.row('MapleStory WZ Version', ModalHtml.input('modalNewArchiveVer', { type: 'number', value: DEFAULT_WZ_VERSION })),
                actions: [
                    {
                        label: 'Create Archive',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (modal) => {
                            const name = modal.field('modalNewArchiveName').value.trim();
                            const version = Number(modal.field('modalNewArchiveVer').value) || DEFAULT_WZ_VERSION;
                            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceCreateArchive, { name, version });
                            if (res.ok && res.data.archiveId) {
                                modal.close();
                                this.app.showToast(`Created archive ${name}`, ToastKind.Success);
                                await this.app.tree.fetchChildren(res.data.archiveId, '');
                                await this.app.selectAndInspectNode(res.data.archiveId, '', true);
                            } else {
                                this.app.showRpcError(res.data, 'Failed to create archive');
                            }
                        },
                    },
                ],
            });
        }

        // ---------------------------------------------------------------------
        // Saving & exporting
        // ---------------------------------------------------------------------

        openSaveRepackModal(): void {
            const ar = this.app.selectedArchive;
            if (!ar) return;

            this.app.modal.open({
                title: `Repack & Save "${ar.name}"`,
                body:
                    ModalHtml.row(
                        'Target Disk File Path (Optional for direct disk write)',
                        ModalHtml.input('modalSaveFilePath', { value: ar.filePath ?? '', placeholder: `/path/to/${ar.name}` })
                    ) + ModalHtml.row('Target WZ Version', ModalHtml.input('modalSaveWzVer', { type: 'number', value: ar.version })),
                actions: [
                    {
                        label: 'Download .wz File',
                        variant: ModalButtonVariant.Accent,
                        onClick: (modal) => {
                            modal.close();
                            void this.downloadSelectedArchive();
                        },
                    },
                    {
                        label: 'Save to Disk Path',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (modal) => {
                            const targetFilePath = modal.field('modalSaveFilePath').value.trim();
                            const version = Number(modal.field('modalSaveWzVer').value) || ar.version;
                            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceSaveArchive, {
                                archiveId: ar.archiveId,
                                targetFilePath,
                                version,
                            });
                            if (res.ok) {
                                modal.close();
                                this.app.showToast(`Repacked and saved ${ar.name}`, ToastKind.Success);
                            } else {
                                this.app.showRpcError(res.data, 'Failed to save archive to disk');
                            }
                        },
                    },
                ],
            });
        }

        /** Downloads the selected node in the given format via the HTTP export endpoint. */
        exportSelectedNode(format: RepackerExportFormat): void {
            const { selectedArchiveId, selectedPath } = this.app;
            if (!selectedArchiveId) return;
            const query = new URLSearchParams({ archiveId: selectedArchiveId, path: selectedPath, format });
            triggerDownload(`/api/export?${query}`);
        }

        async downloadSelectedArchive(): Promise<void> {
            if (this.app.selectedArchiveId) {
                await this.exportAndDownloadArchive(this.app.selectedArchiveId);
            }
        }

        private async exportAndDownloadArchive(archiveId: string, version?: number): Promise<boolean> {
            const res = await rpcCall<BinaryExportResponseDto>(RpcMethod.WorkspaceExportArchive, { archiveId, version });
            if (!res.ok || !res.data?.data) {
                this.app.showRpcError(res.data, 'Failed to export WZ archive');
                return false;
            }
            const blob = new Blob([toUint8Array(res.data.data) as unknown as BlobPart], {
                type: res.data.mimeType || 'application/octet-stream',
            });
            const blobUrl = URL.createObjectURL(blob);
            triggerDownload(blobUrl, res.data.fileName || 'Archive.wz');
            setTimeout(() => URL.revokeObjectURL(blobUrl), 10000);
            return true;
        }

        /** Saves to the archive's file (asking for a path when native dialogs allow), otherwise downloads it. */
        private async saveOrExportArchive(ar: ArchiveSummaryDto): Promise<boolean> {
            let targetFilePath = (ar.filePath || '').trim();
            if (!targetFilePath && this.hasNativeDialogs) {
                const pickedPath = await this.pickSaveWzPath(ar.name);
                if (!pickedPath) {
                    return false;
                }
                targetFilePath = pickedPath;
            }

            if (!targetFilePath) {
                return this.exportAndDownloadArchive(ar.archiveId, ar.version);
            }

            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceSaveArchive, {
                archiveId: ar.archiveId,
                targetFilePath,
                version: ar.version,
            });
            if (!res.ok) {
                this.app.showRpcError(res.data, `Failed to save ${ar.name}`);
                return false;
            }
            this.app.showToast(`Saved ${ar.name}`, ToastKind.Success);
            return true;
        }

        openBulkExportModal(): void {
            const ar = this.app.selectedArchive;
            if (!ar) {
                this.app.showToast('No WZ archive is currently selected', ToastKind.Error);
                return;
            }

            const browseBtn = this.hasNativeDialogs ? '<button type="button" class="cmd-btn" id="btnModalBrowseExportDir">Browse...</button>' : '';
            const modal = this.app.modal.open({
                title: 'Bulk Asset Export to Directory',
                body:
                    ModalHtml.row('Export Scope (Selected Tree Node)', ModalHtml.scope(this.describeScope(ar))) +
                    ModalHtml.row('Export Format', ModalHtml.select('modalBulkExportMode', BULK_EXPORT_MODES)) +
                    ModalHtml.row(
                        'Target Output Directory',
                        ModalHtml.inputWithButton(
                            ModalHtml.input('modalBulkExportDir', { value: this.lastBulkExportDir, placeholder: 'C:\\Exports\\MapleAssets' }),
                            browseBtn
                        )
                    ),
                actions: [
                    {
                        label: 'Run Bulk Export',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (m) => {
                            const outDir = dirInput.value.trim();
                            const mode = (modeSelect.value as RepackerBatchExportMode) || RepackerBatchExportMode.Png;
                            if (!outDir) {
                                this.app.showToast('Enter a target output directory path', ToastKind.Error);
                                return;
                            }
                            this.lastBulkExportDir = outDir;
                            this.lastBulkExportMode = mode;

                            m.setBusy(true);
                            const res = await rpcCall<RpcOperationResponse>(RpcMethod.NodeBatchExport, {
                                archiveId: this.app.selectedArchiveId,
                                path: this.app.selectedPath,
                                outputDirectory: outDir,
                                mode,
                            });
                            m.setBusy(false);

                            if (res.ok) {
                                m.close();
                                this.app.showToast(`Batch-exported ${res.data.count ?? 0} file(s) to ${outDir}`, ToastKind.Success);
                            } else {
                                this.app.showRpcError(res.data, 'Batch export failed');
                            }
                        },
                    },
                ],
            });

            const modeSelect = modal.field<HTMLSelectElement>('modalBulkExportMode');
            modeSelect.value = this.lastBulkExportMode;
            const dirInput = modal.field('modalBulkExportDir');
            if (browseBtn) {
                modal.field<HTMLButtonElement>('btnModalBrowseExportDir').addEventListener('click', async () => {
                    const pickedDir = await this.pickDirectoryPath();
                    if (pickedDir) {
                        dirInput.value = pickedDir;
                    }
                });
            }
        }

        openInlineOutlinksModal(): void {
            const ar = this.app.selectedArchive;
            if (!ar) {
                this.app.showToast('No WZ archive is currently selected', ToastKind.Error);
                return;
            }

            this.app.modal.open({
                title: 'Export / Inline _outlink & _inlink Sprites',
                body:
                    ModalHtml.text(
                        'Resolves external <code>_outlink</code> and internal <code>_inlink</code> sprite references within the selected scope by baking their target pixel data directly into the Canvas nodes, making the archive or image self-contained.',
                        'secondary'
                    ) + ModalHtml.row('Target Scope', ModalHtml.scope(this.describeScope(ar))),
                actions: [
                    {
                        label: 'Inline in Workspace',
                        onClick: async (modal) => {
                            modal.close();
                            await this.app.nodes.inlineLinksInSelection();
                        },
                    },
                    {
                        label: 'Inline & Export .wz',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (modal) => {
                            modal.setBusy(true);
                            await this.app.nodes.inlineLinksInSelection();
                            modal.close();
                            await this.downloadSelectedArchive();
                        },
                    },
                ],
            });
        }

        private describeScope(ar: ArchiveSummaryDto): string {
            return this.app.selectedPath ? `${ar.name} / ${this.app.selectedPath}` : `${ar.name} (Entire Archive)`;
        }

        // ---------------------------------------------------------------------
        // Closing archives & the app
        // ---------------------------------------------------------------------

        getDirtyArchives(): ArchiveSummaryDto[] {
            return this.app.archives.filter(isArchiveDirty);
        }

        async requestCloseArchive(archiveId: string): Promise<void> {
            const ar = this.app.archives.find((a) => a.archiveId === archiveId);
            if (!ar) return;
            if (isArchiveDirty(ar)) {
                this.openCloseArchivePromptModal(ar);
            } else {
                await this.performCloseArchive(ar.archiveId);
            }
        }

        private async performCloseArchive(archiveId: string): Promise<void> {
            const arName = this.app.archives.find((a) => a.archiveId === archiveId)?.name || 'archive';
            const res = await rpcCall<RpcOperationResponse>(RpcMethod.WorkspaceCloseArchive, { archiveId });
            if (!res.ok) {
                this.app.showRpcError(res.data, 'Failed to close archive');
                return;
            }
            this.app.tree.forgetArchive(archiveId);
            this.app.showToast(`Closed "${arName}"`, ToastKind.Info);
            this.app.currentInspection = null;
            // The server pushed the post-close status before this reply; re-apply it now that the inspection is cleared
            const latest = getLatestStatus();
            if (latest) {
                await this.app.handleWorkspaceStatus(latest);
            }
        }

        private openCloseArchivePromptModal(ar: ArchiveSummaryDto): void {
            const modText = ar.modifiedImageCount > 0 ? ` (${modifiedImagesText(ar)})` : '';
            const canSaveDisk = Boolean(ar.filePath) || this.hasNativeDialogs;

            this.app.modal.open({
                title: `Unsaved Changes in "${ar.name}"`,
                body: ModalHtml.text(
                    `<strong>${escapeHtml(ar.name)}</strong> has unsaved changes${modText}. Do you want to save your changes before closing this archive?`
                ),
                actions: [
                    {
                        label: canSaveDisk ? 'Close Without Saving' : 'Close Without Exporting',
                        variant: ModalButtonVariant.Danger,
                        onClick: async (modal) => {
                            modal.close();
                            await this.performCloseArchive(ar.archiveId);
                        },
                    },
                    {
                        label: canSaveDisk ? 'Save & Close' : 'Export .wz & Close',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (modal) => {
                            if (await this.saveOrExportArchive(ar)) {
                                modal.close();
                                await this.performCloseArchive(ar.archiveId);
                            }
                        },
                    },
                ],
            });
        }

        private confirmHostAppClose(): void {
            if (desktopHost.isDesktop) {
                desktopHost.postMessage({ type: HostWebMessageType.ConfirmAppClose });
            }
        }

        /** Desktop shell asked to close the window: prompt for unsaved archives first. */
        handleAppCloseRequest(): void {
            const dirtyArchives = this.getDirtyArchives();
            if (dirtyArchives.length === 0) {
                this.confirmHostAppClose();
            } else {
                this.openAppClosePromptModal(dirtyArchives);
            }
        }

        private openAppClosePromptModal(dirtyArchives: ArchiveSummaryDto[]): void {
            let body: string;
            if (dirtyArchives.length === 1) {
                const ar = dirtyArchives[0];
                const modText = ar.modifiedImageCount > 0 ? ` (${modifiedImagesText(ar)})` : '';
                body = ModalHtml.text(
                    `<strong>${escapeHtml(ar.name)}</strong> has unsaved changes${modText}. Do you want to save your changes before closing?`
                );
            } else {
                const items = dirtyArchives.map((ar) => {
                    const modText =
                        ar.modifiedImageCount > 0 ? modifiedImagesText(ar) : ar.changeState === RepackerChangeState.Added ? 'New archive' : 'Modified';
                    return `<strong>${escapeHtml(ar.name)}</strong> <span class="text-muted">(${escapeHtml(modText)})</span>`;
                });
                body =
                    ModalHtml.text(`The following <strong>${dirtyArchives.length}</strong> archives have unsaved changes:`) +
                    ModalHtml.list(items) +
                    ModalHtml.text('Do you want to save your changes before closing?');
            }

            this.app.modal.open({
                title: 'Unsaved Changes',
                body,
                actions: [
                    {
                        label: 'Close Without Saving',
                        variant: ModalButtonVariant.Danger,
                        onClick: (modal) => {
                            modal.close();
                            this.confirmHostAppClose();
                        },
                    },
                    {
                        label: dirtyArchives.length > 1 ? 'Save All & Close' : 'Save & Close',
                        variant: ModalButtonVariant.Primary,
                        onClick: async (modal) => {
                            modal.setBusy(true);
                            for (const ar of dirtyArchives) {
                                if (!(await this.saveOrExportArchive(ar))) {
                                    modal.setBusy(false);
                                    return;
                                }
                            }
                            modal.close();
                            this.confirmHostAppClose();
                        },
                    },
                ],
            });
        }
    }
}
