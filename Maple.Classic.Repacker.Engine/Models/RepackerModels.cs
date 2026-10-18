using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Canvas;

namespace Maple.Classic.Repacker.Engine.Models;

/// <summary>Kind of node in the workspace tree.</summary>
public enum RepackerNodeKind {
    Archive,
    Directory,
    Image,
    Property,
}

/// <summary>WZ property value type.</summary>
public enum RepackerPropertyKind {
    None,
    Null,
    Short,
    Int,
    Long,
    Float,
    Double,
    String,
    Vector,
    Uol,
    Object,
    Canvas,
    Sound,
    Convex,
    RawData,
}

public enum RepackerChangeState {
    Unchanged,
    Added,
    Modified,
    Deleted,
}

public enum RepackerExportFormat {
    Auto,
    Wz,
    Img,
    Json,
    Png,
    Audio,
}

public enum RepackerBatchExportMode {
    Png,
    Sound,
    Json,
    Img,
    All,
}

public enum RepackerWorkbenchTab {
    Json,
    Search,
    Hex,
    Batch,
    Logs,
}

public enum RepackerCanvasBackground {
    Checker,
    Dark,
    Light,
}

public enum RepackerLogLevel {
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Any response DTO that reports success via <c>Ok</c> (the RPC router reads it to build the reply envelope).</summary>
public interface IResultDto {
    bool Ok { get; }
}

public sealed record RepackerLogEntry(
    long Id,
    string Timestamp,
    RepackerLogLevel Level,
    string Message);

public sealed record ArchiveSummaryDto(
    string ArchiveId,
    string Name,
    string FilePath,
    ushort Version,
    int ImageCount,
    int ModifiedImageCount,
    RepackerChangeState ChangeState = RepackerChangeState.Unchanged,
    string[]? LinkedFilePaths = null,
    bool CanUndo = false,
    bool CanRedo = false);

/// <summary>
/// One row of the lazily-loaded tree. <see cref="ChildCount"/> is 0 for an <c>.img</c> until it is parsed, so the UI
/// treats images (and directories, whose split archives link on expand) as expandable regardless of the count.
/// </summary>
public sealed record TreeNodeDto(
    string ArchiveId,
    string Path,
    string Name,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind,
    string DisplayValue,
    int ChildCount,
    RepackerChangeState ChangeState = RepackerChangeState.Unchanged,
    int SizeBytes = 0);

public sealed record SubPropertyRowDto(
    string Name,
    string Path,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind,
    string DisplayValue,
    int ChildCount,
    int SizeBytes = 0,
    RepackerChangeState ChangeState = RepackerChangeState.Unchanged);

public sealed record VectorPointDto(int X, int Y);

public sealed record CanvasPreviewDto(
    int Width,
    int Height,
    WzCanvasFormat Format,
    string FormatName,
    byte[] PngBytes,
    VectorPointDto Origin,
    VectorPointDto? Lt,
    VectorPointDto? Rb,
    VectorPointDto? Head,
    int DelayMs,
    string? Inlink,
    string? Outlink,
    string? ResolvedLinkPath);

public sealed record AnimationFrameDto(
    string FrameName,
    string FramePath,
    int Width,
    int Height,
    WzCanvasFormat Format,
    int DelayMs,
    VectorPointDto Origin,
    VectorPointDto? Lt,
    VectorPointDto? Rb,
    VectorPointDto? Head,
    int AlphaStart,
    int AlphaEnd,
    byte[] PngBytes);

public sealed record SoundPreviewDto(
    int DataLength,
    int DurationMs,
    string MimeType,
    string FormatName,
    int SampleRate,
    int Channels,
    byte[] AudioBytes);

/// <summary>
/// Everything the UI shows for the selected node: what it is (identity, value, size, change state), its child rows,
/// and the <see cref="Preview"/> rendered on the stage and in the workbench tabs. Failures are reported as an RPC error
/// rather than through this DTO.
/// </summary>
public sealed record NodeInspectionDto(
    string ArchiveId,
    string ArchiveName,
    string Path,
    string Name,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind,
    string DisplayValue,
    string? RawStringValue,
    VectorPointDto? VectorValue,
    int ChildCount,
    int SizeBytes,
    RepackerChangeState ChangeState,
    SubPropertyRowDto[] Children,
    NodePreviewDto Preview) : IResultDto {
    public bool Ok => true;
}

/// <summary>
/// Renderable views of the selected node: the canvas/animation/sound stage (following a UOL link to its target), the
/// JSON editor text and the hex dump bytes.
/// </summary>
public sealed record NodePreviewDto(
    CanvasPreviewDto? Canvas,
    AnimationFrameDto[] AnimationFrames,
    SoundPreviewDto? Sound,
    string? ResolvedUolTargetPath,
    string JsonPreview,
    byte[] RawBinarySlice);

public sealed record SearchQueryDto {
    public string? ArchiveId { get; init; }
    public string? Query { get; init; }
    public string? ValueQuery { get; init; }
    public RepackerPropertyKind? PropertyKindFilter { get; init; }
    public bool ExactMatch { get; init; }
    public int MaxResults { get; init; } = 250;
}

public sealed record SearchResultItemDto(
    string ArchiveId,
    string ArchiveName,
    string Path,
    string Name,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind,
    string DisplayValue);

public sealed record ClipboardStateDto(
    bool HasData,
    string? NodeName,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind);
