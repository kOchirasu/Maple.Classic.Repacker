import { BrowserWindow, Rectangle, screen } from 'electron';
import fs from 'fs';
import path from 'path';

interface PersistedWindowState {
    bounds: Rectangle;
    maximized: boolean;
}

const DEFAULT_BOUNDS = { width: 1580, height: 940 };

/** Remembers window size, position and maximized state across launches (like WindowsDesktopShell does). */
export class WindowStateStore {
    private readonly filePath: string;

    constructor(userDataDir: string) {
        this.filePath = path.join(userDataDir, 'window-state.json');
    }

    /** Last saved bounds if they are still visible on a connected display, otherwise a default size. */
    load(): { bounds: Partial<Rectangle>; maximized: boolean } {
        try {
            const state = JSON.parse(fs.readFileSync(this.filePath, 'utf8')) as PersistedWindowState;
            if (state?.bounds && isVisibleOnAnyDisplay(state.bounds)) {
                return { bounds: state.bounds, maximized: Boolean(state.maximized) };
            }
        } catch {
            // No saved state yet, or unreadable: fall back to defaults
        }
        return { bounds: DEFAULT_BOUNDS, maximized: false };
    }

    track(window: BrowserWindow): void {
        window.on('close', () => {
            const state: PersistedWindowState = {
                bounds: window.isMaximized() ? window.getNormalBounds() : window.getBounds(),
                maximized: window.isMaximized(),
            };
            try {
                fs.mkdirSync(path.dirname(this.filePath), { recursive: true });
                fs.writeFileSync(this.filePath, JSON.stringify(state, null, 2));
            } catch {
                // Non-fatal: window state is a convenience
            }
        });
    }
}

function isVisibleOnAnyDisplay(bounds: Rectangle): boolean {
    return screen.getAllDisplays().some(({ workArea }) => {
        const overlapW = Math.min(bounds.x + bounds.width, workArea.x + workArea.width) - Math.max(bounds.x, workArea.x);
        const overlapH = Math.min(bounds.y + bounds.height, workArea.y + workArea.height) - Math.max(bounds.y, workArea.y);
        return overlapW >= 80 && overlapH >= 60;
    });
}
