using System.Globalization;
using System.Text;
using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Audio;
using Maple.Classic.Repacker.Engine.Canvas;
using Maple.Classic.Repacker.Engine.Models;
using Maple.Classic.Repacker.Engine.Serialization;

namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Central thread-safe workspace engine for inspecting, editing, searching, and repacking
/// MapleStory Classic World (<c>.wz</c> and <c>.img</c>) archives.
/// </summary>
public sealed class RepackerWorkspace : IDisposable {
    private const int MaxLogEntries = 300;

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

    private void AppendLogUnsafe(string message, RepackerLogLevel level = RepackerLogLevel.Info) {
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
        string fullPath = Path.GetFullPath(filePath);
        var wz = new WzArchive(fullPath, version);
        string fileName = Path.GetFileName(fullPath);

        lock (syncLock) {
            ArchiveSession? existing = FindExistingOrLinkedSessionByPathUnsafe(fullPath);
            if (existing is not null) {
                int idx = archives.IndexOf(existing);
                string reloadPath = ChoosePreferredRootArchivePath(existing.FilePath, fullPath);
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
                : TryResolveDiskPathUnsafe(cleanName, data.Length, hintPaths) ?? string.Empty;

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
                    ? ChoosePreferredRootArchivePath(existing.FilePath, resolvedPath)
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

    private ArchiveSession? FindExistingOrLinkedSessionByPathUnsafe(string fullPath) {
        return archives.Find(a =>
            (!string.IsNullOrEmpty(a.FilePath) &&
             string.Equals(a.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)) ||
            a.HasLinkedOrSubdirectoryFile(fullPath));
    }

    private void RemoveSubsumedSessionsUnsafe(ArchiveSession primary, int primaryIdx) {
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

    private static string ChoosePreferredRootArchivePath(string existingPath, string requestedPath) {
        if (string.IsNullOrWhiteSpace(existingPath) || !System.IO.File.Exists(existingPath)) {
            return requestedPath;
        }
        try {
            string existingFull = Path.GetFullPath(existingPath);
            string requestedFull = Path.GetFullPath(requestedPath);
            if (string.Equals(existingFull, requestedFull, StringComparison.OrdinalIgnoreCase)) {
                return requestedFull;
            }

            string? existingDir = Path.GetDirectoryName(existingFull);
            string? requestedDir = Path.GetDirectoryName(requestedFull);
            if (!string.IsNullOrEmpty(existingDir) && !string.IsNullOrEmpty(requestedDir)) {
                string normExistingDir = Path.TrimEndingDirectorySeparator(existingDir);
                string normRequestedDir = Path.TrimEndingDirectorySeparator(requestedDir);

                // If existing is in a parent directory of requested (e.g., Item.wz vs Cash/Cash_000.wz), keep existing root
                if (normRequestedDir.StartsWith(normExistingDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                    return existingFull;
                }

                // If both are in the same directory (e.g., Item.wz vs Item_000.wz), prefer the un-numbered base .wz file
                if (string.Equals(normExistingDir, normRequestedDir, StringComparison.OrdinalIgnoreCase)) {
                    string existingStem = Path.GetFileNameWithoutExtension(existingFull);
                    string existingBase = ArchiveSession.StripNumericShardSuffix(existingStem);
                    if (string.Equals(existingStem, existingBase, StringComparison.OrdinalIgnoreCase) &&
                        !existingStem.EndsWith("_Canvas", StringComparison.OrdinalIgnoreCase)) {
                        return existingFull;
                    }
                }
            }
        } catch {
            // Fall back to requested path
        }
        return requestedPath;
    }

    private string? TryResolveDiskPathUnsafe(string cleanName, int byteLength, IEnumerable<string>? hintPaths) {
        string baseStem = ArchiveSession.StripNumericShardSuffix(Path.GetFileNameWithoutExtension(cleanName));
        var candidateDirs = new List<string>();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDirAndParent(string? pathOrDir) {
            if (string.IsNullOrWhiteSpace(pathOrDir)) {
                return;
            }
            try {
                string full = Path.GetFullPath(pathOrDir.Trim());
                string? dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && seenDirs.Add(dir)) {
                    candidateDirs.Add(dir);
                    string? parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && seenDirs.Add(parent)) {
                        candidateDirs.Add(parent);
                    }
                }
            } catch {
                // Ignore invalid paths
            }
        }

        foreach (ArchiveSession a in archives) {
            AddDirAndParent(a.FilePath);
        }
        if (hintPaths is not null) {
            foreach (string h in hintPaths) {
                AddDirAndParent(h);
            }
        }
        AddDirAndParent(Environment.GetEnvironmentVariable("MSCW_DATA_FOLDER"));
        AddDirAndParent(@"C:\Nexon\Library\maplestorycw\appdata\Data");
        AddDirAndParent(@"C:\Nexon\MapleStory\Data");

        string? sizeFallbackMatch = null;
        foreach (string dir in candidateDirs) {
            string[] candidates = [
                Path.Combine(dir, cleanName),
                Path.Combine(dir, baseStem, cleanName),
            ];
            foreach (string candidate in candidates) {
                try {
                    var fi = new FileInfo(candidate);
                    if (fi.Exists) {
                        if (fi.Length == byteLength) {
                            return fi.FullName;
                        }
                        sizeFallbackMatch ??= fi.FullName;
                    }
                } catch {
                    // Ignore IO errors
                }
            }
        }

        return sizeFallbackMatch;
    }

    public ArchiveSummaryDto[] OpenDataDirectory(string directoryPath, ushort? version = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        string fullDir = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDir)) {
            throw new DirectoryNotFoundException($"Directory not found: {fullDir}");
        }

        string[] wzFiles = Directory.GetFiles(fullDir, "*.wz", SearchOption.AllDirectories)
            .OrderBy(static p => p.Count(static c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .ThenBy(static p => {
                string stem = Path.GetFileNameWithoutExtension(p);
                return string.Equals(stem, "Base", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            })
            .ThenBy(static p => {
                string stem = Path.GetFileNameWithoutExtension(p);
                string baseStem = ArchiveSession.StripNumericShardSuffix(stem);
                return string.Equals(stem, baseStem, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            })
            .ThenBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var opened = new List<ArchiveSummaryDto>();
        foreach (string wzPath in wzFiles) {
            try {
                bool alreadyLinked;
                lock (syncLock) {
                    alreadyLinked = archives.Any(a => a.HasLinkedOrSubdirectoryFile(wzPath));
                }
                if (alreadyLinked) {
                    continue;
                }
                opened.Add(OpenArchiveFromFile(wzPath, version));
            } catch (Exception ex) {
                AppendLog($"Skipped \"{Path.GetFileName(wzPath)}\": {ex.Message}", RepackerLogLevel.Warning);
            }
        }

        AppendLog($"Opened {opened.Count} WZ archive(s) from \"{fullDir}\".", RepackerLogLevel.Success);
        return [.. opened];
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
                    false,
                    false,
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
                    canvasDto = BuildCanvasPreviewUnsafe(session, imgPath, imgRoot, canvasVal);
                }

                // Animation frames (if target is an animation container OR a frame inside an animation container)
                AnimationFrameDto[] animFrames = [];
                if (includeAnimation) {
                    animFrames = ExtractAnimationFramesUnsafe(session, imgPath, imgRoot, effectiveSubPath, effectiveVisualTarget);
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
                            canvasDto = BuildCanvasPreviewUnsafe(session, imgPath, imgRoot, childCanvas);
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
                if (!session.RestoreVirtualNode(norm)) {
                    return (false, $"Node \"{norm}\" is not deleted or could not be restored.", string.Empty);
                }
                SelectedNodePath = norm;
                AppendLogUnsafe($"Restored \"{norm}\" in \"{session.Name}\".", RepackerLogLevel.Success);
                NotifyStateChanged();
                return (true, null, norm);
            }

            try {
                if (session.IsVirtualPathDeleted(imgPath)) {
                    session.RestoreVirtualNode(imgPath);
                }

                WzValue imgRoot = session.GetOrParseImage(imgPath);
                session.RecordUndoSnapshot(imgPath);

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
                    totalInlined = InlineLinksRecursiveUnsafe(session, imgRoot, target);
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
                                int count = InlineLinksRecursiveUnsafe(session, root, root);
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

    public (bool Ok, string? Error) Undo(string archiveId, string path) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.");
            }
            (string? imgPath, _) = ArchiveSession.SplitImageAndSubPath(path);
            if (imgPath is null) {
                return (false, "Select a node inside an .img file to undo.");
            }
            if (!session.Undo(imgPath)) {
                return (false, "Nothing to undo for this image.");
            }
            AppendLogUnsafe($"Undid last change in \"{imgPath}\".", RepackerLogLevel.Info);
            NotifyStateChanged();
            return (true, null);
        }
    }

    public (bool Ok, string? Error) Redo(string archiveId, string path) {
        lock (syncLock) {
            ArchiveSession? session = FindSessionUnsafe(archiveId);
            if (session is null) {
                return (false, $"Archive \"{archiveId}\" not found.");
            }
            (string? imgPath, _) = ArchiveSession.SplitImageAndSubPath(path);
            if (imgPath is null) {
                return (false, "Select a node inside an .img file to redo.");
            }
            if (!session.Redo(imgPath)) {
                return (false, "Nothing to redo for this image.");
            }
            AppendLogUnsafe($"Redid change in \"{imgPath}\".", RepackerLogLevel.Info);
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
                SearchDirectoryUnsafe(session, session.VirtualRoot, "", nameFilter, valFilter, query.PropertyKindFilter, query.ExactMatch, cmp, maxResults, results);
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
                    CanvasPreviewDto preview = BuildCanvasPreviewUnsafe(session, imgPath, imgRoot, canvas);
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
                            CanvasPreviewDto preview = BuildCanvasPreviewUnsafe(session, currentImgPath, imgRoot, canvas);
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
    // Internal Helpers (Link Resolution, Animations, Search, Cloning)
    // -------------------------------------------------------------------------

    private ArchiveSession? FindSessionUnsafe(string? archiveId) {
        if (string.IsNullOrWhiteSpace(archiveId)) {
            return archives.FirstOrDefault();
        }
        return archives.Find(a => a.ArchiveId == archiveId);
    }

    private CanvasPreviewDto BuildCanvasPreviewUnsafe(
        ArchiveSession session,
        string imgPath,
        WzValue imgRoot,
        WzValue.Canvas canvas) {
        string? inlink = canvas["_inlink"]?.AsString();
        string? outlink = canvas["_outlink"]?.AsString();
        string? zLayer = canvas["z"]?.AsString() ?? (canvas["z"]?.AsInt() is { } zi ? zi.ToString(CultureInfo.InvariantCulture) : null);
        int delayMs = canvas["delay"]?.AsInt() ?? 100;
        VectorPointDto origin = canvas["origin"] is WzValue.Vector(var ox, var oy) ? new VectorPointDto(ox, oy) : new VectorPointDto(0, 0);
        VectorPointDto? lt = canvas["lt"] is WzValue.Vector(var ltx, var lty) ? new VectorPointDto(ltx, lty) : null;
        VectorPointDto? rb = canvas["rb"] is WzValue.Vector(var rbx, var rby) ? new VectorPointDto(rbx, rby) : null;
        VectorPointDto? head = canvas["head"] is WzValue.Vector(var hx, var hy) ? new VectorPointDto(hx, hy) : null;

        WzValue.Canvas visualCanvas = canvas;
        string? resolvedLinkPath = null;
        bool isLinkStub = (!string.IsNullOrEmpty(inlink) || !string.IsNullOrEmpty(outlink)) && canvas.Width <= 1 && canvas.Height <= 1;

        if (!string.IsNullOrEmpty(inlink)) {
            if (ResolvePropertyNode(imgRoot, inlink) is WzValue.Canvas inCanvas) {
                visualCanvas = inCanvas;
                resolvedLinkPath = $"{imgPath}/{inlink}";
            }
        } else if (!string.IsNullOrEmpty(outlink)) {
            (WzValue.Canvas? outCanvas, string? outResolved) = ResolveOutlinkCanvasUnsafe(session, outlink);
            if (outCanvas is not null) {
                visualCanvas = outCanvas;
                resolvedLinkPath = outResolved;
            }
        }

        byte[] pngBytes = WzCanvasCodec.DecodeToPng(visualCanvas);
        WzCanvasFormat canvasFormat = WzCanvasFormatExtensions.FromFormatCode(visualCanvas.Format);
        return new CanvasPreviewDto(
            visualCanvas.Width,
            visualCanvas.Height,
            canvasFormat,
            canvasFormat.GetDisplayName(),
            visualCanvas.Payload.Length,
            pngBytes,
            origin,
            lt,
            rb,
            head,
            delayMs,
            zLayer,
            inlink,
            outlink,
            resolvedLinkPath,
            isLinkStub);
    }

    private (WzValue.Canvas? Canvas, string? ResolvedDesc) ResolveOutlinkCanvasUnsafe(ArchiveSession currentSession, string outlink) {
        (string? targetImgPart, string innerSubPath) = ArchiveSession.SplitImageAndSubPath(outlink);
        if (targetImgPart is null || string.IsNullOrEmpty(innerSubPath)) {
            return (null, null);
        }

        // Build candidate .img paths by progressively stripping leading directory segments
        // (e.g., "Character/Coat/_Canvas/01040000.img" -> "Character/Coat/_Canvas/01040000.img",
        // "Coat/_Canvas/01040000.img", "_Canvas/01040000.img", "01040000.img")
        // as well as "_Canvas" variants when "_Canvas" is not already present.
        var candidateImgPaths = new List<string>(8);
        void AddCandidate(string p) {
            if (!string.IsNullOrEmpty(p) && !candidateImgPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) {
                candidateImgPaths.Add(p);
            }
        }

        string[] segments = targetImgPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++) {
            string sub = string.Join('/', segments[i..]);
            AddCandidate(sub);
            if (i < segments.Length - 1 && !string.Equals(segments[^2], "_Canvas", StringComparison.OrdinalIgnoreCase)) {
                string withCanvas = string.Join('/', [.. segments[i..^1], "_Canvas", segments[^1]]);
                AddCandidate(withCanvas);
            }
        }
        if (segments.Length >= 1) {
            AddCandidate($"_Canvas/{segments[^1]}");
        }

        // Check currentSession first, then all other open ArchiveSessions
        var orderedSessions = new List<ArchiveSession>(archives.Count) { currentSession };
        foreach (ArchiveSession s in archives) {
            if (!ReferenceEquals(s, currentSession)) {
                orderedSessions.Add(s);
            }
        }

        foreach (ArchiveSession candidate in orderedSessions) {
            foreach (string tryImgPath in candidateImgPaths) {
                VirtualFsNode? vNode = candidate.ResolveVirtualNode(tryImgPath);
                if (vNode is { IsImage: true }) {
                    try {
                        WzValue parsed = candidate.GetOrParseImage(tryImgPath);
                        if (ResolvePropertyNode(parsed, innerSubPath) is WzValue.Canvas found &&
                            !(found.Width <= 1 && found.Height <= 1 && found["_outlink"] is not null)) {
                            return (found, $"{candidate.Name}:{tryImgPath}/{innerSubPath}");
                        }
                    } catch {
                        // Ignore and keep searching
                    }
                }
            }
        }

        return (null, null);
    }

    private int InlineLinksRecursiveUnsafe(ArchiveSession session, WzValue imgRoot, WzValue node) {
        int count = 0;
        if (node is WzValue.Canvas canvas) {
            string? inlink = canvas["_inlink"]?.AsString();
            string? outlink = canvas["_outlink"]?.AsString();
            WzValue.Canvas? resolved = null;

            if (!string.IsNullOrEmpty(inlink)) {
                resolved = ResolvePropertyNode(imgRoot, inlink) as WzValue.Canvas;
            } else if (!string.IsNullOrEmpty(outlink)) {
                (resolved, _) = ResolveOutlinkCanvasUnsafe(session, outlink);
            }

            if (resolved is not null) {
                canvas.Width = resolved.Width;
                canvas.Height = resolved.Height;
                canvas.Format = resolved.Format;
                canvas.Payload = resolved.Payload;
                canvas.Properties.RemoveAll(static p => p.Name is "_inlink" or "_outlink");
                count++;
            }
        }

        IReadOnlyList<WzProperty>? kids = node.Children;
        if (kids is not null) {
            for (int i = 0; i < kids.Count; i++) {
                count += InlineLinksRecursiveUnsafe(session, imgRoot, kids[i].Value);
            }
        }
        return count;
    }

    private AnimationFrameDto[] ExtractAnimationFramesUnsafe(
        ArchiveSession session,
        string imgPath,
        WzValue imgRoot,
        string subPath,
        WzValue selectedValue) {
        // Only extract animation frames when the parent container node of numbered frames ("0", "1", ...) is selected
        if (selectedValue is not WzValue.Object || !HasNumberedFrameChildren(selectedValue) || selectedValue.Children is null) {
            return [];
        }
        WzValue animContainer = selectedValue;
        string containerSubPath = subPath;

        var numberedProps = new List<(int Index, WzProperty Prop)>();
        foreach (WzProperty prop in animContainer.Children) {
            if (int.TryParse(prop.Name, out int frameIdx) && frameIdx >= 0) {
                numberedProps.Add((frameIdx, prop));
            }
        }
        numberedProps.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        var frames = new List<AnimationFrameDto>(numberedProps.Count);
        foreach ((int idx, WzProperty prop) in numberedProps) {
            string frameSubPath = string.IsNullOrEmpty(containerSubPath) ? prop.Name : $"{containerSubPath}/{prop.Name}";
            WzValue frameVal = prop.Value;
            if (frameVal is WzValue.Uol(var uolStr)) {
                (WzValue? resolved, _) = ResolveUolTarget(imgRoot, frameSubPath, uolStr);
                if (resolved is not null) {
                    frameVal = resolved;
                }
            }

            WzValue.Canvas? frameCanvas = frameVal as WzValue.Canvas;
            int? overrideDelay = null;
            if (frameCanvas is null && frameVal is WzValue.Object frameObj && frameObj.Children is not null) {
                // Character equipment frame object (e.g. stand1/0/mail)
                overrideDelay = frameObj["delay"]?.AsInt();
                foreach (WzProperty sub in frameObj.Children) {
                    WzValue subVal = sub.Value;
                    if (subVal is WzValue.Uol(var subUol)) {
                        (WzValue? resSub, _) = ResolveUolTarget(imgRoot, $"{frameSubPath}/{sub.Name}", subUol);
                        if (resSub is not null) subVal = resSub;
                    }
                    if (subVal is WzValue.Canvas c) {
                        frameCanvas = c;
                        break;
                    }
                }
            }

            if (frameCanvas is null) {
                continue;
            }

            CanvasPreviewDto preview = BuildCanvasPreviewUnsafe(session, imgPath, imgRoot, frameCanvas);
            int delay = overrideDelay ?? preview.DelayMs;
            int a0 = frameCanvas["a0"]?.AsInt() ?? 255;
            int a1 = frameCanvas["a1"]?.AsInt() ?? 255;

            frames.Add(new AnimationFrameDto(
                idx,
                prop.Name,
                $"{imgPath}/{frameSubPath}",
                preview.Width,
                preview.Height,
                preview.Format,
                Math.Max(30, delay),
                preview.Origin,
                preview.Lt,
                preview.Rb,
                preview.Head,
                a0,
                a1,
                preview.PngBytes));
        }

        return frames.Count > 1 ? [.. frames] : [];
    }

    private static bool HasNumberedFrameChildren(WzValue val) {
        if (val.Children is null || val.Children.Count == 0) {
            return false;
        }
        foreach (WzProperty p in val.Children) {
            if (p.Name == "0") {
                return true;
            }
        }
        return false;
    }

    public static (WzValue? Target, string? ResolvedSubPath) ResolveUolTarget(WzValue imgRoot, string uolNodeSubPath, string uolExpression) {
        // UOL is relative to the parent of the UOL property
        int lastSlash = uolNodeSubPath.LastIndexOf('/');
        string parentSubPath = lastSlash >= 0 ? uolNodeSubPath[..lastSlash] : string.Empty;

        var segments = new List<string>();
        if (!string.IsNullOrEmpty(parentSubPath)) {
            segments.AddRange(parentSubPath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (string part in uolExpression.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (part == ".") {
                continue;
            }
            if (part == "..") {
                if (segments.Count > 0) {
                    segments.RemoveAt(segments.Count - 1);
                }
            } else {
                segments.Add(part);
            }
        }

        string targetSubPath = string.Join('/', segments);
        WzValue? resolved = ResolvePropertyNode(imgRoot, targetSubPath);
        return (resolved, resolved is not null ? targetSubPath : null);
    }

    public static WzValue? ResolvePropertyNode(WzValue root, ReadOnlySpan<char> subPath) {
        subPath = subPath.Trim('/');
        if (subPath.IsEmpty) {
            return root;
        }

        WzValue cur = root;
        foreach (Range range in subPath.Split('/')) {
            ReadOnlySpan<char> part = subPath[range];
            if (part.IsEmpty) {
                continue;
            }

            if (cur is WzValue.Convex(var items)) {
                if (int.TryParse(part, out int idx) && idx >= 0 && idx < items.Count) {
                    cur = items[idx];
                    continue;
                }
                return null;
            }

            IReadOnlyList<WzProperty>? kids = cur.Children;
            if (kids is null) {
                return null;
            }

            WzValue? next = null;
            foreach (WzProperty prop in kids) {
                if (prop.Name.AsSpan().SequenceEqual(part)) {
                    next = prop.Value;
                    break;
                }
            }
            if (next is null) {
                return null;
            }
            cur = next;
        }
        return cur;
    }

    private static bool TryReplacePropertyNode(WzValue imgRoot, string subPath, WzValue newValue) {
        subPath = subPath.Trim('/');
        if (string.IsNullOrEmpty(subPath)) {
            return false;
        }

        int lastSlash = subPath.LastIndexOf('/');
        string parentSubPath = lastSlash >= 0 ? subPath[..lastSlash] : string.Empty;
        string leafName = lastSlash >= 0 ? subPath[(lastSlash + 1)..] : subPath;

        WzValue? parent = string.IsNullOrEmpty(parentSubPath) ? imgRoot : ResolvePropertyNode(imgRoot, parentSubPath);
        if (parent is WzValue.Convex(var items) && int.TryParse(leafName, out int cIdx) && cIdx >= 0 && cIdx < items.Count) {
            items[cIdx] = newValue;
            return true;
        }

        List<WzProperty>? kids = GetMutablePropertyList(parent);
        if (kids is null) {
            return false;
        }

        for (int i = 0; i < kids.Count; i++) {
            if (kids[i].Name == leafName) {
                kids[i] = kids[i] with { Value = newValue };
                return true;
            }
        }
        kids.Add(new WzProperty(leafName, newValue));
        return true;
    }

    private static List<WzProperty>? GetMutablePropertyList(WzValue? val) => val switch {
        WzValue.Object(var props) => props,
        WzValue.Canvas { Properties: var props } => props,
        _ => null,
    };

    private static WzValue CloneWzValue(WzValue v) {
        var wrapper = new WzValue.Object([new WzProperty("_", v)]);
        WzValue parsed = WzValue.Parse(wrapper.ToBytes());
        return parsed.Children is { Count: > 0 } ? parsed.Children[0].Value : parsed;
    }

    private static WzValue CreatePrimitiveOrStructuredValue(
        RepackerPropertyKind kind,
        string? rawValue,
        int? vecX,
        int? vecY,
        WzValue? existing) {
        string text = rawValue ?? string.Empty;
        return kind switch {
            RepackerPropertyKind.Null => WzValue.Null.Instance,
            RepackerPropertyKind.Short => new WzValue.Short(short.TryParse(text, CultureInfo.InvariantCulture, out short s) ? s : (short) 0),
            RepackerPropertyKind.Int => new WzValue.Int(int.TryParse(text, CultureInfo.InvariantCulture, out int i) ? i : 0),
            RepackerPropertyKind.Long => new WzValue.Long(long.TryParse(text, CultureInfo.InvariantCulture, out long l) ? l : 0L),
            RepackerPropertyKind.Float => new WzValue.Float(float.TryParse(text, CultureInfo.InvariantCulture, out float f) ? f : 0f),
            RepackerPropertyKind.Double => new WzValue.Double(double.TryParse(text, CultureInfo.InvariantCulture, out double d) ? d : 0.0),
            RepackerPropertyKind.String => new WzValue.String(text),
            RepackerPropertyKind.Uol => new WzValue.Uol(text),
            RepackerPropertyKind.Vector => new WzValue.Vector(vecX ?? 0, vecY ?? 0),
            RepackerPropertyKind.Object => existing is WzValue.Object existingObj ? existingObj : new WzValue.Object(),
            RepackerPropertyKind.Convex => existing is WzValue.Convex existingConv ? existingConv : new WzValue.Convex(),
            _ => new WzValue.String(text),
        };
    }

    private static RepackerChangeState EvaluatePropertyChangeState(
        ArchiveSession session,
        string imgPath,
        string propSubPath,
        WzValue propVal,
        bool isDeleted = false) {
        if (isDeleted) {
            return RepackerChangeState.Deleted;
        }
        if (session.IsImageAdded(imgPath)) {
            return RepackerChangeState.Added;
        }
        if (!session.IsImageModified(imgPath)) {
            return RepackerChangeState.Unchanged;
        }
        if (!session.TryGetBaselineImage(imgPath, out WzValue? baselineRoot) || baselineRoot is null) {
            return RepackerChangeState.Modified;
        }
        if (string.IsNullOrEmpty(propSubPath)) {
            return !propVal.Equals(baselineRoot) ? RepackerChangeState.Modified : RepackerChangeState.Unchanged;
        }
        WzValue? origProp = ResolvePropertyNode(baselineRoot, propSubPath);
        if (origProp is null) {
            return RepackerChangeState.Added;
        }
        return !propVal.Equals(origProp) ? RepackerChangeState.Modified : RepackerChangeState.Unchanged;
    }

    private static List<(string Name, string SubPath, WzValue Value, bool IsDeleted)> BuildMergedChildProperties(
        ArchiveSession session,
        string imgPath,
        string parentSubPath,
        WzValue target,
        bool parentDeleted) {
        var list = new List<(string Name, string SubPath, WzValue Value, bool IsDeleted)>();
        IReadOnlyList<WzProperty>? props = target.Children;
        if (props is not null) {
            foreach (WzProperty prop in props) {
                string propSubPath = string.IsNullOrEmpty(parentSubPath) ? prop.Name : $"{parentSubPath}/{prop.Name}";
                list.Add((prop.Name, propSubPath, prop.Value, parentDeleted));
            }
        }

        IReadOnlyList<DeletedPropertyEntry> deletedProps = session.GetDeletedDirectChildProperties(imgPath, parentSubPath);
        foreach (DeletedPropertyEntry del in deletedProps) {
            if (list.Any(item => string.Equals(item.Name, del.Name, StringComparison.Ordinal))) {
                continue;
            }
            int insertIdx = Math.Clamp(del.OriginalIndex, 0, list.Count);
            list.Insert(insertIdx, (del.Name, del.SubPath, del.Value, true));
        }

        return list;
    }

    private static TreeNodeDto BuildPropertyTreeNodeDto(
        ArchiveSession session,
        string imgPath,
        string propSubPath,
        string path,
        string name,
        WzValue val,
        RepackerChangeState changeState = RepackerChangeState.Unchanged) {
        (RepackerPropertyKind kind, string badge, string display) = DescribeWzValue(val);
        int liveChildCount = val switch {
            WzValue.Object(var p) => p.Count,
            WzValue.Canvas c => c.Properties.Count,
            WzValue.Convex(var items) => items.Count,
            _ => 0,
        };
        int deletedChildCount = session.GetDeletedDirectChildProperties(imgPath, propSubPath).Count;
        int childCount = liveChildCount + deletedChildCount;
        int sizeBytes = val switch {
            WzValue.Canvas c => c.Payload.Length,
            WzValue.Sound s => s.DataLength,
            WzValue.RawData r => r.DataLength,
            _ => 0,
        };

        return new TreeNodeDto(
            session.ArchiveId,
            path,
            name,
            RepackerNodeKind.Property,
            kind,
            display,
            badge,
            childCount > 0,
            childCount,
            changeState,
            sizeBytes);
    }

    private static SubPropertyRowDto[] BuildChildRows(
        ArchiveSession session,
        string imgPath,
        string subPath,
        WzValue target,
        bool parentDeleted = false) {
        if (target is WzValue.Convex(var items)) {
            var rows = new SubPropertyRowDto[items.Count];
            for (int i = 0; i < items.Count; i++) {
                string itemName = i.ToString(CultureInfo.InvariantCulture);
                string itemSubPath = string.IsNullOrEmpty(subPath) ? itemName : $"{subPath}/{itemName}";
                string itemPath = $"{imgPath}/{itemSubPath}";
                (RepackerPropertyKind pk, string badge, string disp) = DescribeWzValue(items[i]);
                RepackerChangeState itemState = EvaluatePropertyChangeState(session, imgPath, itemSubPath, items[i], parentDeleted);
                rows[i] = new SubPropertyRowDto(itemName, itemPath, RepackerNodeKind.Property, pk, badge, disp, 0, 0, itemState);
            }
            return rows;
        }

        List<(string Name, string SubPath, WzValue Value, bool IsDeleted)> mergedProps =
            BuildMergedChildProperties(session, imgPath, subPath, target, parentDeleted);
        if (mergedProps.Count == 0) {
            return [];
        }

        var output = new SubPropertyRowDto[mergedProps.Count];
        for (int i = 0; i < mergedProps.Count; i++) {
            (string pName, string pSubPath, WzValue pVal, bool pDeleted) = mergedProps[i];
            string pPath = $"{imgPath}/{pSubPath}";
            (RepackerPropertyKind pk, string badge, string disp) = DescribeWzValue(pVal);
            int liveChildCount = pVal.Children?.Count ?? (pVal is WzValue.Convex(var ci) ? ci.Count : 0);
            int deletedChildCount = session.GetDeletedDirectChildProperties(imgPath, pSubPath).Count;
            int childCount = liveChildCount + deletedChildCount;
            int sz = pVal switch {
                WzValue.Canvas c => c.Payload.Length,
                WzValue.Sound s => s.DataLength,
                WzValue.RawData r => r.DataLength,
                _ => 0,
            };
            RepackerChangeState pState = EvaluatePropertyChangeState(session, imgPath, pSubPath, pVal, pDeleted);
            output[i] = new SubPropertyRowDto(pName, pPath, RepackerNodeKind.Property, pk, badge, disp, childCount, sz, pState);
        }
        return output;
    }

    public static (RepackerPropertyKind Kind, string Badge, string DisplayValue) DescribeWzValue(WzValue v) => v switch {
        WzValue.Null => (RepackerPropertyKind.Null, "NULL", "null"),
        WzValue.Short(var x) => (RepackerPropertyKind.Short, "I16", x.ToString(CultureInfo.InvariantCulture)),
        WzValue.Int(var x) => (RepackerPropertyKind.Int, "I32", x.ToString(CultureInfo.InvariantCulture)),
        WzValue.Long(var x) => (RepackerPropertyKind.Long, "I64", x.ToString(CultureInfo.InvariantCulture)),
        WzValue.Float(var x) => (RepackerPropertyKind.Float, "F32", x.ToString("G", CultureInfo.InvariantCulture)),
        WzValue.Double(var x) => (RepackerPropertyKind.Double, "F64", x.ToString("G", CultureInfo.InvariantCulture)),
        WzValue.String(var s) => (RepackerPropertyKind.String, "STR", s),
        WzValue.Vector(var x, var y) => (RepackerPropertyKind.Vector, "VEC", $"({x}, {y})"),
        WzValue.Uol(var s) => (RepackerPropertyKind.Uol, "UOL", $"-> {s}"),
        WzValue.Object(var props) => (RepackerPropertyKind.Object, "SUB", $"{props.Count} properties"),
        WzValue.Canvas c => (
            RepackerPropertyKind.Canvas,
            "PNG",
            $"{c.Width}x{c.Height} ({WzCanvasFormatExtensions.GetDisplayName(c.Format)})"),
        WzValue.Sound(var len, var dur) => (
            RepackerPropertyKind.Sound,
            "SND",
            $"{dur} ms ({FormatByteSize(len)})"),
        WzValue.Convex(var items) => (RepackerPropertyKind.Convex, "CVX", $"{items.Count} shapes"),
        WzValue.RawData(var len) => (RepackerPropertyKind.RawData, "RAW", FormatByteSize(len)),
        _ => (RepackerPropertyKind.None, "UNK", string.Empty),
    };

    private static byte[] ExtractBinarySliceForHexView(
        ArchiveSession session,
        string imgPath,
        bool isImageRoot,
        WzValue target) {
        const int maxHexBytes = 65536;
        if (isImageRoot) {
            ReadOnlySpan<byte> span = session.GetRawImageMemory(imgPath).Span;
            return span[..Math.Min(span.Length, maxHexBytes)].ToArray();
        }
        switch (target) {
            case WzValue.Canvas c: {
                ReadOnlySpan<byte> span = c.Payload.Span;
                return span[..Math.Min(span.Length, maxHexBytes)].ToArray();
            }
            case WzValue.Sound s: {
                ReadOnlySpan<byte> hSpan = s.Header.Span;
                ReadOnlySpan<byte> pSpan = s.Payload.Span;
                int total = Math.Min(maxHexBytes, hSpan.Length + pSpan.Length);
                byte[] combined = new byte[total];
                int hCopy = Math.Min(total, hSpan.Length);
                hSpan[..hCopy].CopyTo(combined);
                int pCopy = Math.Min(total - hCopy, pSpan.Length);
                if (pCopy > 0) {
                    pSpan[..pCopy].CopyTo(combined.AsSpan(hCopy, pCopy));
                }
                return combined;
            }
            case WzValue.RawData r: {
                ReadOnlySpan<byte> span = r.Payload.Span;
                return span[..Math.Min(span.Length, maxHexBytes)].ToArray();
            }
            default: {
                var wrapper = new WzValue.Object([new WzProperty("value", target)]);
                byte[] serialized = wrapper.ToBytes();
                return serialized.Length <= maxHexBytes ? serialized : serialized[..maxHexBytes];
            }
        }
    }

    private static void SearchDirectoryUnsafe(
        ArchiveSession session,
        VirtualFsNode dir,
        string prefix,
        string nameFilter,
        string valFilter,
        RepackerPropertyKind? kindFilter,
        bool exact,
        StringComparison cmp,
        int maxResults,
        List<SearchResultItemDto> results) {
        session.EnsureDirectoryLinked(dir);
        foreach (VirtualFsNode child in dir.Children) {
            if (results.Count >= maxResults) return;
            if (child.IsDeleted) continue;
            string fullPath = string.IsNullOrEmpty(prefix) ? child.Name : $"{prefix}/{child.Name}";

            if (child.IsDir) {
                if (kindFilter is null && string.IsNullOrEmpty(valFilter) && MatchesText(child.Name, fullPath, nameFilter, exact, cmp)) {
                    results.Add(new SearchResultItemDto(
                        session.ArchiveId,
                        session.Name,
                        fullPath,
                        child.Name,
                        RepackerNodeKind.Directory,
                        RepackerPropertyKind.None,
                        "DIR",
                        $"{child.Children.Count} items"));
                }
                SearchDirectoryUnsafe(session, child, fullPath, nameFilter, valFilter, kindFilter, exact, cmp, maxResults, results);
            } else if (child.IsImage) {
                if (kindFilter is null && string.IsNullOrEmpty(valFilter) && MatchesText(child.Name, fullPath, nameFilter, exact, cmp)) {
                    results.Add(new SearchResultItemDto(
                        session.ArchiveId,
                        session.Name,
                        fullPath,
                        child.Name,
                        RepackerNodeKind.Image,
                        RepackerPropertyKind.Object,
                        "IMG",
                        FormatByteSize(session.GetImageSize(fullPath))));
                }

                try {
                    WzValue imgRoot = session.GetOrParseImage(fullPath);
                    SearchPropertiesUnsafe(session, fullPath, "", imgRoot, nameFilter, valFilter, kindFilter, exact, cmp, maxResults, results);
                } catch {
                    // Skip unreadable image during global search
                }
            }
        }
    }

    private static void SearchPropertiesUnsafe(
        ArchiveSession session,
        string imgPath,
        string subPath,
        WzValue parentVal,
        string nameFilter,
        string valFilter,
        RepackerPropertyKind? kindFilter,
        bool exact,
        StringComparison cmp,
        int maxResults,
        List<SearchResultItemDto> results) {
        IReadOnlyList<WzProperty>? props = parentVal.Children;
        if (props is null) return;

        foreach (WzProperty prop in props) {
            if (results.Count >= maxResults) return;
            string propSubPath = string.IsNullOrEmpty(subPath) ? prop.Name : $"{subPath}/{prop.Name}";
            string fullPath = $"{imgPath}/{propSubPath}";
            (RepackerPropertyKind pKind, string badge, string disp) = DescribeWzValue(prop.Value);

            bool kindMatches = !kindFilter.HasValue || kindFilter.Value == RepackerPropertyKind.None || kindFilter.Value == pKind;
            if (kindMatches) {
                bool matches;
                if (!string.IsNullOrEmpty(nameFilter) && string.IsNullOrEmpty(valFilter)) {
                    matches = MatchesText(prop.Name, fullPath, nameFilter, exact, cmp)
                        || MatchesSingleText(disp, nameFilter, exact, cmp);
                } else {
                    bool nameMatches = string.IsNullOrEmpty(nameFilter) || MatchesText(prop.Name, fullPath, nameFilter, exact, cmp);
                    bool valMatches = string.IsNullOrEmpty(valFilter) || MatchesSingleText(disp, valFilter, exact, cmp);
                    matches = nameMatches && valMatches && (!string.IsNullOrEmpty(nameFilter) || !string.IsNullOrEmpty(valFilter) || kindFilter.HasValue);
                }
                if (matches) {
                    results.Add(new SearchResultItemDto(
                        session.ArchiveId,
                        session.Name,
                        fullPath,
                        prop.Name,
                        RepackerNodeKind.Property,
                        pKind,
                        badge,
                        disp));
                }
            }

            if (prop.Value.Children is { Count: > 0 }) {
                SearchPropertiesUnsafe(session, imgPath, propSubPath, prop.Value, nameFilter, valFilter, kindFilter, exact, cmp, maxResults, results);
            }
        }
    }

    private static bool MatchesText(string name, string fullPath, string filter, bool exact, StringComparison cmp) {
        if (string.IsNullOrEmpty(filter)) return true;
        if (exact) {
            return string.Equals(name, filter, cmp) || string.Equals(fullPath, filter, cmp);
        }
        return name.Contains(filter, cmp) || fullPath.Contains(filter, cmp);
    }

    private static bool MatchesSingleText(string text, string filter, bool exact, StringComparison cmp) {
        if (string.IsNullOrEmpty(filter)) return true;
        return exact ? string.Equals(text, filter, cmp) : text.Contains(filter, cmp);
    }

    private static NodeInspectionDto ErrorInspection(string archiveId, string path, string error) =>
        new(
            false,
            error,
            archiveId,
            string.Empty,
            path,
            string.Empty,
            null,
            RepackerNodeKind.Property,
            RepackerPropertyKind.None,
            "ERR",
            string.Empty,
            null,
            null,
            null,
            null,
            0,
            0,
            RepackerChangeState.Unchanged,
            false,
            false,
            "{}",
            [],
            null,
            [],
            null,
            null,
            []);

    private static string FormatByteSize(int bytes) => bytes switch {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F2} MB",
    };

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose() {
        lock (syncLock) {
            foreach (ArchiveSession s in archives) {
                s.Dispose();
            }
            archives.Clear();
        }
    }
}
