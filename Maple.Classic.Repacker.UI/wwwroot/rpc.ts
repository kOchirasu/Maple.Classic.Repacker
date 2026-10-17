declare const MessagePack: {
    encode(value: unknown, options?: { ignoreUndefined?: boolean }): Uint8Array;
    decode(buffer: ArrayLike<number> | ArrayBufferView | ArrayBuffer): unknown;
};

namespace MapleRepacker {
    export enum RepackerNodeKind {
        Archive = 'Archive',
        Directory = 'Directory',
        Image = 'Image',
        Property = 'Property',
    }

    export enum RepackerPropertyKind {
        None = 'None',
        Null = 'Null',
        Short = 'Short',
        Int = 'Int',
        Long = 'Long',
        Float = 'Float',
        Double = 'Double',
        String = 'String',
        Vector = 'Vector',
        Uol = 'Uol',
        Object = 'Object',
        Canvas = 'Canvas',
        Sound = 'Sound',
        Convex = 'Convex',
        RawData = 'RawData',
    }

    export enum RepackerChangeState {
        Unchanged = 'Unchanged',
        Added = 'Added',
        Modified = 'Modified',
        Deleted = 'Deleted',
    }

    export enum WzCanvasFormat {
        Unknown = 'Unknown',
        Bgra4444 = 'Bgra4444',
        Bgra8888 = 'Bgra8888',
        Bgr565 = 'Bgr565',
        Bgr565Block16 = 'Bgr565Block16',
        Dxt3 = 'Dxt3',
        Dxt5 = 'Dxt5',
    }

    export enum RepackerExportFormat {
        Auto = 'Auto',
        Wz = 'Wz',
        Img = 'Img',
        Json = 'Json',
        Png = 'Png',
        Audio = 'Audio',
    }

    export enum RepackerBatchExportMode {
        Png = 'Png',
        Sound = 'Sound',
        Json = 'Json',
        Img = 'Img',
        All = 'All',
    }

    export enum RepackerWorkbenchTab {
        Json = 'Json',
        Search = 'Search',
        Hex = 'Hex',
        Batch = 'Batch',
        Logs = 'Logs',
    }

    export enum RepackerCanvasBackground {
        Checker = 'Checker',
        Dark = 'Dark',
        Light = 'Light',
    }

    export enum RepackerLogLevel {
        Info = 'Info',
        Success = 'Success',
        Warning = 'Warning',
        Error = 'Error',
    }

    export enum ToastKind {
        Info = 'info',
        Success = 'success',
        Error = 'error',
    }

    export enum TreeContextMenuCommand {
        Inspect = 'inspect',
        AddProp = 'addProp',
        AddImg = 'addImg',
        AddDir = 'addDir',
        Copy = 'copy',
        Paste = 'paste',
        Duplicate = 'duplicate',
        Rename = 'rename',
        ExportAuto = 'exportAuto',
        Restore = 'restore',
        Delete = 'delete',
        CloseArchive = 'closeArchive',
    }

    export enum MenuBarAction {
        OpenFileModal = 'openFileModal',
        OpenDirModal = 'openDirModal',
        NewArchiveModal = 'newArchiveModal',
        SaveArchiveDisk = 'saveArchiveDisk',
        DownloadArchive = 'downloadArchive',
        OpenBulkExportModal = 'openBulkExportModal',
        OpenInlineOutlinksModal = 'openInlineOutlinksModal',
        UndoNode = 'undoNode',
        RedoNode = 'redoNode',
        AddPropertyModal = 'addPropertyModal',
        AddImageModal = 'addImageModal',
        AddDirectoryModal = 'addDirectoryModal',
        CopyNode = 'copyNode',
        PasteNode = 'pasteNode',
        DuplicateNode = 'duplicateNode',
        RenameNodePrompt = 'renameNodePrompt',
        DeleteNode = 'deleteNode',
        FocusGlobalSearch = 'focusGlobalSearch',
        ExportSelectedJson = 'exportSelectedJson',
        ExportSelectedImg = 'exportSelectedImg',
        ToggleCrosshair = 'toggleCrosshair',
        ToggleVectors = 'toggleVectors',
        TogglePixelGrid = 'togglePixelGrid',
        ToggleInspector = 'toggleInspector',
        ToggleWorkbench = 'toggleWorkbench',
        ResetLayout = 'resetLayout',
    }

    export enum HostWebMessageType {
        OpenTreeContextMenu = 'openTreeContextMenu',
        CloseTreeContextMenu = 'closeTreeContextMenu',
        TreeContextMenuCommand = 'treeContextMenuCommand',
        RequestAppClose = 'requestAppClose',
        ConfirmAppClose = 'confirmAppClose',
    }

    export interface RepackerLogEntry {
        id: number;
        timestamp: string;
        level: RepackerLogLevel;
        message: string;
    }

    export interface ArchiveSummaryDto {
        archiveId: string;
        name: string;
        filePath: string;
        version: number;
        encVer: number;
        fStart: number;
        copyright: string;
        fileSize: number;
        directoryCount: number;
        imageCount: number;
        modifiedImageCount: number;
        changeState: RepackerChangeState;
        linkedFilePaths?: string[];
    }

    export interface TreeNodeDto {
        archiveId: string;
        path: string;
        name: string;
        nodeKind: RepackerNodeKind;
        propertyKind: RepackerPropertyKind;
        displayValue: string;
        typeBadge: string;
        hasChildren: boolean;
        childCount: number;
        sizeBytes: number;
        changeState: RepackerChangeState;
    }

    export interface SubPropertyRowDto {
        name: string;
        path: string;
        nodeKind: RepackerNodeKind;
        propertyKind: RepackerPropertyKind;
        typeBadge: string;
        displayValue: string;
        childCount: number;
        sizeBytes: number;
        changeState: RepackerChangeState;
    }

    export interface VectorPointDto {
        x: number;
        y: number;
    }

    export interface CanvasPreviewDto {
        width: number;
        height: number;
        format: WzCanvasFormat;
        formatName: string;
        compressedBytes: number;
        pngBytes: Uint8Array | number[];
        origin: VectorPointDto;
        lt?: VectorPointDto | null;
        rb?: VectorPointDto | null;
        head?: VectorPointDto | null;
        delayMs: number;
        zLayer?: string | null;
        inlink?: string | null;
        outlink?: string | null;
        resolvedLinkPath?: string | null;
        isLinkStub: boolean;
    }

    export interface AnimationFrameDto {
        frameIndex: number;
        frameName: string;
        framePath: string;
        width: number;
        height: number;
        format: WzCanvasFormat;
        delayMs: number;
        origin: VectorPointDto;
        lt?: VectorPointDto | null;
        rb?: VectorPointDto | null;
        head?: VectorPointDto | null;
        alphaStart: number;
        alphaEnd: number;
        pngBytes: Uint8Array | number[];
    }

    export interface SoundPreviewDto {
        dataLength: number;
        durationMs: number;
        mimeType: string;
        extension: string;
        formatName: string;
        sampleRate: number;
        channels: number;
        bitsPerSample: number;
        audioBytes: Uint8Array | number[];
        headerBytes: Uint8Array | number[];
    }

    export interface NodeInspectionDto {
        ok: boolean;
        error?: string | null;
        archiveId: string;
        archiveName: string;
        path: string;
        name: string;
        imagePath?: string | null;
        nodeKind: RepackerNodeKind;
        propertyKind: RepackerPropertyKind;
        typeBadge: string;
        displayValue: string;
        rawStringValue?: string | null;
        numericIntValue?: number | null;
        numericFloatValue?: number | null;
        vectorValue?: VectorPointDto | null;
        childCount: number;
        sizeBytes: number;
        changeState: RepackerChangeState;
        canUndo: boolean;
        canRedo: boolean;
        jsonPreview: string;
        children: SubPropertyRowDto[];
        canvas?: CanvasPreviewDto | null;
        animationFrames: AnimationFrameDto[];
        sound?: SoundPreviewDto | null;
        resolvedUolTargetPath?: string | null;
        rawBinarySlice: Uint8Array | number[];
    }

    export interface SearchResultItemDto {
        archiveId: string;
        archiveName: string;
        path: string;
        name: string;
        nodeKind: RepackerNodeKind;
        propertyKind: RepackerPropertyKind;
        typeBadge: string;
        displayValue: string;
    }

    export interface ClipboardStateDto {
        hasData: boolean;
        sourceArchiveId?: string | null;
        sourcePath?: string | null;
        nodeName?: string | null;
        nodeKind: RepackerNodeKind;
        propertyKind: RepackerPropertyKind;
    }

    export interface SavedRepackerLayoutDto {
        treePaneWidth?: number | null;
        inspectorPaneWidth?: number | null;
        inspectorCollapsed?: boolean | null;
        workbenchHeight?: number | null;
        workbenchCollapsed?: boolean | null;
    }

    export interface SavedRepackerUiStateDto {
        layout?: SavedRepackerLayoutDto | null;
        activeWorkbenchTab?: RepackerWorkbenchTab | null;
        canvasBackground?: RepackerCanvasBackground | null;
        showOriginCrosshair?: boolean | null;
        showVectorBounds?: boolean | null;
        showPixelGrid?: boolean | null;
        defaultWzVersion?: number | null;
    }

    export interface WorkspaceStatusPayloadDto {
        type: string;
        stateVersion: number;
        selectedArchiveId?: string | null;
        selectedNodePath: string;
        archives: ArchiveSummaryDto[];
        clipboard: ClipboardStateDto;
        logs: RepackerLogEntry[];
        uiState?: SavedRepackerUiStateDto | null;
        recentPaths: string[];
        supportsNativeDialogs?: boolean;
    }

    export interface RpcOperationResponse {
        ok: boolean;
        error?: string | null;
        archiveId?: string | null;
        path?: string | null;
        count?: number | null;
        archive?: ArchiveSummaryDto | null;
        archives?: ArchiveSummaryDto[] | null;
        clipboard?: ClipboardStateDto | null;
        status?: WorkspaceStatusPayloadDto | null;
        cancelled?: boolean;
        filePaths?: string[] | null;
    }

    export interface TreeChildrenResponse {
        ok: boolean;
        error?: string | null;
        archiveId: string;
        parentPath: string;
        nodes: TreeNodeDto[];
    }

    export interface SearchResponseDto {
        ok: boolean;
        error?: string | null;
        results: SearchResultItemDto[];
    }

    export interface BinaryExportResponseDto {
        ok: boolean;
        error?: string | null;
        fileName: string;
        mimeType: string;
        data: Uint8Array | number[];
    }

    export function toUint8Array(raw: Uint8Array | number[] | undefined | null): Uint8Array {
        if (!raw) return new Uint8Array(0);
        if (raw instanceof Uint8Array) return raw;
        return new Uint8Array(raw);
    }

    export function formatByteSize(bytes: number): string {
        if (!bytes || bytes <= 0) return '0 B';
        if (bytes < 1024) return `${bytes} B`;
        if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
        return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
    }

    function toPascalKeys(val: unknown): unknown {
        if (!val || typeof val !== 'object' || val instanceof Uint8Array || Array.isArray(val)) {
            return Array.isArray(val) ? val.map(toPascalKeys) : val;
        }
        const src = val as Record<string, unknown>;
        const out: Record<string, unknown> = {};
        for (const k of Object.keys(src)) {
            const pascalKey = k.length > 0 ? k.charAt(0).toUpperCase() + k.slice(1) : k;
            out[pascalKey] = toPascalKeys(src[k]);
        }
        return out;
    }

    function toCamelKeys(val: unknown): unknown {
        if (!val || typeof val !== 'object' || val instanceof Uint8Array) {
            return val;
        }
        if (Array.isArray(val)) {
            return val.map(toCamelKeys);
        }
        const src = val as Record<string, unknown>;
        const out: Record<string, unknown> = {};
        for (const k of Object.keys(src)) {
            const camelKey = k.length > 0 ? k.charAt(0).toLowerCase() + k.slice(1) : k;
            out[camelKey] = toCamelKeys(src[k]);
        }
        return out;
    }

    interface PendingRpcRequest {
        resolve: (res: { ok: boolean; data: unknown }) => void;
        timer: ReturnType<typeof setTimeout>;
    }

    let rpcSocket: WebSocket | null = null;
    let rpcNextId = 1;
    const rpcPending = new Map<number, PendingRpcRequest>();
    let rpcOpenWaiters: Array<(ws: WebSocket | null) => void> = [];
    const rpcStatusListeners: Array<(status: WorkspaceStatusPayloadDto) => void> = [];
    const rpcConnectionListeners: Array<(connected: boolean) => void> = [];

    export function onRpcStatus(listener: (status: WorkspaceStatusPayloadDto) => void): void {
        rpcStatusListeners.push(listener);
    }

    export function onRpcConnectionChange(listener: (connected: boolean) => void): void {
        rpcConnectionListeners.push(listener);
    }

    function notifyConnectionChange(connected: boolean): void {
        for (const fn of rpcConnectionListeners) {
            fn(connected);
        }
    }

    export function ensureRpcSocket(): WebSocket {
        if (rpcSocket && (rpcSocket.readyState === WebSocket.OPEN || rpcSocket.readyState === WebSocket.CONNECTING)) {
            return rpcSocket;
        }
        const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const ws = new WebSocket(`${proto}//${location.host}/ws`);
        ws.binaryType = 'arraybuffer';
        rpcSocket = ws;

        ws.addEventListener('open', () => {
            notifyConnectionChange(true);
            const waiters = rpcOpenWaiters;
            rpcOpenWaiters = [];
            for (const w of waiters) {
                w(ws);
            }
        });

        ws.addEventListener('message', (ev: MessageEvent) => {
            try {
                const decoded = toCamelKeys(
                    ev.data instanceof ArrayBuffer || ev.data instanceof Uint8Array
                        ? MessagePack.decode(ev.data)
                        : JSON.parse(String(ev.data))
                );
                if (!decoded || typeof decoded !== 'object') {
                    return;
                }
                const msg = decoded as Record<string, unknown>;
                if (msg.type === 'rpc' && typeof msg.id === 'number') {
                    const pending = rpcPending.get(msg.id);
                    if (pending) {
                        rpcPending.delete(msg.id);
                        clearTimeout(pending.timer);
                        pending.resolve({ ok: Boolean(msg.ok), data: msg.data ?? {} });
                    }
                    return;
                }
                if (msg.type === 'status') {
                    const statusPayload = msg as unknown as WorkspaceStatusPayloadDto;
                    for (const listener of rpcStatusListeners) {
                        listener(statusPayload);
                    }
                }
            } catch (err) {
                console.error('WebSocket message decode error:', err);
            }
        });

        ws.addEventListener('close', () => {
            notifyConnectionChange(false);
            if (rpcSocket === ws) {
                rpcSocket = null;
            }
            const waiters = rpcOpenWaiters;
            rpcOpenWaiters = [];
            for (const w of waiters) {
                w(null);
            }
            for (const [id, pending] of rpcPending.entries()) {
                rpcPending.delete(id);
                clearTimeout(pending.timer);
                pending.resolve({ ok: false, data: { ok: false, error: 'WebSocket closed' } });
            }
            setTimeout(() => {
                ensureRpcSocket();
            }, 1000);
        });

        return ws;
    }

    function waitForOpenRpcSocket(timeoutMs = 2000): Promise<WebSocket | null> {
        const ws = ensureRpcSocket();
        if (ws.readyState === WebSocket.OPEN) {
            return Promise.resolve(ws);
        }
        return new Promise<WebSocket | null>((resolve) => {
            let settled = false;
            const timer = setTimeout(() => {
                if (!settled) {
                    settled = true;
                    resolve(ws.readyState === WebSocket.OPEN ? ws : null);
                }
            }, timeoutMs);
            rpcOpenWaiters.push((openedWs) => {
                if (!settled) {
                    settled = true;
                    clearTimeout(timer);
                    resolve(openedWs);
                }
            });
        });
    }

    export async function rpcCall<T = RpcOperationResponse>(
        method: string,
        params?: Record<string, unknown> | unknown
    ): Promise<{ ok: boolean; data: T }> {
        const ws = await waitForOpenRpcSocket();
        if (!ws || ws.readyState !== WebSocket.OPEN) {
            return { ok: false, data: ({ ok: false, error: 'WebSocket not connected' } as unknown) as T };
        }
        const id = rpcNextId++;
        const frame = MessagePack.encode([id, method, toPascalKeys(params ?? {})], { ignoreUndefined: true });
        return new Promise<{ ok: boolean; data: T }>((resolve) => {
            const timer = setTimeout(() => {
                rpcPending.delete(id);
                resolve({ ok: false, data: ({ ok: false, error: 'RPC timeout' } as unknown) as T });
            }, 25000);
            rpcPending.set(id, {
                resolve: (res) => resolve({ ok: res.ok, data: res.data as T }),
                timer,
            });
            ws.send(frame as BufferSource);
        });
    }
}
