namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Helpers for the slash-separated paths used throughout the workspace: archive paths (<c>Dir/Image.img/prop/sub</c>)
/// and property sub-paths inside an image. Paths are expected to be normalized (no leading/trailing slashes).
/// </summary>
public static class WzPath {
    private const char SEPARATOR = '/';

    /// <summary>Joins path parts with <c>/</c>, skipping empty parts (so joining onto the root yields just the child).</summary>
    public static string Combine(string parent, string child) =>
        parent.Length == 0 ? child
        : child.Length == 0 ? parent
        : $"{parent}{SEPARATOR}{child}";

    /// <inheritdoc cref="Combine(string, string)"/>
    public static string Combine(string first, string second, string third) => Combine(Combine(first, second), third);

    /// <summary>Everything before the last <c>/</c>, or empty for a top-level path.</summary>
    public static string Parent(string path) {
        int slash = path.LastIndexOf(SEPARATOR);
        return slash >= 0 ? path[..slash] : string.Empty;
    }

    /// <summary>The last segment: everything after the last <c>/</c>, or the whole path for a top-level path.</summary>
    public static string Name(string path) {
        int slash = path.LastIndexOf(SEPARATOR);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>The proper ancestors of a path, nearest first, excluding the (empty) root.</summary>
    public static IEnumerable<string> Ancestors(string path) {
        for (string cur = Parent(path); cur.Length > 0; cur = Parent(cur)) {
            yield return cur;
        }
    }

    /// <summary>The non-empty segments of a path.</summary>
    public static string[] Segments(string path) => path.Split(SEPARATOR, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Joins segments with <c>/</c>.</summary>
    public static string Join(IEnumerable<string> segments) => string.Join(SEPARATOR, segments);
}
