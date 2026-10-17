using System.Diagnostics;
using System.Globalization;
using System.Text;
using Maple.Classic.File.IO;
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
    private const int MaxLogEntries = 1000;

    private readonly Lock syncLock = new();
    private readonly List<ArchiveSession> archives = [];
    private readonly List<RepackerLogEntry> logs = [];

    private long stateVersion = 1;
    private long nextLogId = 1;
    private int nextArchiveSeq = 1;

    private ClipboardEntry? clipboard;

    private sealed record ClipboardEntry(
        string SourceArchiveId,
        string SourcePath,
        string Name,
        RepackerNodeKind NodeKind,
        RepackerPropertyKind PropertyKind,
        byte[] SerializedBytes);

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
    private void AssertLockHeld([System.Runtime.CompilerServices.CallerMemberName] string caller = "") {
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
        if (logs.Count > MaxLogEntries) {
            logs.RemoveRange(0, logs.Count - MaxLogEntries);
        }
    }

    public IReadOnlyList<RepackerLogEntry> GetLogs() {
        lock (syncLock) {
            return [.. logs];
        }
    }

    public void ClearLogs() {
        lock (syncLock) {
            logs.Clear();
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
            if (clipboard is null) {
                return new ClipboardStateDto(false, null, null, null, RepackerNodeKind.Property, RepackerPropertyKind.None);
            }
            return new ClipboardStateDto(
                true,
                clipboard.SourceArchiveId,
                clipboard.SourcePath,
                clipboard.Name,
                clipboard.NodeKind,
                clipboard.PropertyKind);
        }
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
            ArchiveSession? existing = FindExistingOrLinkedSessionByPathUnsafe(fullPath);
            if (existing is not null) {
                int idx = archives.IndexOf(existing);
                string reloadPath = ArchivePathResolver.ChoosePreferredRootArchivePath(existing.FilePath, fullPath);
                string reloadName = Path.GetFileName(reloadPath);
                WzArchive reloadWz = string.Equals(reloadPath, fullPath, StringComparison.OrdinalIgnoreCase)
                    ? wz
                    : new WzArchive(reloadPath, version);
                if (!ReferenceEquals(reloadWz, wz)) {
                    wz.Dispose();
                }

                existing.Dispose();
                var replacement = new ArchiveSession(existing.ArchiveId, reloadName, reloadPath, reloadWz, version);
                archives[idx] = replacement;
                RemoveSubsumedSessionsUnsafe(replacement, idx);
                SelectedArchiveId = replacement.ArchiveId;
                SelectedNodePath = string.Empty;
                string linkedNote = replacement.LinkedArchiveCount > 0 ? $", +{replacement.LinkedArchiveCount} linked .wz file(s)" : string.Empty;
                AppendLogUnsafe($"Reloaded archive \"{reloadName}\" (v{reloadWz.Version}, encVer {reloadWz.Header.EncVer}{linkedNote}) from {reloadPath}.", RepackerLogLevel.Success);
                NotifyStateChanged();
                return replacement.ToSummaryDto();
            }

            string id = $"wz_{nextArchiveSeq++}";
            var session = new ArchiveSession(id, fileName, fullPath, wz, version);
            ArchiveSession? subsumed = archives.Find(a =>
                !string.IsNullOrEmpty(a.FilePath) &&
                session.HasLinkedOrSubdirectoryFile(a.FilePath));
            if (subsumed is not null) {
                int idx = archives.IndexOf(subsumed);
                string reusedId = subsumed.ArchiveId;
                subsumed.Dispose();
                session.Dispose();
                var reopenWz = new WzArchive(fullPath, version);
                var replacement = new ArchiveSession(reusedId, fileName, fullPath, reopenWz, version);
                archives[idx] = replacement;
                RemoveSubsumedSessionsUnsafe(replacement, idx);
                SelectedArchiveId = replacement.ArchiveId;
                SelectedNodePath = string.Empty;
                string linkedNote = replacement.LinkedArchiveCount > 0 ? $", +{replacement.LinkedArchiveCount} linked .wz file(s)" : string.Empty;
                AppendLogUnsafe($"Reloaded archive \"{fileName}\" (v{reopenWz.Version}, encVer {reopenWz.Header.EncVer}{linkedNote}) from {fullPath}.", RepackerLogLevel.Success);
                NotifyStateChanged();
                return replacement.ToSummaryDto();
            }

            archives.Add(session);
            SelectedArchiveId = id;
            SelectedNodePath = string.Empty;
            string openLinkedNote = session.LinkedArchiveCount > 0 ? $", +{session.LinkedArchiveCount} linked .wz file(s)" : string.Empty;
            AppendLogUnsafe($"Opened archive \"{fileName}\" (v{wz.Version}, encVer {wz.Header.EncVer}{openLinkedNote}) from {fullPath}.", RepackerLogLevel.Success);
            NotifyStateChanged();
            return session.ToSummaryDto();
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
                : ArchivePathResolver.TryResolveDiskPath(archives.Select(static a => a.FilePath), cleanName, data.Length, hintPaths) ?? string.Empty;
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
                int idx = archives.IndexOf(existing);
                string reloadPath = !string.IsNullOrWhiteSpace(resolvedPath)
                    ? ArchivePathResolver.ChoosePreferredRootArchivePath(existing.FilePath, resolvedPath)
                    : string.Empty;
                string reloadName = !string.IsNullOrWhiteSpace(reloadPath)
                    ? Path.GetFileName(reloadPath)
                    : cleanName;
                WzArchive reloadWz = !string.IsNullOrWhiteSpace(reloadPath) &&
                                     !string.Equals(reloadPath, resolvedPath, StringComparison.OrdinalIgnoreCase) &&
                                     System.IO.File.Exists(reloadPath)
                    ? new WzArchive(reloadPath, version)
                    : wz;
                if (!ReferenceEquals(reloadWz, wz)) {
                    wz.Dispose();
                }

                existing.Dispose();
                var replacement = new ArchiveSession(existing.ArchiveId, reloadName, reloadPath, reloadWz, version);
                archives[idx] = replacement;
                RemoveSubsumedSessionsUnsafe(replacement, idx);
                SelectedArchiveId = replacement.ArchiveId;
                SelectedNodePath = string.Empty;
                string linkedNote = replacement.LinkedArchiveCount > 0 ? $", +{replacement.LinkedArchiveCount} linked .wz file(s)" : string.Empty;
                AppendLogUnsafe($"Reloaded archive \"{reloadName}\" ({data.Length:N0} bytes, v{reloadWz.Version}{linkedNote}).", RepackerLogLevel.Success);
                NotifyStateChanged();
                return replacement.ToSummaryDto();
            }

            string id = $"wz_{nextArchiveSeq++}";
            var session = new ArchiveSession(id, cleanName, resolvedPath, wz, version);
            archives.Add(session);
            SelectedArchiveId = id;
            SelectedNodePath = string.Empty;
            string openLinkedNote = session.LinkedArchiveCount > 0 ? $", +{session.LinkedArchiveCount} linked .wz file(s)" : string.Empty;
            AppendLogUnsafe($"Loaded archive \"{cleanName}\" ({data.Length:N0} bytes, v{wz.Version}{openLinkedNote}).", RepackerLogLevel.Success);
            NotifyStateChanged();
            return session.ToSummaryDto();
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

    public ArchiveSummaryDto CreateNewArchive(string name, ushort version = WzArchive.DefaultVersion) {
        string cleanName = string.IsNullOrWhiteSpace(name) ? $"NewArchive_{nextArchiveSeq}.wz" : name.Trim();
        if (!cleanName.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)) {
            cleanName += ".wz";
        }
        var wz = new WzArchive(version);

        lock (syncLock) {
            string id = $"wz_{nextArchiveSeq++}";
            var session = new ArchiveSession(id, cleanName, string.Empty, wz) {
                HasStructureChanges = true,
                IsUserCreatedArchive = true,
            };
            archives.Add(session);
            SelectedArchiveId = id;
            SelectedNodePath = string.Empty;
            AppendLogUnsafe($"Created new empty WZ archive \"{cleanName}\" (v{version}).", RepackerLogLevel.Info);
            NotifyStateChanged();
            return session.ToSummaryDto();
        }
    }

    public (bool Ok, string? Error, ArchiveSummaryDto? Summary) SaveArchiveToDisk(
        string archiveId,
        string? targetFilePath = null,
        ushort? version = null) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", null);
            }

            string savePath = !string.IsNullOrWhiteSpace(targetFilePath) ? targetFilePath.Trim() : session.FilePath;
            if (string.IsNullOrWhiteSpace(savePath)) {
                return (false, "A file path is required to save an in-memory archive to disk.", null);
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
                NotifyStateChanged();
                return (true, null, session.ToSummaryDto());
            } catch (Exception ex) {
                AppendLogUnsafe($"Failed to save \"{session.Name}\": {ex.Message}", RepackerLogLevel.Error);
                return (false, ex.Message, null);
            }
        }
    }

    public (bool Ok, string? Error, string FileName, byte[] Data) ExportArchiveBytes(
        string archiveId,
        ushort? version = null,
        bool commitClean = false) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", string.Empty, []);
            }
            try {
                byte[] bytes = session.BuildArchiveBytes(version, commitCleanState: commitClean);
                AppendLogUnsafe($"Repacked archive \"{session.Name}\" ({bytes.Length:N0} bytes, v{version ?? session.Archive.Version}).", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, session.Name, bytes);
            } catch (Exception ex) {
                AppendLogUnsafe($"Failed to repack \"{session.Name}\": {ex.Message}", RepackerLogLevel.Error);
                return (false, ex.Message, string.Empty, []);
            }
        }
    }

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

    public void CloseAllArchives() {
        lock (syncLock) {
            foreach (ArchiveSession s in archives) {
                s.Dispose();
            }
            archives.Clear();
            SelectedArchiveId = null;
            SelectedNodePath = string.Empty;
            AppendLogUnsafe("Closed all archives.", RepackerLogLevel.Info);
        }
        NotifyStateChanged();
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

                bool parentDeleted = session.IsVirtualPathDeleted(norm);
                var result = new List<TreeNodeDto>(dirNode.Children.Count);
                foreach (VirtualFsNode child in dirNode.Children) {
                    string childPath = string.IsNullOrEmpty(norm) ? child.Name : $"{norm}/{child.Name}";
                    bool childDeleted = parentDeleted || child.IsDeleted;
                    if (child.IsDir) {
                        (int d, int i) = ArchiveSession.CountVirtualRecursive(child);
                        RepackerChangeState dirState = childDeleted
                            ? RepackerChangeState.Deleted
                            : session.GetDirectoryChangeState(childPath, child);
                        result.Add(new TreeNodeDto(
                            session.ArchiveId,
                            childPath,
                            child.Name,
                            RepackerNodeKind.Directory,
                            RepackerPropertyKind.None,
                            $"{d + i} items",
                            "DIR",
                            child.Children.Count > 0,
                            child.Children.Count,
                            dirState,
                            0));
                    } else {
                        int sizeBytes = session.GetImageSize(childPath);
                        RepackerChangeState imgState = session.GetImageChangeState(childPath, childDeleted);
                        result.Add(new TreeNodeDto(
                            session.ArchiveId,
                            childPath,
                            child.Name,
                            RepackerNodeKind.Image,
                            RepackerPropertyKind.Object,
                            FormatByteSize(sizeBytes),
                            "IMG",
                            true,
                            0,
                            imgState,
                            sizeBytes));
                    }
                }
                return [.. result];
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

                bool effectiveParentDeleted = imgDeleted || targetDeleted;

                if (target is WzValue.Convex(var convexItems)) {
                    var convexNodes = new List<TreeNodeDto>(convexItems.Count);
                    for (int i = 0; i < convexItems.Count; i++) {
                        WzValue item = convexItems[i];
                        string itemName = i.ToString(CultureInfo.InvariantCulture);
                        string itemSubPath = string.IsNullOrEmpty(subPath) ? itemName : $"{subPath}/{itemName}";
                        string itemPath = $"{imgPath}/{itemSubPath}";
                        RepackerChangeState itemState = EvaluatePropertyChangeState(session, imgPath, itemSubPath, item, effectiveParentDeleted);
                        convexNodes.Add(BuildPropertyTreeNodeDto(session, imgPath, itemSubPath, itemPath, itemName, item, itemState));
                    }
                    return [.. convexNodes];
                }

                List<(string Name, string SubPath, WzValue Value, bool IsDeleted)> mergedProps =
                    BuildMergedChildProperties(session, imgPath, subPath, target, effectiveParentDeleted);
                if (mergedProps.Count == 0) {
                    return [];
                }

                var propNodes = new List<TreeNodeDto>(mergedProps.Count);
                foreach ((string propName, string propSubPath, WzValue propVal, bool propDeleted) in mergedProps) {
                    string propPath = $"{imgPath}/{propSubPath}";
                    RepackerChangeState propState = EvaluatePropertyChangeState(session, imgPath, propSubPath, propVal, propDeleted);
                    propNodes.Add(BuildPropertyTreeNodeDto(session, imgPath, propSubPath, propPath, propName, propVal, propState));
                }
                return [.. propNodes];
            } catch {
                return [];
            }
        }
    }

    public NodeInspectionDto InspectNode(string archiveId, string? path, bool includeAnimation = true) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return ErrorInspection(archiveId, path ?? string.Empty, $"Archive \"{archiveId}\" not found.");
            }

            string norm = ArchiveSession.NormalizePath(path ?? string.Empty);
            SelectedArchiveId = session.ArchiveId;
            SelectedNodePath = norm;

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);

            // Case 1: Archive root or Directory node
            if (imgPath is null) {
                VirtualFsNode? dirNode = session.ResolveVirtualNode(norm);
                if (dirNode is null) {
                    return ErrorInspection(archiveId, norm, $"Node \"{norm}\" not found in \"{session.Name}\".");
                }

                if (session.EnsureDirectoryLinkedWithChildren(dirNode)) {
                    NotifyStateChanged();
                }

                bool isRoot = string.IsNullOrEmpty(norm);
                bool isDirDeleted = !isRoot && session.IsVirtualPathDeleted(norm);
                RepackerNodeKind nodeKind = isRoot ? RepackerNodeKind.Archive : RepackerNodeKind.Directory;
                string badge = isRoot ? "WZ" : "DIR";
                string displayName = isRoot ? session.Name : dirNode.Name;
                (int dCount, int iCount) = ArchiveSession.CountVirtualRecursive(dirNode);

                var rows = new List<SubPropertyRowDto>(dirNode.Children.Count);
                foreach (VirtualFsNode c in dirNode.Children) {
                    string cPath = isRoot ? c.Name : $"{norm}/{c.Name}";
                    bool cDeleted = isDirDeleted || c.IsDeleted;
                    if (c.IsDir) {
                        (int cd, int ci) = ArchiveSession.CountVirtualRecursive(c);
                        RepackerChangeState cState = cDeleted
                            ? RepackerChangeState.Deleted
                            : session.GetDirectoryChangeState(cPath, c);
                        rows.Add(new SubPropertyRowDto(
                            c.Name,
                            cPath,
                            RepackerNodeKind.Directory,
                            RepackerPropertyKind.None,
                            "DIR",
                            $"{cd} dirs, {ci} images",
                            c.Children.Count,
                            0,
                            cState));
                    } else {
                        int sz = session.GetImageSize(cPath);
                        RepackerChangeState cState = session.GetImageChangeState(cPath, cDeleted);
                        rows.Add(new SubPropertyRowDto(
                            c.Name,
                            cPath,
                            RepackerNodeKind.Image,
                            RepackerPropertyKind.Object,
                            "IMG",
                            FormatByteSize(sz),
                            0,
                            sz,
                            cState));
                    }
                }

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

                return new NodeInspectionDto(
                    true,
                    null,
                    session.ArchiveId,
                    session.Name,
                    norm,
                    displayName,
                    null,
                    nodeKind,
                    RepackerPropertyKind.None,
                    badge,
                    $"{dCount} directories, {iCount} images",
                    null,
                    null,
                    null,
                    null,
                    dirNode.Children.Count,
                    0,
                    dirChangeState,
                    session.CanUndo(norm),
                    session.CanRedo(norm),
                    summaryJson,
                    [.. rows],
                    null,
                    [],
                    null,
                    null,
                    []);
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
                    return ErrorInspection(archiveId, norm, $"Property \"{subPath}\" not found inside \"{imgPath}\".");
                }
                bool isNodeDeleted = imgDeleted || propDeleted;

                string leafName = isImageRoot
                    ? Path.GetFileName(imgPath)
                    : subPath.Contains('/') ? subPath[(subPath.LastIndexOf('/') + 1)..] : subPath;

                RepackerNodeKind nodeKind = isImageRoot ? RepackerNodeKind.Image : RepackerNodeKind.Property;
                (RepackerPropertyKind propKind, string typeBadge, string displayValue) = DescribeWzValue(target);
                if (isImageRoot) {
                    typeBadge = "IMG";
                }

                string? rawStr = target.AsString();
                long? numInt = target is WzValue.Short or WzValue.Int or WzValue.Long ? target.AsLong() : null;
                double? numFloat = target is WzValue.Float or WzValue.Double ? target.AsDouble() : null;
                VectorPointDto? vecDto = target is WzValue.Vector(var vx, var vy) ? new VectorPointDto(vx, vy) : null;

                SubPropertyRowDto[] childRows = BuildChildRows(session, imgPath, subPath, target, isNodeDeleted);
                int ownChildCount = childRows.Length;
                if (ownChildCount == 0 && !isImageRoot && target is not WzValue.Object and not WzValue.Convex) {
                    int lastSlash = subPath.LastIndexOf('/');
                    string parentSubPath = lastSlash >= 0 ? subPath[..lastSlash] : string.Empty;
                    (WzValue? parentVal, bool parentDel) = string.IsNullOrEmpty(parentSubPath)
                        ? (imgRoot, imgDeleted)
                        : session.ResolvePropertyOrDeleted(imgPath, imgRoot, parentSubPath);
                    if (parentVal is not null) {
                        childRows = BuildChildRows(session, imgPath, parentSubPath, parentVal, imgDeleted || parentDel);
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
                        resolvedUolPath = $"{imgPath}/{uolResolvedSubPath}";
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
                            WzCanvasFormatExtensions.GetDisplayName(f0.Format),
                            f0.PngBytes.Length,
                            f0.PngBytes,
                            f0.Origin,
                            f0.Lt,
                            f0.Rb,
                            f0.Head,
                            f0.DelayMs,
                            null,
                            null,
                            null,
                            null,
                            false);
                    }
                }

                // Sound preview (if target or UOL target is Sound_DX8)
                SoundPreviewDto? soundDto = null;
                if (effectiveVisualTarget is WzValue.Sound soundVal) {
                    ExtractedSoundData extracted = WzSoundCodec.ExtractAudioStream(soundVal);
                    soundDto = new SoundPreviewDto(
                        soundVal.DataLength,
                        soundVal.Duration,
                        extracted.MimeType,
                        extracted.Extension,
                        extracted.FormatName,
                        extracted.SampleRate,
                        extracted.Channels,
                        extracted.BitsPerSample,
                        extracted.AudioBytes,
                        soundVal.Header.ToArray());
                }

                // If inspecting a container node with no direct preview yet, preview its first previewable child node
                if (canvasDto is null && soundDto is null && effectiveVisualTarget is not WzValue.String && effectiveVisualTarget.Children is { Count: > 0 } containerChildren) {
                    foreach (WzProperty childProp in containerChildren) {
                        string childSubPath = string.IsNullOrEmpty(effectiveSubPath) ? childProp.Name : $"{effectiveSubPath}/{childProp.Name}";
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
                            ExtractedSoundData extracted = WzSoundCodec.ExtractAudioStream(childSound);
                            soundDto = new SoundPreviewDto(
                                childSound.DataLength,
                                childSound.Duration,
                                extracted.MimeType,
                                extracted.Extension,
                                extracted.FormatName,
                                extracted.SampleRate,
                                extracted.Channels,
                                extracted.BitsPerSample,
                                extracted.AudioBytes,
                                childSound.Header.ToArray());
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

                return new NodeInspectionDto(
                    true,
                    null,
                    session.ArchiveId,
                    session.Name,
                    norm,
                    leafName,
                    imgPath,
                    nodeKind,
                    propKind,
                    typeBadge,
                    displayValue,
                    rawStr,
                    numInt,
                    numFloat,
                    vecDto,
                    ownChildCount,
                    sizeBytes,
                    nodeChangeState,
                    session.CanUndo(imgPath),
                    session.CanRedo(imgPath),
                    jsonPreview,
                    childRows,
                    canvasDto,
                    animFrames,
                    soundDto,
                    resolvedUolPath,
                    rawSlice);
            } catch (Exception ex) {
                return ErrorInspection(archiveId, norm, ex.Message);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Property & Node CRUD Operations
    // -------------------------------------------------------------------------

    public (bool Ok, string? Error) SetPropertyValue(
        string archiveId,
        string path,
        RepackerPropertyKind kind,
        string? rawValue,
        int? vecX = null,
        int? vecY = null) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.");
            }

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(path);
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                return (false, "Cannot set primitive value on an archive, directory, or image root.");
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                WzValue? existing = ResolvePropertyNode(imgRoot, subPath);
                if (existing is null) {
                    return (false, $"Property \"{subPath}\" not found.");
                }

                session.RecordUndoSnapshot(imgPath);

                // Special handling when editing Canvas metadata (origin / delay / z)
                if (existing is WzValue.Canvas canvas && kind == RepackerPropertyKind.Canvas) {
                    if (vecX.HasValue && vecY.HasValue) {
                        canvas["origin"] = new WzValue.Vector(vecX.Value, vecY.Value);
                    }
                    if (!string.IsNullOrWhiteSpace(rawValue) && int.TryParse(rawValue, out int delayVal)) {
                        canvas["delay"] = new WzValue.Int(delayVal);
                    }
                    session.MarkImageModified(imgPath);
                    AppendLogUnsafe($"Updated canvas properties at \"{path}\".", RepackerLogLevel.Info);
                    NotifyStateChanged();
                    return (true, null);
                }

                WzValue newVal = CreatePrimitiveOrStructuredValue(kind, rawValue, vecX, vecY, existing);
                if (!TryReplacePropertyNode(imgRoot, subPath, newVal)) {
                    return (false, $"Failed to update property at \"{subPath}\".");
                }

                session.MarkImageModified(imgPath);
                AppendLogUnsafe($"Updated \"{path}\" ({kind}) = {DescribeWzValue(newVal).DisplayValue}.", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null);
            } catch (Exception ex) {
                return (false, ex.Message);
            }
        }
    }

    public (bool Ok, string? Error, string CreatedPath) AddProperty(
        string archiveId,
        string parentPath,
        string name,
        RepackerPropertyKind kind,
        string? rawValue = null,
        int? vecX = null,
        int? vecY = null,
        byte[]? binaryPayload = null,
        WzCanvasFormat? canvasFormat = null) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", string.Empty);
            }

            string cleanName = name.Trim().Trim('/');
            if (string.IsNullOrEmpty(cleanName)) {
                return (false, "Property name is required.", string.Empty);
            }

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(parentPath);
            if (imgPath is null) {
                return (false, "Select an .img file or property inside an .img file to add a property.", string.Empty);
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                WzValue? targetParent = string.IsNullOrEmpty(subPath) ? imgRoot : ResolvePropertyNode(imgRoot, subPath);
                string effectiveSubPath = subPath;

                // If user selected a leaf property (e.g., Int/String), add as a sibling under its parent!
                if (targetParent is not null && targetParent is not WzValue.Object and not WzValue.Canvas and not WzValue.Convex) {
                    int lastSlash = subPath.LastIndexOf('/');
                    effectiveSubPath = lastSlash >= 0 ? subPath[..lastSlash] : string.Empty;
                    targetParent = string.IsNullOrEmpty(effectiveSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, effectiveSubPath);
                }

                if (targetParent is null) {
                    return (false, $"Target parent \"{parentPath}\" not found.", string.Empty);
                }

                session.RecordUndoSnapshot(imgPath);

                WzValue newVal;
                if (kind == RepackerPropertyKind.Canvas) {
                    WzCanvasFormat fmt = canvasFormat ?? WzCanvasFormat.Bgra8888;
                    if (binaryPayload is { Length: > 0 }) {
                        (int w, int h, byte[] comp) = WzCanvasCodec.EncodePngToCanvasPayload(binaryPayload, fmt);
                        newVal = new WzValue.Canvas(
                            w,
                            h,
                            (int) fmt,
                            comp,
                            [new WzProperty("origin", (vecX ?? w / 2, vecY ?? h))]);
                    } else {
                        int w = 32, h = 32;
                        byte[] blankRgba = new byte[w * h * 4];
                        byte[] comp = WzCanvasCodec.EncodeFromRgba(w, h, fmt, blankRgba);
                        newVal = new WzValue.Canvas(
                            w,
                            h,
                            (int) fmt,
                            comp,
                            [new WzProperty("origin", (vecX ?? 16, vecY ?? 32))]);
                    }
                } else if (kind == RepackerPropertyKind.Sound) {
                    newVal = WzSoundCodec.CreateFromAudioBytes(binaryPayload is { Length: > 0 } ? binaryPayload : []);
                } else {
                    newVal = CreatePrimitiveOrStructuredValue(kind, rawValue, vecX, vecY, null);
                }

                string createdSubPath = string.IsNullOrEmpty(effectiveSubPath) ? cleanName : $"{effectiveSubPath}/{cleanName}";
                if (targetParent is WzValue.Convex convex) {
                    convex.Items.Add(newVal);
                    createdSubPath = string.IsNullOrEmpty(effectiveSubPath)
                        ? $"{convex.Items.Count - 1}"
                        : $"{effectiveSubPath}/{convex.Items.Count - 1}";
                } else {
                    imgRoot.Set(createdSubPath, newVal);
                }

                string fullCreatedPath = $"{imgPath}/{createdSubPath}";
                session.MarkImageModified(imgPath);
                SelectedNodePath = fullCreatedPath;
                AppendLogUnsafe($"Added {kind} property \"{cleanName}\" at \"{fullCreatedPath}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, fullCreatedPath);
            } catch (Exception ex) {
                return (false, ex.Message, string.Empty);
            }
        }
    }

    public (bool Ok, string? Error, string CreatedPath) AddImage(
        string archiveId,
        string? parentPath,
        string imageName,
        byte[]? rawImgBytes = null,
        string? jsonContent = null) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", string.Empty);
            }

            string cleanName = imageName.Trim().Trim('/');
            if (string.IsNullOrEmpty(cleanName)) {
                return (false, "Image name is required.", string.Empty);
            }
            if (!cleanName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) {
                cleanName += ".img";
            }

            string normParent = ArchiveSession.NormalizePath(parentPath ?? string.Empty);
            (string? existingImg, _) = ArchiveSession.SplitImageAndSubPath(normParent);
            if (existingImg is not null) {
                int lastSlash = existingImg.LastIndexOf('/');
                normParent = lastSlash >= 0 ? existingImg[..lastSlash] : string.Empty;
            }

            string fullImgPath = string.IsNullOrEmpty(normParent) ? cleanName : $"{normParent}/{cleanName}";

            try {
                WzValue rootVal;
                if (rawImgBytes is { Length: > 0 }) {
                    rootVal = WzValue.Parse(rawImgBytes);
                } else if (!string.IsNullOrWhiteSpace(jsonContent)) {
                    rootVal = JsonWzCodec.Parse(jsonContent);
                } else {
                    rootVal = new WzValue.Object();
                }

                session.AddOrReplaceVirtualImage(fullImgPath, rootVal);
                SelectedNodePath = fullImgPath;
                AppendLogUnsafe($"Added image \"{fullImgPath}\" to \"{session.Name}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, fullImgPath);
            } catch (Exception ex) {
                return (false, ex.Message, string.Empty);
            }
        }
    }

    public (bool Ok, string? Error, string CreatedPath) AddDirectory(
        string archiveId,
        string? parentPath,
        string dirName) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", string.Empty);
            }

            string cleanName = dirName.Trim().Trim('/');
            if (string.IsNullOrEmpty(cleanName)) {
                return (false, "Directory name is required.", string.Empty);
            }

            string normParent = ArchiveSession.NormalizePath(parentPath ?? string.Empty);
            (string? existingImg, _) = ArchiveSession.SplitImageAndSubPath(normParent);
            if (existingImg is not null) {
                int lastSlash = existingImg.LastIndexOf('/');
                normParent = lastSlash >= 0 ? existingImg[..lastSlash] : string.Empty;
            }

            try {
                session.AddVirtualDirectory(normParent, cleanName);
                string createdPath = string.IsNullOrEmpty(normParent) ? cleanName : $"{normParent}/{cleanName}";
                SelectedNodePath = createdPath;
                AppendLogUnsafe($"Created directory \"{createdPath}\" in \"{session.Name}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, createdPath);
            } catch (Exception ex) {
                return (false, ex.Message, string.Empty);
            }
        }
    }

    public (bool Ok, string? Error, string NewPath) RenameNode(string archiveId, string path, string newName) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", string.Empty);
            }

            string cleanNewName = newName.Trim().Trim('/');
            if (string.IsNullOrEmpty(cleanNewName)) {
                return (false, "New name cannot be empty.", string.Empty);
            }

            string norm = ArchiveSession.NormalizePath(path);
            if (string.IsNullOrEmpty(norm)) {
                if (!cleanNewName.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)) {
                    cleanNewName += ".wz";
                }
                session.Name = cleanNewName;
                AppendLogUnsafe($"Renamed archive to \"{cleanNewName}\".", RepackerLogLevel.Info);
                NotifyStateChanged();
                return (true, null, string.Empty);
            }

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);

            // Rename directory or .img file
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                if (imgPath is not null && !cleanNewName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) {
                    cleanNewName += ".img";
                }
                int lastSlash = norm.LastIndexOf('/');
                string parentDir = lastSlash >= 0 ? norm[..lastSlash] : string.Empty;
                string newFullPath = string.IsNullOrEmpty(parentDir) ? cleanNewName : $"{parentDir}/{cleanNewName}";
                if (!session.RenameVirtualNode(norm, cleanNewName)) {
                    return (false, $"Could not rename \"{norm}\" to \"{cleanNewName}\".", string.Empty);
                }
                SelectedNodePath = newFullPath;
                AppendLogUnsafe($"Renamed \"{norm}\" -> \"{newFullPath}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, newFullPath);
            }

            // Rename property inside .img
            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                int lastSlash = subPath.LastIndexOf('/');
                string parentSubPath = lastSlash >= 0 ? subPath[..lastSlash] : string.Empty;
                string oldPropName = lastSlash >= 0 ? subPath[(lastSlash + 1)..] : subPath;

                WzValue? parentVal = string.IsNullOrEmpty(parentSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, parentSubPath);
                List<WzProperty>? kids = GetMutablePropertyList(parentVal);
                if (kids is null) {
                    return (false, "Parent node does not support named properties.", string.Empty);
                }

                if (kids.Any(p => p.Name == cleanNewName && p.Name != oldPropName)) {
                    return (false, $"A sibling property named \"{cleanNewName}\" already exists.", string.Empty);
                }

                int idx = kids.FindIndex(p => p.Name == oldPropName);
                if (idx < 0) {
                    return (false, $"Property \"{oldPropName}\" not found.", string.Empty);
                }

                session.RecordUndoSnapshot(imgPath);
                kids[idx] = kids[idx] with { Name = cleanNewName };
                session.MarkImageModified(imgPath);

                string newSubPath = string.IsNullOrEmpty(parentSubPath) ? cleanNewName : $"{parentSubPath}/{cleanNewName}";
                string newFullPath = $"{imgPath}/{newSubPath}";
                SelectedNodePath = newFullPath;
                AppendLogUnsafe($"Renamed property \"{norm}\" -> \"{newFullPath}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, newFullPath);
            } catch (Exception ex) {
                return (false, ex.Message, string.Empty);
            }
        }
    }

    public (bool Ok, string? Error, string ParentPath) DeleteNode(string archiveId, string path) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", string.Empty);
            }

            string norm = ArchiveSession.NormalizePath(path);
            if (string.IsNullOrEmpty(norm)) {
                return (false, "Use Close Archive to remove an entire archive.", string.Empty);
            }

            int lastSlash = norm.LastIndexOf('/');
            string parentPath = lastSlash >= 0 ? norm[..lastSlash] : string.Empty;

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                VirtualFsNode? targetNode = session.ResolveVirtualNode(norm);
                if (targetNode is null || targetNode.IsDeleted) {
                    return (false, $"Could not delete node \"{norm}\".", string.Empty);
                }
                if (imgPath is not null) {
                    session.RecordUndoSnapshot(imgPath);
                } else {
                    session.RecordDirectoryUndoSnapshot(norm);
                }
                if (!session.RemoveVirtualNode(norm)) {
                    return (false, $"Could not delete node \"{norm}\".", string.Empty);
                }
                SelectedNodePath = norm;
                AppendLogUnsafe($"Deleted \"{norm}\" from \"{session.Name}\".", RepackerLogLevel.Info);
                NotifyStateChanged();
                return (true, null, parentPath);
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                int subSlash = subPath.LastIndexOf('/');
                string parentSubPath = subSlash >= 0 ? subPath[..subSlash] : string.Empty;
                string leafName = subSlash >= 0 ? subPath[(subSlash + 1)..] : subPath;

                WzValue? parent = string.IsNullOrEmpty(parentSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, parentSubPath);
                if (parent is null) {
                    return (false, $"Property \"{subPath}\" not found.", string.Empty);
                }

                if (parent is WzValue.Convex(var items) && int.TryParse(leafName, out int cIdx) && cIdx >= 0 && cIdx < items.Count) {
                    session.RecordUndoSnapshot(imgPath);
                    items.RemoveAt(cIdx);
                    session.MarkImageModified(imgPath);
                    SelectedNodePath = parentPath;
                    AppendLogUnsafe($"Deleted property \"{norm}\".", RepackerLogLevel.Info);
                    NotifyStateChanged();
                    return (true, null, parentPath);
                }

                List<WzProperty>? kids = GetMutablePropertyList(parent);
                if (kids is null) {
                    return (false, $"Property \"{subPath}\" not found.", string.Empty);
                }

                int propIdx = kids.FindIndex(p => p.Name == leafName);
                if (propIdx < 0) {
                    return (false, $"Property \"{subPath}\" not found.", string.Empty);
                }

                WzValue deletedVal = CloneWzValue(kids[propIdx].Value);
                session.RecordUndoSnapshot(imgPath);
                kids.RemoveAt(propIdx);
                session.RecordDeletedProperty(imgPath, subPath, parentSubPath, leafName, propIdx, deletedVal);

                session.MarkImageModified(imgPath);
                SelectedNodePath = norm;
                AppendLogUnsafe($"Deleted property \"{norm}\".", RepackerLogLevel.Info);
                NotifyStateChanged();
                return (true, null, parentPath);
            } catch (Exception ex) {
                return (false, ex.Message, string.Empty);
            }
        }
    }

    public (bool Ok, string? Error, string RestoredPath) RestoreNode(string archiveId, string path) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", string.Empty);
            }

            string norm = ArchiveSession.NormalizePath(path);
            if (string.IsNullOrEmpty(norm)) {
                return (false, "Select a deleted node to restore.", string.Empty);
            }

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                if (!session.IsVirtualPathDeleted(norm)) {
                    return (false, $"Node \"{norm}\" is not deleted or could not be restored.", string.Empty);
                }
                if (imgPath is not null) {
                    session.RecordUndoSnapshot(imgPath);
                } else {
                    session.RecordDirectoryUndoSnapshot(norm);
                }
                if (!session.RestoreVirtualNode(norm)) {
                    return (false, $"Node \"{norm}\" is not deleted or could not be restored.", string.Empty);
                }
                SelectedNodePath = norm;
                AppendLogUnsafe($"Restored \"{norm}\" in \"{session.Name}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, norm);
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                session.RecordUndoSnapshot(imgPath);
                if (session.IsVirtualPathDeleted(imgPath)) {
                    session.RestoreVirtualNode(imgPath);
                }

                (bool restored, string? restoredSubPath) = session.RestoreDeletedProperty(imgPath, imgRoot, subPath);
                if (!restored) {
                    return (false, $"Property \"{subPath}\" is not deleted or could not be restored.", string.Empty);
                }

                session.MarkImageModified(imgPath);
                string finalPath = string.IsNullOrEmpty(restoredSubPath) ? norm : $"{imgPath}/{restoredSubPath}";
                SelectedNodePath = finalPath;
                AppendLogUnsafe($"Restored property \"{finalPath}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, finalPath);
            } catch (Exception ex) {
                return (false, ex.Message, string.Empty);
            }
        }
    }

    public (bool Ok, string? Error, string NewPath) DuplicateNode(string archiveId, string path) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", string.Empty);
            }

            string norm = ArchiveSession.NormalizePath(path);
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);
            if (imgPath is null) {
                return (false, "Select an .img file or property to duplicate.", string.Empty);
            }

            try {
                // Duplicate an entire .img file
                if (string.IsNullOrEmpty(subPath)) {
                    WzValue origImg = session.GetOrParseImage(imgPath);
                    WzValue clonedImg = WzValue.Parse(origImg.ToBytes());
                    int slash = imgPath.LastIndexOf('/');
                    string dirPart = slash >= 0 ? imgPath[..slash] : string.Empty;
                    string stem = Path.GetFileNameWithoutExtension(imgPath);
                    string candidateName = $"{stem}_copy.img";
                    string candidatePath = string.IsNullOrEmpty(dirPart) ? candidateName : $"{dirPart}/{candidateName}";
                    int n = 2;
                    while (session.ResolveVirtualNode(candidatePath) is not null) {
                        candidateName = $"{stem}_copy{n++}.img";
                        candidatePath = string.IsNullOrEmpty(dirPart) ? candidateName : $"{dirPart}/{candidateName}";
                    }
                    session.AddOrReplaceVirtualImage(candidatePath, clonedImg);
                    SelectedNodePath = candidatePath;
                    AppendLogUnsafe($"Duplicated image \"{imgPath}\" -> \"{candidatePath}\".", RepackerLogLevel.Success);
                    NotifyStateChanged();
                    return (true, null, candidatePath);
                }

                // Duplicate a property inside .img
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                WzValue? target = ResolvePropertyNode(imgRoot, subPath);
                if (target is null) {
                    return (false, $"Property \"{subPath}\" not found.", string.Empty);
                }

                int lastSlash = subPath.LastIndexOf('/');
                string parentSubPath = lastSlash >= 0 ? subPath[..lastSlash] : string.Empty;
                string propName = lastSlash >= 0 ? subPath[(lastSlash + 1)..] : subPath;

                WzValue? parentVal = string.IsNullOrEmpty(parentSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, parentSubPath);
                List<WzProperty>? kids = GetMutablePropertyList(parentVal);
                if (kids is null) {
                    return (false, "Parent does not support named child properties.", string.Empty);
                }

                string copyName = $"{propName}_copy";
                int copySeq = 2;
                while (kids.Any(p => p.Name == copyName)) {
                    copyName = $"{propName}_copy{copySeq++}";
                }

                session.RecordUndoSnapshot(imgPath);
                WzValue clonedVal = CloneWzValue(target);
                kids.Add(new WzProperty(copyName, clonedVal));
                session.MarkImageModified(imgPath);

                string newSubPath = string.IsNullOrEmpty(parentSubPath) ? copyName : $"{parentSubPath}/{copyName}";
                string newFullPath = $"{imgPath}/{newSubPath}";
                SelectedNodePath = newFullPath;
                AppendLogUnsafe($"Duplicated property \"{norm}\" -> \"{newFullPath}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, newFullPath);
            } catch (Exception ex) {
                return (false, ex.Message, string.Empty);
            }
        }
    }

    public (bool Ok, string? Error, ClipboardStateDto Clipboard) CopyNode(string archiveId, string path) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", GetClipboardState());
            }

            string norm = ArchiveSession.NormalizePath(path);
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);
            if (imgPath is null) {
                return (false, "Select an .img file or property to copy.", GetClipboardState());
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                if (string.IsNullOrEmpty(subPath)) {
                    string imgLeaf = Path.GetFileName(imgPath);
                    byte[] bytes = imgRoot.ToBytes();
                    clipboard = new ClipboardEntry(
                        session.ArchiveId,
                        norm,
                        imgLeaf,
                        RepackerNodeKind.Image,
                        RepackerPropertyKind.Object,
                        bytes);
                    AppendLogUnsafe($"Copied image \"{imgLeaf}\" ({bytes.Length:N0} B) to clipboard.", RepackerLogLevel.Info);
                } else {
                    WzValue? propVal = ResolvePropertyNode(imgRoot, subPath);
                    if (propVal is null) {
                        return (false, $"Property \"{subPath}\" not found.", GetClipboardState());
                    }
                    string propLeaf = subPath.Contains('/') ? subPath[(subPath.LastIndexOf('/') + 1)..] : subPath;
                    // Wrap single property inside a 1-property Object so any WzValue type serializes cleanly
                    var wrapper = new WzValue.Object([new WzProperty(propLeaf, propVal)]);
                    byte[] bytes = wrapper.ToBytes();
                    (RepackerPropertyKind pKind, _, _) = DescribeWzValue(propVal);
                    clipboard = new ClipboardEntry(
                        session.ArchiveId,
                        norm,
                        propLeaf,
                        RepackerNodeKind.Property,
                        pKind,
                        bytes);
                    AppendLogUnsafe($"Copied property \"{propLeaf}\" ({pKind}) to clipboard.", RepackerLogLevel.Info);
                }

                NotifyStateChanged();
                return (true, null, GetClipboardState());
            } catch (Exception ex) {
                return (false, ex.Message, GetClipboardState());
            }
        }
    }

    public (bool Ok, string? Error, string PastedPath) PasteNode(
        string targetArchiveId,
        string? targetPath,
        string? overrideName = null) {
        lock (syncLock) {
            if (clipboard is null) {
                return (false, "Clipboard is empty.", string.Empty);
            }

            ArchiveSession? session = FindSessionUnsafe(targetArchiveId);
            if (session is null) {
                return (false, $"Archive \"{targetArchiveId}\" not found.", string.Empty);
            }

            string normTarget = ArchiveSession.NormalizePath(targetPath ?? string.Empty);
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(normTarget);

            try {
                // If clipboard holds an entire .img and target is archive root or directory
                if (clipboard.NodeKind == RepackerNodeKind.Image && imgPath is null) {
                    WzValue parsedImg = WzValue.Parse(clipboard.SerializedBytes);
                    string targetImgName = !string.IsNullOrWhiteSpace(overrideName) ? overrideName.Trim() : clipboard.Name;
                    if (!targetImgName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) {
                        targetImgName += ".img";
                    }
                    string destPath = string.IsNullOrEmpty(normTarget) ? targetImgName : $"{normTarget}/{targetImgName}";
                    int seq = 2;
                    string stem = Path.GetFileNameWithoutExtension(targetImgName);
                    while (session.ResolveVirtualNode(destPath) is not null) {
                        targetImgName = $"{stem}_{seq++}.img";
                        destPath = string.IsNullOrEmpty(normTarget) ? targetImgName : $"{normTarget}/{targetImgName}";
                    }
                    session.AddOrReplaceVirtualImage(destPath, parsedImg);
                    SelectedNodePath = destPath;
                    AppendLogUnsafe($"Pasted image \"{destPath}\" into \"{session.Name}\".", RepackerLogLevel.Success);
                    NotifyStateChanged();
                    return (true, null, destPath);
                }

                if (imgPath is null) {
                    return (false, "Select an .img file or property inside an .img file to paste a property.", string.Empty);
                }

                WzValue imgRoot = session.GetOrParseImage(imgPath);
                WzValue? destParent = string.IsNullOrEmpty(subPath) ? imgRoot : ResolvePropertyNode(imgRoot, subPath);
                string effectiveSubPath = subPath;
                if (destParent is not null && destParent is not WzValue.Object and not WzValue.Canvas) {
                    int lastSlash = subPath.LastIndexOf('/');
                    effectiveSubPath = lastSlash >= 0 ? subPath[..lastSlash] : string.Empty;
                    destParent = string.IsNullOrEmpty(effectiveSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, effectiveSubPath);
                }

                List<WzProperty>? kids = GetMutablePropertyList(destParent);
                if (kids is null) {
                    return (false, "Destination node cannot hold child properties.", string.Empty);
                }

                WzValue unpackedVal;
                if (clipboard.NodeKind == RepackerNodeKind.Image) {
                    unpackedVal = WzValue.Parse(clipboard.SerializedBytes);
                } else {
                    WzValue wrapper = WzValue.Parse(clipboard.SerializedBytes);
                    unpackedVal = wrapper.Children is { Count: > 0 } ? wrapper.Children[0].Value : wrapper;
                }

                string baseName = !string.IsNullOrWhiteSpace(overrideName)
                    ? overrideName.Trim()
                    : Path.GetFileNameWithoutExtension(clipboard.Name);
                string finalName = baseName;
                int n = 2;
                while (kids.Any(p => p.Name == finalName)) {
                    finalName = $"{baseName}_{n++}";
                }

                session.RecordUndoSnapshot(imgPath);
                kids.Add(new WzProperty(finalName, unpackedVal));
                session.MarkImageModified(imgPath);

                string pastedSub = string.IsNullOrEmpty(effectiveSubPath) ? finalName : $"{effectiveSubPath}/{finalName}";
                string pastedFull = $"{imgPath}/{pastedSub}";
                SelectedNodePath = pastedFull;
                AppendLogUnsafe($"Pasted \"{pastedFull}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, pastedFull);
            } catch (Exception ex) {
                return (false, ex.Message, string.Empty);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Canvas, Sound, JSON & Outlink Operations
    // -------------------------------------------------------------------------

    public (bool Ok, string? Error) ReplaceCanvasPng(
        string archiveId,
        string path,
        byte[] pngBytes,
        WzCanvasFormat? format = null,
        int? originX = null,
        int? originY = null,
        int? delayMs = null) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.");
            }
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(path);
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                return (false, "Select a Canvas property to replace its image.");
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                if (ResolvePropertyNode(imgRoot, subPath) is not WzValue.Canvas canvas) {
                    return (false, $"Property \"{subPath}\" is not a Canvas.");
                }

                WzCanvasFormat targetFormat = format ?? WzCanvasFormatExtensions.FromFormatCode(canvas.Format);
                (int width, int height, byte[] compressed) = WzCanvasCodec.EncodePngToCanvasPayload(pngBytes, targetFormat);

                session.RecordUndoSnapshot(imgPath);
                canvas.Width = width;
                canvas.Height = height;
                canvas.Format = (int) targetFormat;
                canvas.Payload = compressed;
                canvas.Properties.RemoveAll(static p => p.Name is "_outlink" or "_inlink");

                if (originX.HasValue && originY.HasValue) {
                    canvas["origin"] = new WzValue.Vector(originX.Value, originY.Value);
                }
                if (delayMs.HasValue) {
                    canvas["delay"] = new WzValue.Int(delayMs.Value);
                }

                session.MarkImageModified(imgPath);
                AppendLogUnsafe($"Replaced Canvas \"{path}\" with {width}x{height} PNG ({targetFormat.GetDisplayName()}, {compressed.Length:N0} B).", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null);
            } catch (Exception ex) {
                return (false, ex.Message);
            }
        }
    }

    public (bool Ok, string? Error) ChangeCanvasFormat(string archiveId, string path, WzCanvasFormat newFormat) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.");
            }
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(path);
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                return (false, "Select a Canvas property to convert its pixel format.");
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                if (ResolvePropertyNode(imgRoot, subPath) is not WzValue.Canvas canvas) {
                    return (false, $"Property \"{subPath}\" is not a Canvas.");
                }

                byte[] rgba = WzCanvasCodec.DecodeToRgba(canvas);
                byte[] reencoded = WzCanvasCodec.EncodeFromRgba(canvas.Width, canvas.Height, newFormat, rgba);

                session.RecordUndoSnapshot(imgPath);
                canvas.Format = (int) newFormat;
                canvas.Payload = reencoded;
                session.MarkImageModified(imgPath);

                AppendLogUnsafe($"Converted Canvas \"{path}\" to {newFormat.GetDisplayName()} ({reencoded.Length:N0} B).", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null);
            } catch (Exception ex) {
                return (false, ex.Message);
            }
        }
    }

    public (bool Ok, string? Error) ReplaceSound(
        string archiveId,
        string path,
        byte[] audioBytes,
        int? durationMs = null) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.");
            }
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(path);
            if (imgPath is null || string.IsNullOrEmpty(subPath)) {
                return (false, "Select a Sound property to replace its audio.");
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                WzValue.Sound newSound = WzSoundCodec.CreateFromAudioBytes(audioBytes, durationMs);

                session.RecordUndoSnapshot(imgPath);
                if (!TryReplacePropertyNode(imgRoot, subPath, newSound)) {
                    return (false, $"Failed to replace Sound at \"{subPath}\".");
                }
                session.MarkImageModified(imgPath);

                AppendLogUnsafe($"Replaced Sound_DX8 \"{path}\" ({audioBytes.Length:N0} B, {newSound.Duration} ms).", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null);
            } catch (Exception ex) {
                return (false, ex.Message);
            }
        }
    }

    public (bool Ok, string? Error) ApplyJsonSubtree(string archiveId, string path, string jsonText) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.");
            }
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(path);
            if (imgPath is null) {
                return (false, "Select an .img file or a property inside an .img file to apply JSON changes.");
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                bool isImgRoot = string.IsNullOrEmpty(subPath);
                WzValue? existing = isImgRoot ? imgRoot : ResolvePropertyNode(imgRoot, subPath);
                WzValue updated = JsonWzCodec.Parse(jsonText, existing);

                session.RecordUndoSnapshot(imgPath);
                if (isImgRoot) {
                    session.MarkImageModified(imgPath, updated);
                } else {
                    if (!TryReplacePropertyNode(imgRoot, subPath, updated)) {
                        return (false, $"Failed to apply JSON to \"{subPath}\".");
                    }
                    session.MarkImageModified(imgPath);
                }

                AppendLogUnsafe($"Applied JSON edits to \"{path}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null);
            } catch (Exception ex) {
                return (false, $"Invalid JSON: {ex.Message}");
            }
        }
    }

    public (bool Ok, string? Error, int InlinedCount) InlineOutlinks(string archiveId, string? path = null) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", 0);
            }

            string norm = ArchiveSession.NormalizePath(path ?? string.Empty);
            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);

            try {
                int totalInlined = 0;
                if (imgPath is not null) {
                    WzValue imgRoot = session.GetOrParseImage(imgPath);
                    WzValue? target = string.IsNullOrEmpty(subPath) ? imgRoot : ResolvePropertyNode(imgRoot, subPath);
                    if (target is null) {
                        return (false, $"Node \"{norm}\" not found.", 0);
                    }
                    session.RecordUndoSnapshot(imgPath);
                    totalInlined = CanvasLinkResolver.InlineLinksRecursive(archives, session, imgRoot, target);
                    if (totalInlined > 0) {
                        session.MarkImageModified(imgPath);
                    }
                } else {
                    // Inline across all images under directory / archive
                    VirtualFsNode? dir = session.ResolveVirtualNode(norm);
                    if (dir is null) {
                        return (false, $"Directory \"{norm}\" not found.", 0);
                    }

                    void WalkDir(VirtualFsNode d, string prefix) {
                        session.EnsureDirectoryLinked(d);
                        foreach (VirtualFsNode c in d.Children) {
                            if (c.IsDeleted) continue;
                            string full = string.IsNullOrEmpty(prefix) ? c.Name : $"{prefix}/{c.Name}";
                            if (c.IsDir) {
                                WalkDir(c, full);
                            } else if (c.IsImage) {
                                WzValue root = session.GetOrParseImage(full);
                                int count = CanvasLinkResolver.InlineLinksRecursive(archives, session, root, root);
                                if (count > 0) {
                                    session.MarkImageModified(full);
                                    totalInlined += count;
                                }
                            }
                        }
                    }

                    WalkDir(dir, norm);
                }

                AppendLogUnsafe($"Inlined {totalInlined} linked Canvas sprite(s) in \"{session.Name}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, totalInlined);
            } catch (Exception ex) {
                return (false, ex.Message, 0);
            }
        }
    }

    public (bool Ok, string? Error) Undo(string archiveId, string? path = null) =>
        Undo(archiveId, path, out _);

    public (bool Ok, string? Error) Undo(string archiveId, string? path, out string affectedPath) {
        lock (syncLock) {
            affectedPath = string.Empty;
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.");
            }
            if (!session.Undo(out affectedPath)) {
                return (false, "Nothing to undo.");
            }
            AppendLogUnsafe($"Undid last change in \"{affectedPath}\".", RepackerLogLevel.Info);
            NotifyStateChanged();
            return (true, null);
        }
    }

    public (bool Ok, string? Error) Redo(string archiveId, string? path = null) =>
        Redo(archiveId, path, out _);

    public (bool Ok, string? Error) Redo(string archiveId, string? path, out string affectedPath) {
        lock (syncLock) {
            affectedPath = string.Empty;
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.");
            }
            if (!session.Redo(out affectedPath)) {
                return (false, "Nothing to redo.");
            }
            AppendLogUnsafe($"Redid change in \"{affectedPath}\".", RepackerLogLevel.Info);
            NotifyStateChanged();
            return (true, null);
        }
    }

    // -------------------------------------------------------------------------
    // Global Search & Export
    // -------------------------------------------------------------------------

    public SearchResultItemDto[] Search(SearchQueryDto query) {
        ArgumentNullException.ThrowIfNull(query);
        int maxResults = Math.Clamp(query.MaxResults, 1, 2000);
        string nameFilter = (query.Query ?? string.Empty).Trim();
        string valFilter = (query.ValueQuery ?? string.Empty).Trim();
        StringComparison cmp = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

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

    public (bool Ok, string? Error, string FileName, string MimeType, byte[] Data) ExportNode(
        string archiveId,
        string? path,
        RepackerExportFormat format = RepackerExportFormat.Auto) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", string.Empty, string.Empty, []);
            }

            string norm = ArchiveSession.NormalizePath(path ?? string.Empty);

            if (string.IsNullOrEmpty(norm) || format == RepackerExportFormat.Wz) {
                byte[] wzBytes = session.BuildArchiveBytes();
                return (true, null, session.Name, "application/octet-stream", wzBytes);
            }

            (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);
            if (imgPath is null) {
                return (false, "Select an .img file or property to export.", string.Empty, string.Empty, []);
            }

            try {
                WzValue imgRoot = session.GetOrParseImage(imgPath);
                bool isImgRoot = string.IsNullOrEmpty(subPath);
                WzValue? target = isImgRoot ? imgRoot : ResolvePropertyNode(imgRoot, subPath);
                if (target is null) {
                    return (false, $"Property \"{subPath}\" not found.", string.Empty, string.Empty, []);
                }

                string baseName = isImgRoot
                    ? Path.GetFileNameWithoutExtension(imgPath)
                    : subPath.Replace('/', '_');

                if (format == RepackerExportFormat.Img || (format == RepackerExportFormat.Auto && isImgRoot)) {
                    byte[] imgBytes = target.ToBytes();
                    string imgFileName = baseName.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? baseName : $"{baseName}.img";
                    return (true, null, imgFileName, "application/octet-stream", imgBytes);
                }

                if (format == RepackerExportFormat.Json) {
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(target.ToJson());
                    return (true, null, $"{baseName}.json", "application/json", jsonBytes);
                }

                // Resolve UOL if needed
                if (target is WzValue.Uol(var uolStr)) {
                    (WzValue? uolResolved, _) = ResolveUolTarget(imgRoot, subPath, uolStr);
                    if (uolResolved is not null) {
                        target = uolResolved;
                    }
                }

                if (target is WzValue.Canvas canvas && format is RepackerExportFormat.Png or RepackerExportFormat.Auto) {
                    CanvasPreviewDto preview = CanvasLinkResolver.BuildCanvasPreview(archives, session, imgPath, imgRoot, canvas);
                    return (true, null, $"{baseName}.png", "image/png", preview.PngBytes);
                }

                if (target is WzValue.Sound sound && format is RepackerExportFormat.Audio or RepackerExportFormat.Auto) {
                    ExtractedSoundData audio = WzSoundCodec.ExtractAudioStream(sound);
                    return (true, null, $"{baseName}.{audio.Extension}", audio.MimeType, audio.AudioBytes);
                }

                // Default to JSON export for any other property
                byte[] fallbackJson = Encoding.UTF8.GetBytes(target.ToJson());
                return (true, null, $"{baseName}.json", "application/json", fallbackJson);
            } catch (Exception ex) {
                return (false, ex.Message, string.Empty, string.Empty, []);
            }
        }
    }

    public (bool Ok, string? Error, int ExportedCount) BatchExportToDirectory(
        string archiveId,
        string? path,
        string outputDirectory,
        RepackerBatchExportMode mode = RepackerBatchExportMode.Png) {
        if (string.IsNullOrWhiteSpace(outputDirectory)) {
            return (false, "Output directory is required.", 0);
        }

        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.", 0);
            }

            try {
                string fullOutDir = Path.GetFullPath(outputDirectory.Trim());
                Directory.CreateDirectory(fullOutDir);
                string norm = ArchiveSession.NormalizePath(path ?? string.Empty);
                (string? imgPath, string subPath) = ArchiveSession.SplitImageAndSubPath(norm);

                int exported = 0;

                void ExportFromImage(string currentImgPath, string startSubPath) {
                    WzValue imgRoot = session.GetOrParseImage(currentImgPath);
                    string imgRelDir = Path.Combine(
                        fullOutDir,
                        currentImgPath.Replace('/', Path.DirectorySeparatorChar));

                    if (mode == RepackerBatchExportMode.Img) {
                        string destFile = imgRelDir;
                        Directory.CreateDirectory(Path.GetDirectoryName(destFile) ?? fullOutDir);
                        System.IO.File.WriteAllBytes(destFile, imgRoot.ToBytes());
                        exported++;
                        return;
                    }

                    if (mode == RepackerBatchExportMode.Json) {
                        WzValue targetNode = string.IsNullOrEmpty(startSubPath)
                            ? imgRoot
                            : ResolvePropertyNode(imgRoot, startSubPath) ?? imgRoot;
                        string destFile = string.IsNullOrEmpty(startSubPath)
                            ? $"{imgRelDir}.json"
                            : Path.Combine(imgRelDir, $"{startSubPath.Replace('/', '_')}.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(destFile) ?? fullOutDir);
                        System.IO.File.WriteAllText(destFile, targetNode.ToJson());
                        exported++;
                        return;
                    }

                    WzValue walkRoot = string.IsNullOrEmpty(startSubPath)
                        ? imgRoot
                        : ResolvePropertyNode(imgRoot, startSubPath) ?? imgRoot;

                    void WalkProperties(WzValue node, string propPrefix) {
                        if (node is WzValue.Canvas canvas && mode is RepackerBatchExportMode.Png or RepackerBatchExportMode.All) {
                            CanvasPreviewDto preview = CanvasLinkResolver.BuildCanvasPreview(archives, session, currentImgPath, imgRoot, canvas);
                            string safeRel = string.IsNullOrEmpty(propPrefix) ? "canvas" : propPrefix.Replace('/', Path.DirectorySeparatorChar);
                            string destFile = Path.Combine(imgRelDir, $"{safeRel}.png");
                            Directory.CreateDirectory(Path.GetDirectoryName(destFile) ?? imgRelDir);
                            System.IO.File.WriteAllBytes(destFile, preview.PngBytes);
                            exported++;
                        } else if (node is WzValue.Sound sound && mode is RepackerBatchExportMode.Sound or RepackerBatchExportMode.All) {
                            ExtractedSoundData audio = WzSoundCodec.ExtractAudioStream(sound);
                            string safeRel = string.IsNullOrEmpty(propPrefix) ? "sound" : propPrefix.Replace('/', Path.DirectorySeparatorChar);
                            string destFile = Path.Combine(imgRelDir, $"{safeRel}.{audio.Extension}");
                            Directory.CreateDirectory(Path.GetDirectoryName(destFile) ?? imgRelDir);
                            System.IO.File.WriteAllBytes(destFile, audio.AudioBytes);
                            exported++;
                        }

                        if (node.Children is { Count: > 0 } children) {
                            foreach (WzProperty child in children) {
                                string nextPrefix = string.IsNullOrEmpty(propPrefix) ? child.Name : $"{propPrefix}/{child.Name}";
                                WalkProperties(child.Value, nextPrefix);
                            }
                        }
                    }

                    WalkProperties(walkRoot, startSubPath);
                }

                if (imgPath is not null) {
                    ExportFromImage(imgPath, subPath);
                } else {
                    VirtualFsNode? dir = session.ResolveVirtualNode(norm);
                    if (dir is null) {
                        return (false, $"Node \"{norm}\" not found.", 0);
                    }

                    void WalkDirs(VirtualFsNode d, string prefix) {
                        session.EnsureDirectoryLinked(d);
                        foreach (VirtualFsNode c in d.Children) {
                            if (c.IsDeleted) continue;
                            string full = string.IsNullOrEmpty(prefix) ? c.Name : $"{prefix}/{c.Name}";
                            if (c.IsDir) {
                                WalkDirs(c, full);
                            } else if (c.IsImage) {
                                ExportFromImage(full, string.Empty);
                            }
                        }
                    }

                    WalkDirs(dir, norm);
                }

                AppendLogUnsafe($"Batch-exported {exported} file(s) ({mode}) to \"{fullOutDir}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, exported);
            } catch (Exception ex) {
                AppendLogUnsafe($"Batch export failed: {ex.Message}", RepackerLogLevel.Error);
                return (false, ex.Message, 0);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Internal Helpers
    // -------------------------------------------------------------------------

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
