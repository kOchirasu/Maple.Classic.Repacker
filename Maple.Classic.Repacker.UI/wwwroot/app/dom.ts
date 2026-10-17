namespace MapleRepacker {
    /** Looks up a required element once; a renamed or missing ID fails loudly at startup instead of at first use. */
    export function byId<T extends HTMLElement = HTMLElement>(id: string): T {
        const el = document.getElementById(id);
        if (!el) {
            throw new Error(`Missing required element #${id}`);
        }
        return el as T;
    }

    export function escapeHtml(str: string | null | undefined): string {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    /** Starts a browser download of a same-origin or blob URL. */
    export function triggerDownload(href: string, fileName = ''): void {
        const a = document.createElement('a');
        a.href = href;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
    }

    /** Tree/table badge for a node's kind. */
    export function typeBadgeHtml(nodeKind: RepackerNodeKind, propertyKind?: RepackerPropertyKind): string {
        const badge = typeBadge(nodeKind, propertyKind);
        return `<span class="tree-badge badge-${badge}">${badge}</span>`;
    }

    /** True when an archive has changes that would be lost on close. */
    export function isArchiveDirty(ar: ArchiveSummaryDto): boolean {
        return ar.changeState === RepackerChangeState.Modified || ar.changeState === RepackerChangeState.Added;
    }

    export function pluralize(count: number, singular: string, plural = `${singular}s`): string {
        return `${count} ${count === 1 ? singular : plural}`;
    }
}
