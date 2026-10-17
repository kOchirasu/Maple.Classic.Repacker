using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Asset payload edits: Canvas PNG/format replacement, Sound replacement, JSON subtree application and link inlining.
/// </summary>
internal static class NodeAssetRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map(RpcMethod.NodeReplaceCanvas, NodeReplaceCanvas);
        router.Map(RpcMethod.NodeChangeCanvasFormat, NodeChangeCanvasFormat);
        router.Map(RpcMethod.NodeReplaceSound, NodeReplaceSound);
        router.Map(RpcMethod.NodeApplyJson, NodeApplyJson);
        router.Map(RpcMethod.NodeInlineOutlinks, NodeInlineOutlinks);
    }

    private static (bool Ok, object? Data) NodeReplaceCanvas(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        ReplaceCanvasRequest req = RpcSerializer.DecodeParams<ReplaceCanvasRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err) = ctx.Workspace.ReplaceCanvasPng(
            archiveId,
            path,
            req.PngBytes ?? [],
            req.Format,
            req.OriginX,
            req.OriginY,
            req.DelayMs);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: path,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeChangeCanvasFormat(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        ChangeCanvasFormatRequest req = RpcSerializer.DecodeParams<ChangeCanvasFormatRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err) = ctx.Workspace.ChangeCanvasFormat(archiveId, path, req.Format);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: path,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeReplaceSound(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        ReplaceSoundRequest req = RpcSerializer.DecodeParams<ReplaceSoundRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err) = ctx.Workspace.ReplaceSound(
            archiveId,
            path,
            req.AudioBytes ?? [],
            req.DurationMs);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: path,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeApplyJson(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        ApplyJsonRequest req = RpcSerializer.DecodeParams<ApplyJsonRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err) = ctx.Workspace.ApplyJsonSubtree(archiveId, path, req.JsonText ?? string.Empty);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: path,
            Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) NodeInlineOutlinks(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err, int count) = ctx.Workspace.InlineOutlinks(archiveId, path);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: path,
            Count: count,
            Status: ctx.BuildStatus()));
    }
}
