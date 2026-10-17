using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Structural node edits (values, add/rename/delete/restore, duplicate, copy/paste) and undo/redo history.
/// Every edit replies with the path the UI should select next.
/// </summary>
internal static class NodeEditRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map<SetPropertyValueRequest>(RpcMethod.NodeSetValue, SetValue);
        router.Map<AddPropertyRequest>(RpcMethod.NodeAddProperty, AddProperty);
        router.Map<AddImageRequest>(RpcMethod.NodeAddImage, AddImage);
        router.Map<AddDirectoryRequest>(RpcMethod.NodeAddDirectory, AddDirectory);
        router.Map<RenameNodeRequest>(RpcMethod.NodeRename, Rename);
        router.Map<NodeRefRequest>(RpcMethod.NodeDelete, Delete);
        router.Map<NodeRefRequest>(RpcMethod.NodeRestore, Restore);
        router.Map<NodeRefRequest>(RpcMethod.NodeDuplicate, Duplicate);
        router.Map<NodeRefRequest>(RpcMethod.NodeCopy, Copy);
        router.Map<PasteNodeRequest>(RpcMethod.NodePaste, Paste);
        router.Map<NodeRefRequest>(RpcMethod.NodeUndo, Undo);
        router.Map<NodeRefRequest>(RpcMethod.NodeRedo, Redo);
    }

    private static RpcOperationResponse SetValue(RpcContext ctx, SetPropertyValueRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        string? error = ctx.Workspace.SetPropertyValue(archiveId, path, req.Kind, req.RawValue, req.VecX, req.VecY);
        return ctx.Result(error, archiveId, path);
    }

    private static RpcOperationResponse AddProperty(RpcContext ctx, AddPropertyRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string createdPath) = ctx.Workspace.AddProperty(
            archiveId,
            ctx.ResolvePath(req.ParentPath),
            req.Name ?? string.Empty,
            req.Kind,
            req.RawValue,
            req.VecX,
            req.VecY,
            req.BinaryPayload,
            req.CanvasFormat);
        return ctx.Result(error, archiveId, createdPath);
    }

    private static RpcOperationResponse AddImage(RpcContext ctx, AddImageRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string createdPath) = ctx.Workspace.AddImage(
            archiveId,
            ctx.ResolvePath(req.ParentPath),
            req.ImageName ?? string.Empty,
            req.RawImgBytes,
            req.JsonContent);
        return ctx.Result(error, archiveId, createdPath);
    }

    private static RpcOperationResponse AddDirectory(RpcContext ctx, AddDirectoryRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string createdPath) = ctx.Workspace.AddDirectory(archiveId, ctx.ResolvePath(req.ParentPath), req.DirName ?? string.Empty);
        return ctx.Result(error, archiveId, createdPath);
    }

    private static RpcOperationResponse Rename(RpcContext ctx, RenameNodeRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string newPath) = ctx.Workspace.RenameNode(archiveId, ctx.ResolvePath(req.Path), req.NewName ?? string.Empty);
        return ctx.Result(error, archiveId, newPath);
    }

    private static RpcOperationResponse Delete(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string parentPath) = ctx.Workspace.DeleteNode(archiveId, ctx.ResolvePath(req.Path));
        return ctx.Result(error, archiveId, parentPath);
    }

    private static RpcOperationResponse Restore(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string restoredPath) = ctx.Workspace.RestoreNode(archiveId, ctx.ResolvePath(req.Path));
        return ctx.Result(error, archiveId, restoredPath);
    }

    private static RpcOperationResponse Duplicate(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string newPath) = ctx.Workspace.DuplicateNode(archiveId, ctx.ResolvePath(req.Path));
        return ctx.Result(error, archiveId, newPath);
    }

    private static RpcOperationResponse Copy(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (string? error, ClipboardStateDto clipboard) = ctx.Workspace.CopyNode(archiveId, path);
        return ctx.Result(error, archiveId, path) with { Clipboard = clipboard };
    }

    private static RpcOperationResponse Paste(RpcContext ctx, PasteNodeRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string pastedPath) = ctx.Workspace.PasteNode(archiveId, ctx.ResolvePath(req.TargetPath), req.OverrideName);
        return ctx.Result(error, archiveId, pastedPath);
    }

    /// <summary>Undo is archive-wide; the reply's path is the node the undone change touched.</summary>
    private static RpcOperationResponse Undo(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string affectedPath) = ctx.Workspace.Undo(archiveId);
        return ctx.Result(error, archiveId, error is null ? affectedPath : ctx.ResolvePath(req.Path));
    }

    /// <summary>Redo is archive-wide; the reply's path is the node the redone change touched.</summary>
    private static RpcOperationResponse Redo(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (string? error, string affectedPath) = ctx.Workspace.Redo(archiveId);
        return ctx.Result(error, archiveId, error is null ? affectedPath : ctx.ResolvePath(req.Path));
    }
}
