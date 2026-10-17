using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Global search and batch export to disk.
/// </summary>
internal static class SearchExportRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map(RpcMethod.SearchQuery, SearchQuery);
        router.Map(RpcMethod.NodeBatchExport, NodeBatchExport);
    }

    private static (bool Ok, object? Data) SearchQuery(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        SearchNodesRequest req = RpcSerializer.DecodeParams<SearchNodesRequest>(rawParams);
        SearchResultItemDto[] hits = ctx.Workspace.Search(new SearchQueryDto {
            ArchiveId = req.ArchiveId,
            Query = req.Query,
            ValueQuery = req.ValueQuery,
            PropertyKindFilter = req.PropertyKindFilter,
            CaseSensitive = req.CaseSensitive,
            ExactMatch = req.ExactMatch,
            MaxResults = req.MaxResults > 0 ? req.MaxResults : 250,
        });
        return (true, new SearchResponseDto(true, null, hits));
    }

    private static (bool Ok, object? Data) NodeBatchExport(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        BatchExportRequest req = RpcSerializer.DecodeParams<BatchExportRequest>(rawParams);
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (bool ok, string? err, int count) = ctx.Workspace.BatchExportToDirectory(
            archiveId,
            path,
            req.OutputDirectory ?? string.Empty,
            req.Mode);
        return (ok, new RpcOperationResponse(
            Ok: ok,
            Error: err,
            ArchiveId: archiveId,
            Path: path,
            Count: count,
            Status: ctx.BuildStatus()));
    }
}
