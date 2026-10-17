using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc;

/// <summary>
/// Tracks what one WebSocket client has already been sent, and builds status messages that contain only what changed
/// since then (<c>null</c> sections are unchanged, and <c>Logs</c> holds only new entries). The first message to a new
/// client is therefore a full snapshot.
/// </summary>
/// <remarks>
/// Not thread-safe: each connection owns one tracker and only uses it while holding its send lock, which also keeps
/// status messages ordered relative to RPC replies.
/// </remarks>
internal sealed class StatusDeltaTracker {
    private byte[]? sentArchives;
    private byte[]? sentClipboard;
    private byte[]? sentUiState;
    private byte[]? sentRecentPaths;
    private string? sentSelectedArchiveId;
    private string? sentSelectedNodePath;
    private long sentLogsEpoch = -1;
    private long sentLastLogId;

    /// <summary>The changes since the last call, or <c>null</c> if the client is already up to date.</summary>
    public WorkspaceStatusPayloadDto? NextDelta(RpcContext ctx) {
        ArchiveSummaryDto[]? archives = Changed(ref sentArchives, ctx.Workspace.GetArchiveSummaries());
        ClipboardStateDto? clipboard = Changed(ref sentClipboard, ctx.Workspace.GetClipboardState());
        SavedRepackerUiStateDto? uiState = Changed(ref sentUiState, ctx.Settings.GetUiState());
        string[]? recentPaths = Changed(ref sentRecentPaths, ctx.Settings.GetRecentPaths());

        // Log ids keep increasing across clears, so "entries after the last id sent" is right even after a clear;
        // a changed epoch only tells the client to drop the entries it already has first.
        (long epoch, RepackerLogEntry[] newLogs) = ctx.Workspace.GetLogsSince(sentLastLogId);
        bool logsReset = epoch != sentLogsEpoch;

        string? selectedArchiveId = ctx.Workspace.SelectedArchiveId;
        string selectedNodePath = ctx.Workspace.SelectedNodePath;
        bool selectionChanged = selectedArchiveId != sentSelectedArchiveId || selectedNodePath != sentSelectedNodePath;

        if (archives is null && clipboard is null && uiState is null && recentPaths is null &&
            !logsReset && newLogs.Length == 0 && !selectionChanged) {
            return null;
        }

        sentSelectedArchiveId = selectedArchiveId;
        sentSelectedNodePath = selectedNodePath;
        sentLogsEpoch = epoch;
        if (newLogs.Length > 0) {
            sentLastLogId = newLogs[^1].Id;
        }

        return new WorkspaceStatusPayloadDto(
            "status",
            ctx.Workspace.StateVersion,
            selectedArchiveId,
            selectedNodePath,
            archives,
            clipboard,
            newLogs,
            logsReset,
            uiState,
            recentPaths,
            RpcContext.SupportsNativeDialogs);
    }

    /// <summary>Returns <paramref name="current"/> if its serialized form differs from what was last sent, else null.</summary>
    private static T? Changed<T>(ref byte[]? sent, T current) where T : class {
        byte[] encoded = RpcSerializer.Encode(current);
        if (sent is not null && encoded.AsSpan().SequenceEqual(sent)) {
            return null;
        }
        sent = encoded;
        return current;
    }
}
