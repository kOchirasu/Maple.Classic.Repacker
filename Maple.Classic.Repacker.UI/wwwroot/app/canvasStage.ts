namespace MapleRepacker {
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
        frameName: string;
        framePath: string;
    }

    /** Canvas overlays that can be toggled from the stage toolbar or the View menu. */
    export enum CanvasOverlay {
        Origin = 'origin',
        Vectors = 'vectors',
        PixelGrid = 'pixelGrid',
    }

    /** UI-state key each overlay is persisted under. */
    const OVERLAY_STATE_KEYS = {
        [CanvasOverlay.Origin]: 'showOriginCrosshair',
        [CanvasOverlay.Vectors]: 'showVectorBounds',
        [CanvasOverlay.PixelGrid]: 'showPixelGrid',
    } as const satisfies Record<CanvasOverlay, keyof SavedRepackerUiStateDto>;

    const DEFAULT_ZOOM = 2.0;

    /** Canvas / animation viewport: frame decoding, playback, zoom & pan, overlays and the canvas toolbar. */
    export class CanvasStage {
        private readonly section = byId('canvasStageSection');
        private readonly viewport = byId('canvasViewport');
        private readonly canvas = byId<HTMLCanvasElement>('spriteCanvas');
        private readonly ctx: CanvasRenderingContext2D;
        private readonly hud = byId('canvasHudOverlay');
        private readonly zoomReadout = byId('zoomReadout');
        private readonly linkBanner = byId('linkResolutionBanner');
        private readonly linkText = byId('linkBannerText');
        private readonly formatSelect = byId<HTMLSelectElement>('canvasFormatSelect');
        private readonly animBar = byId('animPlayerBar');
        private readonly animStrip = byId('animTimelineStrip');
        private readonly animCounter = byId('animFrameCounter');
        private readonly animPlayBtn = byId('btnAnimPlayPause');
        private readonly backgroundButtons = Array.from(document.querySelectorAll<HTMLElement>('[data-bg]'));
        private readonly overlayCheckboxes: Record<CanvasOverlay, HTMLInputElement> = {
            [CanvasOverlay.Origin]: byId<HTMLInputElement>('chkShowOrigin'),
            [CanvasOverlay.Vectors]: byId<HTMLInputElement>('chkShowVectors'),
            [CanvasOverlay.PixelGrid]: byId<HTMLInputElement>('chkShowPixelGrid'),
        };
        private readonly overlays: Record<CanvasOverlay, boolean> = {
            [CanvasOverlay.Origin]: true,
            [CanvasOverlay.Vectors]: true,
            [CanvasOverlay.PixelGrid]: false,
        };

        private frames: DecodedSpriteFrame[] = [];
        private activeFrameIdx = 0;
        private animPlaying = false;
        private animTimer: ReturnType<typeof setTimeout> | null = null;
        private zoomScale = DEFAULT_ZOOM;
        private panX = 0;
        private panY = 0;

        constructor(private readonly app: RepackerStudioApp) {
            this.ctx = this.canvas.getContext('2d')!;
            this.bindViewport();
            this.bindAnimationControls();
            this.bindToolbar();
        }

        /** The pixel format chosen in the toolbar, used when converting or replacing the canvas. */
        get selectedFormat(): WzCanvasFormat {
            return (this.formatSelect.value as WzCanvasFormat) || WzCanvasFormat.Bgra8888;
        }

        applySavedUiState(uiState: SavedRepackerUiStateDto): void {
            if (uiState.canvasBackground) {
                this.setBackground(uiState.canvasBackground);
            }
            for (const overlay of Object.values(CanvasOverlay)) {
                const saved = uiState[OVERLAY_STATE_KEYS[overlay]];
                if (typeof saved === 'boolean') {
                    this.overlays[overlay] = saved;
                    this.overlayCheckboxes[overlay].checked = saved;
                }
            }
        }

        toggleOverlay(overlay: CanvasOverlay): void {
            this.setOverlay(overlay, !this.overlays[overlay]);
        }

        private setOverlay(overlay: CanvasOverlay, on: boolean): void {
            this.overlays[overlay] = on;
            this.overlayCheckboxes[overlay].checked = on;
            this.redraw();
            void rpcCall(RpcMethod.UiSaveState, { uiState: { [OVERLAY_STATE_KEYS[overlay]]: on } });
        }

        private setBackground(bg: RepackerCanvasBackground): void {
            for (const btn of this.backgroundButtons) {
                btn.classList.toggle('active', btn.dataset.bg === bg);
            }
            this.viewport.className = `canvas-viewport bg-${bg.toLowerCase()}`;
        }

        clear(): void {
            this.stopAnimationLoop();
            this.revokeFrames();
            this.section.classList.add('hidden');
        }

        async render(dto: NodeInspectionDto): Promise<void> {
            this.stopAnimationLoop();
            this.revokeFrames();

            const { canvas, animationFrames, resolvedUolTargetPath } = dto.preview;
            const hasSingleCanvas = Boolean(canvas && canvas.pngBytes);
            const hasAnimFrames = Array.isArray(animationFrames) && animationFrames.length > 0;
            if (!hasSingleCanvas && !hasAnimFrames) {
                this.section.classList.add('hidden');
                return;
            }
            this.section.classList.remove('hidden');

            // Outlink / Inlink / UOL resolution banner
            if (canvas && (canvas.outlink || canvas.inlink)) {
                const linkType = canvas.outlink ? '_outlink' : '_inlink';
                const rawLink = canvas.outlink || canvas.inlink || '';
                const resolved = canvas.resolvedLinkPath ? ` -> Resolved from ${canvas.resolvedLinkPath}` : ' (Unresolved external archive)';
                this.linkText.textContent = `${linkType}: "${rawLink}"${resolved}`;
                this.linkBanner.classList.remove('hidden');
            } else if (resolvedUolTargetPath) {
                this.linkText.textContent = `UOL Link -> "${resolvedUolTargetPath}"`;
                this.linkBanner.classList.remove('hidden');
            } else {
                this.linkBanner.classList.add('hidden');
            }

            if (canvas) {
                this.formatSelect.value = canvas.format || WzCanvasFormat.Bgra8888;
            }

            if (hasAnimFrames && animationFrames.length > 1) {
                for (const f of animationFrames) {
                    const decoded = await this.decodeFrame(toUint8Array(f.pngBytes), f, f.alphaStart, f.frameName, f.framePath);
                    if (decoded) {
                        this.frames.push(decoded);
                    }
                }
            } else if (canvas) {
                const single = await this.decodeFrame(toUint8Array(canvas.pngBytes), canvas, 255, dto.name, dto.path);
                if (single) {
                    this.frames.push(single);
                }
            }

            this.activeFrameIdx = 0;
            this.animPlaying = false;
            this.panX = 0;
            this.panY = 0;
            this.renderAnimationPlayerBar();
            this.redraw();
        }

        private decodeFrame(
            pngBytes: Uint8Array,
            geometry: Pick<DecodedSpriteFrame, 'width' | 'height' | 'origin' | 'lt' | 'rb' | 'head' | 'delayMs'>,
            alphaStart: number,
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
                        width: geometry.width || img.width,
                        height: geometry.height || img.height,
                        origin: geometry.origin || { x: 0, y: 0 },
                        lt: geometry.lt,
                        rb: geometry.rb,
                        head: geometry.head,
                        delayMs: Math.max(30, geometry.delayMs || 120),
                        alphaStart: alphaStart ?? 255,
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

        private revokeFrames(): void {
            for (const f of this.frames) {
                URL.revokeObjectURL(f.url);
            }
            this.frames = [];
        }

        // ---------------------------------------------------------------------
        // Animation playback
        // ---------------------------------------------------------------------

        private stopAnimationLoop(): void {
            if (this.animTimer !== null) {
                clearTimeout(this.animTimer);
                this.animTimer = null;
            }
        }

        private scheduleNextAnimationFrame(): void {
            this.stopAnimationLoop();
            if (!this.animPlaying || this.frames.length <= 1) return;
            const current = this.frames[this.activeFrameIdx];
            const delay = current ? Math.max(30, current.delayMs) : 120;
            this.animTimer = setTimeout(() => {
                this.showFrame((this.activeFrameIdx + 1) % this.frames.length);
                this.scheduleNextAnimationFrame();
            }, delay);
        }

        private showFrame(idx: number): void {
            this.activeFrameIdx = idx;
            this.updateActiveFrameHighlight();
            this.redraw();
        }

        /** Stops playback and steps to another frame. */
        private stepToFrame(idx: number): void {
            this.animPlaying = false;
            this.updatePlayPauseButton();
            this.stopAnimationLoop();
            this.showFrame(idx);
        }

        private updatePlayPauseButton(): void {
            this.animPlayBtn.classList.toggle('play', !this.animPlaying);
            this.animPlayBtn.classList.toggle('stop', this.animPlaying);
            this.animPlayBtn.innerHTML = this.animPlaying
                ? '<span class="anim-btn-icon">&#9632;</span><span>Stop</span>'
                : '<span class="anim-btn-icon">&#9654;</span><span>Play</span>';
        }

        private renderAnimationPlayerBar(): void {
            this.animStrip.innerHTML = '';
            if (this.frames.length <= 1) {
                this.animBar.classList.add('hidden');
                return;
            }
            this.animBar.classList.remove('hidden');
            this.updatePlayPauseButton();

            this.frames.forEach((f, idx) => {
                const pill = document.createElement('button');
                pill.type = 'button';
                pill.className = `anim-frame-pill${idx === this.activeFrameIdx ? ' active' : ''}`;
                pill.innerHTML = `<span>#${escapeHtml(f.frameName)}</span><span>${f.delayMs}ms</span>`;
                pill.addEventListener('click', () => this.showFrame(idx));
                pill.addEventListener('dblclick', () => {
                    if (this.app.selectedArchiveId && f.framePath) {
                        void this.app.selectAndInspectNode(this.app.selectedArchiveId, f.framePath, true);
                    }
                });
                this.animStrip.appendChild(pill);
            });

            this.updateActiveFrameHighlight();
        }

        private updateActiveFrameHighlight(): void {
            this.animCounter.textContent = `Frame ${this.activeFrameIdx + 1} / ${this.frames.length}`;
            this.animStrip.querySelectorAll('.anim-frame-pill').forEach((el, idx) => {
                el.classList.toggle('active', idx === this.activeFrameIdx);
            });

            const activeFrame = this.frames[this.activeFrameIdx];
            if (activeFrame?.framePath) {
                this.app.stage.highlightChildRow(activeFrame.framePath);
            }
        }

        private bindAnimationControls(): void {
            this.animPlayBtn.addEventListener('click', () => {
                if (this.frames.length <= 1) return;
                this.animPlaying = !this.animPlaying;
                this.updatePlayPauseButton();
                if (this.animPlaying) {
                    this.scheduleNextAnimationFrame();
                } else {
                    this.stopAnimationLoop();
                    this.showFrame(0);
                }
            });
            byId('btnAnimPrev').addEventListener('click', () => {
                if (this.frames.length === 0) return;
                this.stepToFrame((this.activeFrameIdx - 1 + this.frames.length) % this.frames.length);
            });
            byId('btnAnimNext').addEventListener('click', () => {
                if (this.frames.length === 0) return;
                this.stepToFrame((this.activeFrameIdx + 1) % this.frames.length);
            });
        }

        // ---------------------------------------------------------------------
        // Viewport: background, zoom, pan, overlays
        // ---------------------------------------------------------------------

        private setZoom(newZoom: number): void {
            this.zoomScale = Math.max(0.25, Math.min(12.0, newZoom));
            this.zoomReadout.textContent = `${Math.round(this.zoomScale * 100)}%`;
            this.redraw();
        }

        private bindViewport(): void {
            for (const btn of this.backgroundButtons) {
                btn.addEventListener('click', () => {
                    const bg = (btn.dataset.bg as RepackerCanvasBackground) || RepackerCanvasBackground.Checker;
                    this.setBackground(bg);
                    void rpcCall(RpcMethod.UiSaveState, { uiState: { canvasBackground: bg } });
                });
            }

            byId('btnZoomIn').addEventListener('click', () => this.setZoom(this.zoomScale + 0.5));
            byId('btnZoomOut').addEventListener('click', () => this.setZoom(this.zoomScale - 0.5));
            byId('btnZoom1x').addEventListener('click', () => this.setZoom(1.0));
            byId('btnZoomReset').addEventListener('click', () => {
                this.panX = 0;
                this.panY = 0;
                this.setZoom(DEFAULT_ZOOM);
            });

            this.viewport.addEventListener('wheel', (ev: WheelEvent) => {
                ev.preventDefault();
                this.setZoom(this.zoomScale + (ev.deltaY < 0 ? 0.25 : -0.25));
            });

            let dragging = false;
            let dragStartX = 0;
            let dragStartY = 0;
            this.viewport.addEventListener('mousedown', (ev: MouseEvent) => {
                dragging = true;
                dragStartX = ev.clientX - this.panX;
                dragStartY = ev.clientY - this.panY;
            });
            window.addEventListener('mousemove', (ev: MouseEvent) => {
                if (!dragging) return;
                this.panX = ev.clientX - dragStartX;
                this.panY = ev.clientY - dragStartY;
                this.redraw();
            });
            window.addEventListener('mouseup', () => {
                dragging = false;
            });
            window.addEventListener('resize', () => this.redraw());

            for (const overlay of Object.values(CanvasOverlay)) {
                const chk = this.overlayCheckboxes[overlay];
                chk.addEventListener('change', () => this.setOverlay(overlay, chk.checked));
            }
        }

        private bindToolbar(): void {
            byId('btnConvertCanvasFormat').addEventListener('click', () => void this.app.nodes.convertSelectedCanvasFormat(this.selectedFormat));
            byId('btnReplacePng').addEventListener('click', () => this.app.nodes.pickCanvasReplacement());
            byId('btnExportPng').addEventListener('click', () => this.app.files.exportSelectedNode(RepackerExportFormat.Png));
            byId('btnJumpToLinkedTarget').addEventListener('click', () => this.jumpToLinkedTarget());
        }

        private jumpToLinkedTarget(): void {
            const dto = this.app.currentInspection;
            if (!dto) return;
            if (dto.preview.resolvedUolTargetPath) {
                void this.app.selectAndInspectNode(dto.archiveId, dto.preview.resolvedUolTargetPath, true);
                return;
            }
            const resolved = dto.preview.canvas?.resolvedLinkPath;
            if (!resolved) return;

            // Outlinks resolve to "ArchiveName:path"; inlinks are paths inside the same archive
            const colonIdx = resolved.indexOf(':');
            if (colonIdx > 0) {
                const arName = resolved.slice(0, colonIdx).toLowerCase();
                const targetAr = this.app.archives.find((a) => a.name.toLowerCase() === arName);
                if (targetAr) {
                    void this.app.selectAndInspectNode(targetAr.archiveId, resolved.slice(colonIdx + 1), true);
                }
            } else {
                void this.app.selectAndInspectNode(dto.archiveId, resolved, true);
            }
        }

        // ---------------------------------------------------------------------
        // Drawing
        // ---------------------------------------------------------------------

        /** Redraws the active frame; call after anything that changes the viewport size. */
        redraw(): void {
            const rect = this.viewport.getBoundingClientRect();
            const cw = Math.max(300, Math.floor(rect.width));
            const ch = Math.max(180, Math.floor(rect.height));
            if (this.canvas.width !== cw || this.canvas.height !== ch) {
                this.canvas.width = cw;
                this.canvas.height = ch;
            }

            const ctx = this.ctx;
            ctx.clearRect(0, 0, cw, ch);

            const frame = this.frames[this.activeFrameIdx];
            if (!frame) return;
            const zoom = this.zoomScale;

            // Center the bounding box of the sprite (or animation sequence) in the viewport + user pan offset
            let minRelX = -frame.origin.x;
            let maxRelX = frame.width - frame.origin.x;
            let minRelY = -frame.origin.y;
            let maxRelY = frame.height - frame.origin.y;
            for (const f of this.frames) {
                minRelX = Math.min(minRelX, -f.origin.x);
                maxRelX = Math.max(maxRelX, f.width - f.origin.x);
                minRelY = Math.min(minRelY, -f.origin.y);
                maxRelY = Math.max(maxRelY, f.height - f.origin.y);
            }
            const centerRelX = (minRelX + maxRelX) * 0.5;
            const centerRelY = (minRelY + maxRelY) * 0.5;

            const anchorX = Math.round(cw * 0.5 - centerRelX * zoom + this.panX);
            const anchorY = Math.round(ch * 0.5 - centerRelY * zoom + this.panY);

            const drawX = Math.round(anchorX - frame.origin.x * zoom);
            const drawY = Math.round(anchorY - frame.origin.y * zoom);
            const drawW = Math.round(frame.width * zoom);
            const drawH = Math.round(frame.height * zoom);

            ctx.save();
            ctx.imageSmoothingEnabled = false;
            ctx.globalAlpha = Math.max(0.05, Math.min(1.0, frame.alphaStart / 255));
            ctx.drawImage(frame.img, drawX, drawY, drawW, drawH);
            ctx.restore();

            // Optional pixel grid when zoomed >= 3x
            if (this.overlays[CanvasOverlay.PixelGrid] && zoom >= 3) {
                ctx.save();
                ctx.strokeStyle = 'rgba(255, 255, 255, 0.12)';
                ctx.lineWidth = 1;
                for (let x = 0; x <= frame.width; x++) {
                    const px = drawX + Math.floor(x * zoom) + 0.5;
                    ctx.beginPath();
                    ctx.moveTo(px, drawY);
                    ctx.lineTo(px, drawY + drawH);
                    ctx.stroke();
                }
                for (let y = 0; y <= frame.height; y++) {
                    const py = drawY + Math.floor(y * zoom) + 0.5;
                    ctx.beginPath();
                    ctx.moveTo(drawX, py);
                    ctx.lineTo(drawX + drawW, py);
                    ctx.stroke();
                }
                ctx.restore();
            }

            // Optional lt / rb bounding box & head marker
            if (this.overlays[CanvasOverlay.Vectors]) {
                if (frame.lt && frame.rb) {
                    ctx.save();
                    ctx.strokeStyle = '#3794ff';
                    ctx.lineWidth = 1.5;
                    ctx.setLineDash([4, 3]);
                    ctx.strokeRect(
                        anchorX + frame.lt.x * zoom,
                        anchorY + frame.lt.y * zoom,
                        (frame.rb.x - frame.lt.x) * zoom,
                        (frame.rb.y - frame.lt.y) * zoom
                    );
                    ctx.restore();
                }
                if (frame.head) {
                    ctx.save();
                    ctx.fillStyle = '#4cd964';
                    ctx.beginPath();
                    ctx.arc(anchorX + frame.head.x * zoom, anchorY + frame.head.y * zoom, 4, 0, Math.PI * 2);
                    ctx.fill();
                    ctx.restore();
                }
            }

            // Optional Origin Crosshair
            if (this.overlays[CanvasOverlay.Origin]) {
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

            const fmtInfo = this.app.currentInspection?.preview.canvas?.formatName || 'Canvas';
            this.hud.innerHTML = `
                <span>${frame.width}&times;${frame.height} px</span>
                <span>Origin: (${frame.origin.x}, ${frame.origin.y})</span>
                <span>Delay: ${frame.delayMs} ms</span>
                <span>${escapeHtml(fmtInfo)}</span>
            `;
        }
    }
}
