using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Read-only tree browsing and node inspection.
/// </summary>
internal static class NodeQueryRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map<NodeRefRequest>(RpcMethod.TreeChildren, TreeChildren);
        router.Map<NodeRefRequest>(RpcMethod.NodeInspect, Inspect);
    }

    private static TreeChildrenResponse TreeChildren(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string parentPath = req.Path ?? string.Empty;
        return new TreeChildrenResponse(true, ctx.Workspace.GetTreeChildren(archiveId, parentPath));
    }

    private static IResultDto Inspect(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = req.Path ?? string.Empty;
        ctx.Workspace.SelectNode(archiveId, path);
        (string? error, NodeInspectionDto? inspection) = ctx.Workspace.InspectNode(archiveId, path);
        return inspection ?? (IResultDto) RpcContext.Fail(error ?? "Inspection failed.");
    }
}
