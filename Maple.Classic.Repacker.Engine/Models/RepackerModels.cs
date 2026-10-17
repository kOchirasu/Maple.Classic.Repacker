namespace Maple.Classic.Repacker.Engine.Models;

public enum RepackerNodeKind {
    Archive,
    Directory,
    Image,
    Property,
}

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

public enum RepackerLogLevel {
    Info,
    Success,
    Warning,
    Error,
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
    bool IsModified);

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
    bool IsModified,
    int SizeBytes = 0);

public sealed record SubPropertyRowDto(
    string Name,
    string Path,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind,
    string TypeBadge,
    string DisplayValue,
    int ChildCount,
    int SizeBytes = 0);

public sealed record VectorPointDto(int X, int Y);

public sealed record CanvasPreviewDto(
    int Width,
    int Height,
    int Format,
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
    int Format,
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
    bool IsModified,
    bool CanUndo,
    bool CanRedo,
    string JsonPreview,
    SubPropertyRowDto[] Children,
    CanvasPreviewDto? Canvas,
    AnimationFrameDto[] AnimationFrames,
    SoundPreviewDto? Sound,
    string? ResolvedUolTargetPath,
    byte[] RawBinarySlice);

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
