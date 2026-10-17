namespace MapleRepacker {
    export interface LayoutControllerOptions {
        onLayoutChanged?: (layout: SavedRepackerLayoutDto) => void;
    }

    export class LayoutController {
        private readonly treePane: HTMLElement;
        private readonly inspectorPane: HTMLElement;
        private readonly workbenchPane: HTMLElement;
        private readonly leftSplitter: HTMLElement;
        private readonly rightSplitter: HTMLElement;
        private readonly bottomSplitter: HTMLElement;
        private readonly onLayoutChanged?: (layout: SavedRepackerLayoutDto) => void;

        private treeWidth = 310;
        private inspectorWidth = 380;
        private inspectorCollapsed = false;
        private workbenchHeight = 250;
        private workbenchCollapsed = false;

        constructor(options?: LayoutControllerOptions) {
            this.treePane = document.getElementById('treePane')!;
            this.inspectorPane = document.getElementById('inspectorPane')!;
            this.workbenchPane = document.getElementById('workbenchPane')!;
            this.leftSplitter = document.getElementById('leftSplitter')!;
            this.rightSplitter = document.getElementById('rightSplitter')!;
            this.bottomSplitter = document.getElementById('bottomSplitter')!;
            this.onLayoutChanged = options?.onLayoutChanged;

            this.bindSplitters();
            this.applySizes();
        }

        public applySavedLayout(saved?: SavedRepackerLayoutDto | null): void {
            if (!saved) return;
            if (typeof saved.treePaneWidth === 'number' && saved.treePaneWidth >= 200 && saved.treePaneWidth <= 650) {
                this.treeWidth = saved.treePaneWidth;
            }
            if (typeof saved.inspectorPaneWidth === 'number' && saved.inspectorPaneWidth >= 260 && saved.inspectorPaneWidth <= 720) {
                this.inspectorWidth = saved.inspectorPaneWidth;
            }
            if (typeof saved.inspectorCollapsed === 'boolean') {
                this.inspectorCollapsed = saved.inspectorCollapsed;
            }
            if (typeof saved.workbenchHeight === 'number' && saved.workbenchHeight >= 110 && saved.workbenchHeight <= 600) {
                this.workbenchHeight = saved.workbenchHeight;
            }
            if (typeof saved.workbenchCollapsed === 'boolean') {
                this.workbenchCollapsed = saved.workbenchCollapsed;
            }
            this.applySizes();
        }

        public toggleInspector(forceCollapsed?: boolean): boolean {
            this.inspectorCollapsed = typeof forceCollapsed === 'boolean' ? forceCollapsed : !this.inspectorCollapsed;
            this.applySizes();
            this.emitLayoutChange();
            return this.inspectorCollapsed;
        }

        public isInspectorCollapsed(): boolean {
            return this.inspectorCollapsed;
        }

        public toggleWorkbench(forceCollapsed?: boolean): boolean {
            this.workbenchCollapsed = typeof forceCollapsed === 'boolean' ? forceCollapsed : !this.workbenchCollapsed;
            this.applySizes();
            this.emitLayoutChange();
            return this.workbenchCollapsed;
        }

        public isWorkbenchCollapsed(): boolean {
            return this.workbenchCollapsed;
        }

        public resetLayout(): void {
            this.treeWidth = 310;
            this.inspectorWidth = 380;
            this.inspectorCollapsed = false;
            this.workbenchHeight = 250;
            this.workbenchCollapsed = false;
            this.applySizes();
            this.emitLayoutChange();
        }

        private applySizes(): void {
            this.treePane.style.width = `${Math.round(this.treeWidth)}px`;
            if (this.inspectorCollapsed) {
                this.inspectorPane.classList.add('collapsed');
                this.inspectorPane.style.width = '32px';
            } else {
                this.inspectorPane.classList.remove('collapsed');
                this.inspectorPane.style.width = `${Math.round(this.inspectorWidth)}px`;
            }
            if (this.workbenchCollapsed) {
                this.workbenchPane.classList.add('collapsed');
                this.workbenchPane.style.height = '32px';
            } else {
                this.workbenchPane.classList.remove('collapsed');
                this.workbenchPane.style.height = `${Math.round(this.workbenchHeight)}px`;
            }

            const inspectorToggleBtn = document.getElementById('btnToggleInspectorCollapse');
            if (inspectorToggleBtn) {
                inspectorToggleBtn.innerHTML = this.inspectorCollapsed ? '&#9666;' : '&#9656;';
                inspectorToggleBtn.title = this.inspectorCollapsed
                    ? 'Expand Property Inspector'
                    : 'Minimize Property Inspector';
            }

            const toggleBtn = document.getElementById('btnToggleWorkbenchCollapse');
            if (toggleBtn) {
                toggleBtn.innerHTML = this.workbenchCollapsed ? '&#9652;' : '&#9662;';
                toggleBtn.title = this.workbenchCollapsed
                    ? 'Expand bottom workbench'
                    : 'Minimize bottom workbench';
            }
        }

        private emitLayoutChange(): void {
            if (this.onLayoutChanged) {
                this.onLayoutChanged({
                    treePaneWidth: Math.round(this.treeWidth),
                    inspectorPaneWidth: Math.round(this.inspectorWidth),
                    inspectorCollapsed: this.inspectorCollapsed,
                    workbenchHeight: Math.round(this.workbenchHeight),
                    workbenchCollapsed: this.workbenchCollapsed,
                });
            }
        }

        private bindSplitters(): void {
            // Left vertical splitter (Tree pane width)
            this.leftSplitter.addEventListener('mousedown', (ev: MouseEvent) => {
                ev.preventDefault();
                const startX = ev.clientX;
                const startW = this.treeWidth;
                this.leftSplitter.classList.add('dragging');
                document.body.classList.add('resizing-col');

                const onMove = (moveEv: MouseEvent) => {
                    const delta = moveEv.clientX - startX;
                    this.treeWidth = Math.max(210, Math.min(620, startW + delta));
                    this.applySizes();
                };

                const onUp = () => {
                    this.leftSplitter.classList.remove('dragging');
                    document.body.classList.remove('resizing-col');
                    window.removeEventListener('mousemove', onMove);
                    window.removeEventListener('mouseup', onUp);
                    this.emitLayoutChange();
                };

                window.addEventListener('mousemove', onMove);
                window.addEventListener('mouseup', onUp);
            });

            // Right vertical splitter (Inspector pane width)
            this.rightSplitter.addEventListener('mousedown', (ev: MouseEvent) => {
                ev.preventDefault();
                if (this.inspectorCollapsed) {
                    this.inspectorCollapsed = false;
                }
                const startX = ev.clientX;
                const startW = this.inspectorWidth;
                this.rightSplitter.classList.add('dragging');
                document.body.classList.add('resizing-col');

                const onMove = (moveEv: MouseEvent) => {
                    const delta = startX - moveEv.clientX;
                    this.inspectorWidth = Math.max(260, Math.min(680, startW + delta));
                    this.applySizes();
                };

                const onUp = () => {
                    this.rightSplitter.classList.remove('dragging');
                    document.body.classList.remove('resizing-col');
                    window.removeEventListener('mousemove', onMove);
                    window.removeEventListener('mouseup', onUp);
                    this.emitLayoutChange();
                };

                window.addEventListener('mousemove', onMove);
                window.addEventListener('mouseup', onUp);
            });

            // Bottom horizontal splitter (Workbench height)
            this.bottomSplitter.addEventListener('mousedown', (ev: MouseEvent) => {
                ev.preventDefault();
                if (this.workbenchCollapsed) {
                    this.workbenchCollapsed = false;
                }
                const startY = ev.clientY;
                const startH = this.workbenchHeight;
                this.bottomSplitter.classList.add('dragging');
                document.body.classList.add('resizing-row');

                const onMove = (moveEv: MouseEvent) => {
                    const delta = startY - moveEv.clientY;
                    this.workbenchHeight = Math.max(120, Math.min(580, startH + delta));
                    this.applySizes();
                };

                const onUp = () => {
                    this.bottomSplitter.classList.remove('dragging');
                    document.body.classList.remove('resizing-row');
                    window.removeEventListener('mousemove', onMove);
                    window.removeEventListener('mouseup', onUp);
                    this.emitLayoutChange();
                };

                window.addEventListener('mousemove', onMove);
                window.addEventListener('mouseup', onUp);
            });
        }
    }
}
