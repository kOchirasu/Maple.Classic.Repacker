using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Archive lifecycle: opening files/bytes/directories, creating, saving, exporting and closing archives.
/// </summary>
internal static class WorkspaceRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map(RpcMethod.WorkspaceOpenFile, WorkspaceOpenFile);
        router.Map(RpcMethod.WorkspaceOpenBytes, WorkspaceOpenBytes);
        router.Map(RpcMethod.WorkspaceOpenDirectory, WorkspaceOpenDirectory);
        router.Map(RpcMethod.WorkspaceCreateArchive, WorkspaceCreateArchive);
        router.Map(RpcMethod.WorkspaceSaveArchive, WorkspaceSaveArchive);
        router.Map(RpcMethod.WorkspaceExportArchive, WorkspaceExportArchive);
        router.Map(RpcMethod.WorkspaceCloseArchive, WorkspaceCloseArchive);
    }

    private static (bool Ok, object? Data) WorkspaceOpenFile(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        OpenArchiveFileRequest req = RpcSerializer.DecodeParams<OpenArchiveFileRequest>(rawParams);
        if (string.IsNullOrWhiteSpace(req.FilePath)) {
            return (false, new RpcOperationResponse(false, "File path is required."));
        }
        ArchiveSummaryDto summary = ctx.Workspace.OpenArchiveFromFile(req.FilePath, req.Version);
        ctx.Settings.RecordRecentPath(summary.FilePath);
        return (true, new RpcOperationResponse(
            Ok: true,
            ArchiveId: summary.ArchiveId,
            Path: "",
            Archive: summary,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) WorkspaceOpenBytes(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        OpenArchiveBytesRequest req = RpcSerializer.DecodeParams<OpenArchiveBytesRequest>(rawParams);
        if (req.Data is not { Length: > 0 }) {
            return (false, new RpcOperationResponse(false, "Uploaded archive buffer is empty."));
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
        return (true, new RpcOperationResponse(
            Ok: true,
            ArchiveId: summary.ArchiveId,
            Path: "",
            Archive: summary,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) WorkspaceOpenDirectory(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        OpenDirectoryRequest req = RpcSerializer.DecodeParams<OpenDirectoryRequest>(rawParams);
        if (string.IsNullOrWhiteSpace(req.DirectoryPath)) {
            return (false, new RpcOperationResponse(false, "Directory path is required."));
        }
        ArchiveSummaryDto[] opened = ctx.Workspace.OpenDataDirectory(req.DirectoryPath, req.Version);
        if (opened.Length > 0) {
            ctx.Settings.RecordRecentPath(Path.GetFullPath(req.DirectoryPath.Trim()));
        }
        return (true, new RpcOperationResponse(
            Ok: opened.Length > 0,
            Error: opened.Length == 0 ? "No readable .wz files found in the specified directory." : null,
            ArchiveId: ctx.Workspace.SelectedArchiveId,
            Path: ctx.Workspace.SelectedNodePath,
            Archives: opened,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) WorkspaceCreateArchive(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        CreateArchiveRequest req = RpcSerializer.DecodeParams<CreateArchiveRequest>(rawParams);
        ArchiveSummaryDto summary = ctx.Workspace.CreateNewArchive(
            req.Name ?? "Custom_000.wz",
            req.Version ?? 779);
        return (true, new RpcOperationResponse(
            Ok: true,
            ArchiveId: summary.ArchiveId,
            Path: "",
            Archive: summary,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) WorkspaceSaveArchive(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        SaveArchiveRequest req = RpcSerializer.DecodeParams<SaveArchiveRequest>(rawParams);
        string targetArchiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (bool ok, string? err, ArchiveSummaryDto? summary) =
            ctx.Workspace.SaveArchiveToDisk(targetArchiveId, req.TargetFilePath, req.Version);
        if (ok && summary is not null && !string.IsNullOrEmpty(summary.FilePath)) {
            ctx.Settings.RecordRecentPath(summary.FilePath);
        }
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: summary?.ArchiveId ?? targetArchiveId,
            Archive: summary,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) WorkspaceExportArchive(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        SaveArchiveRequest req = RpcSerializer.DecodeParams<SaveArchiveRequest>(rawParams);
        string targetArchiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (bool ok, string? err, string fileName, byte[] bytes) =
            ctx.Workspace.ExportArchiveBytes(targetArchiveId, req.Version, commitClean: false);
        return (ok, new BinaryExportResponseDto(ok, err, fileName, "application/octet-stream", bytes));
    }

    private static (bool Ok, object? Data) WorkspaceCloseArchive(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        CloseArchiveRequest req = RpcSerializer.DecodeParams<CloseArchiveRequest>(rawParams);
        string targetArchiveId = ctx.ResolveArchiveId(req.ArchiveId);
        bool closed = ctx.Workspace.CloseArchive(targetArchiveId);
        return (closed, new RpcOperationResponse(
            Ok: closed,
            Error: closed ? null : "Archive not found.",
            ArchiveId: ctx.Workspace.SelectedArchiveId,
            Path: ctx.Workspace.SelectedNodePath,
            Status: ctx.BuildStatus()));
    }
}
