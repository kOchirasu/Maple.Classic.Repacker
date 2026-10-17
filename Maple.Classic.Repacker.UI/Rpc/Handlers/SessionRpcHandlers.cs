using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc.Handlers;

/// <summary>
/// UI session housekeeping: log clearing and persisted UI state.
/// </summary>
internal static class SessionRpcHandlers {
    public static void Register(RpcRouter router) {
        router.Map(RpcMethod.LogsClear, LogsClear);
        router.Map(RpcMethod.UiSaveState, UiSaveState);
    }

    private static (bool Ok, object? Data) LogsClear(RpcContext ctx, ReadOnlyMemory<byte> _) {
        ctx.Workspace.ClearLogs();
        return (true, new RpcOperationResponse(Ok: true, Status: ctx.BuildStatus()));
    }

    private static (bool Ok, object? Data) UiSaveState(RpcContext ctx, ReadOnlyMemory<byte> rawParams) {
        SaveUiStateRequest req = RpcSerializer.DecodeParams<SaveUiStateRequest>(rawParams);
        if (req.UiState is not null) {
            ctx.Settings.UpdateUiState(req.UiState);
        }
        return (true, new RpcOperationResponse(Ok: true));
    }
}
