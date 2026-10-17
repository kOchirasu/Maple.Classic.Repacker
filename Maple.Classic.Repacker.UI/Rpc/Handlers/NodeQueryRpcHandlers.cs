using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Read-only tree browsing and node inspection.
/// </summary>
internal static class NodeQueryRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map(RpcMethod.TreeChildren, TreeChildren);
        router.Map(RpcMethod.NodeInspect, NodeInspect);
    }

    private static (bool Ok, object? Data) TreeChildren(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string parentPath = req.Path ?? string.Empty;
        TreeNodeDto[] children = ctx.Workspace.GetTreeChildren(archiveId, parentPath);
        return (true, new TreeChildrenResponse(true, null, archiveId, parentPath, children));
    }

    private static (bool Ok, object? Data) NodeInspect(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = req.Path ?? string.Empty;
        ctx.Workspace.SelectNode(archiveId, path);
        NodeInspectionDto dto = ctx.Workspace.InspectNode(archiveId, path);
        return (dto.Ok, dto);
    }
}
