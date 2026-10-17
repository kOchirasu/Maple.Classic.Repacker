namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Native Windows file/folder pickers used by the WebView2 shell. The Electron shell shows its own dialogs instead,
/// so on non-Windows builds these return a "not supported" error.
/// </summary>
internal static class NativeDialogRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map(RpcMethod.WorkspaceBrowseFiles, BrowseFiles);
        router.Map<BrowseSaveFileRequest>(RpcMethod.WorkspaceBrowseSaveFile, BrowseSaveFile);
        router.Map(RpcMethod.WorkspaceBrowseDirectory, BrowseDirectory);
    }

    private static readonly RpcOperationResponse Cancelled = new(Ok: true, Cancelled: true);

    private static RpcOperationResponse BrowseFiles(RpcContext ctx) {
#if WINDOWS
        string[]? filePaths = WindowsDesktopShell.PickOpenWzFiles(ctx.Settings.GetRecentPaths().FirstOrDefault());
        return filePaths is { Length: > 0 } ? new RpcOperationResponse(Ok: true, FilePaths: filePaths) : Cancelled;
#else
        return RpcContext.Fail("Native file dialog is not supported on this platform.");
#endif
    }

    private static RpcOperationResponse BrowseSaveFile(RpcContext ctx, BrowseSaveFileRequest req) {
#if WINDOWS
        string? savedPath = WindowsDesktopShell.PickSaveWzFile(req.DefaultFileName, ctx.Settings.GetRecentPaths().FirstOrDefault());
        return string.IsNullOrWhiteSpace(savedPath) ? Cancelled : new RpcOperationResponse(Ok: true, Path: savedPath);
#else
        return RpcContext.Fail("Native save dialog is not supported on this platform.");
#endif
    }

    private static RpcOperationResponse BrowseDirectory(RpcContext ctx) {
#if WINDOWS
        string? directoryPath = WindowsDesktopShell.PickOpenDirectory(ctx.Settings.GetRecentPaths().FirstOrDefault());
        return string.IsNullOrWhiteSpace(directoryPath) ? Cancelled : new RpcOperationResponse(Ok: true, Path: directoryPath);
#else
        return RpcContext.Fail("Native folder dialog is not supported on this platform.");
#endif
    }
}
