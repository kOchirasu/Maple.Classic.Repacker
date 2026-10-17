namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Asset payload edits: Canvas PNG/format replacement, Sound replacement, JSON subtree application and link inlining.
/// </summary>
internal static class NodeAssetRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map<ReplaceCanvasRequest>(RpcMethod.NodeReplaceCanvas, ReplaceCanvas);
        router.Map<ChangeCanvasFormatRequest>(RpcMethod.NodeChangeCanvasFormat, ChangeCanvasFormat);
        router.Map<ReplaceSoundRequest>(RpcMethod.NodeReplaceSound, ReplaceSound);
        router.Map<ApplyJsonRequest>(RpcMethod.NodeApplyJson, ApplyJson);
        router.Map<NodeRefRequest>(RpcMethod.NodeInlineOutlinks, InlineOutlinks);
    }

    private static RpcOperationResponse ReplaceCanvas(RpcContext ctx, ReplaceCanvasRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        string? error = ctx.Workspace.ReplaceCanvasPng(archiveId, path, req.PngBytes ?? [], req.Format);
        return ctx.Result(error, archiveId, path);
    }

    private static RpcOperationResponse ChangeCanvasFormat(RpcContext ctx, ChangeCanvasFormatRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        return ctx.Result(ctx.Workspace.ChangeCanvasFormat(archiveId, path, req.Format), archiveId, path);
    }

    private static RpcOperationResponse ReplaceSound(RpcContext ctx, ReplaceSoundRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        return ctx.Result(ctx.Workspace.ReplaceSound(archiveId, path, req.AudioBytes ?? []), archiveId, path);
    }

    private static RpcOperationResponse ApplyJson(RpcContext ctx, ApplyJsonRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        return ctx.Result(ctx.Workspace.ApplyJsonSubtree(archiveId, path, req.JsonText ?? string.Empty), archiveId, path);
    }

    private static RpcOperationResponse InlineOutlinks(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (string? error, int count) = ctx.Workspace.InlineOutlinks(archiveId, path);
        return ctx.Result(error, archiveId, path, count);
    }
}
