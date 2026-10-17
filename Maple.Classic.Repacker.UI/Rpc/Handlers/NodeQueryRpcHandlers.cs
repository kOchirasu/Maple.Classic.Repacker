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
        return new TreeChildrenResponse(true, null, archiveId, parentPath, ctx.Workspace.GetTreeChildren(archiveId, parentPath));
    }

    private static NodeInspectionDto Inspect(RpcContext ctx, NodeRefRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = req.Path ?? string.Empty;
        ctx.Workspace.SelectNode(archiveId, path);
        return ctx.Workspace.InspectNode(archiveId, path);
    }
}
