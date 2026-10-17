namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Resolves on-disk <c>.wz</c> archive paths, preferring un-numbered root archives (e.g. <c>Base.wz</c>) over numbered shards (e.g. <c>Base_000.wz</c>).
/// </summary>
internal static class ArchivePathResolver {
    /// <summary>
    /// Returns every <c>.wz</c> file under <paramref name="fullDir"/> in open order: shallowest first, un-numbered root
    /// archives before numbered shards, and <c>Base.wz</c> first so it can subsume the category archives it links.
    /// </summary>
    internal static string[] GetOrderedDataDirectoryWzFiles(string fullDir) =>
        Directory.GetFiles(fullDir, "*.wz", SearchOption.AllDirectories)
            .OrderBy(static p => p.Count(static c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .ThenBy(static p => {
                string stem = Path.GetFileNameWithoutExtension(p);
                string baseStem = ArchiveSession.StripNumericShardSuffix(stem);
                return string.Equals(stem, baseStem, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            })
            .ThenBy(static p => {
                string stem = Path.GetFileNameWithoutExtension(p);
                string baseStem = ArchiveSession.StripNumericShardSuffix(stem);
                return string.Equals(baseStem, "Base", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            })
            .ThenBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static string ResolvePreferredRootArchivePathOnDisk(string fullPath) {
        try {
            string? dir = Path.GetDirectoryName(fullPath);
            string stem = Path.GetFileNameWithoutExtension(fullPath);
            string baseStem = ArchiveSession.StripNumericShardSuffix(stem);
            if (!string.IsNullOrEmpty(dir) &&
                !string.Equals(stem, baseStem, StringComparison.OrdinalIgnoreCase) &&
                !baseStem.EndsWith("_Canvas", StringComparison.OrdinalIgnoreCase)) {
                string rootCandidate = Path.Combine(dir, $"{baseStem}.wz");
                if (System.IO.File.Exists(rootCandidate)) {
                    return Path.GetFullPath(rootCandidate);
                }
            }
        } catch {
            // Fall back to original path
        }
        return fullPath;
    }

    internal static string ChoosePreferredRootArchivePath(string existingPath, string requestedPath) {
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

    internal static string? TryResolveDiskPath(IEnumerable<string> openArchivePaths, string cleanName, int byteLength, IEnumerable<string>? hintPaths) {
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

        foreach (string openPath in openArchivePaths) {
            AddDirAndParent(openPath);
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
}
