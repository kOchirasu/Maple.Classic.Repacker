using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Models;
using static Maple.Classic.Repacker.Engine.Workspace.WzValueFormatter;

namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Recursive name/value/type search across archive directories, images and properties.
/// </summary>
internal static class WorkspaceSearch {
    internal static void SearchDirectory(
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
            string fullPath = WzPath.Combine(prefix, child.Name);

            if (child.IsDir) {
                if (kindFilter is null && string.IsNullOrEmpty(valFilter) && MatchesText(child.Name, fullPath, nameFilter, exact, cmp)) {
                    results.Add(new SearchResultItemDto(
                        session.ArchiveId,
                        session.Name,
                        fullPath,
                        child.Name,
                        RepackerNodeKind.Directory,
                        RepackerPropertyKind.None,
                        $"{child.Children.Count} items"));
                }
                SearchDirectory(session, child, fullPath, nameFilter, valFilter, kindFilter, exact, cmp, maxResults, results);
            } else if (child.IsImage) {
                if (kindFilter is null && string.IsNullOrEmpty(valFilter) && MatchesText(child.Name, fullPath, nameFilter, exact, cmp)) {
                    results.Add(new SearchResultItemDto(
                        session.ArchiveId,
                        session.Name,
                        fullPath,
                        child.Name,
                        RepackerNodeKind.Image,
                        RepackerPropertyKind.Object,
                        FormatByteSize(session.GetImageSize(fullPath))));
                }

                try {
                    WzValue imgRoot = session.GetOrParseImage(fullPath);
                    SearchProperties(session, fullPath, "", imgRoot, nameFilter, valFilter, kindFilter, exact, cmp, maxResults, results);
                } catch {
                    // Skip unreadable image during global search
                }
            }
        }
    }

    internal static void SearchProperties(
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
            string propSubPath = WzPath.Combine(subPath, prop.Name);
            string fullPath = WzPath.Combine(imgPath, propSubPath);
            (RepackerPropertyKind pKind, string disp) = DescribeWzValue(prop.Value);

            bool kindMatches = !kindFilter.HasValue || kindFilter.Value == RepackerPropertyKind.None || kindFilter.Value == pKind;
            if (kindMatches) {
                string searchableVal = GetSearchablePropertyValue(prop.Value, disp);
                bool matches;
                if (!string.IsNullOrEmpty(nameFilter) && string.IsNullOrEmpty(valFilter)) {
                    matches = MatchesText(prop.Name, fullPath, nameFilter, exact, cmp)
                        || (!string.IsNullOrEmpty(searchableVal) && MatchesSingleText(searchableVal, nameFilter, exact, cmp));
                } else {
                    bool nameMatches = string.IsNullOrEmpty(nameFilter) || MatchesText(prop.Name, fullPath, nameFilter, exact, cmp);
                    bool valMatches = string.IsNullOrEmpty(valFilter) || (!string.IsNullOrEmpty(searchableVal) && MatchesSingleText(searchableVal, valFilter, exact, cmp));
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
                        disp));
                }
            }

            if (prop.Value.Children is { Count: > 0 }) {
                SearchProperties(session, imgPath, propSubPath, prop.Value, nameFilter, valFilter, kindFilter, exact, cmp, maxResults, results);
            }
        }
    }

    internal static string GetSearchablePropertyValue(WzValue val, string displayValue) => val switch {
        WzValue.Short or WzValue.Int or WzValue.Long or WzValue.Float or WzValue.Double or WzValue.String or WzValue.Vector => displayValue,
        WzValue.Uol(var uolStr) => uolStr,
        _ => string.Empty,
    };

    internal static bool MatchesText(string name, string fullPath, string filter, bool exact, StringComparison cmp) {
        if (string.IsNullOrEmpty(filter)) return true;
        if (exact) {
            return string.Equals(name, filter, cmp) || string.Equals(fullPath, filter, cmp);
        }
        return name.Contains(filter, cmp) || fullPath.Contains(filter, cmp);
    }

    internal static bool MatchesSingleText(string text, string filter, bool exact, StringComparison cmp) {
        if (string.IsNullOrEmpty(filter)) return true;
        return exact ? string.Equals(text, filter, cmp) : text.Contains(filter, cmp);
    }
}
