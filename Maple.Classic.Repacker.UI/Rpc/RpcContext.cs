using Maple.Classic.Repacker.Engine.Workspace;

namespace Maple.Classic.Repacker.UI.Rpc;

/// <summary>
/// Shared services and helpers available to every RPC handler.
/// </summary>
internal sealed class RpcContext(RepackerWorkspace workspace, UserSettingsStore settings) {
    public RepackerWorkspace Workspace { get; } = workspace;
    public UserSettingsStore Settings { get; } = settings;

    /// <summary>Whether this build can show native Windows file/folder dialogs.</summary>
    public static bool SupportsNativeDialogs =>
#if WINDOWS
        true;
#else
        false;
#endif

    /// <summary>Falls back to the currently selected archive when the request omits an archive id.</summary>
    public string ResolveArchiveId(string? requested) =>
        requested ?? Workspace.SelectedArchiveId ?? string.Empty;

    /// <summary>Falls back to the currently selected node path when the request omits a path.</summary>
    public string ResolvePath(string? requested) =>
        requested ?? Workspace.SelectedNodePath;

    /// <summary>
    /// Standard reply for a workspace operation: success when <paramref name="error"/> is null. State changes are not
    /// included; the connection pushes a status delta to the client right before every reply.
    /// </summary>
    public RpcOperationResponse Result(string? error, string? archiveId = null, string? path = null, int? count = null) =>
        new(Ok: error is null, Error: error, ArchiveId: archiveId, Path: path, Count: count);

    public static RpcOperationResponse Fail(string error) => new(Ok: false, Error: error);
}
