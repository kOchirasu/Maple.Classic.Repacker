using Maple.Classic.Repacker.Engine.Models;
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

    /// <summary>Snapshot of workspace + settings state pushed to clients after state changes.</summary>
    public WorkspaceStatusPayloadDto BuildStatus() =>
        new(
            "status",
            Workspace.StateVersion,
            Workspace.SelectedArchiveId,
            Workspace.SelectedNodePath,
            Workspace.GetArchiveSummaries(),
            Workspace.GetClipboardState(),
            Workspace.GetLogs().ToArray(),
            Settings.GetUiState(),
            Settings.GetRecentPaths(),
            SupportsNativeDialogs);

    /// <summary>Falls back to the currently selected archive when the request omits an archive id.</summary>
    public string ResolveArchiveId(string? requested) =>
        requested ?? Workspace.SelectedArchiveId ?? string.Empty;

    /// <summary>Falls back to the currently selected node path when the request omits a path.</summary>
    public string ResolvePath(string? requested) =>
        requested ?? Workspace.SelectedNodePath;
}
