namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// UI session housekeeping: log clearing and persisted UI state.
/// </summary>
internal static class SessionRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map(RpcMethod.LogsClear, ClearLogs);
        router.Map<SaveUiStateRequest>(RpcMethod.UiSaveState, SaveUiState);
    }

    private static RpcOperationResponse ClearLogs(RpcContext ctx) {
        ctx.Workspace.ClearLogs();
        return ctx.Result(null);
    }

    private static RpcOperationResponse SaveUiState(RpcContext ctx, SaveUiStateRequest req) {
        if (req.UiState is not null) {
            ctx.Settings.UpdateUiState(req.UiState);
        }
        return new RpcOperationResponse(Ok: true);
    }
}
