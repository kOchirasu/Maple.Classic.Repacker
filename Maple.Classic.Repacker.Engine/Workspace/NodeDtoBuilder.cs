using System.Globalization;
using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Models;
using static Maple.Classic.Repacker.Engine.Workspace.WzPropertyPaths;
using static Maple.Classic.Repacker.Engine.Workspace.WzValueFormatter;

namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Builds tree, child-row and inspection DTOs for properties inside <c>.img</c> nodes, including change-state evaluation against the baseline image.
/// </summary>
internal static class NodeDtoBuilder {
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

    internal static List<(string Name, string SubPath, WzValue Value, bool IsDeleted)> BuildMergedChildProperties(
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

    internal static TreeNodeDto BuildPropertyTreeNodeDto(
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

    internal static SubPropertyRowDto[] BuildChildRows(
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

    internal static NodeInspectionDto ErrorInspection(string archiveId, string path, string error) =>
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
}
