using System.Diagnostics;
using Maple.Classic.Repacker.Engine.Models;
using Maple.Classic.Repacker.UI.Rpc.Handlers;

namespace Maple.Classic.Repacker.UI.Rpc;

internal delegate IResultDto RpcHandler(RpcContext ctx, ReadOnlyMemory<byte> rawParams);

/// <summary>
/// Routes incoming WebSocket RPC frames to the handler registered for their <see cref="RpcMethod"/>.
/// Handlers are grouped by feature area in <c>Rpc/Handlers/*RpcHandlers.cs</c>; each receives its decoded request and
/// returns a response DTO whose <see cref="IResultDto.Ok"/> becomes the reply's success flag.
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

    /// <summary>Registers a handler whose request parameters are decoded as <typeparamref name="TRequest"/>.</summary>
    public void Map<TRequest>(RpcMethod method, Func<RpcContext, TRequest, IResultDto> handler) where TRequest : new() =>
        Register(method, (ctx, raw) => handler(ctx, RpcSerializer.DecodeParams<TRequest>(raw)));

    /// <summary>Registers a handler that takes no request parameters.</summary>
    public void Map(RpcMethod method, Func<RpcContext, IResultDto> handler) =>
        Register(method, (ctx, _) => handler(ctx));

    public (bool Ok, object? Data) Dispatch(string wireMethod, ReadOnlyMemory<byte> rawParams) {
        if (!RpcMethodWireNames.TryParse(wireMethod, out RpcMethod method) ||
            !handlers.TryGetValue(method, out RpcHandler? handler)) {
            return (false, RpcContext.Fail($"Unknown RPC method: {wireMethod}"));
        }

        try {
            IResultDto response = handler(context, rawParams);
            return (response.Ok, response);
        } catch (Exception ex) {
            return (false, RpcContext.Fail(ex.Message));
        }
    }

    private void Register(RpcMethod method, RpcHandler handler) {
        if (!handlers.TryAdd(method, handler)) {
            throw new InvalidOperationException($"RPC method {method} is registered more than once.");
        }
    }
}
