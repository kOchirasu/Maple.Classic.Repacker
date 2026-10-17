using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.Serialization;

namespace Maple.Classic.Repacker.UI.Rpc;

/// <summary>
/// WebSocket RPC methods. The <see cref="EnumMemberAttribute.Value"/> is the dotted wire name sent by the front end
/// (mirrors <c>RpcMethod</c> in <c>wwwroot/rpc.ts</c>).
/// </summary>
public enum RpcMethod {
    // workspace.*
    [EnumMember(Value = "workspace.browseFiles")]
    WorkspaceBrowseFiles,
    [EnumMember(Value = "workspace.browseSaveFile")]
    WorkspaceBrowseSaveFile,
    [EnumMember(Value = "workspace.browseDirectory")]
    WorkspaceBrowseDirectory,
    [EnumMember(Value = "workspace.openFile")]
    WorkspaceOpenFile,
    [EnumMember(Value = "workspace.openBytes")]
    WorkspaceOpenBytes,
    [EnumMember(Value = "workspace.openDirectory")]
    WorkspaceOpenDirectory,
    [EnumMember(Value = "workspace.createArchive")]
    WorkspaceCreateArchive,
    [EnumMember(Value = "workspace.saveArchive")]
    WorkspaceSaveArchive,
    [EnumMember(Value = "workspace.exportArchive")]
    WorkspaceExportArchive,
    [EnumMember(Value = "workspace.closeArchive")]
    WorkspaceCloseArchive,

    // tree.*
    [EnumMember(Value = "tree.children")]
    TreeChildren,

    // node.*
    [EnumMember(Value = "node.inspect")]
    NodeInspect,
    [EnumMember(Value = "node.setValue")]
    NodeSetValue,
    [EnumMember(Value = "node.addProperty")]
    NodeAddProperty,
    [EnumMember(Value = "node.addImage")]
    NodeAddImage,
    [EnumMember(Value = "node.addDirectory")]
    NodeAddDirectory,
    [EnumMember(Value = "node.rename")]
    NodeRename,
    [EnumMember(Value = "node.delete")]
    NodeDelete,
    [EnumMember(Value = "node.restore")]
    NodeRestore,
    [EnumMember(Value = "node.duplicate")]
    NodeDuplicate,
    [EnumMember(Value = "node.copy")]
    NodeCopy,
    [EnumMember(Value = "node.paste")]
    NodePaste,
    [EnumMember(Value = "node.replaceCanvas")]
    NodeReplaceCanvas,
    [EnumMember(Value = "node.changeCanvasFormat")]
    NodeChangeCanvasFormat,
    [EnumMember(Value = "node.replaceSound")]
    NodeReplaceSound,
    [EnumMember(Value = "node.applyJson")]
    NodeApplyJson,
    [EnumMember(Value = "node.inlineOutlinks")]
    NodeInlineOutlinks,
    [EnumMember(Value = "node.undo")]
    NodeUndo,
    [EnumMember(Value = "node.redo")]
    NodeRedo,
    [EnumMember(Value = "node.batchExport")]
    NodeBatchExport,

    // search.*
    [EnumMember(Value = "search.query")]
    SearchQuery,

    // logs.*
    [EnumMember(Value = "logs.clear")]
    LogsClear,

    // ui.*
    [EnumMember(Value = "ui.saveState")]
    UiSaveState,
}

public static class RpcMethodWireNames {
    private static readonly FrozenDictionary<string, RpcMethod> ByWireName = typeof(RpcMethod)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .ToFrozenDictionary(
            static f => f.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? f.Name,
            static f => (RpcMethod) f.GetValue(null)!,
            StringComparer.Ordinal);

    public static bool TryParse(string? wireName, out RpcMethod method) =>
        ByWireName.TryGetValue(wireName ?? string.Empty, out method);
}
