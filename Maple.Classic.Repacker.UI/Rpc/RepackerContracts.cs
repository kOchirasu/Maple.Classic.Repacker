using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Canvas;
using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI.Rpc;

/// <summary>
/// Workspace status pushed to a client over the WebSocket. Built by <see cref="StatusDeltaTracker"/> as a delta: null
/// sections are unchanged since the client's previous status, <see cref="Logs"/> holds only new entries (after the client
/// drops its existing ones if <see cref="LogsReset"/>), and the first status after connecting is a full snapshot.
/// </summary>
public sealed record WorkspaceStatusPayloadDto(
    string Type,
    string? SelectedArchiveId,
    string SelectedNodePath,
    ArchiveSummaryDto[]? Archives,
    ClipboardStateDto? Clipboard,
    RepackerLogEntry[] Logs,
    bool LogsReset,
    SavedRepackerUiStateDto? UiState,
    bool SupportsNativeDialogs = false);

public sealed record RpcOperationResponse(
    bool Ok = true,
    string? Error = null,
    string? ArchiveId = null,
    string? Path = null,
    int? Count = null,
    ArchiveSummaryDto? Archive = null,
    ArchiveSummaryDto[]? Archives = null,
    ClipboardStateDto? Clipboard = null,
    bool Cancelled = false,
    string[]? FilePaths = null) : IResultDto;

public sealed record TreeChildrenResponse(
    bool Ok,
    TreeNodeDto[] Nodes) : IResultDto;

public sealed record SearchResponseDto(
    bool Ok,
    SearchResultItemDto[] Results) : IResultDto;

public sealed record BinaryExportResponseDto(
    bool Ok,
    string? Error,
    string FileName,
    string MimeType,
    byte[] Data) : IResultDto;

public sealed class OpenArchiveFileRequest {
    public string? FilePath { get; set; }
    public ushort? Version { get; set; }
}

public sealed class OpenArchiveBytesRequest {
    public string? FileName { get; set; }
    public byte[]? Data { get; set; }
    public string? FilePath { get; set; }
    public ushort? Version { get; set; }
}

public sealed class OpenDirectoryRequest {
    public string? DirectoryPath { get; set; }
    public ushort? Version { get; set; }
}

public sealed class CreateArchiveRequest {
    public string? Name { get; set; }
    public ushort? Version { get; set; }
}

public sealed class SaveArchiveRequest {
    public string? ArchiveId { get; set; }
    public string? TargetFilePath { get; set; }
    public ushort? Version { get; set; }
}

public sealed class BrowseSaveFileRequest {
    public string? DefaultFileName { get; set; }
}

public sealed class CloseArchiveRequest {
    public string? ArchiveId { get; set; }
}

public sealed class NodeRefRequest {
    public string? ArchiveId { get; set; }
    public string? Path { get; set; }
}

public sealed class SetPropertyValueRequest {
    public string? ArchiveId { get; set; }
    public string? Path { get; set; }
    public RepackerPropertyKind Kind { get; set; } = RepackerPropertyKind.String;
    public string? RawValue { get; set; }
    public int? VecX { get; set; }
    public int? VecY { get; set; }
}

public sealed class AddPropertyRequest {
    public string? ArchiveId { get; set; }
    public string? ParentPath { get; set; }
    public string? Name { get; set; }
    public RepackerPropertyKind Kind { get; set; } = RepackerPropertyKind.String;
    public string? RawValue { get; set; }
    public int? VecX { get; set; }
    public int? VecY { get; set; }
    public byte[]? BinaryPayload { get; set; }
    public WzCanvasFormat? CanvasFormat { get; set; }
}

public sealed class AddImageRequest {
    public string? ArchiveId { get; set; }
    public string? ParentPath { get; set; }
    public string? ImageName { get; set; }
    public byte[]? RawImgBytes { get; set; }
    public string? JsonContent { get; set; }
}

public sealed class AddDirectoryRequest {
    public string? ArchiveId { get; set; }
    public string? ParentPath { get; set; }
    public string? DirName { get; set; }
}

public sealed class RenameNodeRequest {
    public string? ArchiveId { get; set; }
    public string? Path { get; set; }
    public string? NewName { get; set; }
}

public sealed class PasteNodeRequest {
    public string? ArchiveId { get; set; }
    public string? TargetPath { get; set; }
}

public sealed class ReplaceCanvasRequest {
    public string? ArchiveId { get; set; }
    public string? Path { get; set; }
    public byte[]? PngBytes { get; set; }
    public WzCanvasFormat? Format { get; set; }
}

public sealed class ChangeCanvasFormatRequest {
    public string? ArchiveId { get; set; }
    public string? Path { get; set; }
    public WzCanvasFormat Format { get; set; } = WzCanvasFormat.Bgra8888;
}

public sealed class ReplaceSoundRequest {
    public string? ArchiveId { get; set; }
    public string? Path { get; set; }
    public byte[]? AudioBytes { get; set; }
}

public sealed class ApplyJsonRequest {
    public string? ArchiveId { get; set; }
    public string? Path { get; set; }
    public string? JsonText { get; set; }
}

public sealed class SearchNodesRequest {
    public string? ArchiveId { get; set; }
    public string? Query { get; set; }
    public string? ValueQuery { get; set; }
    public RepackerPropertyKind? PropertyKindFilter { get; set; }
    public bool ExactMatch { get; set; }
    public int MaxResults { get; set; } = 250;
}

public sealed class SaveUiStateRequest {
    public SavedRepackerUiStateDto? UiState { get; set; }
}

public sealed class BatchExportRequest {
    public string? ArchiveId { get; set; }
    public string? Path { get; set; }
    public string? OutputDirectory { get; set; }
    public RepackerBatchExportMode Mode { get; set; } = RepackerBatchExportMode.Png;
}
