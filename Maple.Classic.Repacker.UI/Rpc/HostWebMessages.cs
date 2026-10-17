using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maple.Classic.Repacker.UI.Rpc;

/// <summary>
/// Message types exchanged between the WebView2 front end and the native desktop shell.
/// Mirrors <c>HostWebMessageType</c> in <c>wwwroot/rpc.ts</c>; wire values are the camelCase member names.
/// </summary>
public enum HostWebMessageType {
    OpenTreeContextMenu,
    CloseTreeContextMenu,
    TreeContextMenuCommand,
    RequestAppClose,
    ConfirmAppClose,
}

/// <summary>
/// Commands raised by the native tree context menu.
/// Mirrors <c>TreeContextMenuCommand</c> in <c>wwwroot/rpc.ts</c>; wire values are the camelCase member names.
/// </summary>
public enum TreeContextMenuCommand {
    Inspect,
    AddProp,
    AddImg,
    AddDir,
    Copy,
    Paste,
    Duplicate,
    Rename,
    ExportAuto,
    Restore,
    Delete,
    CloseArchive,
}

internal static class HostWebMessageJson {
    /// <summary>Serializes enums as camelCase strings so they match the TypeScript enum values.</summary>
    public static readonly JsonSerializerOptions Options = new() {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
