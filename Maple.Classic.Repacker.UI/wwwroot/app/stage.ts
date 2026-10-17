namespace MapleRepacker {
    interface StringPreviewTarget {
        name: string;
        path: string;
        text: string;
        sizeBytes: number;
    }

    const CHANGE_STATE_CHIPS: Partial<Record<RepackerChangeState, string>> = {
        [RepackerChangeState.Deleted]: '<span class="meta-chip chip-deleted">DELETED</span>',
        [RepackerChangeState.Added]: '<span class="meta-chip chip-added">ADDED</span>',
        [RepackerChangeState.Modified]: '<span class="meta-chip chip-modified">MODIFIED</span>',
    };

    const DEFAULT_CHILDREN_TABLE_HEIGHT = 185;

    /** Center stage: breadcrumbs, sound/string previews, the canvas stage and the child-node table. */
    export class StagePanel {
        private readonly breadcrumbBar = byId('breadcrumbBar');
        private readonly metaBadges = byId('stageMetaBadges');

        private readonly soundSection = byId('soundStageSection');
        private readonly audioEl = byId<HTMLAudioElement>('audioPlayerEl');
        private readonly soundTitle = byId('soundTitle');
        private readonly soundSubtitle = byId('soundSubtitle');
        private readonly soundMetrics = byId('soundMetricsGrid');
        private readonly waveformCanvas = byId<HTMLCanvasElement>('waveformCanvas');
        private activeAudioUrl: string | null = null;

        private readonly stringSection = byId('stringStageSection');
        private readonly stringTitle = byId('stringTitle');
        private readonly stringSubtitle = byId('stringSubtitle');
        private readonly stringLengthMeta = byId('stringLengthMeta');
        private readonly stringViewer = byId('stringViewerContent');

        private readonly stageContent = byId('stageContent');
        private readonly childrenSection = byId('childrenTableSection');
        private readonly childrenSplitter = byId('childrenTableSplitter');
        private readonly childrenToggleBtn = byId('btnToggleChildrenTable');
        private readonly childrenTitle = byId('childrenTableTitle');
        private readonly childrenMeta = byId('childrenTableMeta');
        private readonly childrenBody = byId('childrenTableBody');
        private childrenTableMinimized = false;
        private childrenTableHeight = DEFAULT_CHILDREN_TABLE_HEIGHT;
        private lastInspectedLayerKey = '';

        constructor(private readonly app: RepackerStudioApp) {
            byId('btnReplaceAudio').addEventListener('click', () => this.app.nodes.pickSoundReplacement());
            byId('btnExportAudio').addEventListener('click', () => this.app.files.exportSelectedNode(RepackerExportFormat.Audio));
            byId('btnCopyStringStage').addEventListener('click', () => void this.copyStringPreview());
            this.childrenToggleBtn.addEventListener('click', () => {
                this.childrenTableMinimized = !this.childrenTableMinimized;
                this.applyChildrenTableLayout();
                this.app.canvas.redraw();
            });
            this.bindChildrenSplitter();
        }

        async render(dto: NodeInspectionDto): Promise<void> {
            this.renderBreadcrumbs(dto);
            // Size the child table before the canvas stage measures its viewport
            this.syncChildrenTableMinimizedState(dto);
            this.renderSound(dto);
            this.renderString(dto);
            await this.app.canvas.render(dto);
            this.renderChildrenTable(dto);
        }

        clear(): void {
            this.app.canvas.clear();
            this.soundSection.classList.add('hidden');
            this.stringSection.classList.add('hidden');
            this.childrenTableMinimized = false;
            this.lastInspectedLayerKey = '';
            this.applyChildrenTableLayout();
            this.breadcrumbBar.innerHTML = '';
            this.metaBadges.innerHTML = '';
            this.childrenBody.innerHTML = '';
        }

        /** View > Reset Layout: restores the default child-table height. */
        resetLayout(): void {
            this.childrenTableHeight = DEFAULT_CHILDREN_TABLE_HEIGHT;
            this.childrenTableMinimized = false;
            this.applyChildrenTableLayout();
        }

        highlightChildRow(path: string): void {
            this.childrenBody.querySelectorAll<HTMLElement>('tr').forEach((tr) => {
                tr.classList.toggle('active-row', tr.dataset.path === path);
            });
        }

        private renderBreadcrumbs(dto: NodeInspectionDto): void {
            this.breadcrumbBar.innerHTML = '';
            const addCrumb = (label: string, path: string, active: boolean) => {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = `crumb-btn${active ? ' active' : ''}`;
                btn.textContent = label;
                btn.addEventListener('click', () => void this.app.selectAndInspectNode(dto.archiveId, path, true));
                this.breadcrumbBar.appendChild(btn);
            };

            addCrumb(dto.archiveName, '', !dto.path);
            const parts = dto.path ? dto.path.split('/').filter((p) => p.length > 0) : [];
            let accum = '';
            parts.forEach((part, idx) => {
                const sep = document.createElement('span');
                sep.className = 'crumb-sep';
                sep.textContent = '/';
                this.breadcrumbBar.appendChild(sep);
                accum = accum ? `${accum}/${part}` : part;
                addCrumb(part, accum, idx === parts.length - 1);
            });

            this.metaBadges.innerHTML = `
                <span class="meta-chip">${typeBadge(dto.nodeKind, dto.propertyKind)}</span>
                <span class="meta-chip">${formatByteSize(dto.sizeBytes)}</span>
                ${CHANGE_STATE_CHIPS[dto.changeState] ?? ''}
            `;
        }

        // ---------------------------------------------------------------------
        // Sound preview
        // ---------------------------------------------------------------------

        private renderSound(dto: NodeInspectionDto): void {
            if (this.activeAudioUrl) {
                URL.revokeObjectURL(this.activeAudioUrl);
                this.activeAudioUrl = null;
            }

            const s = dto.preview.sound;
            if (!s) {
                this.soundSection.classList.add('hidden');
                this.audioEl.removeAttribute('src');
                return;
            }

            this.soundSection.classList.remove('hidden');
            const audioBytes = toUint8Array(s.audioBytes);

            // A parent node previews its first Sound child
            const firstSoundChild =
                dto.propertyKind !== RepackerPropertyKind.Sound
                    ? (dto.children || []).find((c) => c.propertyKind === RepackerPropertyKind.Sound)
                    : null;
            this.soundTitle.textContent = `${firstSoundChild ? firstSoundChild.name : dto.name} (${s.formatName})`;
            this.soundSubtitle.textContent = `${dto.archiveName} \u2022 ${firstSoundChild ? firstSoundChild.path : dto.path}`;

            const blob = new Blob([audioBytes as unknown as BlobPart], { type: s.mimeType || 'audio/wav' });
            this.activeAudioUrl = URL.createObjectURL(blob);
            this.audioEl.src = this.activeAudioUrl;

            const metric = (label: string, value: string) =>
                `<div class="metric-box"><div class="metric-label">${label}</div><div class="metric-val">${value}</div></div>`;
            this.soundMetrics.innerHTML = [
                metric('Duration', `${s.durationMs} ms`),
                metric('Sample Rate', `${s.sampleRate} Hz`),
                metric('Channels', s.channels === 1 ? 'Mono (1ch)' : `Stereo (${s.channels}ch)`),
                metric('Stream Size', formatByteSize(s.dataLength)),
            ].join('');

            this.drawWaveform(audioBytes);
        }

        private drawWaveform(audioBytes: Uint8Array): void {
            const canvas = this.waveformCanvas;
            const ctx = canvas.getContext('2d')!;
            const w = canvas.width;
            const h = canvas.height;
            ctx.clearRect(0, 0, w, h);

            ctx.strokeStyle = 'rgba(255, 255, 255, 0.08)';
            ctx.beginPath();
            ctx.moveTo(0, h / 2);
            ctx.lineTo(w, h / 2);
            ctx.stroke();

            // Treat the payload as 16-bit PCM after a 44-byte WAV header
            const startOffset = 44;
            if (audioBytes.length <= startOffset) return;
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

        // ---------------------------------------------------------------------
        // String preview
        // ---------------------------------------------------------------------

        /** The string to preview: the node itself, or the first String child of a node without other previews. */
        private getStringPreviewTarget(dto: NodeInspectionDto): StringPreviewTarget | null {
            if (dto.propertyKind === RepackerPropertyKind.String) {
                return {
                    name: dto.name,
                    path: dto.path,
                    text: dto.rawStringValue ?? dto.displayValue ?? '',
                    sizeBytes: dto.sizeBytes,
                };
            }
            const { canvas, sound, animationFrames } = dto.preview;
            if (!canvas && !sound && (!animationFrames || animationFrames.length === 0)) {
                const firstStrChild = (dto.children || []).find((c) => c.propertyKind === RepackerPropertyKind.String);
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

        private renderString(dto: NodeInspectionDto): void {
            const target = this.getStringPreviewTarget(dto);
            if (!target) {
                this.stringSection.classList.add('hidden');
                return;
            }

            this.stringSection.classList.remove('hidden');
            const text = target.text;
            this.stringTitle.textContent = target.name;
            this.stringSubtitle.textContent = `${dto.archiveName} \u2022 ${target.path}`;

            const lineCount = text.length === 0 ? 0 : text.split(/\r\n|\r|\n|\\n/).length;
            const lineLabel = lineCount > 1 ? ` \u2022 ${lineCount} lines` : '';
            this.stringLengthMeta.textContent = `${text.length} chars${lineLabel} \u2022 ${formatByteSize(target.sizeBytes)}`;

            if (text.length === 0) {
                this.stringViewer.textContent = '(Empty string)';
                this.stringViewer.classList.add('empty');
            } else {
                // WZ strings often store escaped "\n" sequences; show them as real line breaks
                this.stringViewer.textContent = text.includes('\n') ? text : text.replace(/\\r\\n/g, '\n').replace(/\\n/g, '\n');
                this.stringViewer.classList.remove('empty');
            }
        }

        private async copyStringPreview(): Promise<void> {
            const dto = this.app.currentInspection;
            const target = dto ? this.getStringPreviewTarget(dto) : null;
            if (!target) return;
            try {
                await navigator.clipboard.writeText(target.text);
                this.app.showToast('Copied string to clipboard', ToastKind.Success);
            } catch {
                this.app.showToast('Failed to copy string', ToastKind.Error);
            }
        }

        // ---------------------------------------------------------------------
        // Child nodes table
        // ---------------------------------------------------------------------

        private hasStagePreview(dto: NodeInspectionDto | null): boolean {
            if (!dto) return false;
            const { canvas, animationFrames, sound } = dto.preview;
            const hasCanvas = Boolean(canvas && canvas.pngBytes) || (Array.isArray(animationFrames) && animationFrames.length > 0);
            const hasSound = Boolean(sound && sound.audioBytes);
            return hasCanvas || hasSound || Boolean(this.getStringPreviewTarget(dto));
        }

        /** The row to highlight: the previewed child (frame, canvas, sound or string), or the node itself. */
        private getHighlightedChildRowPath(dto: NodeInspectionDto): string {
            if (dto.childCount === 0) {
                return dto.path;
            }
            const { canvas, animationFrames, sound } = dto.preview;
            const children = dto.children || [];
            if (animationFrames && animationFrames.length > 0) {
                return animationFrames[0].framePath;
            }
            if (canvas && dto.propertyKind !== RepackerPropertyKind.Canvas) {
                const firstCanvas = children.find((c) => c.propertyKind === RepackerPropertyKind.Canvas);
                if (firstCanvas) return firstCanvas.path;
            }
            if (sound && dto.propertyKind !== RepackerPropertyKind.Sound) {
                const firstSound = children.find((c) => c.propertyKind === RepackerPropertyKind.Sound);
                if (firstSound) return firstSound.path;
            }
            const strTarget = this.getStringPreviewTarget(dto);
            if (strTarget && strTarget.path !== dto.path) {
                return strTarget.path;
            }
            return dto.path;
        }

        /** Siblings share a layer key, so the table stays minimized while browsing between them. */
        private getNodeLayerKey(dto: NodeInspectionDto): string {
            const parts = dto.path ? dto.path.split('/').filter((p) => p.length > 0) : [];
            const parentPath = parts.length > 1 ? parts.slice(0, -1).join('/') : '';
            return `${dto.archiveId}::${parentPath}::${parts.length}`;
        }

        private syncChildrenTableMinimizedState(dto: NodeInspectionDto): void {
            const layerKey = this.getNodeLayerKey(dto);
            if (this.childrenTableMinimized && layerKey !== this.lastInspectedLayerKey) {
                this.childrenTableMinimized = false;
            }
            this.lastInspectedLayerKey = layerKey;
            this.applyChildrenTableLayout(this.hasStagePreview(dto));
        }

        private applyChildrenTableLayout(hasPreview = this.hasStagePreview(this.app.currentInspection)): void {
            this.childrenSplitter.classList.toggle('hidden', !hasPreview);

            const section = this.childrenSection;
            section.classList.toggle('minimized', this.childrenTableMinimized);
            if (hasPreview && !this.childrenTableMinimized) {
                section.style.flex = '0 0 auto';
                section.style.height = `${Math.round(this.childrenTableHeight)}px`;
            } else {
                section.style.flex = '';
                section.style.height = '';
            }

            this.childrenToggleBtn.innerHTML = this.childrenTableMinimized ? '&#9652;' : '&#9662;';
            this.childrenToggleBtn.title = this.childrenTableMinimized ? 'Expand child nodes table' : 'Minimize child nodes table';
        }

        private bindChildrenSplitter(): void {
            const splitter = this.childrenSplitter;
            splitter.addEventListener('mousedown', (ev: MouseEvent) => {
                ev.preventDefault();
                const startY = ev.clientY;
                const startH = this.childrenTableMinimized ? 29 : this.childrenSection.getBoundingClientRect().height || this.childrenTableHeight;
                this.childrenTableMinimized = false;

                splitter.classList.add('dragging');
                document.body.classList.add('resizing-row');

                const onMove = (moveEv: MouseEvent) => {
                    const maxH = Math.max(120, this.stageContent.clientHeight - 130);
                    this.childrenTableHeight = Math.max(85, Math.min(maxH, startH + (startY - moveEv.clientY)));
                    this.applyChildrenTableLayout();
                    this.app.canvas.redraw();
                };
                const onUp = () => {
                    splitter.classList.remove('dragging');
                    document.body.classList.remove('resizing-row');
                    window.removeEventListener('mousemove', onMove);
                    window.removeEventListener('mouseup', onUp);
                };
                window.addEventListener('mousemove', onMove);
                window.addEventListener('mouseup', onUp);
            });
        }

        private renderChildrenTable(dto: NodeInspectionDto): void {
            this.childrenBody.innerHTML = '';

            // Leaf nodes list their siblings (the parent's children) so the table stays useful
            const children = dto.children || [];
            const isParentChildren = dto.childCount === 0 && children.length > 0;
            if (isParentChildren) {
                const lastSlash = dto.path.lastIndexOf('/');
                const parentPath = lastSlash >= 0 ? dto.path.slice(0, lastSlash) : '';
                const parentName = parentPath.includes('/') ? parentPath.slice(parentPath.lastIndexOf('/') + 1) : parentPath || dto.archiveName;
                this.childrenTitle.textContent = `Parent Child Nodes (${children.length})`;
                this.childrenMeta.textContent = `Children of ${parentName} \u2022 Click a row to inspect`;
            } else {
                this.childrenTitle.textContent = `Child Nodes (${children.length})`;
                this.childrenMeta.textContent = children.length > 0 ? 'Click a row to inspect' : 'No child nodes';
            }

            const highlightedPath = this.getHighlightedChildRowPath(dto);
            let activeRowEl: HTMLTableRowElement | null = null;
            for (const c of children) {
                const tr = document.createElement('tr');
                tr.dataset.path = c.path;
                if (c.path === highlightedPath) {
                    tr.classList.add('active-row');
                    activeRowEl = tr;
                }
                if (c.changeState === RepackerChangeState.Deleted) {
                    tr.classList.add('deleted-row');
                }
                const sizeOrItems = c.sizeBytes > 0 ? formatByteSize(c.sizeBytes) : c.childCount > 0 ? `${c.childCount} items` : '-';

                tr.innerHTML = `
                    <td>${escapeHtml(c.name)}</td>
                    <td>${typeBadgeHtml(c.nodeKind, c.propertyKind)}</td>
                    <td>${escapeHtml(c.displayValue)}</td>
                    <td class="cell-size">${escapeHtml(sizeOrItems)}</td>
                `;

                tr.addEventListener('click', () => {
                    this.app.contextMenu.close();
                    void this.app.selectAndInspectNode(dto.archiveId, c.path, true);
                });
                tr.addEventListener('contextmenu', (ev) => {
                    ev.preventDefault();
                    ev.stopPropagation();
                    this.app.contextMenu.open(ev.clientX, ev.clientY, dto.archiveId, c.path, c.changeState);
                });
                this.childrenBody.appendChild(tr);
            }

            const scrollWrap = this.childrenBody.closest<HTMLElement>('.table-scroll-wrap');
            if (!scrollWrap) return;
            if (!isParentChildren || !activeRowEl) {
                scrollWrap.scrollTop = 0;
                return;
            }
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
