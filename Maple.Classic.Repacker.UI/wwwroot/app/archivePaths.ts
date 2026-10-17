namespace MapleRepacker {
    function stripNumericShardSuffix(stem: string): string {
        const lastUnderscore = stem.lastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore === stem.length - 1) {
            return stem;
        }
        const suffix = stem.slice(lastUnderscore + 1);
        if (!/^\d+$/.test(suffix)) {
            return stem;
        }
        return stem.slice(0, lastUnderscore);
    }

    function isSameOrCanvasStem(stemA: string, stemB: string): boolean {
        const baseA = stripNumericShardSuffix(stemA.toLowerCase());
        const baseB = stripNumericShardSuffix(stemB.toLowerCase());
        return (
            baseA === baseB ||
            baseA === `${baseB}_canvas` ||
            baseA === `${baseB}__canvas` ||
            baseB === `${baseA}_canvas` ||
            baseB === `${baseA}__canvas`
        );
    }

    function normalizePathForCompare(p: string): string {
        return p.trim().replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
    }

    function getDirPart(normPath: string): string {
        const lastSlash = normPath.lastIndexOf('/');
        return lastSlash >= 0 ? normPath.slice(0, lastSlash) : '';
    }

    /**
     * Finds an open archive that already covers a file the user is about to open: the same file, a shard or
     * _Canvas sibling of it, one of its linked files, or an archive in a parent/child data directory.
     */
    export function findExistingOpenArchive(
        archives: readonly ArchiveSummaryDto[],
        filePathOrName: string
    ): ArchiveSummaryDto | undefined {
        const rawTrimmed = filePathOrName.trim();
        if (!rawTrimmed) return undefined;

        const normTarget = normalizePathForCompare(rawTrimmed);
        const targetDir = getDirPart(normTarget);
        const basePart = normTarget.split('/').pop() || normTarget;
        const cleanName = basePart.endsWith('.wz') ? basePart : `${basePart}.wz`;
        const targetStem = cleanName.slice(0, -3);
        const targetBaseStem = stripNumericShardSuffix(targetStem);

        for (const a of archives) {
            const aLowerName = a.name.toLowerCase();
            const aStem = aLowerName.endsWith('.wz') ? aLowerName.slice(0, -3) : aLowerName;
            const aNormPath = a.filePath ? normalizePathForCompare(a.filePath) : '';
            const aDir = aNormPath ? getDirPart(aNormPath) : '';

            if ((aNormPath && aNormPath === normTarget) || aLowerName === cleanName) {
                return a;
            }

            // Same-directory (or pathless) shard / _Canvas stem match (e.g. Item_000.wz when Item.wz is open)
            if ((!targetDir || !aDir || targetDir === aDir) && isSameOrCanvasStem(targetStem, aStem)) {
                return a;
            }

            // Check all linked file paths belonging to this open archive
            const linkedPaths = a.linkedFilePaths || [];
            for (const lp of linkedPaths) {
                const normLp = normalizePathForCompare(lp);
                if (normLp === normTarget) {
                    return a;
                }
                const lpFile = normLp.split('/').pop() || '';
                if (!targetDir && lpFile === cleanName) {
                    return a;
                }
                const lpDir = getDirPart(normLp);
                const lpStem = lpFile.endsWith('.wz') ? lpFile.slice(0, -3) : lpFile;
                if (targetDir && lpDir === targetDir && isSameOrCanvasStem(targetStem, lpStem)) {
                    return a;
                }
            }

            // Parent / subdirectory containment (e.g. Cash_000.wz inside Data/Item when Data/Item/Item.wz is open, or vice versa)
            if (aDir && targetDir) {
                if (targetDir.startsWith(`${aDir}/`)) {
                    return a;
                }
                if (aDir.startsWith(`${targetDir}/`)) {
                    const targetFolderName = targetDir.split('/').pop() || '';
                    if (targetFolderName === targetBaseStem) {
                        return a;
                    }
                }
            }
        }

        return undefined;
    }
}
