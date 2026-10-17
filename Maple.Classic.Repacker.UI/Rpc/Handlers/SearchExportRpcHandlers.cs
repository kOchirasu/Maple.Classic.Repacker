using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// Global search and batch export to disk.
/// </summary>
internal static class SearchExportRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map<SearchNodesRequest>(RpcMethod.SearchQuery, Search);
        router.Map<BatchExportRequest>(RpcMethod.NodeBatchExport, BatchExport);
    }

    private static SearchResponseDto Search(RpcContext ctx, SearchNodesRequest req) {
        SearchResultItemDto[] hits = ctx.Workspace.Search(new SearchQueryDto {
            ArchiveId = req.ArchiveId,
            Query = req.Query,
            ValueQuery = req.ValueQuery,
            PropertyKindFilter = req.PropertyKindFilter,
            CaseSensitive = req.CaseSensitive,
            ExactMatch = req.ExactMatch,
            MaxResults = req.MaxResults > 0 ? req.MaxResults : 250,
        });
        return new SearchResponseDto(true, null, hits);
    }

    private static RpcOperationResponse BatchExport(RpcContext ctx, BatchExportRequest req) {
        string archiveId = ctx.ResolveArchiveId(req.ArchiveId);
        string path = ctx.ResolvePath(req.Path);
        (string? error, int count) = ctx.Workspace.BatchExportToDirectory(archiveId, path, req.OutputDirectory ?? string.Empty, req.Mode);
        return ctx.Result(error, archiveId, path, count);
    }
}
