using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.Engine.Workspace;

public sealed class VirtualFsNode(string name, WzNodeKind kind, WzNode? backingNode = null) {
    public string Name { get; set; } = name;
    public WzNodeKind Kind { get; } = kind;
    public WzNode? BackingNode { get; set; } = backingNode;
    public List<VirtualFsNode> Children { get; } = [];

    public bool IsDir => Kind == WzNodeKind.Directory;
    public bool IsImage => Kind == WzNodeKind.Image;
}

/// <summary>
/// Represents an open WZ archive in the Repacker workspace with lazy-loaded mutable <see cref="WzValue"/> trees,
/// virtual directory/image tracking, and per-image undo/redo history.
/// </summary>
public sealed class ArchiveSession : IDisposable {
    private const int MaxUndoDepth = 32;

    private readonly Dictionary<string, WzValue> loadedImages = new(StringComparer.Ordinal);
    private readonly HashSet<string> modifiedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stack<byte[]>> undoStacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stack<byte[]>> redoStacks = new(StringComparer.Ordinal);

    public string ArchiveId { get; }
    public string Name { get; set; }
    public string FilePath { get; set; }
    public WzArchive Archive { get; private set; }
    public VirtualFsNode VirtualRoot { get; private set; }
    public bool HasStructureChanges { get; set; }

    public bool IsModified => HasStructureChanges || modifiedImages.Count > 0;
    public int ModifiedImageCount => modifiedImages.Count;

    public ArchiveSession(string archiveId, string name, string filePath, WzArchive archive) {
        ArchiveId = archiveId;
        Name = name;
        FilePath = filePath;
        Archive = archive;
        VirtualRoot = BuildVirtualTree(archive.Root);
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
            IsModified);
    }

    public static (int Directories, int Images) CountVirtualRecursive(VirtualFsNode root) {
        int dirs = 0;
        int imgs = 0;
        foreach (VirtualFsNode c in root.Children) {
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

    public VirtualFsNode? ResolveVirtualNode(ReadOnlySpan<char> path) {
        path = path.Trim('/');
        if (path.IsEmpty) {
            return VirtualRoot;
        }

        VirtualFsNode cur = VirtualRoot;
        foreach (Range range in path.Split('/')) {
            ReadOnlySpan<char> part = path[range];
            if (part.IsEmpty) {
                continue;
            }
            VirtualFsNode? next = null;
            foreach (VirtualFsNode child in cur.Children) {
                if (child.Name.AsSpan().SequenceEqual(part)) {
                    next = child;
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

    public bool IsImageModified(string imagePath) => modifiedImages.Contains(NormalizePath(imagePath));

    public bool IsPathModified(string path) {
        string norm = NormalizePath(path);
        if (string.IsNullOrEmpty(norm)) {
            return IsModified;
        }
        (string? imgPath, _) = SplitImageAndSubPath(norm);
        if (imgPath is not null) {
            return modifiedImages.Contains(imgPath);
        }
        string prefix = norm + "/";
        foreach (string modImg in modifiedImages) {
            if (modImg.StartsWith(prefix, StringComparison.Ordinal)) {
                return true;
            }
        }
        return false;
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
            ? Archive.ParseImage(vNode.BackingNode)
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
            return Archive.GetImageMemory(vNode.BackingNode);
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

    public void RecordUndoSnapshot(string imagePath) {
        string norm = NormalizePath(imagePath);
        WzValue current = GetOrParseImage(norm);
        byte[] snapshot = current.ToBytes();

        if (!undoStacks.TryGetValue(norm, out Stack<byte[]>? stack)) {
            stack = new Stack<byte[]>();
            undoStacks[norm] = stack;
        }
        if (stack.Count >= MaxUndoDepth) {
            byte[][] items = stack.ToArray();
            stack.Clear();
            for (int i = Math.Min(items.Length - 1, MaxUndoDepth - 2); i >= 0; i--) {
                stack.Push(items[i]);
            }
        }
        stack.Push(snapshot);

        if (redoStacks.TryGetValue(norm, out Stack<byte[]>? redo)) {
            redo.Clear();
        }
    }

    public void MarkImageModified(string imagePath, WzValue? newRoot = null) {
        string norm = NormalizePath(imagePath);
        if (newRoot is not null) {
            loadedImages[norm] = newRoot;
        }
        modifiedImages.Add(norm);
    }

    public bool CanUndo(string? imagePath) {
        if (string.IsNullOrEmpty(imagePath)) return false;
        string norm = NormalizePath(imagePath);
        return undoStacks.TryGetValue(norm, out Stack<byte[]>? s) && s.Count > 0;
    }

    public bool CanRedo(string? imagePath) {
        if (string.IsNullOrEmpty(imagePath)) return false;
        string norm = NormalizePath(imagePath);
        return redoStacks.TryGetValue(norm, out Stack<byte[]>? s) && s.Count > 0;
    }

    public bool Undo(string imagePath) {
        string norm = NormalizePath(imagePath);
        if (!undoStacks.TryGetValue(norm, out Stack<byte[]>? undo) || undo.Count == 0) {
            return false;
        }

        WzValue current = GetOrParseImage(norm);
        if (!redoStacks.TryGetValue(norm, out Stack<byte[]>? redo)) {
            redo = new Stack<byte[]>();
            redoStacks[norm] = redo;
        }
        redo.Push(current.ToBytes());

        byte[] prevBytes = undo.Pop();
        loadedImages[norm] = WzValue.Parse(prevBytes);
        modifiedImages.Add(norm);
        return true;
    }

    public bool Redo(string imagePath) {
        string norm = NormalizePath(imagePath);
        if (!redoStacks.TryGetValue(norm, out Stack<byte[]>? redo) || redo.Count == 0) {
            return false;
        }

        WzValue current = GetOrParseImage(norm);
        if (!undoStacks.TryGetValue(norm, out Stack<byte[]>? undo)) {
            undo = new Stack<byte[]>();
            undoStacks[norm] = undo;
        }
        undo.Push(current.ToBytes());

        byte[] nextBytes = redo.Pop();
        loadedImages[norm] = WzValue.Parse(nextBytes);
        modifiedImages.Add(norm);
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
                next = new VirtualFsNode(dirName, WzNodeKind.Directory);
                cur.Children.Add(next);
            }
            cur = next;
        }

        string leafName = parts[^1];
        VirtualFsNode? existing = cur.Children.Find(c => c.Name == leafName);
        if (existing is null) {
            cur.Children.Add(new VirtualFsNode(leafName, WzNodeKind.Image));
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
            return existing;
        }
        var created = new VirtualFsNode(dirName, WzNodeKind.Directory);
        parent.Children.Add(created);
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
        int removed = parent.Children.RemoveAll(c => c.Name == leafName);
        if (removed == 0) {
            return false;
        }

        loadedImages.Remove(norm);
        modifiedImages.Remove(norm);
        undoStacks.Remove(norm);
        redoStacks.Remove(norm);

        string prefix = norm + "/";
        foreach (string key in loadedImages.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) {
            loadedImages.Remove(key);
            modifiedImages.Remove(key);
            undoStacks.Remove(key);
            redoStacks.Remove(key);
        }

        HasStructureChanges = true;
        return true;
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

        string newFullPath = string.IsNullOrEmpty(parentPath) ? newLeafName : $"{parentPath}/{newLeafName}";
        if (target.IsImage) {
            WzValue parsed = GetOrParseImage(norm);
            target.Name = newLeafName;
            loadedImages.Remove(norm);
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
            foreach (VirtualFsNode child in dir.Children) {
                string fullPath = string.IsNullOrEmpty(prefix) ? child.Name : $"{prefix}/{child.Name}";
                if (child.IsDir) {
                    CollectRecursive(child, fullPath);
                } else if (child.IsImage) {
                    if (modifiedImages.Contains(fullPath) && loadedImages.TryGetValue(fullPath, out WzValue? modifiedVal)) {
                        outArchive.SetImage(fullPath, modifiedVal);
                    } else if (child.BackingNode is not null) {
                        outArchive.SetImage(fullPath, Archive.GetImageMemory(child.BackingNode));
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
            Archive = WzArchive.FromBytes(bytes, Path.GetFileNameWithoutExtension(Name), targetVersion);
            VirtualRoot = BuildVirtualTree(Archive.Root);
            modifiedImages.Clear();
            HasStructureChanges = false;
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
    }
}
