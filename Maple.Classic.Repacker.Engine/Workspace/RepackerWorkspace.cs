using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Maple.Classic.File.IO.Archive;
using Maple.Classic.File.IO.Values;
using Maple.Classic.Repacker.Engine.Audio;
using Maple.Classic.Repacker.Engine.Canvas;
using Maple.Classic.Repacker.Engine.Models;
using Maple.Classic.Repacker.Engine.Serialization;
using static Maple.Classic.Repacker.Engine.Workspace.NodeDtoBuilder;
using static Maple.Classic.Repacker.Engine.Workspace.WzPropertyPaths;
using static Maple.Classic.Repacker.Engine.Workspace.WzValueFormatter;

namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Central thread-safe workspace engine for inspecting, editing, searching, and repacking
/// MapleStory Classic World (<c>.wz</c> and <c>.img</c>) archives.
/// </summary>
/// <remarks>
/// Threading convention: every public member acquires <see cref="syncLock"/> before touching shared state
/// (<see cref="archives"/>, <see cref="logs"/>, <see cref="clipboard"/>, selection). Private members suffixed with
/// <c>Unsafe</c> do NOT acquire the lock themselves; they must only be called while the caller already holds
/// <see cref="syncLock"/>. This lets a public operation perform several steps atomically without re-entering the lock.
/// Each <c>Unsafe</c> member calls <see cref="AssertLockHeld"/> so violations fail fast in Debug builds.
/// </remarks>
public sealed class RepackerWorkspace : IDisposable {
    private const int MAX_LOG_ENTRIES = 1000;

    private readonly Lock syncLock = new();
    private readonly List<ArchiveSession> archives = [];
    private readonly List<RepackerLogEntry> logs = [];
    private readonly string[] dataFolders;

    private long stateVersion = 1;
    private long nextLogId = 1;
    private long logsEpoch;
    private int nextArchiveSeq = 1;

    private ClipboardEntry? clipboard;

    private sealed record ClipboardEntry(
        string Name,
        RepackerNodeKind NodeKind,
        RepackerPropertyKind PropertyKind,
        byte[] SerializedBytes);

    /// <param name="dataFolders">
    /// Game data folders searched for an uploaded archive's original file (so it can be saved back in place). Defaults
    /// to <c>MSCW_DATA_FOLDER</c> and the standard install locations; pass an empty list to only use paths the caller
    /// provides (e.g. in tests, so a local game install can't be picked up).
    /// </param>
    public RepackerWorkspace(IEnumerable<string>? dataFolders = null) {
        this.dataFolders = [.. dataFolders ?? ArchivePathResolver.DefaultDataFolders];
    }

    public long StateVersion => Interlocked.Read(ref stateVersion);
    public string? SelectedArchiveId { get; private set; }
    public string SelectedNodePath { get; private set; } = string.Empty;

    public void NotifyStateChanged() {
        Interlocked.Increment(ref stateVersion);
    }

    public void AppendLog(string message, RepackerLogLevel level = RepackerLogLevel.Info) {
        lock (syncLock) {
            AppendLogUnsafe(message, level);
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// Debug-only guard used by every <c>...Unsafe</c> member to verify the caller already holds <see cref="syncLock"/>.
    /// Compiled out of Release builds.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertLockHeld([CallerMemberName] string caller = "") {
        Debug.Assert(syncLock.IsHeldByCurrentThread, $"{caller} must be called while holding {nameof(syncLock)}.");
    }

    /// <summary>Appends a log entry. Caller must hold <see cref="syncLock"/>.</summary>
    private void AppendLogUnsafe(string message, RepackerLogLevel level = RepackerLogLevel.Info) {
        AssertLockHeld();
        var entry = new RepackerLogEntry(
            nextLogId++,
            DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            level,
            message);
        logs.Add(entry);
        if (logs.Count > MAX_LOG_ENTRIES) {
            logs.RemoveRange(0, logs.Count - MAX_LOG_ENTRIES);
        }
    }

    public IReadOnlyList<RepackerLogEntry> GetLogs() {
        lock (syncLock) {
            return [.. logs];
        }
    }

    /// <summary>
    /// Log entries newer than <paramref name="afterId"/>, plus the current clear-epoch. The epoch increases every time
    /// the log is cleared, so a consumer that tracks it knows when to drop the entries it already has.
    /// </summary>
    public (long Epoch, RepackerLogEntry[] Entries) GetLogsSince(long afterId) {
        lock (syncLock) {
            return (logsEpoch, [.. logs.Where(l => l.Id > afterId)]);
        }
    }

    public void ClearLogs() {
        lock (syncLock) {
            logs.Clear();
            logsEpoch++;
        }
        NotifyStateChanged();
    }

    public ArchiveSummaryDto[] GetArchiveSummaries() {
        lock (syncLock) {
            return archives.Select(a => a.ToSummaryDto()).ToArray();
        }
    }

    public ClipboardStateDto GetClipboardState() {
        lock (syncLock) {
            return GetClipboardStateUnsafe();
        }
    }

    /// <summary>Snapshot of the clipboard. Caller must hold <see cref="syncLock"/>.</summary>
    private ClipboardStateDto GetClipboardStateUnsafe() {
        AssertLockHeld();
        return clipboard is null
            ? new ClipboardStateDto(false, null, RepackerNodeKind.Property, RepackerPropertyKind.None)
            : new ClipboardStateDto(true, clipboard.Name, clipboard.NodeKind, clipboard.PropertyKind);
    }

    public void SelectNode(string? archiveId, string? path) {
        lock (syncLock) {
            if (!string.IsNullOrWhiteSpace(archiveId) && archives.Any(a => a.ArchiveId == archiveId)) {
                SelectedArchiveId = archiveId;
            }
            SelectedNodePath = ArchiveSession.NormalizePath(path ?? string.Empty);
        }
        NotifyStateChanged();
    }

    // -------------------------------------------------------------------------
    // Archive Lifecycle (Open, Create, Save, Close)
    // -------------------------------------------------------------------------

    public ArchiveSummaryDto OpenArchiveFromFile(string filePath, ushort? version = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = ArchivePathResolver.ResolvePreferredRootArchivePathOnDisk(Path.GetFullPath(filePath));
        var wz = new WzArchive(fullPath, version);
        string fileName = Path.GetFileName(fullPath);

        lock (syncLock) {
            // Re-opening a file that is open (or linked by an open archive): reload that session in place
            ArchiveSession? existing = FindExistingOrLinkedSessionByPathUnsafe(fullPath);
            if (existing is not null) {
                string reloadPath = ArchivePathResolver.ChoosePreferredRootArchivePath(existing.FilePath, fullPath);
                WzArchive reloadWz = ReuseOrOpenArchive(wz, fullPath, reloadPath, version);
                return ReplaceSessionUnsafe(
                    archives.IndexOf(existing),
                    () => new ArchiveSession(existing.ArchiveId, Path.GetFileName(reloadPath), reloadPath, reloadWz, version),
                    s => $"Reloaded archive \"{s.Name}\" (v{reloadWz.Version}, encVer {reloadWz.Header.EncVer}{LinkedNote(s)}) from {reloadPath}.");
            }

            var session = new ArchiveSession($"wz_{nextArchiveSeq++}", fileName, fullPath, wz, version);

            // The new archive links an already-open one (e.g. opening Base.wz while Character.wz is open): take its slot
            ArchiveSession? subsumed = archives.Find(a => !string.IsNullOrEmpty(a.FilePath) && session.HasLinkedOrSubdirectoryFile(a.FilePath));
            if (subsumed is not null) {
                session.Dispose();
                return ReplaceSessionUnsafe(
                    archives.IndexOf(subsumed),
                    () => new ArchiveSession(subsumed.ArchiveId, fileName, fullPath, new WzArchive(fullPath, version), version),
                    s => $"Reloaded archive \"{fileName}\" (v{s.Archive.Version}, encVer {s.Archive.Header.EncVer}{LinkedNote(s)}) from {fullPath}.");
            }

            return AddSessionUnsafe(
                session,
                $"Opened archive \"{fileName}\" (v{wz.Version}, encVer {wz.Header.EncVer}{LinkedNote(session)}) from {fullPath}.");
        }
    }

    public ArchiveSummaryDto OpenArchiveFromBytes(
        string fileName,
        byte[] data,
        string? filePath = null,
        ushort? version = null,
        IEnumerable<string>? hintPaths = null) {
        ArgumentNullException.ThrowIfNull(data);
        string cleanName = string.IsNullOrWhiteSpace(fileName) ? $"Archive_{nextArchiveSeq}.wz" : Path.GetFileName(fileName.Trim());
        if (!cleanName.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)) {
            cleanName += ".wz";
        }
        string stem = Path.GetFileNameWithoutExtension(cleanName);
        WzArchive wz = WzArchive.FromBytes(data, stem, version);

        lock (syncLock) {
            string resolvedPath = !string.IsNullOrWhiteSpace(filePath)
                ? filePath.Trim()
                : ArchivePathResolver.TryResolveDiskPath(archives.Select(static a => a.FilePath), cleanName, data.Length, hintPaths, dataFolders) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(resolvedPath)) {
                string preferredPath = ArchivePathResolver.ResolvePreferredRootArchivePathOnDisk(resolvedPath);
                if (!string.Equals(preferredPath, resolvedPath, StringComparison.OrdinalIgnoreCase) &&
                    System.IO.File.Exists(preferredPath)) {
                    wz.Dispose();
                    resolvedPath = preferredPath;
                    cleanName = Path.GetFileName(preferredPath);
                    stem = Path.GetFileNameWithoutExtension(cleanName);
                    wz = new WzArchive(preferredPath, version);
                }
            }

            ArchiveSession? existing = !string.IsNullOrWhiteSpace(resolvedPath)
                ? FindExistingOrLinkedSessionByPathUnsafe(resolvedPath)
                : archives.Find(a =>
                    string.Equals(a.Name, cleanName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        ArchiveSession.StripNumericShardSuffix(Path.GetFileNameWithoutExtension(a.Name)),
                        ArchiveSession.StripNumericShardSuffix(stem),
                        StringComparison.OrdinalIgnoreCase));

            if (existing is not null) {
                string reloadPath = !string.IsNullOrWhiteSpace(resolvedPath)
                    ? ArchivePathResolver.ChoosePreferredRootArchivePath(existing.FilePath, resolvedPath)
                    : string.Empty;
                string reloadName = !string.IsNullOrWhiteSpace(reloadPath) ? Path.GetFileName(reloadPath) : cleanName;
                WzArchive reloadWz = !string.IsNullOrWhiteSpace(reloadPath) && System.IO.File.Exists(reloadPath)
                    ? ReuseOrOpenArchive(wz, resolvedPath, reloadPath, version)
                    : wz;
                return ReplaceSessionUnsafe(
                    archives.IndexOf(existing),
                    () => new ArchiveSession(existing.ArchiveId, reloadName, reloadPath, reloadWz, version),
                    s => $"Reloaded archive \"{reloadName}\" ({data.Length:N0} bytes, v{reloadWz.Version}{LinkedNote(s)}).");
            }

            var session = new ArchiveSession($"wz_{nextArchiveSeq++}", cleanName, resolvedPath, wz, version);
            return AddSessionUnsafe(session, $"Loaded archive \"{cleanName}\" ({data.Length:N0} bytes, v{wz.Version}{LinkedNote(session)}).");
        }
    }

    /// <summary>
    /// Finds the open session whose root file is <paramref name="fullPath"/> or that already links it (as a shard or
    /// subdirectory archive). Caller must hold <see cref="syncLock"/>.
    /// </summary>
    private ArchiveSession? FindExistingOrLinkedSessionByPathUnsafe(string fullPath) {
        AssertLockHeld();
        return archives.Find(a =>
            (!string.IsNullOrEmpty(a.FilePath) &&
             string.Equals(a.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)) ||
            a.HasLinkedOrSubdirectoryFile(fullPath));
    }

    /// <summary>
    /// Disposes and removes every other open session whose file is now linked by <paramref name="primary"/>.
    /// Caller must hold <see cref="syncLock"/>.
    /// </summary>
    private void RemoveSubsumedSessionsUnsafe(ArchiveSession primary, int primaryIdx) {
        AssertLockHeld();
        for (int i = archives.Count - 1; i >= 0; i--) {
            if (i == primaryIdx) {
                continue;
            }
            ArchiveSession other = archives[i];
            if (!string.IsNullOrEmpty(other.FilePath) && primary.HasLinkedOrSubdirectoryFile(other.FilePath)) {
                other.Dispose();
                archives.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Disposes the session at <paramref name="index"/> and replaces it with the one built by <paramref name="create"/>
    /// (created only after disposal, so its files are no longer held open), then drops sessions it now links, selects it
    /// and logs. Caller must hold <see cref="syncLock"/>.
    /// </summary>
    private ArchiveSummaryDto ReplaceSessionUnsafe(int index, Func<ArchiveSession> create, Func<ArchiveSession, string> logMessage) {
        AssertLockHeld();
        archives[index].Dispose();
        ArchiveSession replacement = create();
        archives[index] = replacement;
        RemoveSubsumedSessionsUnsafe(replacement, index);
        return SelectOpenedSessionUnsafe(replacement, logMessage(replacement), RepackerLogLevel.Success);
    }

    /// <summary>Adds a newly opened session, selects it and logs. Caller must hold <see cref="syncLock"/>.</summary>
    private ArchiveSummaryDto AddSessionUnsafe(ArchiveSession session, string logMessage, RepackerLogLevel level = RepackerLogLevel.Success) {
        AssertLockHeld();
        archives.Add(session);
        return SelectOpenedSessionUnsafe(session, logMessage, level);
    }

    private ArchiveSummaryDto SelectOpenedSessionUnsafe(ArchiveSession session, string logMessage, RepackerLogLevel level) {
        AssertLockHeld();
        SelectedArchiveId = session.ArchiveId;
        SelectedNodePath = string.Empty;
        AppendLogUnsafe(logMessage, level);
        NotifyStateChanged();
        return session.ToSummaryDto();
    }

    /// <summary>Returns <paramref name="opened"/> if it already is <paramref name="targetPath"/>, otherwise disposes it and opens the target.</summary>
    private static WzArchive ReuseOrOpenArchive(WzArchive opened, string openedPath, string targetPath, ushort? version) {
        if (string.Equals(targetPath, openedPath, StringComparison.OrdinalIgnoreCase)) {
            return opened;
        }
        opened.Dispose();
        return new WzArchive(targetPath, version);
    }

    private static string LinkedNote(ArchiveSession session) =>
        session.LinkedArchiveCount > 0 ? $", +{session.LinkedArchiveCount} linked .wz file(s)" : string.Empty;

    public ArchiveSummaryDto[] OpenDataDirectory(string directoryPath, ushort? version = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        string fullDir = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDir)) {
            throw new DirectoryNotFoundException($"Directory not found: {fullDir}");
        }

        string[] wzFiles = ArchivePathResolver.GetOrderedDataDirectoryWzFiles(fullDir);

        var openedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string wzPath in wzFiles) {
            try {
                bool alreadyLinked;
                lock (syncLock) {
                    alreadyLinked = archives.Any(a => a.HasLinkedOrSubdirectoryFile(wzPath));
                }
                if (alreadyLinked) {
                    continue;
                }
                ArchiveSummaryDto summary = OpenArchiveFromFile(wzPath, version);
                openedIds.Add(summary.ArchiveId);
            } catch (Exception ex) {
                AppendLog($"Skipped \"{Path.GetFileName(wzPath)}\": {ex.Message}", RepackerLogLevel.Warning);
            }
        }

        ArchiveSummaryDto[] opened;
        lock (syncLock) {
            opened = archives
                .Where(a => openedIds.Contains(a.ArchiveId))
                .Select(static a => a.ToSummaryDto())
                .ToArray();
        }

        AppendLog($"Opened {opened.Length} WZ archive(s) from \"{fullDir}\".", RepackerLogLevel.Success);
        return opened;
    }

    public ArchiveSummaryDto CreateNewArchive(string name, ushort version = WzArchive.DEFAULT_VERSION) {
        string cleanName = string.IsNullOrWhiteSpace(name) ? $"NewArchive_{nextArchiveSeq}.wz" : name.Trim();
        if (!cleanName.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)) {
            cleanName += ".wz";
        }

        lock (syncLock) {
            var session = new ArchiveSession($"wz_{nextArchiveSeq++}", cleanName, string.Empty, new WzArchive(version)) {
                HasStructureChanges = true,
                IsUserCreatedArchive = true,
            };
            return AddSessionUnsafe(session, $"Created new empty WZ archive \"{cleanName}\" (v{version}).", RepackerLogLevel.Info);
        }
    }

    public (string? Error, ArchiveSummaryDto? Summary) SaveArchiveToDisk(
        string archiveId,
        string? targetFilePath = null,
        ushort? version = null) =>
        WithSession<(string? Error, ArchiveSummaryDto? Summary)>(archiveId, static e => (e, null), session => {
            string savePath = !string.IsNullOrWhiteSpace(targetFilePath) ? targetFilePath.Trim() : session.FilePath;
            if (string.IsNullOrWhiteSpace(savePath)) {
                return ("A file path is required to save an in-memory archive to disk.", null);
            }

            try {
                string fullPath = Path.GetFullPath(savePath);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) {
                    Directory.CreateDirectory(dir);
                }

                byte[] bytes = session.BuildArchiveBytes(version, commitCleanState: true);
                System.IO.File.WriteAllBytes(fullPath, bytes);
                session.FilePath = fullPath;
                session.Name = Path.GetFileName(fullPath);
                AppendLogUnsafe($"Saved archive \"{session.Name}\" ({bytes.Length:N0} bytes) to {fullPath}.", RepackerLogLevel.Success);
                return (null, session.ToSummaryDto());
            } catch (Exception ex) {
                AppendLogUnsafe($"Failed to save \"{session.Name}\": {ex.Message}", RepackerLogLevel.Error);
                return (ex.Message, null);
            }
        });

    public (string? Error, string FileName, byte[] Data) ExportArchiveBytes(
        string archiveId,
        ushort? version = null,
        bool commitClean = false) =>
        WithSession<(string? Error, string FileName, byte[] Data)>(archiveId, static e => (e, string.Empty, []), session => {
            try {
                byte[] bytes = session.BuildArchiveBytes(version, commitCleanState: commitClean);
                AppendLogUnsafe($"Repacked archive \"{session.Name}\" ({bytes.Length:N0} bytes, v{version ?? session.Archive.Version}).", RepackerLogLevel.Success);
                return (null, session.Name, bytes);
            } catch (Exception ex) {
                AppendLogUnsafe($"Failed to repack \"{session.Name}\": {ex.Message}", RepackerLogLevel.Error);
                return (ex.Message, string.Empty, []);
            }
        });

    public bool CloseArchive(string archiveId) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return false;
            }
            archives.Remove(session);
            session.Dispose();
            AppendLogUnsafe($"Closed archive \"{session.Name}\".", RepackerLogLevel.Info);

            if (SelectedArchiveId == archiveId) {
                SelectedArchiveId = archives.FirstOrDefault()?.ArchiveId;
                SelectedNodePath = string.Empty;
            }
        }
        NotifyStateChanged();
        return true;
    }

    // -------------------------------------------------------------------------
    // Tree Expansion & Node Inspection
    // -------------------------------------------------------------------------

    public TreeNodeDto[] GetTreeChildren(string archiveId, string? path) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return [];
            }

            string norm = ArchiveSession.NormalizePath(path ?? string.Empty);
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);

            // 1. Archive root or subdirectory
            if (imgPath is null) {
                VirtualFsNode? dirNode = session.ResolveVirtualNode(norm);
                if (dirNode is null || !dirNode.IsDir) {
                    return [];
                }

                if (session.EnsureDirectoryLinkedWithChildren(dirNode)) {
                    NotifyStateChanged();
                }

                return [.. DescribeDirectoryChildren(session, norm, dirNode).Select(c => c.ToTreeNode(session.ArchiveId))];
            }

            // 2. Inside an .img property tree
            try {
                bool imgDeleted = session.IsVirtualPathDeleted(imgPath);
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                (WzValue? target, bool targetDeleted) = string.IsNullOrEmpty(subPath)
                    ? (imgRoot, imgDeleted)
                    : session.ResolvePropertyOrDeleted(imgPath, imgRoot, subPath);
                if (target is null) {
                    return [];
                }

                return [.. DescribePropertyChildren(session, imgPath, subPath, target, imgDeleted || targetDeleted).Select(c => c.ToTreeNode(session.ArchiveId))];
            } catch {
                return [];
            }
        }
    }

    public (string? Error, NodeInspectionDto? Inspection) InspectNode(string archiveId, string? path, bool includeAnimation = true) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return ($"Archive \"{archiveId}\" not found.", null);
            }

            string norm = ArchiveSession.NormalizePath(path ?? string.Empty);
            SelectedArchiveId = session.ArchiveId;
            SelectedNodePath = norm;

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);

            // Case 1: Archive root or Directory node
            if (imgPath is null) {
                VirtualFsNode? dirNode = session.ResolveVirtualNode(norm);
                if (dirNode is null) {
                    return ($"Node \"{norm}\" not found in \"{session.Name}\".", null);
                }

                if (session.EnsureDirectoryLinkedWithChildren(dirNode)) {
                    NotifyStateChanged();
                }

                bool isRoot = string.IsNullOrEmpty(norm);
                bool isDirDeleted = !isRoot && session.IsVirtualPathDeleted(norm);
                RepackerNodeKind nodeKind = isRoot ? RepackerNodeKind.Archive : RepackerNodeKind.Directory;
                string displayName = isRoot ? session.Name : dirNode.Name;
                (int dCount, int iCount) = ArchiveSession.CountVirtualRecursive(dirNode);

                SubPropertyRowDto[] rows = [.. DescribeDirectoryChildren(session, norm, dirNode).Select(static c => c.ToChildRow())];

                string summaryJson = $$"""
                {
                  "archive": "{{EscapeJson(session.Name)}}",
                  "path": "{{EscapeJson(norm)}}",
                  "version": {{session.Archive.Version}},
                  "encVer": {{session.Archive.Header.EncVer}},
                  "fstart": {{session.Archive.Header.FStart}},
                  "copyright": "{{EscapeJson(session.Archive.Header.Copyright)}}",
                  "directories": {{dCount}},
                  "images": {{iCount}},
                  "modifiedImages": {{session.ModifiedImageCount}}
                }
                """;

                RepackerChangeState dirChangeState = isDirDeleted
                    ? RepackerChangeState.Deleted
                    : isRoot
                        ? (session.IsUserCreatedArchive
                            ? RepackerChangeState.Added
                            : session.IsModified
                                ? RepackerChangeState.Modified
                                : RepackerChangeState.Unchanged)
                        : session.GetDirectoryChangeState(norm, dirNode);

                return (null, new NodeInspectionDto(
                    session.ArchiveId,
                    session.Name,
                    norm,
                    displayName,
                    nodeKind,
                    RepackerPropertyKind.None,
                    $"{dCount} directories, {iCount} images",
                    RawStringValue: null,
                    VectorValue: null,
                    dirNode.Children.Count,
                    SizeBytes: 0,
                    dirChangeState,
                    rows,
                    new NodePreviewDto(null, [], null, null, summaryJson, [])));
            }

            // Case 2: Image node or Property inside an Image
            try {
                bool imgDeleted = session.IsVirtualPathDeleted(imgPath);
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                bool isImageRoot = string.IsNullOrEmpty(subPath);
                (WzValue? target, bool propDeleted) = isImageRoot
                    ? (imgRoot, imgDeleted)
                    : session.ResolvePropertyOrDeleted(imgPath, imgRoot, subPath);
                if (target is null) {
                    return ($"Property \"{subPath}\" not found inside \"{imgPath}\".", null);
                }
                bool isNodeDeleted = imgDeleted || propDeleted;

                string leafName = isImageRoot
                    ? Path.GetFileName(imgPath)
                    : WzPath.Name(subPath);

                RepackerNodeKind nodeKind = isImageRoot ? RepackerNodeKind.Image : RepackerNodeKind.Property;
                (RepackerPropertyKind propKind, string displayValue) = DescribeWzValue(target);

                string? rawStr = target.AsString();
                VectorPointDto? vecDto = target is WzValue.Vector(var vx, var vy) ? new VectorPointDto(vx, vy) : null;

                SubPropertyRowDto[] childRows = ChildRows(session, imgPath, subPath, target, isNodeDeleted);
                int ownChildCount = childRows.Length;
                if (ownChildCount == 0 && !isImageRoot && target is not WzValue.Object and not WzValue.Convex) {
                    string parentSubPath = WzPath.Parent(subPath);
                    (WzValue? parentVal, bool parentDel) = string.IsNullOrEmpty(parentSubPath)
                        ? (imgRoot, imgDeleted)
                        : session.ResolvePropertyOrDeleted(imgPath, imgRoot, parentSubPath);
                    if (parentVal is not null) {
                        childRows = ChildRows(session, imgPath, parentSubPath, parentVal, imgDeleted || parentDel);
                    }
                }

                // Resolve UOL if target is a UOL link
                string? resolvedUolPath = null;
                WzValue effectiveVisualTarget = target;
                string effectiveSubPath = subPath;
                if (target is WzValue.Uol(var uolStr)) {
                    (WzValue? uolResolved, string? uolResolvedSubPath) = ResolveUolTarget(imgRoot, subPath, uolStr);
                    if (uolResolved is not null && uolResolvedSubPath is not null) {
                        effectiveVisualTarget = uolResolved;
                        effectiveSubPath = uolResolvedSubPath;
                        resolvedUolPath = WzPath.Combine(imgPath, uolResolvedSubPath);
                    }
                }

                // Canvas preview (if target or UOL target is a Canvas)
                CanvasPreviewDto? canvasDto = null;
                if (effectiveVisualTarget is WzValue.Canvas canvasVal) {
                    canvasDto = CanvasLinkResolver.BuildCanvasPreview(archives, session, imgPath, imgRoot, canvasVal);
                }

                // Animation frames (if target is an animation container OR a frame inside an animation container)
                AnimationFrameDto[] animFrames = [];
                if (includeAnimation) {
                    animFrames = CanvasLinkResolver.ExtractAnimationFrames(archives, session, imgPath, imgRoot, effectiveSubPath, effectiveVisualTarget);
                    // If user clicked an animation folder (like stand1) and canvasDto is null, populate canvasDto from frame 0
                    if (canvasDto is null && animFrames.Length > 0) {
                        AnimationFrameDto f0 = animFrames[0];
                        canvasDto = new CanvasPreviewDto(
                            f0.Width,
                            f0.Height,
                            f0.Format,
                            f0.Format.GetDisplayName(),
                            f0.PngBytes,
                            f0.Origin,
                            f0.Lt,
                            f0.Rb,
                            f0.Head,
                            f0.DelayMs,
                            Inlink: null,
                            Outlink: null,
                            ResolvedLinkPath: null);
                    }
                }

                // Sound preview (if target or UOL target is Sound_DX8)
                SoundPreviewDto? soundDto = effectiveVisualTarget is WzValue.Sound soundVal ? BuildSoundPreview(soundVal) : null;

                // If inspecting a container node with no direct preview yet, preview its first previewable child node
                if (canvasDto is null && soundDto is null && effectiveVisualTarget is not WzValue.String && effectiveVisualTarget.Children is { Count: > 0 } containerChildren) {
                    foreach (WzProperty childProp in containerChildren) {
                        string childSubPath = WzPath.Combine(effectiveSubPath, childProp.Name);
                        WzValue childVal = childProp.Value;
                        if (childVal is WzValue.Uol(var childUol)) {
                            (WzValue? resolvedChild, _) = ResolveUolTarget(imgRoot, childSubPath, childUol);
                            if (resolvedChild is not null) {
                                childVal = resolvedChild;
                            }
                        }

                        if (childVal is WzValue.Canvas childCanvas) {
                            canvasDto = CanvasLinkResolver.BuildCanvasPreview(archives, session, imgPath, imgRoot, childCanvas);
                            break;
                        }
                        if (childVal is WzValue.Sound childSound) {
                            soundDto = BuildSoundPreview(childSound);
                            break;
                        }
                        if (childVal is WzValue.String) {
                            break;
                        }
                    }
                }

                // Raw binary slice for Hex Dump tab (limit to 64 KB for instant UI responsiveness)
                byte[] rawSlice = ExtractBinarySliceForHexView(session, imgPath, isImageRoot, effectiveVisualTarget);

                int sizeBytes = isImageRoot
                    ? session.GetImageSize(imgPath)
                    : rawSlice.Length;

                string jsonPreview = target.ToJson();
                RepackerChangeState nodeChangeState = EvaluatePropertyChangeState(session, imgPath, subPath, target, isNodeDeleted);

                return (null, new NodeInspectionDto(
                    session.ArchiveId,
                    session.Name,
                    norm,
                    leafName,
                    nodeKind,
                    propKind,
                    displayValue,
                    rawStr,
                    vecDto,
                    ownChildCount,
                    sizeBytes,
                    nodeChangeState,
                    childRows,
                    new NodePreviewDto(canvasDto, animFrames, soundDto, resolvedUolPath, jsonPreview, rawSlice)));
            } catch (Exception ex) {
                return (ex.Message, null);
            }
        }
    }

    private static SoundPreviewDto BuildSoundPreview(WzValue.Sound sound) {
        ExtractedSoundData extracted = WzSoundCodec.ExtractAudioStream(sound);
        return new SoundPreviewDto(
            sound.DataLength,
            sound.Duration,
            extracted.MimeType,
            extracted.FormatName,
            extracted.SampleRate,
            extracted.Channels,
            extracted.AudioBytes);
    }

    private static SubPropertyRowDto[] ChildRows(ArchiveSession session, string imgPath, string subPath, WzValue target, bool parentDeleted) =>
        [.. DescribePropertyChildren(session, imgPath, subPath, target, parentDeleted).Select(static c => c.ToChildRow())];

    // -------------------------------------------------------------------------
    // Property & Node CRUD Operations
    // -------------------------------------------------------------------------

    public string? SetPropertyValue(
        string archiveId,
        string path,
        RepackerPropertyKind kind,
        string? rawValue,
        int? vecX = null,
        int? vecY = null) =>
        WithSession(archiveId, session => {
            (string? error, NodeTarget<WzValue> target) = ResolveNode<WzValue>(
                session, path, "Cannot set primitive value on an archive, directory, or image root.");
            if (error is not null) {
                return error;
            }

            // Editing Canvas metadata (origin / delay) keeps the pixel data
            if (target.Value is WzValue.Canvas canvas && kind == RepackerPropertyKind.Canvas) {
                return EditImage(session, target.ImagePath, () => {
                    if (vecX.HasValue && vecY.HasValue) {
                        canvas["origin"] = new WzValue.Vector(vecX.Value, vecY.Value);
                    }
                    if (!string.IsNullOrWhiteSpace(rawValue) && int.TryParse(rawValue, out int delayVal)) {
                        canvas["delay"] = new WzValue.Int(delayVal);
                    }
                    return null;
                }, () => $"Updated canvas properties at \"{path}\".", RepackerLogLevel.Info);
            }

            WzValue newVal = CreatePrimitiveOrStructuredValue(kind, rawValue, vecX, vecY, target.Value);
            return EditImage(
                session,
                target.ImagePath,
                () => TryReplacePropertyNode(target.ImageRoot, target.SubPath, newVal) ? null : $"Failed to update property at \"{target.SubPath}\".",
                () => $"Updated \"{path}\" ({kind}) = {DescribeWzValue(newVal).DisplayValue}.");
        });

    public (string? Error, string Path) AddProperty(
        string archiveId,
        string parentPath,
        string name,
        RepackerPropertyKind kind,
        string? rawValue = null,
        int? vecX = null,
        int? vecY = null,
        byte[]? binaryPayload = null,
        WzCanvasFormat? canvasFormat = null) =>
        WithSessionPath(archiveId, session => {
            string cleanName = name.Trim().Trim('/');
            if (string.IsNullOrEmpty(cleanName)) {
                return ("Property name is required.", string.Empty);
            }

            (string? error, NodeTarget<WzValue> target) = ResolveNode<WzValue>(
                session, parentPath, "Select an .img file or property inside an .img file to add a property.", allowImageRoot: true);
            if (error is not null) {
                return (error, string.Empty);
            }

            // A leaf property (e.g. Int/String) is selected: add the new property as its sibling
            WzValue? targetParent = target.Value;
            string effectiveSubPath = target.SubPath;
            if (targetParent is not WzValue.Object and not WzValue.Canvas and not WzValue.Convex) {
                effectiveSubPath = WzPath.Parent(target.SubPath);
                targetParent = string.IsNullOrEmpty(effectiveSubPath) ? target.ImageRoot : ResolvePropertyNode(target.ImageRoot, effectiveSubPath);
            }
            if (targetParent is null) {
                return ($"Target parent \"{parentPath}\" not found.", string.Empty);
            }

            WzValue newVal = CreateNewPropertyValue(kind, rawValue, vecX, vecY, binaryPayload, canvasFormat);
            string createdSubPath = WzPath.Combine(effectiveSubPath, cleanName);
            string? editError = EditImage(session, target.ImagePath, () => {
                if (targetParent is WzValue.Convex convex) {
                    convex.Items.Add(newVal);
                    createdSubPath = WzPath.Combine(effectiveSubPath, (convex.Items.Count - 1).ToString(CultureInfo.InvariantCulture));
                } else {
                    target.ImageRoot.Set(createdSubPath, newVal);
                }
                return null;
            }, () => $"Added {kind} property \"{cleanName}\" at \"{target.ImagePath}/{createdSubPath}\".");
            return Selected(editError, WzPath.Combine(target.ImagePath, createdSubPath));
        });

    public (string? Error, string Path) AddImage(
        string archiveId,
        string? parentPath,
        string imageName,
        byte[]? rawImgBytes = null,
        string? jsonContent = null) =>
        WithSessionPath(archiveId, session => {
            string cleanName = imageName.Trim().Trim('/');
            if (string.IsNullOrEmpty(cleanName)) {
                return ("Image name is required.", string.Empty);
            }
            if (!cleanName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) {
                cleanName += ".img";
            }

            string fullImgPath = WzPath.Combine(ContainingDirectory(parentPath), cleanName);
            WzValue rootVal = rawImgBytes is { Length: > 0 } ? WzValue.Parse(rawImgBytes)
                : !string.IsNullOrWhiteSpace(jsonContent) ? JsonWzCodec.Parse(jsonContent)
                : new WzValue.Object();

            session.AddOrReplaceVirtualImage(fullImgPath, rootVal);
            AppendLogUnsafe($"Added image \"{fullImgPath}\" to \"{session.Name}\".", RepackerLogLevel.Success);
            return Selected(null, fullImgPath);
        });

    public (string? Error, string Path) AddDirectory(
        string archiveId,
        string? parentPath,
        string dirName) =>
        WithSessionPath(archiveId, session => {
            string cleanName = dirName.Trim().Trim('/');
            if (string.IsNullOrEmpty(cleanName)) {
                return ("Directory name is required.", string.Empty);
            }

            string normParent = ContainingDirectory(parentPath);
            session.AddVirtualDirectory(normParent, cleanName);
            string createdPath = WzPath.Combine(normParent, cleanName);
            AppendLogUnsafe($"Created directory \"{createdPath}\" in \"{session.Name}\".", RepackerLogLevel.Success);
            return Selected(null, createdPath);
        });

    public (string? Error, string Path) RenameNode(string archiveId, string path, string newName) =>
        WithSessionPath(archiveId, session => {
            string cleanNewName = newName.Trim().Trim('/');
            if (string.IsNullOrEmpty(cleanNewName)) {
                return ("New name cannot be empty.", string.Empty);
            }

            string norm = ArchiveSession.NormalizePath(path);
            if (string.IsNullOrEmpty(norm)) {
                if (!cleanNewName.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)) {
                    cleanNewName += ".wz";
                }
                session.Name = cleanNewName;
                AppendLogUnsafe($"Renamed archive to \"{cleanNewName}\".", RepackerLogLevel.Info);
                return (null, string.Empty);
            }

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);

            // Rename a directory or an .img file
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                if (imgPath is not null && !cleanNewName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) {
                    cleanNewName += ".img";
                }
                if (!session.RenameVirtualNode(norm, cleanNewName)) {
                    return ($"Could not rename \"{norm}\" to \"{cleanNewName}\".", string.Empty);
                }
                string renamedPath = WzPath.Combine(WzPath.Parent(norm), cleanNewName);
                AppendLogUnsafe($"Renamed \"{norm}\" -> \"{renamedPath}\".", RepackerLogLevel.Success);
                return Selected(null, renamedPath);
            }

            // Rename a property inside an .img
            WzValue imgRoot = session.GetOrParseImage(imgPath);
            string parentSubPath = WzPath.Parent(subPath);
            string oldPropName = WzPath.Name(subPath);
            List<WzProperty>? kids = GetMutablePropertyList(string.IsNullOrEmpty(parentSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, parentSubPath));
            if (kids is null) {
                return ("Parent node does not support named properties.", string.Empty);
            }
            if (kids.Any(p => p.Name == cleanNewName && p.Name != oldPropName)) {
                return ($"A sibling property named \"{cleanNewName}\" already exists.", string.Empty);
            }
            int idx = kids.FindIndex(p => p.Name == oldPropName);
            if (idx < 0) {
                return ($"Property \"{oldPropName}\" not found.", string.Empty);
            }

            string newFullPath = WzPath.Combine(imgPath, parentSubPath, cleanNewName);
            string? error = EditImage(session, imgPath, () => {
                kids[idx] = kids[idx] with { Name = cleanNewName };
                return null;
            }, () => $"Renamed property \"{norm}\" -> \"{newFullPath}\".");
            return Selected(error, newFullPath);
        });

    /// <summary>Marks a node deleted (it stays visible, shown as deleted, until saved). Returns the parent path.</summary>
    public (string? Error, string Path) DeleteNode(string archiveId, string path) =>
        WithSessionPath(archiveId, session => {
            string norm = ArchiveSession.NormalizePath(path);
            if (string.IsNullOrEmpty(norm)) {
                return ("Use Close Archive to remove an entire archive.", string.Empty);
            }
            string parentPath = WzPath.Parent(norm);

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                VirtualFsNode? targetNode = session.ResolveVirtualNode(norm);
                string cannotDelete = $"Could not delete node \"{norm}\".";
                string? error = targetNode is null || targetNode.IsDeleted
                    ? cannotDelete
                    : ChangeNodeDeletion(session, norm, imgPath, delete: true, cannotDelete);
                if (error is null) {
                    SelectedNodePath = norm;
                    AppendLogUnsafe($"Deleted \"{norm}\" from \"{session.Name}\".", RepackerLogLevel.Info);
                }
                return (error, parentPath);
            }

            WzValue imgRoot = session.GetOrParseImage(imgPath);
            string parentSubPath = WzPath.Parent(subPath);
            string leafName = WzPath.Name(subPath);
            WzValue? parent = string.IsNullOrEmpty(parentSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, parentSubPath);
            string notFound = $"Property \"{subPath}\" not found.";

            // Convex items are removed outright (they have no name to show as "deleted")
            if (parent is WzValue.Convex(var items) && int.TryParse(leafName, out int cIdx) && cIdx >= 0 && cIdx < items.Count) {
                string? convexError = EditImage(session, imgPath, () => {
                    items.RemoveAt(cIdx);
                    return null;
                }, () => $"Deleted property \"{norm}\".", RepackerLogLevel.Info);
                return Selected(convexError, parentPath);
            }

            List<WzProperty>? kids = GetMutablePropertyList(parent);
            int propIdx = kids?.FindIndex(p => p.Name == leafName) ?? -1;
            if (kids is null || propIdx < 0) {
                return (notFound, string.Empty);
            }

            WzValue deletedVal = CloneWzValue(kids[propIdx].Value);
            string? deleteError = EditImage(session, imgPath, () => {
                kids.RemoveAt(propIdx);
                session.RecordDeletedProperty(imgPath, subPath, parentSubPath, leafName, propIdx, deletedVal);
                return null;
            }, () => $"Deleted property \"{norm}\".", RepackerLogLevel.Info);
            if (deleteError is null) {
                SelectedNodePath = norm;
            }
            return (deleteError, parentPath);
        });

    /// <summary>Restores a deleted node. Returns the restored node's path.</summary>
    public (string? Error, string Path) RestoreNode(string archiveId, string path) =>
        WithSessionPath(archiveId, session => {
            string norm = ArchiveSession.NormalizePath(path);
            if (string.IsNullOrEmpty(norm)) {
                return ("Select a deleted node to restore.", string.Empty);
            }

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                string notRestorable = $"Node \"{norm}\" is not deleted or could not be restored.";
                string? error = session.IsVirtualPathDeleted(norm)
                    ? ChangeNodeDeletion(session, norm, imgPath, delete: false, notRestorable)
                    : notRestorable;
                if (error is null) {
                    AppendLogUnsafe($"Restored \"{norm}\" in \"{session.Name}\".", RepackerLogLevel.Success);
                }
                return Selected(error, norm);
            }

            WzValue imgRoot = session.GetOrParseImage(imgPath);
            string finalPath = norm;
            string? restoreError = EditImage(session, imgPath, () => {
                if (session.IsVirtualPathDeleted(imgPath)) {
                    session.RestoreVirtualNode(imgPath);
                }
                (bool restored, string? restoredSubPath) = session.RestoreDeletedProperty(imgPath, imgRoot, subPath);
                if (!restored) {
                    return $"Property \"{subPath}\" is not deleted or could not be restored.";
                }
                if (!string.IsNullOrEmpty(restoredSubPath)) {
                    finalPath = WzPath.Combine(imgPath, restoredSubPath);
                }
                return null;
            }, () => $"Restored property \"{finalPath}\".");
            return Selected(restoreError, finalPath);
        });

    /// <summary>Duplicates an .img (as <c>name_copy.img</c>) or a property (as <c>name_copy</c>). Returns the copy's path.</summary>
    public (string? Error, string Path) DuplicateNode(string archiveId, string path) =>
        WithSessionPath(archiveId, session => {
            (string? error, NodeTarget<WzValue> target) = ResolveNode<WzValue>(
                session, path, "Select an .img file or property to duplicate.", allowImageRoot: true);
            if (error is not null) {
                return (error, string.Empty);
            }

            // Duplicate an entire .img file
            if (target.IsImageRoot) {
                string dirPart = WzPath.Parent(target.ImagePath);
                string stem = Path.GetFileNameWithoutExtension(target.ImagePath);
                string candidatePath = WzPath.Combine(dirPart, $"{stem}_copy.img");
                for (int n = 2; session.ResolveVirtualNode(candidatePath) is not null; n++) {
                    candidatePath = WzPath.Combine(dirPart, $"{stem}_copy{n}.img");
                }
                session.AddOrReplaceVirtualImage(candidatePath, WzValue.Parse(target.ImageRoot.ToBytes()));
                AppendLogUnsafe($"Duplicated image \"{target.ImagePath}\" -> \"{candidatePath}\".", RepackerLogLevel.Success);
                return Selected(null, candidatePath);
            }

            // Duplicate a property inside the .img
            string parentSubPath = WzPath.Parent(target.SubPath);
            string propName = WzPath.Name(target.SubPath);
            List<WzProperty>? kids = GetMutablePropertyList(string.IsNullOrEmpty(parentSubPath) ? target.ImageRoot : ResolvePropertyNode(target.ImageRoot, parentSubPath));
            if (kids is null) {
                return ("Parent does not support named child properties.", string.Empty);
            }

            string copyName = $"{propName}_copy";
            for (int n = 2; kids.Any(p => p.Name == copyName); n++) {
                copyName = $"{propName}_copy{n}";
            }
            string newFullPath = WzPath.Combine(target.ImagePath, parentSubPath, copyName);
            string? editError = EditImage(session, target.ImagePath, () => {
                kids.Add(new WzProperty(copyName, CloneWzValue(target.Value)));
                return null;
            }, () => $"Duplicated property \"{target.ImagePath}/{target.SubPath}\" -> \"{newFullPath}\".");
            return Selected(editError, newFullPath);
        });

    public (string? Error, ClipboardStateDto Clipboard) CopyNode(string archiveId, string path) =>
        WithSession<(string? Error, ClipboardStateDto Clipboard)>(archiveId, e => (e, GetClipboardStateUnsafe()), session => {
            (string? error, NodeTarget<WzValue> target) = ResolveNode<WzValue>(
                session, path, "Select an .img file or property to copy.", allowImageRoot: true);
            if (error is not null) {
                return (error, GetClipboardStateUnsafe());
            }

            string norm = ArchiveSession.NormalizePath(path);
            if (target.IsImageRoot) {
                string imgLeaf = Path.GetFileName(target.ImagePath);
                byte[] bytes = target.ImageRoot.ToBytes();
                clipboard = new ClipboardEntry(imgLeaf, RepackerNodeKind.Image, RepackerPropertyKind.Object, bytes);
                AppendLogUnsafe($"Copied image \"{imgLeaf}\" ({bytes.Length:N0} B) to clipboard.", RepackerLogLevel.Info);
            } else {
                string propLeaf = WzPath.Name(target.SubPath);
                // Wrap the property in a 1-property Object so any WzValue type serializes cleanly
                byte[] bytes = new WzValue.Object([new WzProperty(propLeaf, target.Value)]).ToBytes();
                RepackerPropertyKind kind = DescribeWzValue(target.Value).Kind;
                clipboard = new ClipboardEntry(propLeaf, RepackerNodeKind.Property, kind, bytes);
                AppendLogUnsafe($"Copied property \"{propLeaf}\" ({kind}) to clipboard.", RepackerLogLevel.Info);
            }
            return (null, GetClipboardStateUnsafe());
        });

    public (string? Error, string Path) PasteNode(
        string targetArchiveId,
        string? targetPath) =>
        WithSessionPath(targetArchiveId, session => {
            if (clipboard is null) {
                return ("Clipboard is empty.", string.Empty);
            }

            string normTarget = ArchiveSession.NormalizePath(targetPath ?? string.Empty);
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(normTarget);

            // A whole .img pasted onto the archive root or a directory
            if (clipboard.NodeKind == RepackerNodeKind.Image && imgPath is null) {
                string targetImgName = clipboard.Name;
                if (!targetImgName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) {
                    targetImgName += ".img";
                }
                string stem = Path.GetFileNameWithoutExtension(targetImgName);
                string destPath = WzPath.Combine(normTarget, targetImgName);
                for (int seq = 2; session.ResolveVirtualNode(destPath) is not null; seq++) {
                    destPath = WzPath.Combine(normTarget, $"{stem}_{seq}.img");
                }
                session.AddOrReplaceVirtualImage(destPath, WzValue.Parse(clipboard.SerializedBytes));
                AppendLogUnsafe($"Pasted image \"{destPath}\" into \"{session.Name}\".", RepackerLogLevel.Success);
                return Selected(null, destPath);
            }

            if (imgPath is null) {
                return ("Select an .img file or property inside an .img file to paste a property.", string.Empty);
            }

            // Paste into the selected container, or next to the selected leaf property
            WzValue imgRoot = session.GetOrParseImage(imgPath);
            WzValue? destParent = string.IsNullOrEmpty(subPath) ? imgRoot : ResolvePropertyNode(imgRoot, subPath);
            string effectiveSubPath = subPath;
            if (destParent is not null and not WzValue.Object and not WzValue.Canvas) {
                effectiveSubPath = WzPath.Parent(subPath);
                destParent = string.IsNullOrEmpty(effectiveSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, effectiveSubPath);
            }
            List<WzProperty>? kids = GetMutablePropertyList(destParent);
            if (kids is null) {
                return ("Destination node cannot hold child properties.", string.Empty);
            }

            WzValue pasted = WzValue.Parse(clipboard.SerializedBytes);
            WzValue unpackedVal = clipboard.NodeKind == RepackerNodeKind.Image || pasted.Children is not { Count: > 0 }
                ? pasted
                : pasted.Children[0].Value;

            string baseName = Path.GetFileNameWithoutExtension(clipboard.Name);
            string finalName = baseName;
            for (int n = 2; kids.Any(p => p.Name == finalName); n++) {
                finalName = $"{baseName}_{n}";
            }

            string pastedFull = WzPath.Combine(imgPath, effectiveSubPath, finalName);
            string? error = EditImage(session, imgPath, () => {
                kids.Add(new WzProperty(finalName, unpackedVal));
                return null;
            }, () => $"Pasted \"{pastedFull}\".");
            return Selected(error, pastedFull);
        });

    // -------------------------------------------------------------------------
    // Canvas, Sound, JSON & Outlink Operations
    // -------------------------------------------------------------------------

    public string? ReplaceCanvasPng(
        string archiveId,
        string path,
        byte[] pngBytes,
        WzCanvasFormat? format = null) =>
        WithSession(archiveId, session => {
            (string? error, NodeTarget<WzValue.Canvas> target) = ResolveNode<WzValue.Canvas>(
                session, path, "Select a Canvas property to replace its image.");
            if (error is not null) {
                return error;
            }

            WzValue.Canvas canvas = target.Value;
            WzCanvasFormat targetFormat = format ?? canvas.Format;
            if (!Enum.IsDefined(targetFormat)) {
                targetFormat = WzCanvasFormat.Bgra8888;
            }
            (int width, int height, byte[] compressed) = WzCanvasCodec.EncodePngToCanvasPayload(pngBytes, targetFormat);

            return EditImage(session, target.ImagePath, () => {
                canvas.Properties.RemoveAll(static p => p.Name is "_outlink" or "_inlink");
                WzValue.Canvas updated = canvas with {
                    Width = width,
                    Height = height,
                    Format = targetFormat,
                    Payload = compressed,
                };
                return TryReplacePropertyNode(target.ImageRoot, target.SubPath, updated)
                    ? null
                    : $"Failed to replace Canvas at \"{target.SubPath}\".";
            }, () => $"Replaced Canvas \"{path}\" with {width}x{height} PNG ({targetFormat.GetDisplayName()}, {compressed.Length:N0} B).");
        });

    public string? ChangeCanvasFormat(string archiveId, string path, WzCanvasFormat newFormat) =>
        WithSession(archiveId, session => {
            (string? error, NodeTarget<WzValue.Canvas> target) = ResolveNode<WzValue.Canvas>(
                session, path, "Select a Canvas property to convert its pixel format.");
            if (error is not null) {
                return error;
            }

            WzValue.Canvas canvas = target.Value;
            byte[] reencoded = WzCanvasCodec.EncodeFromRgba(canvas.Width, canvas.Height, newFormat, WzCanvasCodec.DecodeToRgba(canvas));
            return EditImage(session, target.ImagePath, () => {
                WzValue.Canvas updated = canvas with {
                    Format = newFormat,
                    Payload = reencoded,
                };
                return TryReplacePropertyNode(target.ImageRoot, target.SubPath, updated)
                    ? null
                    : $"Failed to convert Canvas at \"{target.SubPath}\".";
            }, () => $"Converted Canvas \"{path}\" to {newFormat.GetDisplayName()} ({reencoded.Length:N0} B).");
        });

    public string? ReplaceSound(
        string archiveId,
        string path,
        byte[] audioBytes) =>
        WithSession(archiveId, session => {
            (string? error, NodeTarget<WzValue> target) = ResolveNode<WzValue>(
                session, path, "Select a Sound property to replace its audio.");
            if (error is not null) {
                return error;
            }

            WzValue.Sound newSound = WzSoundCodec.CreateFromAudioBytes(audioBytes);
            return EditImage(
                session,
                target.ImagePath,
                () => TryReplacePropertyNode(target.ImageRoot, target.SubPath, newSound) ? null : $"Failed to replace Sound at \"{target.SubPath}\".",
                () => $"Replaced Sound_DX8 \"{path}\" ({audioBytes.Length:N0} B, {newSound.Duration} ms).");
        });

    public string? ApplyJsonSubtree(string archiveId, string path, string jsonText) =>
        WithSession(archiveId, session => {
            (string? error, NodeTarget<WzValue> target) = ResolveNode<WzValue>(
                session, path, "Select an .img file or a property inside an .img file to apply JSON changes.", allowImageRoot: true);
            if (error is not null) {
                return error;
            }

            WzValue updated;
            try {
                updated = JsonWzCodec.Parse(jsonText, target.Value);
            } catch (Exception ex) {
                return $"Invalid JSON: {ex.Message}";
            }

            return EditImage(session, target.ImagePath, () => {
                if (target.IsImageRoot) {
                    session.MarkImageModified(target.ImagePath, updated);
                    return null;
                }
                return TryReplacePropertyNode(target.ImageRoot, target.SubPath, updated) ? null : $"Failed to apply JSON to \"{target.SubPath}\".";
            }, () => $"Applied JSON edits to \"{path}\".");
        });

    /// <summary>Bakes linked (<c>_inlink</c>/<c>_outlink</c>) sprite pixels into the Canvas nodes under a node.</summary>
    public (string? Error, int InlinedCount) InlineOutlinks(string archiveId, string? path = null) =>
        WithSession<(string? Error, int InlinedCount)>(archiveId, static e => (e, 0), session => {
            string norm = ArchiveSession.NormalizePath(path ?? string.Empty);
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);
            int totalInlined = 0;

            if (imgPath is not null) {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                WzValue? target = string.IsNullOrEmpty(subPath) ? imgRoot : ResolvePropertyNode(imgRoot, subPath);
                if (target is null) {
                    return ($"Node \"{norm}\" not found.", 0);
                }
                // One undo step, committed only if something was actually inlined
                ArchiveSession.UndoHistoryEntry undo = session.CaptureImageUndo(imgPath);
                totalInlined = CanvasLinkResolver.InlineLinksRecursive(archives, session, imgRoot, subPath, target);
                if (totalInlined > 0) {
                    session.CommitUndo(undo);
                    session.MarkImageModified(imgPath);
                }
            } else {
                // Inline across all images under a directory / the archive root
                VirtualFsNode? dir = session.ResolveVirtualNode(norm);
                if (dir is null) {
                    return ($"Directory \"{norm}\" not found.", 0);
                }
                foreach (string imagePath in EnumerateLiveImages(session, dir, norm)) {
                    WzValue root = session.GetOrParseImage(imagePath);
                    int count = CanvasLinkResolver.InlineLinksRecursive(archives, session, root, string.Empty, root);
                    if (count > 0) {
                        session.MarkImageModified(imagePath);
                        totalInlined += count;
                    }
                }
            }
            AppendLogUnsafe($"Inlined {totalInlined} linked Canvas sprite(s) in \"{session.Name}\".", RepackerLogLevel.Success);
            return (null, totalInlined);
        });

    /// <summary>Reverts the archive's most recent change. Returns the path of the node it affected.</summary>
    public (string? Error, string AffectedPath) Undo(string archiveId) =>
        WithSessionPath(archiveId, session => session.Undo(out string affected)
            ? LoggedHistoryStep($"Undid last change in \"{affected}\".", affected)
            : ("Nothing to undo.", string.Empty));

    /// <summary>Re-applies the archive's most recently undone change. Returns the path of the node it affected.</summary>
    public (string? Error, string AffectedPath) Redo(string archiveId) =>
        WithSessionPath(archiveId, session => session.Redo(out string affected)
            ? LoggedHistoryStep($"Redid change in \"{affected}\".", affected)
            : ("Nothing to redo.", string.Empty));

    private (string? Error, string Path) LoggedHistoryStep(string message, string affectedPath) {
        AppendLogUnsafe(message, RepackerLogLevel.Info);
        return (null, affectedPath);
    }

    // -------------------------------------------------------------------------
    // Global Search & Export
    // -------------------------------------------------------------------------

    public SearchResultItemDto[] Search(SearchQueryDto query) {
        ArgumentNullException.ThrowIfNull(query);
        int maxResults = Math.Clamp(query.MaxResults, 1, 2000);
        string nameFilter = (query.Query ?? string.Empty).Trim();
        string valFilter = (query.ValueQuery ?? string.Empty).Trim();
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;

        var results = new List<SearchResultItemDto>();

        lock (syncLock) {
            IEnumerable<ArchiveSession> targetSessions = !string.IsNullOrWhiteSpace(query.ArchiveId)
                ? archives.Where(a => a.ArchiveId == query.ArchiveId)
                : archives;

            foreach (ArchiveSession session in targetSessions) {
                if (results.Count >= maxResults) break;
                WorkspaceSearch.SearchDirectory(session, session.VirtualRoot, "", nameFilter, valFilter, query.PropertyKindFilter, query.ExactMatch, cmp, maxResults, results);
            }
        }

        return [.. results];
    }

    /// <summary>
    /// Exports a node as a downloadable file: the whole archive (.wz), an .img, JSON, a Canvas as PNG or a Sound as audio.
    /// <see cref="RepackerExportFormat.Auto"/> picks the most natural format for the node.
    /// </summary>
    public (string? Error, string FileName, string MimeType, byte[] Data) ExportNode(
        string archiveId,
        string? path,
        RepackerExportFormat format = RepackerExportFormat.Auto) =>
        WithSession<(string? Error, string FileName, string MimeType, byte[] Data)>(archiveId, static e => (e, string.Empty, string.Empty, []), session => {
            const string binaryMime = "application/octet-stream";
            string norm = ArchiveSession.NormalizePath(path ?? string.Empty);
            if (string.IsNullOrEmpty(norm) || format == RepackerExportFormat.Wz) {
                return (null, session.Name, binaryMime, session.BuildArchiveBytes());
            }

            (string? error, NodeTarget<WzValue> target) = ResolveNode<WzValue>(
                session, norm, "Select an .img file or property to export.", allowImageRoot: true);
            if (error is not null) {
                return (error, string.Empty, string.Empty, []);
            }

            string baseName = target.IsImageRoot ? Path.GetFileNameWithoutExtension(target.ImagePath) : target.SubPath.Replace('/', '_');
            if (format == RepackerExportFormat.Img || (format == RepackerExportFormat.Auto && target.IsImageRoot)) {
                string imgFileName = baseName.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? baseName : $"{baseName}.img";
                return (null, imgFileName, binaryMime, target.Value.ToBytes());
            }
            if (format == RepackerExportFormat.Json) {
                return (null, $"{baseName}.json", "application/json", Encoding.UTF8.GetBytes(target.Value.ToJson()));
            }

            WzValue value = target.Value;
            if (value is WzValue.Uol(var uolStr) && ResolveUolTarget(target.ImageRoot, target.SubPath, uolStr).Target is { } uolResolved) {
                value = uolResolved;
            }
            if (value is WzValue.Canvas canvas && format is RepackerExportFormat.Png or RepackerExportFormat.Auto) {
                CanvasPreviewDto preview = CanvasLinkResolver.BuildCanvasPreview(archives, session, target.ImagePath, target.ImageRoot, canvas);
                return (null, $"{baseName}.png", "image/png", preview.PngBytes);
            }
            if (value is WzValue.Sound sound && format is RepackerExportFormat.Audio or RepackerExportFormat.Auto) {
                ExtractedSoundData audio = WzSoundCodec.ExtractAudioStream(sound);
                return (null, $"{baseName}.{audio.Extension}", audio.MimeType, audio.AudioBytes);
            }

            // Anything else exports as JSON
            return (null, $"{baseName}.json", "application/json", Encoding.UTF8.GetBytes(value.ToJson()));
        }, mutates: false);

    /// <summary>Writes every matching asset under a node to <paramref name="outputDirectory"/>, mirroring the WZ paths.</summary>
    public (string? Error, int ExportedCount) BatchExportToDirectory(
        string archiveId,
        string? path,
        string outputDirectory,
        RepackerBatchExportMode mode = RepackerBatchExportMode.Png) {
        if (string.IsNullOrWhiteSpace(outputDirectory)) {
            return ("Output directory is required.", 0);
        }

        return WithSession<(string? Error, int ExportedCount)>(archiveId, static e => (e, 0), session => {
            try {
                string fullOutDir = Path.GetFullPath(outputDirectory.Trim());
                Directory.CreateDirectory(fullOutDir);
                string norm = ArchiveSession.NormalizePath(path ?? string.Empty);
                (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);

                int exported = 0;

                void WriteFile(string destFile, byte[] bytes) {
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile) ?? fullOutDir);
                    System.IO.File.WriteAllBytes(destFile, bytes);
                    exported++;
                }

                void ExportFromImage(string currentImgPath, string startSubPath) {
                    WzValue imgRoot = session.GetOrParseImage(currentImgPath);
                    string imgRelDir = Path.Combine(fullOutDir, currentImgPath.Replace('/', Path.DirectorySeparatorChar));
                    WzValue startNode = string.IsNullOrEmpty(startSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, startSubPath) ?? imgRoot;

                    if (mode == RepackerBatchExportMode.Img) {
                        WriteFile(imgRelDir, imgRoot.ToBytes());
                        return;
                    }
                    if (mode == RepackerBatchExportMode.Json) {
                        string jsonFile = string.IsNullOrEmpty(startSubPath)
                            ? $"{imgRelDir}.json"
                            : Path.Combine(imgRelDir, $"{startSubPath.Replace('/', '_')}.json");
                        WriteFile(jsonFile, Encoding.UTF8.GetBytes(startNode.ToJson()));
                        return;
                    }

                    void WalkProperties(WzValue node, string propPrefix) {
                        string safeRel = propPrefix.Replace('/', Path.DirectorySeparatorChar);
                        if (node is WzValue.Canvas canvas && mode is RepackerBatchExportMode.Png or RepackerBatchExportMode.All) {
                            CanvasPreviewDto preview = CanvasLinkResolver.BuildCanvasPreview(archives, session, currentImgPath, imgRoot, canvas);
                            WriteFile(Path.Combine(imgRelDir, $"{(safeRel.Length > 0 ? safeRel : "canvas")}.png"), preview.PngBytes);
                        } else if (node is WzValue.Sound sound && mode is RepackerBatchExportMode.Sound or RepackerBatchExportMode.All) {
                            ExtractedSoundData audio = WzSoundCodec.ExtractAudioStream(sound);
                            WriteFile(Path.Combine(imgRelDir, $"{(safeRel.Length > 0 ? safeRel : "sound")}.{audio.Extension}"), audio.AudioBytes);
                        }

                        if (node.Children is { Count: > 0 } children) {
                            foreach (WzProperty child in children) {
                                WalkProperties(child.Value, WzPath.Combine(propPrefix, child.Name));
                            }
                        }
                    }

                    WalkProperties(startNode, startSubPath);
                }

                if (imgPath is not null) {
                    ExportFromImage(imgPath, subPath);
                } else {
                    VirtualFsNode? dir = session.ResolveVirtualNode(norm);
                    if (dir is null) {
                        return ($"Node \"{norm}\" not found.", 0);
                    }
                    foreach (string imagePath in EnumerateLiveImages(session, dir, norm)) {
                        ExportFromImage(imagePath, string.Empty);
                    }
                }

                AppendLogUnsafe($"Batch-exported {exported} file(s) ({mode}) to \"{fullOutDir}\".", RepackerLogLevel.Success);
                return (null, exported);
            } catch (Exception ex) {
                AppendLogUnsafe($"Batch export failed: {ex.Message}", RepackerLogLevel.Error);
                return (ex.Message, 0);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Operation helpers
    //
    // Convention: operations return their error message first (null = success), optionally followed by values,
    // e.g. (string? Error, string Path). WithSession relies on this to tell success from failure.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="action"/> under the workspace lock with the archive's session. Returns
    /// <paramref name="fail"/>(message) when the archive is missing or the action throws, and bumps
    /// <see cref="StateVersion"/> when a <paramref name="mutates"/> action succeeds.
    /// </summary>
    private TResult WithSession<TResult>(string archiveId, Func<string, TResult> fail, Func<ArchiveSession, TResult> action, bool mutates = true) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return fail($"Archive \"{archiveId}\" not found.");
            }

            TResult result;
            try {
                result = action(session);
            } catch (Exception ex) {
                return fail(ex.Message);
            }

            if (mutates && Succeeded(result)) {
                NotifyStateChanged();
            }
            return result;
        }
    }

    /// <summary><see cref="WithSession{TResult}"/> for operations that only return an error (null = success).</summary>
    private string? WithSession(string archiveId, Func<ArchiveSession, string?> action) =>
        WithSession(archiveId, static error => error, action);

    /// <summary><see cref="WithSession{TResult}"/> for operations that return the path to select next.</summary>
    private (string? Error, string Path) WithSessionPath(string archiveId, Func<ArchiveSession, (string? Error, string Path)> action) =>
        WithSession(archiveId, static error => (error, string.Empty), action);

    private static bool Succeeded<TResult>(TResult result) => result switch {
        null => true,
        string => false,
        ITuple tuple => tuple.Length == 0 || tuple[0] is null,
        _ => true,
    };

    /// <summary>Selects <paramref name="path"/> if the operation succeeded, and returns the operation result.</summary>
    private (string? Error, string Path) Selected(string? error, string path) {
        if (error is null) {
            SelectedNodePath = path;
        }
        return (error, path);
    }

    /// <summary>A path resolved to its .img, the property path inside it, the parsed image, and the node itself.</summary>
    private readonly record struct NodeTarget<T>(string ImagePath, string SubPath, WzValue ImageRoot, T Value) where T : WzValue {
        public bool IsImageRoot => SubPath.Length == 0;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to a node of type <typeparamref name="T"/> inside an .img. Fails with
    /// <paramref name="selectHint"/> when the path is not inside an .img (or is the .img itself, unless
    /// <paramref name="allowImageRoot"/>), when the property does not exist, or when it is not a <typeparamref name="T"/>.
    /// </summary>
    private static (string? Error, NodeTarget<T> Target) ResolveNode<T>(
        ArchiveSession session,
        string path,
        string selectHint,
        bool allowImageRoot = false) where T : WzValue {
        (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(path);
        if (imgPath is null || (!allowImageRoot && string.IsNullOrEmpty(subPath))) {
            return (selectHint, default);
        }

        WzValue imgRoot = session.GetOrParseImage(imgPath);
        WzValue? node = string.IsNullOrEmpty(subPath) ? imgRoot : ResolvePropertyNode(imgRoot, subPath);
        return node switch {
            null => ($"Property \"{subPath}\" not found.", default),
            T typed => (null, new NodeTarget<T>(imgPath, subPath, imgRoot, typed)),
            _ => ($"Property \"{subPath}\" is not a {typeof(T).Name}.", default),
        };
    }

    /// <summary>
    /// Applies an edit to one .img as a single undo step. The image state is captured first; only if
    /// <paramref name="edit"/> succeeds (returns null) is the undo step committed, the image marked modified, and
    /// <paramref name="successLog"/> logged. A failed edit leaves no undo entry behind.
    /// </summary>
    private string? EditImage(
        ArchiveSession session,
        string imagePath,
        Func<string?> edit,
        Func<string> successLog,
        RepackerLogLevel level = RepackerLogLevel.Success) {
        ArchiveSession.UndoHistoryEntry undo = session.CaptureImageUndo(imagePath);
        string? error = edit();
        if (error is not null) {
            return error;
        }
        session.CommitUndo(undo);
        session.MarkImageModified(imagePath);
        AppendLogUnsafe(successLog(), level);
        return null;
    }

    /// <summary>
    /// Deletes or restores a directory or .img node as a single undo step. Returns <paramref name="failMessage"/> (and
    /// records nothing) if the node did not change.
    /// </summary>
    private static string? ChangeNodeDeletion(ArchiveSession session, string nodePath, string? imagePath, bool delete, string failMessage) {
        ArchiveSession.UndoHistoryEntry undo = imagePath is not null
            ? session.CaptureImageUndo(imagePath)
            : session.CaptureDirectoryUndo(nodePath);
        bool changed = delete ? session.RemoveVirtualNode(nodePath) : session.RestoreVirtualNode(nodePath);
        if (!changed) {
            return failMessage;
        }
        session.CommitUndo(undo);
        return null;
    }

    /// <summary>Paths of every non-deleted .img under <paramref name="dir"/>, linking split/subdirectory archives as needed.</summary>
    private static IEnumerable<string> EnumerateLiveImages(ArchiveSession session, VirtualFsNode dir, string dirPath) {
        session.EnsureDirectoryLinked(dir);
        foreach (VirtualFsNode child in dir.Children.ToArray()) {
            if (child.IsDeleted) {
                continue;
            }
            string childPath = WzPath.Combine(dirPath, child.Name);
            if (child.IsDir) {
                foreach (string nested in EnumerateLiveImages(session, child, childPath)) {
                    yield return nested;
                }
            } else if (child.IsImage) {
                yield return childPath;
            }
        }
    }

    /// <summary>Builds the value for a newly added property, including blank/PNG-backed Canvases and Sounds.</summary>
    private static WzValue CreateNewPropertyValue(
        RepackerPropertyKind kind,
        string? rawValue,
        int? vecX,
        int? vecY,
        byte[]? binaryPayload,
        WzCanvasFormat? canvasFormat) {
        if (kind == RepackerPropertyKind.Sound) {
            return WzSoundCodec.CreateFromAudioBytes(binaryPayload is { Length: > 0 } ? binaryPayload : []);
        }
        if (kind != RepackerPropertyKind.Canvas) {
            return CreatePrimitiveOrStructuredValue(kind, rawValue, vecX, vecY, null);
        }

        WzCanvasFormat fmt = canvasFormat ?? WzCanvasFormat.Bgra8888;
        if (binaryPayload is { Length: > 0 }) {
            (int w, int h, byte[] comp) = WzCanvasCodec.EncodePngToCanvasPayload(binaryPayload, fmt);
            return new WzValue.Canvas(w, h, fmt, comp, [new WzProperty("origin", (vecX ?? w / 2, vecY ?? h))]);
        }
        const int blankSize = 32;
        byte[] blank = WzCanvasCodec.EncodeFromRgba(blankSize, blankSize, fmt, new byte[blankSize * blankSize * 4]);
        return new WzValue.Canvas(blankSize, blankSize, fmt, blank, [new WzProperty("origin", (vecX ?? 16, vecY ?? 32))]);
    }

    /// <summary>The directory a new .img or directory goes into: the path itself, or the directory of the .img it is inside.</summary>
    private static string ContainingDirectory(string? path) {
        string norm = ArchiveSession.NormalizePath(path ?? string.Empty);
        (string? imgPath, _) = ArchiveSession.SplitImageAndSubPath(norm);
        return imgPath is null ? norm : WzPath.Parent(imgPath);
    }

    /// <summary>
    /// Looks up an open session by id (or the first session when <paramref name="archiveId"/> is empty).
    /// Caller must hold <see cref="syncLock"/>.
    /// </summary>
    private ArchiveSession? FindSessionUnsafe(string? archiveId) {
        AssertLockHeld();
        if (string.IsNullOrWhiteSpace(archiveId)) {
            return archives.FirstOrDefault();
        }
        return archives.Find(a => a.ArchiveId == archiveId);
    }

    public void Dispose() {
        lock (syncLock) {
            foreach (ArchiveSession s in archives) {
                s.Dispose();
            }
            archives.Clear();
        }
    }
}
