using System.Diagnostics;
using Maple.Classic.Repacker.UI.Rpc.Handlers;

namespace Maple.Classic.Repacker.UI.Rpc;

internal delegate (bool Ok, object? Data) RpcHandler(RpcContext ctx, ReadOnlyMemory<byte> rawParams);

/// <summary>
/// Routes incoming WebSocket RPC frames to the handler registered for their <see cref="RpcMethod"/>.
/// Handlers are grouped by feature area in <c>Rpc/Handlers/*RpcHandlers.cs</c>.
/// </summary>
internal sealed class RpcRouter {
    private readonly Dictionary<RpcMethod, RpcHandler> handlers = [];
    private readonly RpcContext context;

    public RpcRouter(RpcContext context) {
        this.context = context;
        WorkspaceRpcHandlers.Register(this);
        NativeDialogRpcHandlers.Register(this);
        NodeQueryRpcHandlers.Register(this);
        NodeEditRpcHandlers.Register(this);
        NodeAssetRpcHandlers.Register(this);
        SearchExportRpcHandlers.Register(this);
        SessionRpcHandlers.Register(this);

        Debug.Assert(
            handlers.Count == Enum.GetValues<RpcMethod>().Length,
            $"Unregistered RPC methods: {string.Join(", ", Enum.GetValues<RpcMethod>().Where(m => !handlers.ContainsKey(m)))}");
    }

    public void Map(RpcMethod method, RpcHandler handler) {
        if (!handlers.TryAdd(method, handler)) {
            throw new InvalidOperationException($"RPC method {method} is registered more than once.");
        }
    }

    public (bool Ok, object? Data) Dispatch(string wireMethod, ReadOnlyMemory<byte> rawParams) {
        if (!RpcMethodWireNames.TryParse(wireMethod, out RpcMethod method) ||
            !handlers.TryGetValue(method, out RpcHandler? handler)) {
            return (false, new RpcOperationResponse(false, $"Unknown RPC method: {wireMethod}"));
        }

        try {
            return handler(context, rawParams);
        } catch (Exception ex) {
            return (false, new RpcOperationResponse(false, ex.Message));
        }
    }
}
