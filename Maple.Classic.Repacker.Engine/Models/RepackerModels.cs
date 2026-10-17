using System.ComponentModel.DataAnnotations;
using Maple.Classic.Repacker.Engine.Canvas;

namespace Maple.Classic.Repacker.Engine.Models;

/// <summary>Kind of node in the workspace tree. <see cref="DisplayAttribute.ShortName"/> is the UI type badge.</summary>
public enum RepackerNodeKind {
    [Display(ShortName = "WZ")]
    Archive,

    [Display(ShortName = "DIR")]
    Directory,

    [Display(ShortName = "IMG")]
    Image,

    Property,
}

/// <summary>WZ property value type. <see cref="DisplayAttribute.ShortName"/> is the UI type badge.</summary>
public enum RepackerPropertyKind {
    [Display(ShortName = "UNK")]
    None,

    [Display(ShortName = "NULL")]
    Null,

    [Display(ShortName = "I16")]
    Short,

    [Display(ShortName = "I32")]
    Int,

    [Display(ShortName = "I64")]
    Long,

    [Display(ShortName = "F32")]
    Float,

    [Display(ShortName = "F64")]
    Double,

    [Display(ShortName = "STR")]
    String,

    [Display(ShortName = "VEC")]
    Vector,

    [Display(ShortName = "UOL")]
    Uol,

    [Display(ShortName = "SUB")]
    Object,

    [Display(ShortName = "PNG")]
    Canvas,

    [Display(ShortName = "SND")]
    Sound,

    [Display(ShortName = "CVX")]
    Convex,

    [Display(ShortName = "RAW")]
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
    ushort EncVer,
    uint FStart,
    string Copyright,
    long FileSize,
    int DirectoryCount,
    int ImageCount,
    int ModifiedImageCount,
    RepackerChangeState ChangeState = RepackerChangeState.Unchanged,
    string[]? LinkedFilePaths = null);

public sealed record TreeNodeDto(
    string ArchiveId,
    string Path,
    string Name,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind,
    string DisplayValue,
    string TypeBadge,
    bool HasChildren,
    int ChildCount,
    RepackerChangeState ChangeState = RepackerChangeState.Unchanged,
    int SizeBytes = 0);

public sealed record SubPropertyRowDto(
    string Name,
    string Path,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind,
    string TypeBadge,
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
    int CompressedBytes,
    byte[] PngBytes,
    VectorPointDto Origin,
    VectorPointDto? Lt,
    VectorPointDto? Rb,
    VectorPointDto? Head,
    int DelayMs,
    string? ZLayer,
    string? Inlink,
    string? Outlink,
    string? ResolvedLinkPath,
    bool IsLinkStub);

public sealed record AnimationFrameDto(
    int FrameIndex,
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
    string Extension,
    string FormatName,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    byte[] AudioBytes,
    byte[] HeaderBytes);

public sealed record NodeInspectionDto(
    bool Ok,
    string? Error,
    string ArchiveId,
    string ArchiveName,
    string Path,
    string Name,
    string? ImagePath,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind,
    string TypeBadge,
    string DisplayValue,
    string? RawStringValue,
    long? NumericIntValue,
    double? NumericFloatValue,
    VectorPointDto? VectorValue,
    int ChildCount,
    int SizeBytes,
    RepackerChangeState ChangeState,
    bool CanUndo,
    bool CanRedo,
    string JsonPreview,
    SubPropertyRowDto[] Children,
    CanvasPreviewDto? Canvas,
    AnimationFrameDto[] AnimationFrames,
    SoundPreviewDto? Sound,
    string? ResolvedUolTargetPath,
    byte[] RawBinarySlice) : IResultDto;

public sealed record SearchQueryDto {
    public string? ArchiveId { get; init; }
    public string? Query { get; init; }
    public string? ValueQuery { get; init; }
    public RepackerPropertyKind? PropertyKindFilter { get; init; }
    public bool CaseSensitive { get; init; }
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
    string TypeBadge,
    string DisplayValue);

public sealed record ClipboardStateDto(
    bool HasData,
    string? SourceArchiveId,
    string? SourcePath,
    string? NodeName,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind);
