using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Archive lifecycle: opening files/bytes/directories, creating, saving, exporting and closing archives.
/// </summary>
internal static class WorkspaceRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map<OpenArchiveFileRequest>(RpcMethod.WorkspaceOpenFile, OpenFile);
        router.Map<OpenArchiveBytesRequest>(RpcMethod.WorkspaceOpenBytes, OpenBytes);
        router.Map<OpenDirectoryRequest>(RpcMethod.WorkspaceOpenDirectory, OpenDirectory);
        router.Map<CreateArchiveRequest>(RpcMethod.WorkspaceCreateArchive, CreateArchive);
        router.Map<SaveArchiveRequest>(RpcMethod.WorkspaceSaveArchive, SaveArchive);
        router.Map<SaveArchiveRequest>(RpcMethod.WorkspaceExportArchive, ExportArchive);
        router.Map<CloseArchiveRequest>(RpcMethod.WorkspaceCloseArchive, CloseArchive);
    }

    private static RpcOperationResponse OpenFile(RpcContext ctx, OpenArchiveFileRequest req) {
        if (string.IsNullOrWhiteSpace(req.FilePath)) {
            return RpcContext.Fail("File path is required.");
        }
        ArchiveSummaryDto summary = ctx.Workspace.OpenArchiveFromFile(req.FilePath, req.Version);
        ctx.Settings.RecordRecentPath(summary.FilePath);
        return ctx.Result(null, summary.ArchiveId, path: string.Empty) with { Archive = summary };
    }

    private static RpcOperationResponse OpenBytes(RpcContext ctx, OpenArchiveBytesRequest req) {
        if (req.Data is not { Length: > 0 }) {
            return RpcContext.Fail("Uploaded archive buffer is empty.");
        }
        ArchiveSummaryDto summary = ctx.Workspace.OpenArchiveFromBytes(
            req.FileName ?? "Uploaded.wz",
            req.Data,
            req.FilePath,
            req.Version,
            ctx.Settings.GetRecentPaths());
        if (!string.IsNullOrWhiteSpace(summary.FilePath)) {
            ctx.Settings.RecordRecentPath(summary.FilePath);
        }
        return ctx.Result(null, summary.ArchiveId, path: string.Empty) with { Archive = summary };
    }

    private static RpcOperationResponse OpenDirectory(RpcContext ctx, OpenDirectoryRequest req) {
        if (string.IsNullOrWhiteSpace(req.DirectoryPath)) {
            return RpcContext.Fail("Directory path is required.");
        }
        ArchiveSummaryDto[] opened = ctx.Workspace.OpenDataDirectory(req.DirectoryPath, req.Version);
        if (opened.Length > 0) {
            ctx.Settings.RecordRecentPath(Path.GetFullPath(req.DirectoryPath.Trim()));
        }
        string? error = opened.Length == 0 ? "No readable .wz files found in the specified directory." : null;
        return ctx.Result(error, ctx.Workspace.SelectedArchiveId, ctx.Workspace.SelectedNodePath) with { Archives = opened };
    }

    private static RpcOperationResponse CreateArchive(RpcContext ctx, CreateArchiveRequest req) {
        ArchiveSummaryDto summary = ctx.Workspace.CreateNewArchive(req.Name ?? "Custom_000.wz", req.Version ?? 779);
        return ctx.Result(null, summary.ArchiveId, path: string.Empty) with { Archive = summary };
    }

    private static RpcOperationResponse SaveArchive(RpcContext ctx, SaveArchiveRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, ArchiveSummaryDto? summary) = ctx.Workspace.SaveArchiveToDisk(archiveId, req.TargetFilePath, req.Version);
        if (!string.IsNullOrEmpty(summary?.FilePath)) {
            ctx.Settings.RecordRecentPath(summary.FilePath);
        }
        return ctx.Result(error, summary?.ArchiveId ?? archiveId) with { Archive = summary };
    }

    private static BinaryExportResponseDto ExportArchive(RpcContext ctx, SaveArchiveRequest req) {
        (string? error, string fileName, byte[] bytes) =
            ctx.Workspace.ExportArchiveBytes(ctx.ResolveArchiveId(req.ArchiveId), req.Version, commitClean: false);
        return new BinaryExportResponseDto(error is null, error, fileName, "application/octet-stream", bytes);
    }

    private static RpcOperationResponse CloseArchive(RpcContext ctx, CloseArchiveRequest req) {
        bool closed = ctx.Workspace.CloseArchive(ctx.ResolveArchiveId(req.ArchiveId));
        return ctx.Result(closed ? null : "Archive not found.", ctx.Workspace.SelectedArchiveId, ctx.Workspace.SelectedNodePath);
    }
}
