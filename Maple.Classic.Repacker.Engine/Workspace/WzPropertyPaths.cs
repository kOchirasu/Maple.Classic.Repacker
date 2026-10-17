using Maple.Classic.File.IO;

namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Path resolution and mutation helpers for navigating <see cref="WzValue"/> property trees (sub-paths, UOL links, cloning).
/// </summary>
public static class WzPropertyPaths {
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

    internal static bool TryReplacePropertyNode(WzValue imgRoot, string subPath, WzValue newValue) {
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

    internal static List<WzProperty>? GetMutablePropertyList(WzValue? val) => val switch {
        WzValue.Object(var props) => props,
        WzValue.Canvas { Properties: var props } => props,
        _ => null,
    };

    internal static WzValue CloneWzValue(WzValue v) {
        var wrapper = new WzValue.Object([new WzProperty("_", v)]);
        WzValue parsed = WzValue.Parse(wrapper.ToBytes());
        return parsed.Children is { Count: > 0 } ? parsed.Children[0].Value : parsed;
    }
}
