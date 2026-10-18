using System.Globalization;
using Maple.Classic.File.IO.Values;
using Maple.Classic.Repacker.Engine.Models;
using static Maple.Classic.Repacker.Engine.Workspace.WzPropertyPaths;
using static Maple.Classic.Repacker.Engine.Workspace.WzValueFormatter;

namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// One child shown under a node (a directory, an <c>.img</c>, or a property inside an <c>.img</c>). Built once and
/// then mapped to either a tree node (<see cref="NodeDtoBuilder.ToTreeNode"/>) or a child-table row
/// (<see cref="NodeDtoBuilder.ToChildRow"/>), so both views always agree.
/// </summary>
internal readonly record struct NodeChild(
    string Path,
    string Name,
    RepackerNodeKind NodeKind,
    RepackerPropertyKind PropertyKind,
    string DisplayValue,
    int ChildCount,
    int SizeBytes,
    RepackerChangeState ChangeState,
    int DirectoryCount = 0,
    int ImageCount = 0);

/// <summary>
/// Builds tree, child-row and inspection DTOs for workspace nodes, including change-state evaluation against the
/// baseline image.
/// </summary>
internal static class NodeDtoBuilder {
    // -------------------------------------------------------------------------
    // Children of a directory / archive root
    // -------------------------------------------------------------------------

    internal static IEnumerable<NodeChild> DescribeDirectoryChildren(ArchiveSession session, string dirPath, VirtualFsNode dirNode) {
        bool parentDeleted = session.IsVirtualPathDeleted(dirPath);
        foreach (VirtualFsNode child in dirNode.Children) {
            string childPath = WzPath.Combine(dirPath, child.Name);
            bool deleted = parentDeleted || child.IsDeleted;
            if (child.IsDir) {
                (int dirs, int images) = ArchiveSession.CountVirtualRecursive(child);
                yield return new NodeChild(
                    childPath,
                    child.Name,
                    RepackerNodeKind.Directory,
                    RepackerPropertyKind.None,
                    DisplayValue: string.Empty,
                    child.Children.Count,
                    SizeBytes: 0,
                    deleted ? RepackerChangeState.Deleted : session.GetDirectoryChangeState(childPath, child),
                    dirs,
                    images);
            } else {
                int size = session.GetImageSize(childPath);
                yield return new NodeChild(
                    childPath,
                    child.Name,
                    RepackerNodeKind.Image,
                    RepackerPropertyKind.Object,
                    FormatByteSize(size),
                    ChildCount: 0,
                    size,
                    session.GetImageChangeState(childPath, deleted));
            }
        }
    }

    // -------------------------------------------------------------------------
    // Children of a property (or .img root) inside an image
    // -------------------------------------------------------------------------

    /// <summary>
    /// Direct children of <paramref name="target"/>: Convex items, or named properties merged with deleted properties
    /// (which are still listed, flagged as deleted, at their original position).
    /// </summary>
    internal static IEnumerable<NodeChild> DescribePropertyChildren(
        ArchiveSession session,
        string imgPath,
        string subPath,
        WzValue target,
        bool parentDeleted) {
        if (target is WzValue.Convex(var items)) {
            for (int i = 0; i < items.Count; i++) {
                string itemName = i.ToString(CultureInfo.InvariantCulture);
                yield return DescribeProperty(session, imgPath, WzPath.Combine(subPath, itemName), itemName, items[i], parentDeleted);
            }
            yield break;
        }

        foreach ((string name, string propSubPath, WzValue value, bool deleted) in BuildMergedChildProperties(session, imgPath, subPath, target, parentDeleted)) {
            yield return DescribeProperty(session, imgPath, propSubPath, name, value, deleted);
        }
    }

    private static NodeChild DescribeProperty(ArchiveSession session, string imgPath, string propSubPath, string name, WzValue value, bool deleted) {
        (RepackerPropertyKind kind, string display) = DescribeWzValue(value);
        int childCount = GetChildCount(value) + session.GetDeletedDirectChildProperties(imgPath, propSubPath).Count;
        return new NodeChild(
            WzPath.Combine(imgPath, propSubPath),
            name,
            RepackerNodeKind.Property,
            kind,
            display,
            childCount,
            GetPayloadSize(value),
            EvaluatePropertyChangeState(session, imgPath, propSubPath, value, deleted));
    }

    private static List<(string Name, string SubPath, WzValue Value, bool IsDeleted)> BuildMergedChildProperties(
        ArchiveSession session,
        string imgPath,
        string parentSubPath,
        WzValue target,
        bool parentDeleted) {
        var list = new List<(string Name, string SubPath, WzValue Value, bool IsDeleted)>();
        if (target.Children is { } props) {
            foreach (WzProperty prop in props) {
                list.Add((prop.Name, WzPath.Combine(parentSubPath, prop.Name), prop.Value, parentDeleted));
            }
        }

        foreach (DeletedPropertyEntry del in session.GetDeletedDirectChildProperties(imgPath, parentSubPath)) {
            if (list.Any(item => string.Equals(item.Name, del.Name, StringComparison.Ordinal))) {
                continue;
            }
            list.Insert(Math.Clamp(del.OriginalIndex, 0, list.Count), (del.Name, del.SubPath, del.Value, true));
        }
        return list;
    }

    // -------------------------------------------------------------------------
    // Mapping to DTOs
    // -------------------------------------------------------------------------

    internal static TreeNodeDto ToTreeNode(this NodeChild c, string archiveId) =>
        new(
            archiveId,
            c.Path,
            c.Name,
            c.NodeKind,
            c.PropertyKind,
            c.NodeKind == RepackerNodeKind.Directory ? $"{c.DirectoryCount + c.ImageCount} items" : c.DisplayValue,
            c.ChildCount,
            c.ChangeState,
            c.SizeBytes);

    internal static SubPropertyRowDto ToChildRow(this NodeChild c) =>
        new(
            c.Name,
            c.Path,
            c.NodeKind,
            c.PropertyKind,
            c.NodeKind == RepackerNodeKind.Directory ? $"{c.DirectoryCount} dirs, {c.ImageCount} images" : c.DisplayValue,
            c.ChildCount,
            c.SizeBytes,
            c.ChangeState);

    // -------------------------------------------------------------------------
    // Change state
    // -------------------------------------------------------------------------

    internal static RepackerChangeState EvaluatePropertyChangeState(
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

}
