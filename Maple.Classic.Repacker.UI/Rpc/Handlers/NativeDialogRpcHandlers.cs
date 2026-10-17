using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Native Windows file/folder pickers. On non-Windows builds these return a "not supported" error.
/// </summary>
internal static class NativeDialogRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map(RpcMethod.WorkspaceBrowseFiles, WorkspaceBrowseFiles);
        router.Map(RpcMethod.WorkspaceBrowseSaveFile, WorkspaceBrowseSaveFile);
        router.Map(RpcMethod.WorkspaceBrowseDirectory, WorkspaceBrowseDirectory);
        router.Map(RpcMethod.WorkspaceBrowseAndOpenDirectory, WorkspaceBrowseAndOpenDirectory);
    }

    private static (bool Ok, object? Data) WorkspaceBrowseFiles(RpcContext ctx, ReadOnlyMemory<byte> _) {
#if WINDOWS
        string? initialDir = ctx.Settings.GetRecentPaths().FirstOrDefault();
        string[]? filePaths = WindowsDesktopShell.PickOpenWzFiles(initialDir);
        if (filePaths is not { Length: > 0 }) {
            return (true, new RpcOperationResponse(Ok: true, Cancelled: true));
        }
        return (true, new RpcOperationResponse(
            Ok: true,
            FilePaths: filePaths));
#else
        return (false, new RpcOperationResponse(false, "Native file dialog is not supported on this platform."));
#endif
    }

    private static (bool Ok, object? Data) WorkspaceBrowseSaveFile(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
#if WINDOWS
        BrowseSaveFileRequest req = RpcSerializer.DecodeParams<BrowseSaveFileRequest>(rawParams);
        string? initialDir = !string.IsNullOrWhiteSpace(req.InitialDirectory)
            ? req.InitialDirectory
            : ctx.Settings.GetRecentPaths().FirstOrDefault();
        string? savedPath = WindowsDesktopShell.PickSaveWzFile(req.DefaultFileName, initialDir);
        if (string.IsNullOrWhiteSpace(savedPath)) {
            return (true, new RpcOperationResponse(Ok: true, Cancelled: true));
        }
        return (true, new RpcOperationResponse(
            Ok: true,
            Path: savedPath));
#else
        return (false, new RpcOperationResponse(false, "Native save dialog is not supported on this platform."));
#endif
    }

    private static (bool Ok, object? Data) WorkspaceBrowseDirectory(RpcContext ctx, ReadOnlyMemory<byte> _) {
#if WINDOWS
        string? initialDir = ctx.Settings.GetRecentPaths().FirstOrDefault();
        string? directoryPath = WindowsDesktopShell.PickOpenDirectory(initialDir);
        if (string.IsNullOrWhiteSpace(directoryPath)) {
            return (true, new RpcOperationResponse(Ok: true, Cancelled: true));
        }
        return (true, new RpcOperationResponse(
            Ok: true,
            Path: directoryPath));
#else
        return (false, new RpcOperationResponse(false, "Native folder dialog is not supported on this platform."));
#endif
    }

    private static (bool Ok, object? Data) WorkspaceBrowseAndOpenDirectory(RpcContext ctx, ReadOnlyMemory<byte> _) {
#if WINDOWS
        string? initialDir = ctx.Settings.GetRecentPaths().FirstOrDefault();
        string? directoryPath = WindowsDesktopShell.PickOpenDirectory(initialDir);
        if (string.IsNullOrWhiteSpace(directoryPath)) {
            return (true, new RpcOperationResponse(Ok: true, Cancelled: true));
        }
        ArchiveSummaryDto[] opened = ctx.Workspace.OpenDataDirectory(directoryPath);
        if (opened.Length > 0) {
            ctx.Settings.RecordRecentPath(Path.GetFullPath(directoryPath));
        }
        return (opened.Length > 0, new RpcOperationResponse(
            Ok: opened.Length > 0,
            Error: opened.Length == 0 ? "No readable .wz files found in the selected directory." : null,
            ArchiveId: ctx.Workspace.SelectedArchiveId,
            Path: ctx.Workspace.SelectedNodePath,
            Archives: opened,
            Status: ctx.BuildStatus()));
#else
        return (false, new RpcOperationResponse(false, "Native folder dialog is not supported on this platform."));
#endif
    }
}
