namespace MapleRepacker {
    const WORKBENCH_PANEL_IDS: Partial<Record<RepackerWorkbenchTab, string>> = {
        [RepackerWorkbenchTab.Json]: 'wbPanelJson',
        [RepackerWorkbenchTab.Search]: 'wbPanelSearch',
        [RepackerWorkbenchTab.Hex]: 'wbPanelHex',
        [RepackerWorkbenchTab.Logs]: 'wbPanelLogs',
    };

    const EMPTY_JSON_PLACEHOLDER = '{\n  "//": "Select an .img or property node to inspect & edit JSON"\n}';
    const HEX_BYTES_PER_ROW = 16;
    const HEX_MAX_RENDER_BYTES = 4096;

    /** Bottom workbench: tab strip, JSON subtree editor, hex inspector and the log console. */
    export class Workbench {
        private readonly tabs = Array.from(document.querySelectorAll<HTMLElement>('#workbenchTabs .wb-tab'));
        private readonly panels = new Map<RepackerWorkbenchTab, HTMLElement>(
            Object.entries(WORKBENCH_PANEL_IDS).map(([tab, id]) => [tab as RepackerWorkbenchTab, byId(id!)])
        );
        private readonly jsonActions = byId('wbJsonActions');
        private readonly jsonEditor: Cm6EditorBridge | null;
        private lastLoadedJsonText = '';

        private readonly hexDump = byId('hexDumpContainer');
        private readonly hexSidebar = byId('hexDataInspector');
        private hexBytes: Uint8Array = new Uint8Array(0);
        private selectedHexOffset = 0;

        private readonly logCountPill = byId('logCountPill');
        private readonly logsContainer = byId('logsContainer');

        constructor(private readonly app: RepackerStudioApp) {
            const host = byId('jsonEditorHost');
            this.jsonEditor = window.MapleCM6
                ? window.MapleCM6.createEditor(host, {
                      initialValue: EMPTY_JSON_PLACEHOLDER,
                      onSave: () => void this.applyJson(),
                  })
                : null;

            for (const tab of this.tabs) {
                tab.addEventListener('click', () => {
                    this.activateTab((tab.dataset.tab as RepackerWorkbenchTab) || RepackerWorkbenchTab.Json);
                });
            }
            byId('btnToggleWorkbenchCollapse').addEventListener('click', () => {
                this.app.layout.toggleWorkbench();
                this.app.canvas.redraw();
            });

            byId('btnApplyJsonSubtree').addEventListener('click', () => void this.applyJson());
            byId('btnFormatJson').addEventListener('click', () => this.formatJson());
            byId('btnResetJson').addEventListener('click', () => this.jsonEditor?.setValue(this.lastLoadedJsonText));
            byId('btnClearLogs').addEventListener('click', () => void rpcCall(RpcMethod.LogsClear, {}));
        }

        activateTab(tabId: RepackerWorkbenchTab, expandIfCollapsed = true): void {
            // The batch tab was folded into a modal; saved UI state may still reference it
            if (tabId === RepackerWorkbenchTab.Batch) {
                tabId = RepackerWorkbenchTab.Json;
            }
            if (expandIfCollapsed && this.app.layout.isWorkbenchCollapsed()) {
                this.app.layout.toggleWorkbench(false);
            }
            for (const tab of this.tabs) {
                tab.classList.toggle('active', tab.dataset.tab === tabId);
            }
            for (const [tab, panel] of this.panels) {
                panel.classList.toggle('active', tab === tabId);
            }

            this.jsonActions.classList.toggle('hidden', tabId !== RepackerWorkbenchTab.Json);
            if (tabId === RepackerWorkbenchTab.Json) {
                this.jsonEditor?.refresh();
            }
            void rpcCall(RpcMethod.UiSaveState, { uiState: { activeWorkbenchTab: tabId } });
        }

        /** Loads the inspected node into the JSON editor and hex inspector. */
        render(dto: NodeInspectionDto): void {
            this.loadJson(dto.preview.jsonPreview || '{}');
            this.setHexBytes(toUint8Array(dto.preview.rawBinarySlice));
        }

        clear(): void {
            this.loadJson('{}');
            this.setHexBytes(new Uint8Array(0));
        }

        // ---------------------------------------------------------------------
        // JSON subtree editor
        // ---------------------------------------------------------------------

        private loadJson(text: string): void {
            this.lastLoadedJsonText = text;
            this.jsonEditor?.setValue(text);
        }

        private async applyJson(): Promise<void> {
            if (this.jsonEditor) {
                await this.app.nodes.applyJsonToSelected(this.jsonEditor.getValue());
            }
        }

        private formatJson(): void {
            if (!this.jsonEditor) return;
            try {
                const parsed = JSON.parse(this.jsonEditor.getValue());
                this.jsonEditor.setValue(JSON.stringify(parsed, null, 2));
            } catch (err) {
                this.app.showToast(`JSON Syntax Error: ${(err as Error).message}`, ToastKind.Error);
            }
        }

        // ---------------------------------------------------------------------
        // Hex & binary data inspector
        // ---------------------------------------------------------------------

        private setHexBytes(bytes: Uint8Array): void {
            this.hexBytes = bytes;
            this.selectedHexOffset = 0;
            this.renderHexDump();
        }

        private renderHexDump(): void {
            this.hexDump.innerHTML = '';
            const bytes = this.hexBytes;
            if (bytes.length === 0) {
                this.hexDump.textContent = 'No binary slice available for this node.';
                this.renderHexSidebar();
                return;
            }

            const maxRenderBytes = Math.min(bytes.length, HEX_MAX_RENDER_BYTES);
            for (let offset = 0; offset < maxRenderBytes; offset += HEX_BYTES_PER_ROW) {
                const rowLen = Math.min(HEX_BYTES_PER_ROW, maxRenderBytes - offset);
                const line = document.createElement('div');
                line.className = 'hex-line';

                const offSpan = document.createElement('span');
                offSpan.className = 'hex-offset';
                offSpan.textContent = offset.toString(16).toUpperCase().padStart(8, '0');
                line.appendChild(offSpan);

                const hexBytesSpan = document.createElement('span');
                hexBytesSpan.className = 'hex-bytes';
                let asciiStr = '';

                for (let i = 0; i < HEX_BYTES_PER_ROW; i++) {
                    const bSpan = document.createElement('span');
                    if (i < rowLen) {
                        const byteIndex = offset + i;
                        const b = bytes[byteIndex];
                        bSpan.className = `hex-byte${byteIndex === this.selectedHexOffset ? ' active' : ''}`;
                        bSpan.textContent = b.toString(16).toUpperCase().padStart(2, '0');
                        bSpan.addEventListener('click', () => {
                            this.selectedHexOffset = byteIndex;
                            this.hexDump.querySelectorAll('.hex-byte.active').forEach((el) => el.classList.remove('active'));
                            bSpan.classList.add('active');
                            this.renderHexSidebar();
                        });
                        asciiStr += b >= 32 && b <= 126 ? String.fromCharCode(b) : '.';
                    } else {
                        // Placeholder with the same box as a real byte so the ASCII column stays aligned.
                        bSpan.className = 'hex-byte hex-byte-empty';
                        bSpan.textContent = '  ';
                        asciiStr += ' ';
                    }
                    hexBytesSpan.appendChild(bSpan);
                    if (i < HEX_BYTES_PER_ROW - 1) {
                        hexBytesSpan.appendChild(document.createTextNode(i === 7 ? '  ' : ' '));
                    }
                }
                line.appendChild(hexBytesSpan);

                const asciiSpan = document.createElement('span');
                asciiSpan.className = 'hex-ascii';
                asciiSpan.textContent = `|${asciiStr}|`;
                line.appendChild(asciiSpan);

                this.hexDump.appendChild(line);
            }

            this.renderHexSidebar();
        }

        private renderHexSidebar(): void {
            const bytes = this.hexBytes;
            const off = this.selectedHexOffset;
            if (bytes.length === 0 || off >= bytes.length) {
                this.hexSidebar.innerHTML = '<div class="card-heading">Data Inspector</div><div class="text-muted">No byte selected</div>';
                return;
            }

            const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
            const rem = bytes.length - off;
            const u8 = dv.getUint8(off);
            const hexOff = off.toString(16).toUpperCase();
            const rows: Array<[string, string]> = [
                ['Offset', `${off} (0x${hexOff})`],
                ['UInt8 / Byte', `${u8} (0x${u8.toString(16).toUpperCase().padStart(2, '0')})`],
                ['Int8 / SByte', String(dv.getInt8(off))],
                ['Int16 (LE)', rem >= 2 ? String(dv.getInt16(off, true)) : '-'],
                ['UInt16 (LE)', rem >= 2 ? String(dv.getUint16(off, true)) : '-'],
                ['Int32 (LE)', rem >= 4 ? String(dv.getInt32(off, true)) : '-'],
                ['UInt32 (LE)', rem >= 4 ? String(dv.getUint32(off, true)) : '-'],
                ['Float32', rem >= 4 ? dv.getFloat32(off, true).toPrecision(6) : '-'],
                ['Float64', rem >= 8 ? dv.getFloat64(off, true).toPrecision(8) : '-'],
            ];
            this.hexSidebar.innerHTML = `
                <div class="card-heading">Byte Inspector @ 0x${hexOff}</div>
                <div class="kv-grid">
                    ${rows.map(([key, val]) => `<div class="kv-key">${key}</div><div class="kv-val">${val}</div>`).join('')}
                </div>
            `;
        }

        // ---------------------------------------------------------------------
        // Log console
        // ---------------------------------------------------------------------

        renderLogs(logs: RepackerLogEntry[]): void {
            this.logCountPill.textContent = String(logs.length);
            this.logsContainer.innerHTML = '';
            for (let i = logs.length - 1; i >= 0; i--) {
                const entry = logs[i];
                const row = document.createElement('div');
                row.className = 'log-row';
                row.innerHTML = `
                    <span class="log-ts">[${escapeHtml(entry.timestamp)}]</span>
                    <span class="log-lvl lvl-${escapeHtml(entry.level)}">${escapeHtml(entry.level.toUpperCase())}</span>
                    <span class="log-msg">${escapeHtml(entry.message)}</span>
                `;
                this.logsContainer.appendChild(row);
            }
        }
    }
}
