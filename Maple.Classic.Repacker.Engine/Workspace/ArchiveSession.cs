using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.Engine.Workspace;

public sealed class VirtualFsNode(string name, WzNodeKind kind, WzNode? backingNode = null) {
    public string Name { get; set; } = name;
    public WzNodeKind Kind { get; } = kind;
    public WzNode? BackingNode { get; set; } = backingNode;
    public List<VirtualFsNode> Children { get; } = [];
    public string? DiskDirectoryPath { get; set; }
    public bool IsDiskLinked { get; set; }
    public bool IsUserAdded { get; set; }
    public bool IsDeleted { get; set; }
    public bool HasChildStructureChanges { get; set; }

    public bool IsDir => Kind == WzNodeKind.Directory;
    public bool IsImage => Kind == WzNodeKind.Image;
}

public sealed record DeletedPropertyEntry(
    string SubPath,
    string ParentSubPath,
    string Name,
    int OriginalIndex,
    WzValue Value);

/// <summary>
/// Represents an open WZ archive in the Repacker workspace with lazy-loaded mutable <see cref="WzValue"/> trees,
/// virtual directory/image tracking, automatic split/directory <c>.wz</c> linking, and per-image undo/redo history.
/// </summary>
public sealed class ArchiveSession : IDisposable {
    private const int MaxUndoDepth = 32;

    private sealed record ImageUndoSnapshot(
        byte[] ImageBytes,
        Dictionary<string, DeletedPropertyEntry> DeletedProperties);

    private readonly Dictionary<string, WzValue> loadedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WzValue> baselineImages = new(StringComparer.Ordinal);
    private readonly HashSet<string> modifiedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, DeletedPropertyEntry>> deletedPropertiesByImage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stack<ImageUndoSnapshot>> undoStacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stack<ImageUndoSnapshot>> redoStacks = new(StringComparer.Ordinal);
    private readonly List<WzArchive> linkedArchives = [];
    private readonly HashSet<string> openedArchivePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ushort? explicitVersion;

    public string ArchiveId { get; }
    public string Name { get; set; }
    public string FilePath { get; set; }
    public WzArchive Archive { get; private set; }
    public VirtualFsNode VirtualRoot { get; private set; }
    public bool HasStructureChanges { get; set; }
    public bool IsUserCreatedArchive { get; set; }

    public bool IsModified => HasStructureChanges || modifiedImages.Count > 0;
    public int ModifiedImageCount => modifiedImages.Count;
    public int LinkedArchiveCount => linkedArchives.Count;

    public ArchiveSession(string archiveId, string name, string filePath, WzArchive archive, ushort? version = null) {
        ArchiveId = archiveId;
        Name = name;
        FilePath = filePath;
        Archive = archive;
        explicitVersion = version;
        VirtualRoot = BuildVirtualTree(archive.Root);

        if (!string.IsNullOrWhiteSpace(filePath)) {
            string fullPath = Path.GetFullPath(filePath);
            openedArchivePaths.Add(fullPath);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) {
                VirtualRoot.DiskDirectoryPath = dir;
                AssignChildDiskDirectories(VirtualRoot, isRoot: true);
                EnsureDirectoryLinkedWithChildren(VirtualRoot);
            }
        }
    }

    public string[] GetLinkedFilePaths() {
        return [.. openedArchivePaths.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)];
    }

    public bool HasLinkedFile(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            return false;
        }
        return openedArchivePaths.Contains(Path.GetFullPath(filePath));
    }

    public bool HasLinkedOrSubdirectoryFile(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            return false;
        }
        string fullPath = Path.GetFullPath(filePath);
        if (openedArchivePaths.Contains(fullPath)) {
            return true;
        }
        if (string.IsNullOrEmpty(VirtualRoot.DiskDirectoryPath)) {
            return false;
        }

        string rootDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(VirtualRoot.DiskDirectoryPath));
        string? fileDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(fileDir)) {
            string normFileDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fileDir));
            if (string.Equals(rootDir, normFileDir, StringComparison.OrdinalIgnoreCase)) {
                string rootStem = StripNumericShardSuffix(Path.GetFileNameWithoutExtension(Name));
                string candidateStem = StripNumericShardSuffix(Path.GetFileNameWithoutExtension(fullPath));
                if (IsSameOrCanvasStem(rootStem, candidateStem)) {
                    return true;
                }
            }
        }

        if (IsInsideAnyVirtualChildDirectory(VirtualRoot, fullPath)) {
            return true;
        }

        string rootPrefix = rootDir + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        string rel = fullPath[rootPrefix.Length..];
        int firstSep = rel.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (firstSep <= 0) {
            return false;
        }

        string firstSubDir = rel[..firstSep];
        return VirtualRoot.Children.Any(c =>
            c.IsDir && string.Equals(c.Name, firstSubDir, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSameOrCanvasStem(string stemA, string stemB) {
        string baseA = StripNumericShardSuffix(stemA);
        string baseB = StripNumericShardSuffix(stemB);
        return string.Equals(baseA, baseB, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(baseA, $"{baseB}_Canvas", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(baseA, $"{baseB}__Canvas", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(baseB, $"{baseA}_Canvas", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(baseB, $"{baseA}__Canvas", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideAnyVirtualChildDirectory(VirtualFsNode node, string fullFilePath) {
        foreach (VirtualFsNode child in node.Children) {
            if (!child.IsDir) {
                continue;
            }
            if (!string.IsNullOrEmpty(child.DiskDirectoryPath)) {
                string childDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child.DiskDirectoryPath));
                string childPrefix = childDir + Path.DirectorySeparatorChar;
                if (fullFilePath.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            if (IsInsideAnyVirtualChildDirectory(child, fullFilePath)) {
                return true;
            }
        }
        return false;
    }

    public static string StripNumericShardSuffix(string stem) {
        int lastUnderscore = stem.LastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore == stem.Length - 1) {
            return stem;
        }
        ReadOnlySpan<char> suffix = stem.AsSpan(lastUnderscore + 1);
        foreach (char c in suffix) {
            if (!char.IsAsciiDigit(c)) {
                return stem;
            }
        }
        return stem[..lastUnderscore];
    }

    private void AssignChildDiskDirectories(VirtualFsNode dirNode, bool isRoot = false) {
        string? diskDir = dirNode.DiskDirectoryPath;
        if (string.IsNullOrEmpty(diskDir) || !Directory.Exists(diskDir)) {
            return;
        }

        string dirStem = isRoot
            ? StripNumericShardSuffix(Path.GetFileNameWithoutExtension(Name))
            : StripNumericShardSuffix(dirNode.Name);

        foreach (VirtualFsNode child in dirNode.Children) {
            if (!child.IsDir) {
                continue;
            }
            if (!string.IsNullOrEmpty(child.DiskDirectoryPath) && Directory.Exists(child.DiskDirectoryPath)) {
                continue;
            }

            string directSubDir = Path.Combine(diskDir, child.Name);
            if (Directory.Exists(directSubDir)) {
                child.DiskDirectoryPath = directSubDir;
                continue;
            }

            string nestedStemSubDir = Path.Combine(diskDir, dirStem, child.Name);
            if (Directory.Exists(nestedStemSubDir)) {
                child.DiskDirectoryPath = nestedStemSubDir;
                continue;
            }

            // If this is a root archive inside Data/<Stem>/<Stem>.wz (such as Data/Base/Base.wz),
            // DIR nodes like "Character", "Map", "String" live in sibling directories Data/<ChildName>.
            if (isRoot &&
                string.Equals(Path.GetFileName(diskDir), dirStem, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(child.Name, dirStem, StringComparison.OrdinalIgnoreCase)) {
                string? parentDataDir = Path.GetDirectoryName(diskDir);
                if (!string.IsNullOrEmpty(parentDataDir)) {
                    string siblingDir = Path.Combine(parentDataDir, child.Name);
                    if (Directory.Exists(siblingDir)) {
                        child.DiskDirectoryPath = siblingDir;
                    }
                }
            }
        }
    }

    public bool EnsureDirectoryLinkedWithChildren(VirtualFsNode dirNode, int maxDepth = 3) {
        if (!dirNode.IsDir) {
            return false;
        }
        bool changed = EnsureDirectoryLinked(dirNode);
        if (maxDepth <= 0) {
            return changed;
        }
        for (int i = 0; i < dirNode.Children.Count; i++) {
            VirtualFsNode child = dirNode.Children[i];
            if (child.IsDir && EnsureDirectoryLinkedWithChildren(child, maxDepth - 1)) {
                changed = true;
            }
        }
        return changed;
    }

    public bool EnsureDirectoryLinked(VirtualFsNode dirNode) {
        if (!dirNode.IsDir || dirNode.IsDiskLinked) {
            return false;
        }
        dirNode.IsDiskLinked = true;

        bool isRoot = ReferenceEquals(dirNode, VirtualRoot);
        string targetStem = isRoot
            ? StripNumericShardSuffix(Path.GetFileNameWithoutExtension(Name))
            : StripNumericShardSuffix(dirNode.Name);

        bool linkedAny = false;
        string? diskDir = dirNode.DiskDirectoryPath;

        if (!string.IsNullOrEmpty(diskDir) && Directory.Exists(diskDir)) {
            try {
                string[] wzFiles = OrderWzFiles(Directory.GetFiles(diskDir, "*.wz", SearchOption.TopDirectoryOnly)).ToArray();
                foreach (string wzFile in wzFiles) {
                    string fullWzPath = Path.GetFullPath(wzFile);
                    if (openedArchivePaths.Contains(fullWzPath)) {
                        continue;
                    }

                    string fileStem = Path.GetFileNameWithoutExtension(fullWzPath);
                    string baseFileStem = StripNumericShardSuffix(fileStem);

                    if (string.Equals(baseFileStem, targetStem, StringComparison.OrdinalIgnoreCase)) {
                        if (TryOpenAndMergeArchive(fullWzPath, dirNode)) {
                            linkedAny = true;
                        }
                    } else if (string.Equals(baseFileStem, $"{targetStem}_Canvas", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(baseFileStem, $"{targetStem}__Canvas", StringComparison.OrdinalIgnoreCase)) {
                        VirtualFsNode canvasDir = GetOrCreateChildDirectory(dirNode, "_Canvas");
                        if (TryOpenAndMergeArchive(fullWzPath, canvasDir)) {
                            linkedAny = true;
                        }
                    } else if (!isRoot) {
                        // In a dedicated subdirectory for a DIR node, if there is no child directory matching baseFileStem,
                        // merge the .wz file directly into this DIR node.
                        string potentialSubDir = Path.Combine(diskDir, baseFileStem);
                        bool matchesChildDir = Directory.Exists(potentialSubDir) ||
                            dirNode.Children.Any(c => c.IsDir && string.Equals(c.Name, baseFileStem, StringComparison.OrdinalIgnoreCase));
                        if (!matchesChildDir && TryOpenAndMergeArchive(fullWzPath, dirNode)) {
                            linkedAny = true;
                        }
                    }
                }

                // Also discover subdirectories on disk that contain .wz files
                foreach (string subDirPath in Directory.GetDirectories(diskDir).OrderBy(static d => d, StringComparer.OrdinalIgnoreCase)) {
                    string subDirName = Path.GetFileName(subDirPath);
                    VirtualFsNode? existingDir = dirNode.Children.Find(c =>
                        c.IsDir && string.Equals(c.Name, subDirName, StringComparison.OrdinalIgnoreCase));
                    if (existingDir is not null) {
                        existingDir.DiskDirectoryPath ??= subDirPath;
                    } else if (!isRoot && DirectoryContainsWzFiles(subDirPath)) {
                        var subNode = new VirtualFsNode(subDirName, WzNodeKind.Directory) {
                            DiskDirectoryPath = subDirPath,
                        };
                        dirNode.Children.Add(subNode);
                        linkedAny = true;
                    } else if (isRoot && VirtualRoot.Children.Count == 0 && DirectoryContainsWzFiles(subDirPath)) {
                        var subNode = new VirtualFsNode(subDirName, WzNodeKind.Directory) {
                            DiskDirectoryPath = subDirPath,
                        };
                        dirNode.Children.Add(subNode);
                        linkedAny = true;
                    }
                }
            } catch {
                // Ignore IO access errors on restricted directories
            }
        } else if (!isRoot && !string.IsNullOrEmpty(VirtualRoot.DiskDirectoryPath) && Directory.Exists(VirtualRoot.DiskDirectoryPath)) {
            // Flat directory fallback: check if <DirName>.wz / <DirName>_000.wz or <RootStem>_Canvas_000.wz exist in root folder
            string rootDiskDir = VirtualRoot.DiskDirectoryPath;
            string rootStem = StripNumericShardSuffix(Path.GetFileNameWithoutExtension(Name));
            try {
                string[] wzFiles = OrderWzFiles(Directory.GetFiles(rootDiskDir, "*.wz", SearchOption.TopDirectoryOnly)).ToArray();
                foreach (string wzFile in wzFiles) {
                    string fullWzPath = Path.GetFullPath(wzFile);
                    if (openedArchivePaths.Contains(fullWzPath)) {
                        continue;
                    }

                    string fileStem = Path.GetFileNameWithoutExtension(fullWzPath);
                    string baseFileStem = StripNumericShardSuffix(fileStem);

                    bool matchesDir = string.Equals(baseFileStem, targetStem, StringComparison.OrdinalIgnoreCase) ||
                        ((string.Equals(targetStem, "_Canvas", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(targetStem, "Canvas", StringComparison.OrdinalIgnoreCase)) &&
                         (string.Equals(baseFileStem, $"{rootStem}_Canvas", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(baseFileStem, $"{rootStem}__Canvas", StringComparison.OrdinalIgnoreCase)));

                    if (matchesDir && TryOpenAndMergeArchive(fullWzPath, dirNode)) {
                        linkedAny = true;
                    }
                }
            } catch {
                // Ignore IO errors
            }
        }

        AssignChildDiskDirectories(dirNode, isRoot);
        return linkedAny;
    }

    private bool TryOpenAndMergeArchive(string fullWzPath, VirtualFsNode targetDir) {
        try {
            var linkedWz = new WzArchive(fullWzPath, explicitVersion);
            openedArchivePaths.Add(fullWzPath);
            linkedArchives.Add(linkedWz);
            MergeWzNodeChildrenIntoVirtualDir(targetDir, linkedWz.Root.Children);
            return true;
        } catch {
            return false;
        }
    }

    private static VirtualFsNode GetOrCreateChildDirectory(VirtualFsNode parent, string dirName) {
        VirtualFsNode? existing = parent.Children.Find(c =>
            c.IsDir && string.Equals(c.Name, dirName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) {
            return existing;
        }
        var created = new VirtualFsNode(dirName, WzNodeKind.Directory);
        parent.Children.Add(created);
        return created;
    }

    private static void MergeWzNodeChildrenIntoVirtualDir(VirtualFsNode targetDir, IReadOnlyList<WzNode> wzChildren) {
        foreach (WzNode child in wzChildren) {
            if (child.IsDir) {
                VirtualFsNode? existingDir = targetDir.Children.Find(c =>
                    c.IsDir && string.Equals(c.Name, child.Name, StringComparison.OrdinalIgnoreCase));
                if (existingDir is null) {
                    existingDir = new VirtualFsNode(child.Name, WzNodeKind.Directory, child);
                    targetDir.Children.Add(existingDir);
                } else {
                    existingDir.BackingNode ??= child;
                }

                if (child.Children.Count > 0) {
                    MergeWzNodeChildrenIntoVirtualDir(existingDir, child.Children);
                }
            } else {
                VirtualFsNode? existingImg = targetDir.Children.Find(c =>
                    c.IsImage && string.Equals(c.Name, child.Name, StringComparison.OrdinalIgnoreCase));
                if (existingImg is null) {
                    targetDir.Children.Add(new VirtualFsNode(child.Name, WzNodeKind.Image, child));
                } else if ((existingImg.BackingNode is null || existingImg.BackingNode.Size == 0) && child.Size > 0) {
                    existingImg.BackingNode = child;
                }
            }
        }
    }

    private static IEnumerable<string> OrderWzFiles(IEnumerable<string> paths) {
        return paths
            .OrderBy(static p => {
                string stem = Path.GetFileNameWithoutExtension(p);
                string baseStem = StripNumericShardSuffix(stem);
                return string.Equals(stem, baseStem, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            })
            .ThenBy(static p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase);
    }

    private static bool DirectoryContainsWzFiles(string dirPath) {
        try {
            return Directory.EnumerateFiles(dirPath, "*.wz", SearchOption.AllDirectories).Any();
        } catch {
            return false;
        }
    }

    private static VirtualFsNode BuildVirtualTree(WzNode wzNode) {
        var vNode = new VirtualFsNode(wzNode.Name, wzNode.Kind, wzNode);
        foreach (WzNode child in wzNode.Children) {
            vNode.Children.Add(BuildVirtualTree(child));
        }
        return vNode;
    }

    public ArchiveSummaryDto ToSummaryDto() {
        (int dirs, int imgs) = CountVirtualRecursive(VirtualRoot);
        long fileSize = 0;
        if (!string.IsNullOrEmpty(FilePath) && System.IO.File.Exists(FilePath)) {
            try {
                fileSize = new FileInfo(FilePath).Length;
            } catch {
                // Fall back to header size
            }
        }
        if (fileSize == 0) {
            fileSize = (long) Archive.Header.FSize + Archive.Header.FStart;
        }

        RepackerChangeState archiveChangeState = IsUserCreatedArchive
            ? RepackerChangeState.Added
            : IsModified
                ? RepackerChangeState.Modified
                : RepackerChangeState.Unchanged;

        return new ArchiveSummaryDto(
            ArchiveId,
            Name,
            FilePath,
            Archive.Version,
            Archive.Header.EncVer,
            Archive.Header.FStart,
            Archive.Header.Copyright,
            fileSize,
            dirs,
            imgs,
            modifiedImages.Count,
            archiveChangeState,
            GetLinkedFilePaths());
    }

    public static (int Directories, int Images) CountVirtualRecursive(VirtualFsNode root) {
        int dirs = 0;
        int imgs = 0;
        foreach (VirtualFsNode c in root.Children) {
            if (c.IsDeleted) {
                continue;
            }
            if (c.IsDir) {
                dirs++;
                (int d, int i) = CountVirtualRecursive(c);
                dirs += d;
                imgs += i;
            } else {
                imgs++;
            }
        }
        return (dirs, imgs);
    }

    public bool IsVirtualPathDeleted(ReadOnlySpan<char> path) {
        path = path.Trim('/');
        if (path.IsEmpty) {
            return false;
        }

        VirtualFsNode cur = VirtualRoot;
        foreach (Range range in path.Split('/')) {
            ReadOnlySpan<char> part = path[range];
            if (part.IsEmpty) {
                continue;
            }
            if (cur.IsDir) {
                EnsureDirectoryLinked(cur);
            }
            VirtualFsNode? next = null;
            foreach (VirtualFsNode child in cur.Children) {
                if (child.Name.AsSpan().SequenceEqual(part)) {
                    next = child;
                    break;
                }
            }
            if (next is null) {
                foreach (VirtualFsNode child in cur.Children) {
                    if (child.Name.AsSpan().Equals(part, StringComparison.OrdinalIgnoreCase)) {
                        next = child;
                        break;
                    }
                }
            }
            if (next is null) {
                return false;
            }
            if (next.IsDeleted) {
                return true;
            }
            cur = next;
        }
        return cur.IsDeleted;
    }

    public VirtualFsNode? ResolveVirtualNode(ReadOnlySpan<char> path) {
        path = path.Trim('/');
        EnsureDirectoryLinked(VirtualRoot);
        if (path.IsEmpty) {
            return VirtualRoot;
        }

        VirtualFsNode cur = VirtualRoot;
        foreach (Range range in path.Split('/')) {
            ReadOnlySpan<char> part = path[range];
            if (part.IsEmpty) {
                continue;
            }
            if (cur.IsDir) {
                EnsureDirectoryLinked(cur);
            }
            VirtualFsNode? next = null;
            foreach (VirtualFsNode child in cur.Children) {
                if (child.Name.AsSpan().SequenceEqual(part)) {
                    next = child;
                    break;
                }
            }
            if (next is null) {
                foreach (VirtualFsNode child in cur.Children) {
                    if (child.Name.AsSpan().Equals(part, StringComparison.OrdinalIgnoreCase)) {
                        next = child;
                        break;
                    }
                }
            }
            if (next is null) {
                return null;
            }
            if (next.IsDir) {
                EnsureDirectoryLinked(next);
            }
            cur = next;
        }
        return cur;
    }

    public bool IsImageModified(string imagePath) => modifiedImages.Contains(NormalizePath(imagePath));

    public bool IsImageAdded(string imagePath) {
        if (IsUserCreatedArchive) {
            return true;
        }
        VirtualFsNode? vNode = ResolveVirtualNode(NormalizePath(imagePath));
        return vNode?.IsUserAdded == true;
    }

    public bool TryGetBaselineImage(string imagePath, out WzValue? baseline) =>
        baselineImages.TryGetValue(NormalizePath(imagePath), out baseline);

    public RepackerChangeState GetDirectoryChangeState(string dirPath, VirtualFsNode dirNode) {
        if (dirNode.IsDeleted || IsVirtualPathDeleted(dirPath)) {
            return RepackerChangeState.Deleted;
        }
        if (IsUserCreatedArchive || dirNode.IsUserAdded) {
            return RepackerChangeState.Added;
        }
        return IsDirectorySubtreeDirty(NormalizePath(dirPath), dirNode)
            ? RepackerChangeState.Modified
            : RepackerChangeState.Unchanged;
    }

    public RepackerChangeState GetImageChangeState(string imagePath, bool parentDeleted = false) {
        string norm = NormalizePath(imagePath);
        if (parentDeleted || IsVirtualPathDeleted(norm)) {
            return RepackerChangeState.Deleted;
        }
        if (IsImageAdded(norm)) {
            return RepackerChangeState.Added;
        }
        return IsImageModified(norm)
            ? RepackerChangeState.Modified
            : RepackerChangeState.Unchanged;
    }

    private bool IsDirectorySubtreeDirty(string normDirPath, VirtualFsNode dirNode) {
        if (dirNode.HasChildStructureChanges) {
            return true;
        }
        string prefix = string.IsNullOrEmpty(normDirPath) ? string.Empty : normDirPath + "/";
        foreach (string modImg in modifiedImages) {
            if (string.IsNullOrEmpty(prefix) || modImg.StartsWith(prefix, StringComparison.Ordinal)) {
                return true;
            }
        }
        foreach (VirtualFsNode child in dirNode.Children) {
            if (child.IsUserAdded || child.IsDeleted) {
                return true;
            }
            if (child.IsDir) {
                string childPath = string.IsNullOrEmpty(normDirPath) ? child.Name : $"{normDirPath}/{child.Name}";
                if (IsDirectorySubtreeDirty(childPath, child)) {
                    return true;
                }
            }
        }
        return false;
    }

    public bool IsPathModified(string path) {
        string norm = NormalizePath(path);
        if (string.IsNullOrEmpty(norm)) {
            return !IsUserCreatedArchive && IsModified;
        }
        (string? imgPath, _) = SplitImageAndSubPath(norm);
        if (imgPath is not null) {
            return GetImageChangeState(imgPath) == RepackerChangeState.Modified;
        }
        VirtualFsNode? dirNode = ResolveVirtualNode(norm);
        if (dirNode is null) {
            return false;
        }
        return GetDirectoryChangeState(norm, dirNode) == RepackerChangeState.Modified;
    }

    public WzValue GetOrParseImage(string imagePath) {
        string norm = NormalizePath(imagePath);
        if (loadedImages.TryGetValue(norm, out WzValue? cached)) {
            return cached;
        }

        VirtualFsNode? vNode = ResolveVirtualNode(norm);
        if (vNode is null || !vNode.IsImage) {
            throw new KeyNotFoundException($"No image node found at path \"{norm}\" in archive \"{Name}\".");
        }

        WzValue parsed = vNode.BackingNode is not null
            ? vNode.BackingNode.ParseImage()
            : new WzValue.Object();
        loadedImages[norm] = parsed;
        return parsed;
    }

    public ReadOnlyMemory<byte> GetRawImageMemory(string imagePath) {
        string norm = NormalizePath(imagePath);
        if (modifiedImages.Contains(norm) && loadedImages.TryGetValue(norm, out WzValue? modifiedVal)) {
            return modifiedVal.ToBytes();
        }
        VirtualFsNode? vNode = ResolveVirtualNode(norm);
        if (vNode?.BackingNode is not null) {
            return vNode.BackingNode.GetMemory();
        }
        if (loadedImages.TryGetValue(norm, out WzValue? loaded)) {
            return loaded.ToBytes();
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    public int GetImageSize(string imagePath) {
        string norm = NormalizePath(imagePath);
        VirtualFsNode? vNode = ResolveVirtualNode(norm);
        if (!modifiedImages.Contains(norm) && vNode?.BackingNode is not null) {
            return vNode.BackingNode.Size;
        }
        if (loadedImages.TryGetValue(norm, out WzValue? val)) {
            return val.ToBytes().Length;
        }
        return vNode?.BackingNode?.Size ?? 0;
    }

    private void EnsureBaselineRecorded(string normImagePath) {
        if (baselineImages.ContainsKey(normImagePath)) {
            return;
        }
        VirtualFsNode? vNode = ResolveVirtualNode(normImagePath);
        if (vNode is { IsUserAdded: false, BackingNode: not null }) {
            baselineImages[normImagePath] = vNode.BackingNode.ParseImage();
        }
    }

    private Dictionary<string, DeletedPropertyEntry> CloneDeletedMap(string normImagePath) {
        if (!deletedPropertiesByImage.TryGetValue(normImagePath, out Dictionary<string, DeletedPropertyEntry>? map) || map.Count == 0) {
            return new Dictionary<string, DeletedPropertyEntry>(StringComparer.Ordinal);
        }
        return new Dictionary<string, DeletedPropertyEntry>(map, StringComparer.Ordinal);
    }

    public void RecordUndoSnapshot(string imagePath) {
        string norm = NormalizePath(imagePath);
        EnsureBaselineRecorded(norm);
        WzValue current = GetOrParseImage(norm);
        var snapshot = new ImageUndoSnapshot(current.ToBytes(), CloneDeletedMap(norm));

        if (!undoStacks.TryGetValue(norm, out Stack<ImageUndoSnapshot>? stack)) {
            stack = new Stack<ImageUndoSnapshot>();
            undoStacks[norm] = stack;
        }
        if (stack.Count >= MaxUndoDepth) {
            ImageUndoSnapshot[] items = stack.ToArray();
            stack.Clear();
            for (int i = Math.Min(items.Length - 1, MaxUndoDepth - 2); i >= 0; i--) {
                stack.Push(items[i]);
            }
        }
        stack.Push(snapshot);

        if (redoStacks.TryGetValue(norm, out Stack<ImageUndoSnapshot>? redo)) {
            redo.Clear();
        }
    }

    public void MarkImageModified(string imagePath, WzValue? newRoot = null) {
        string norm = NormalizePath(imagePath);
        EnsureBaselineRecorded(norm);
        if (newRoot is not null) {
            loadedImages[norm] = newRoot;
        }
        SyncDeletedProperties(norm);
        UpdateImageModifiedFlag(norm);
    }

    private void UpdateImageModifiedFlag(string normImagePath) {
        bool hasDeletedProps = deletedPropertiesByImage.TryGetValue(normImagePath, out Dictionary<string, DeletedPropertyEntry>? delMap) && delMap.Count > 0;
        if (!hasDeletedProps &&
            baselineImages.TryGetValue(normImagePath, out WzValue? baseImg) &&
            loadedImages.TryGetValue(normImagePath, out WzValue? curImg) &&
            curImg.Equals(baseImg)) {
            modifiedImages.Remove(normImagePath);
        } else {
            modifiedImages.Add(normImagePath);
        }
    }

    public void RecordDeletedProperty(
        string imagePath,
        string subPath,
        string parentSubPath,
        string name,
        int originalIndex,
        WzValue value) {
        string normImg = NormalizePath(imagePath);
        string normSub = NormalizePath(subPath);
        string normParent = NormalizePath(parentSubPath);
        if (!deletedPropertiesByImage.TryGetValue(normImg, out Dictionary<string, DeletedPropertyEntry>? map)) {
            map = new Dictionary<string, DeletedPropertyEntry>(StringComparer.Ordinal);
            deletedPropertiesByImage[normImg] = map;
        }

        string nestedPrefix = normSub + "/";
        foreach (string key in map.Keys.Where(k => k.StartsWith(nestedPrefix, StringComparison.Ordinal)).ToArray()) {
            map.Remove(key);
        }

        map[normSub] = new DeletedPropertyEntry(normSub, normParent, name, Math.Max(0, originalIndex), value);
    }

    public IReadOnlyList<DeletedPropertyEntry> GetDeletedDirectChildProperties(string imagePath, string parentSubPath) {
        string normImg = NormalizePath(imagePath);
        string normParent = NormalizePath(parentSubPath);
        if (!deletedPropertiesByImage.TryGetValue(normImg, out Dictionary<string, DeletedPropertyEntry>? map) || map.Count == 0) {
            return [];
        }
        return map.Values
            .Where(e => string.Equals(e.ParentSubPath, normParent, StringComparison.Ordinal))
            .OrderBy(static e => e.OriginalIndex)
            .ToArray();
    }

    public (WzValue? Value, bool IsDeleted) ResolvePropertyOrDeleted(string imagePath, WzValue imgRoot, string subPath) {
        string normImg = NormalizePath(imagePath);
        string normSub = NormalizePath(subPath);
        bool imgDeleted = IsVirtualPathDeleted(normImg);

        if (string.IsNullOrEmpty(normSub)) {
            return (imgRoot, imgDeleted);
        }

        WzValue? active = RepackerWorkspace.ResolvePropertyNode(imgRoot, normSub);
        if (active is not null) {
            return (active, imgDeleted);
        }

        if (!deletedPropertiesByImage.TryGetValue(normImg, out Dictionary<string, DeletedPropertyEntry>? map) || map.Count == 0) {
            return (null, false);
        }

        if (map.TryGetValue(normSub, out DeletedPropertyEntry? directEntry)) {
            return (directEntry.Value, true);
        }

        // Check if an ancestor property is deleted and resolve relative to that ancestor
        string cur = normSub;
        while (true) {
            int slash = cur.LastIndexOf('/');
            if (slash <= 0) {
                break;
            }
            cur = cur[..slash];
            if (map.TryGetValue(cur, out DeletedPropertyEntry? ancestorEntry)) {
                string remainder = normSub[(cur.Length + 1)..];
                WzValue? nested = RepackerWorkspace.ResolvePropertyNode(ancestorEntry.Value, remainder);
                if (nested is not null) {
                    return (nested, true);
                }
            }
        }

        return (null, false);
    }

    public (bool Ok, string? RestoredSubPath) RestoreDeletedProperty(string imagePath, WzValue imgRoot, string subPath) {
        string normImg = NormalizePath(imagePath);
        string normSub = NormalizePath(subPath);
        if (!deletedPropertiesByImage.TryGetValue(normImg, out Dictionary<string, DeletedPropertyEntry>? map) || map.Count == 0) {
            return (false, null);
        }

        DeletedPropertyEntry? targetEntry = null;
        if (!map.TryGetValue(normSub, out targetEntry)) {
            string cur = normSub;
            while (true) {
                int slash = cur.LastIndexOf('/');
                if (slash <= 0) {
                    break;
                }
                cur = cur[..slash];
                if (map.TryGetValue(cur, out targetEntry)) {
                    break;
                }
            }
        }

        if (targetEntry is null) {
            return (false, null);
        }

        WzValue? parentVal = string.IsNullOrEmpty(targetEntry.ParentSubPath)
            ? imgRoot
            : RepackerWorkspace.ResolvePropertyNode(imgRoot, targetEntry.ParentSubPath);
        if (parentVal is null) {
            return (false, null);
        }

        if (parentVal is WzValue.Convex(var items)) {
            int insertIdx = Math.Clamp(targetEntry.OriginalIndex, 0, items.Count);
            items.Insert(insertIdx, targetEntry.Value);
        } else {
            List<WzProperty>? props = parentVal switch {
                WzValue.Object(var p) => p,
                WzValue.Canvas { Properties: var p } => p,
                _ => null,
            };
            if (props is null) {
                return (false, null);
            }
            if (props.All(p => p.Name != targetEntry.Name)) {
                int insertIdx = Math.Clamp(targetEntry.OriginalIndex, 0, props.Count);
                props.Insert(insertIdx, new WzProperty(targetEntry.Name, targetEntry.Value));
            }
        }

        map.Remove(targetEntry.SubPath);
        return (true, targetEntry.SubPath);
    }

    private void SyncDeletedProperties(string normImagePath) {
        if (!loadedImages.TryGetValue(normImagePath, out WzValue? curImg)) {
            return;
        }

        if (deletedPropertiesByImage.TryGetValue(normImagePath, out Dictionary<string, DeletedPropertyEntry>? existingMap)) {
            foreach (DeletedPropertyEntry entry in existingMap.Values.ToArray()) {
                // If the property exists again in curImg or its parent no longer exists in curImg, remove the entry
                if (RepackerWorkspace.ResolvePropertyNode(curImg, entry.SubPath) is not null) {
                    existingMap.Remove(entry.SubPath);
                    continue;
                }
                if (!string.IsNullOrEmpty(entry.ParentSubPath) &&
                    RepackerWorkspace.ResolvePropertyNode(curImg, entry.ParentSubPath) is null) {
                    existingMap.Remove(entry.SubPath);
                }
            }
        }
    }

    public bool CanUndo(string? imagePath) {
        if (string.IsNullOrEmpty(imagePath)) return false;
        string norm = NormalizePath(imagePath);
        return undoStacks.TryGetValue(norm, out Stack<ImageUndoSnapshot>? s) && s.Count > 0;
    }

    public bool CanRedo(string? imagePath) {
        if (string.IsNullOrEmpty(imagePath)) return false;
        string norm = NormalizePath(imagePath);
        return redoStacks.TryGetValue(norm, out Stack<ImageUndoSnapshot>? s) && s.Count > 0;
    }

    public bool Undo(string imagePath) {
        string norm = NormalizePath(imagePath);
        if (!undoStacks.TryGetValue(norm, out Stack<ImageUndoSnapshot>? undo) || undo.Count == 0) {
            return false;
        }

        WzValue current = GetOrParseImage(norm);
        if (!redoStacks.TryGetValue(norm, out Stack<ImageUndoSnapshot>? redo)) {
            redo = new Stack<ImageUndoSnapshot>();
            redoStacks[norm] = redo;
        }
        redo.Push(new ImageUndoSnapshot(current.ToBytes(), CloneDeletedMap(norm)));

        ImageUndoSnapshot prev = undo.Pop();
        WzValue restored = WzValue.Parse(prev.ImageBytes);
        loadedImages[norm] = restored;
        deletedPropertiesByImage[norm] = new Dictionary<string, DeletedPropertyEntry>(prev.DeletedProperties, StringComparer.Ordinal);
        SyncDeletedProperties(norm);
        UpdateImageModifiedFlag(norm);
        return true;
    }

    public bool Redo(string imagePath) {
        string norm = NormalizePath(imagePath);
        if (!redoStacks.TryGetValue(norm, out Stack<ImageUndoSnapshot>? redo) || redo.Count == 0) {
            return false;
        }

        WzValue current = GetOrParseImage(norm);
        if (!undoStacks.TryGetValue(norm, out Stack<ImageUndoSnapshot>? undo)) {
            undo = new Stack<ImageUndoSnapshot>();
            undoStacks[norm] = undo;
        }
        undo.Push(new ImageUndoSnapshot(current.ToBytes(), CloneDeletedMap(norm)));

        ImageUndoSnapshot next = redo.Pop();
        WzValue restored = WzValue.Parse(next.ImageBytes);
        loadedImages[norm] = restored;
        deletedPropertiesByImage[norm] = new Dictionary<string, DeletedPropertyEntry>(next.DeletedProperties, StringComparer.Ordinal);
        SyncDeletedProperties(norm);
        UpdateImageModifiedFlag(norm);
        return true;
    }

    public void AddOrReplaceVirtualImage(string imagePath, WzValue rootValue) {
        string norm = NormalizePath(imagePath);
        if (!norm.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) {
            norm += ".img";
        }
        string[] parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        VirtualFsNode cur = VirtualRoot;
        for (int i = 0; i < parts.Length - 1; i++) {
            string dirName = parts[i];
            VirtualFsNode? next = cur.Children.Find(c => c.IsDir && c.Name == dirName);
            if (next is null) {
                next = new VirtualFsNode(dirName, WzNodeKind.Directory) {
                    IsUserAdded = true,
                };
                cur.Children.Add(next);
            } else {
                next.IsDeleted = false;
            }
            cur = next;
        }

        string leafName = parts[^1];
        VirtualFsNode? existing = cur.Children.Find(c => c.Name == leafName);
        if (existing is null) {
            cur.Children.Add(new VirtualFsNode(leafName, WzNodeKind.Image) {
                IsUserAdded = true,
            });
        } else {
            existing.IsDeleted = false;
        }
        loadedImages[norm] = rootValue;
        modifiedImages.Add(norm);
        HasStructureChanges = true;
    }

    public VirtualFsNode AddVirtualDirectory(string parentPath, string dirName) {
        VirtualFsNode parent = ResolveVirtualNode(parentPath)
            ?? throw new KeyNotFoundException($"Parent directory \"{parentPath}\" not found.");
        if (!parent.IsDir) {
            throw new InvalidOperationException($"Node \"{parentPath}\" is not a directory.");
        }
        VirtualFsNode? existing = parent.Children.Find(c => c.Name == dirName);
        if (existing is not null) {
            existing.IsDeleted = false;
            return existing;
        }
        var created = new VirtualFsNode(dirName, WzNodeKind.Directory) {
            IsUserAdded = true,
        };
        parent.Children.Add(created);
        parent.HasChildStructureChanges = true;
        HasStructureChanges = true;
        return created;
    }

    public bool RemoveVirtualNode(string path) {
        string norm = NormalizePath(path);
        if (string.IsNullOrEmpty(norm)) {
            return false;
        }
        int lastSlash = norm.LastIndexOf('/');
        string parentPath = lastSlash >= 0 ? norm[..lastSlash] : "";
        string leafName = lastSlash >= 0 ? norm[(lastSlash + 1)..] : norm;

        VirtualFsNode? parent = ResolveVirtualNode(parentPath);
        if (parent is null) {
            return false;
        }
        VirtualFsNode? target = parent.Children.Find(c => c.Name == leafName);
        if (target is null || target.IsDeleted) {
            return false;
        }

        target.IsDeleted = true;
        parent.HasChildStructureChanges = true;
        HasStructureChanges = true;
        return true;
    }

    public bool RestoreVirtualNode(string path) {
        string norm = NormalizePath(path);
        if (string.IsNullOrEmpty(norm)) {
            return false;
        }

        bool restoredAny = false;
        VirtualFsNode cur = VirtualRoot;
        foreach (string part in norm.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (cur.IsDir) {
                EnsureDirectoryLinked(cur);
            }
            VirtualFsNode? next = cur.Children.Find(c => c.Name == part)
                ?? cur.Children.Find(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next is null) {
                return false;
            }
            if (next.IsDeleted) {
                next.IsDeleted = false;
                restoredAny = true;
            }
            cur = next;
        }

        static void RestoreDescendants(VirtualFsNode node, ref bool changed) {
            foreach (VirtualFsNode child in node.Children) {
                if (child.IsDeleted) {
                    child.IsDeleted = false;
                    changed = true;
                }
                if (child.IsDir) {
                    RestoreDescendants(child, ref changed);
                }
            }
        }

        if (cur.IsDir) {
            RestoreDescendants(cur, ref restoredAny);
        }

        if (restoredAny) {
            RecomputeStructureChangeFlags(VirtualRoot);
            HasStructureChanges = VirtualRoot.HasChildStructureChanges ||
                VirtualRoot.Children.Any(c => c.IsUserAdded || c.IsDeleted || c.HasChildStructureChanges);
        }
        return restoredAny;
    }

    private static bool RecomputeStructureChangeFlags(VirtualFsNode dir) {
        bool dirty = false;
        foreach (VirtualFsNode child in dir.Children) {
            if (child.IsUserAdded || child.IsDeleted) {
                dirty = true;
            }
            if (child.IsDir && RecomputeStructureChangeFlags(child)) {
                dirty = true;
            }
        }
        dir.HasChildStructureChanges = dirty;
        return dirty;
    }

    public bool RenameVirtualNode(string path, string newLeafName) {
        string norm = NormalizePath(path);
        VirtualFsNode? target = ResolveVirtualNode(norm);
        if (target is null || string.IsNullOrWhiteSpace(newLeafName)) {
            return false;
        }

        int lastSlash = norm.LastIndexOf('/');
        string parentPath = lastSlash >= 0 ? norm[..lastSlash] : "";
        VirtualFsNode? parent = ResolveVirtualNode(parentPath);
        if (parent is null || parent.Children.Any(c => !ReferenceEquals(c, target) && c.Name == newLeafName)) {
            return false;
        }

        parent.HasChildStructureChanges = true;
        string newFullPath = string.IsNullOrEmpty(parentPath) ? newLeafName : $"{parentPath}/{newLeafName}";
        if (target.IsImage) {
            EnsureBaselineRecorded(norm);
            WzValue parsed = GetOrParseImage(norm);
            target.Name = newLeafName;
            loadedImages.Remove(norm);
            if (baselineImages.Remove(norm, out WzValue? baseImg)) {
                baselineImages[newFullPath] = baseImg;
            }
            modifiedImages.Remove(norm);
            loadedImages[newFullPath] = parsed;
            modifiedImages.Add(newFullPath);
        } else {
            target.Name = newLeafName;
            string oldPrefix = norm + "/";
            string newPrefix = newFullPath + "/";
            foreach (string key in loadedImages.Keys.Where(k => k.StartsWith(oldPrefix, StringComparison.Ordinal)).ToArray()) {
                WzValue val = loadedImages[key];
                loadedImages.Remove(key);
                string migrated = newPrefix + key[oldPrefix.Length..];
                loadedImages[migrated] = val;
                if (baselineImages.Remove(key, out WzValue? baseImg)) {
                    baselineImages[migrated] = baseImg;
                }
                if (modifiedImages.Remove(key)) {
                    modifiedImages.Add(migrated);
                }
            }
        }

        HasStructureChanges = true;
        return true;
    }

    /// <summary>
    /// Serializes the full archive (merging unmodified base images with modified/added images and honoring deletions)
    /// and resets dirty flags when committed.
    /// </summary>
    public byte[] BuildArchiveBytes(ushort? version = null, bool commitCleanState = false) {
        ushort targetVersion = version ?? Archive.Version;
        using var outArchive = new WzArchive(targetVersion);

        void CollectRecursive(VirtualFsNode dir, string prefix) {
            EnsureDirectoryLinked(dir);
            foreach (VirtualFsNode child in dir.Children) {
                if (child.IsDeleted) {
                    continue;
                }
                string fullPath = string.IsNullOrEmpty(prefix) ? child.Name : $"{prefix}/{child.Name}";
                if (child.IsDir) {
                    CollectRecursive(child, fullPath);
                } else if (child.IsImage) {
                    if (modifiedImages.Contains(fullPath) && loadedImages.TryGetValue(fullPath, out WzValue? modifiedVal)) {
                        outArchive.SetImage(fullPath, modifiedVal);
                    } else if (child.BackingNode is not null) {
                        outArchive.SetImage(fullPath, child.BackingNode.GetMemory());
                    } else if (loadedImages.TryGetValue(fullPath, out WzValue? newVal)) {
                        outArchive.SetImage(fullPath, newVal);
                    }
                }
            }
        }

        CollectRecursive(VirtualRoot, "");
        byte[] bytes = outArchive.ToBytes(targetVersion);

        if (commitCleanState) {
            Archive.Dispose();
            foreach (WzArchive linked in linkedArchives) {
                linked.Dispose();
            }
            linkedArchives.Clear();
            openedArchivePaths.Clear();
            if (!string.IsNullOrWhiteSpace(FilePath)) {
                openedArchivePaths.Add(Path.GetFullPath(FilePath));
            }
            Archive = WzArchive.FromBytes(bytes, Path.GetFileNameWithoutExtension(Name), targetVersion);
            VirtualRoot = BuildVirtualTree(Archive.Root);
            if (!string.IsNullOrWhiteSpace(FilePath)) {
                string? rootDir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
                if (!string.IsNullOrEmpty(rootDir) && Directory.Exists(rootDir)) {
                    VirtualRoot.DiskDirectoryPath = rootDir;
                    AssignChildDiskDirectories(VirtualRoot, isRoot: true);
                }
            }
            baselineImages.Clear();
            modifiedImages.Clear();
            deletedPropertiesByImage.Clear();
            HasStructureChanges = false;
            IsUserCreatedArchive = false;
        }

        return bytes;
    }

    public static string NormalizePath(ReadOnlySpan<char> path) =>
        path.Trim('/').ToString();

    public static (string? ImagePath, string SubPath) SplitImageAndSubPath(ReadOnlySpan<char> path) {
        ReadOnlySpan<char> trimmed = path.Trim('/');
        if (trimmed.IsEmpty) {
            return (null, string.Empty);
        }

        string s = trimmed.ToString();
        int idx = s.IndexOf(".img", StringComparison.OrdinalIgnoreCase);
        while (idx >= 0) {
            int afterImg = idx + 4;
            if (afterImg == s.Length) {
                return (s, string.Empty);
            }
            if (s[afterImg] == '/') {
                string imgPart = s[..afterImg];
                string subPart = s[(afterImg + 1)..].Trim('/');
                return (imgPart, subPart);
            }
            idx = s.IndexOf(".img", afterImg, StringComparison.OrdinalIgnoreCase);
        }

        return (null, string.Empty);
    }

    public void Dispose() {
        Archive.Dispose();
        foreach (WzArchive linked in linkedArchives) {
            linked.Dispose();
        }
        linkedArchives.Clear();
    }
}
