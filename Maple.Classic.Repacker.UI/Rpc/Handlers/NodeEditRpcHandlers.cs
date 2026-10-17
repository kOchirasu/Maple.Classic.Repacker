using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Structural node edits (values, add/rename/delete/restore, duplicate, copy/paste) and undo/redo history.
/// </summary>
internal static class NodeEditRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map(RpcMethod.NodeSetValue, NodeSetValue);
        router.Map(RpcMethod.NodeAddProperty, NodeAddProperty);
        router.Map(RpcMethod.NodeAddImage, NodeAddImage);
        router.Map(RpcMethod.NodeAddDirectory, NodeAddDirectory);
        router.Map(RpcMethod.NodeRename, NodeRename);
        router.Map(RpcMethod.NodeDelete, NodeDelete);
        router.Map(RpcMethod.NodeRestore, NodeRestore);
        router.Map(RpcMethod.NodeDuplicate, NodeDuplicate);
        router.Map(RpcMethod.NodeCopy, NodeCopy);
        router.Map(RpcMethod.NodePaste, NodePaste);
        router.Map(RpcMethod.NodeUndo, NodeUndo);
        router.Map(RpcMethod.NodeRedo, NodeRedo);
    }

    private static (bool Ok, object? Data) NodeSetValue(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        SetPropertyValueRequest req = RpcSerializer.DecodeParams<SetPropertyValueRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err) = ctx.Workspace.SetPropertyValue(
            archiveId,
            path,
            req.Kind,
            req.RawValue,
            req.VecX,
            req.VecY);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: path,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeAddProperty(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        AddPropertyRequest req = RpcSerializer.DecodeParams<AddPropertyRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string parentPath = ctx.ResolvePath(req.ParentPath);
        (bool ok, string? err, string createdPath) = ctx.Workspace.AddProperty(
            archiveId,
            parentPath,
            req.Name ?? string.Empty,
            req.Kind,
            req.RawValue,
            req.VecX,
            req.VecY,
            req.BinaryPayload,
            req.CanvasFormat);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: createdPath,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeAddImage(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        AddImageRequest req = RpcSerializer.DecodeParams<AddImageRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (bool ok, string? err, string createdPath) = ctx.Workspace.AddImage(
            archiveId,
            ctx.ResolvePath(req.ParentPath),
            req.ImageName ?? string.Empty,
            req.RawImgBytes,
            req.JsonContent);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: createdPath,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeAddDirectory(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        AddDirectoryRequest req = RpcSerializer.DecodeParams<AddDirectoryRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        (bool ok, string? err, string createdPath) = ctx.Workspace.AddDirectory(
            archiveId,
            ctx.ResolvePath(req.ParentPath),
            req.DirName ?? string.Empty);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: createdPath,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeRename(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        RenameNodeRequest req = RpcSerializer.DecodeParams<RenameNodeRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err, string newPath) = ctx.Workspace.RenameNode(archiveId, path, req.NewName ?? string.Empty);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: newPath,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeDelete(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err, string parentPath) = ctx.Workspace.DeleteNode(archiveId, path);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: parentPath,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeRestore(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err, string restoredPath) = ctx.Workspace.RestoreNode(archiveId, path);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: restoredPath,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeDuplicate(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err, string newPath) = ctx.Workspace.DuplicateNode(archiveId, path);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: newPath,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeCopy(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err, ClipboardStateDto clip) = ctx.Workspace.CopyNode(archiveId, path);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: path,
            Clipboard: clip,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodePaste(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        PasteNodeRequest req = RpcSerializer.DecodeParams<PasteNodeRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string targetPath = ctx.ResolvePath(req.TargetPath);
        (bool ok, string? err, string pastedPath) = ctx.Workspace.PasteNode(archiveId, targetPath, req.OverrideName);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: pastedPath,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeUndo(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err) = ctx.Workspace.Undo(archiveId, path, out string affectedPath);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: !string.IsNullOrEmpty(affectedPath) ? affectedPath : path,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeRedo(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err) = ctx.Workspace.Redo(archiveId, path, out string affectedPath);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: !string.IsNullOrEmpty(affectedPath) ? affectedPath : path,
            Status: ctx.BuildStatus()));
    }
}
