namespace MapleRepacker {
    const SEARCH_MAX_RESULTS = 300;

    /** Workbench search tab: archive scope, name/value/kind filters and the result table. */
    export class SearchPanel {
        private readonly scopeSelect = byId<HTMLSelectElement>('searchArchiveScope');
        private readonly queryInput = byId<HTMLInputElement>('searchQueryInput');
        private readonly valueInput = byId<HTMLInputElement>('searchValueInput');
        private readonly kindSelect = byId<HTMLSelectElement>('searchKindSelect');
        private readonly exactCheckbox = byId<HTMLInputElement>('chkSearchExact');
        private readonly countPill = byId('searchCountPill');
        private readonly resultsBody = byId('searchResultsBody');

        constructor(private readonly app: RepackerStudioApp) {
            byId('btnRunSearch').addEventListener('click', () => void this.run());
            for (const input of [this.queryInput, this.valueInput]) {
                input.addEventListener('keydown', (ev) => {
                    if (ev.key === 'Enter') void this.run();
                });
            }
        }

        /** Rebuilds the archive scope dropdown, keeping the current choice. */
        renderArchiveScope(archives: readonly ArchiveSummaryDto[]): void {
            const prev = this.scopeSelect.value;
            this.scopeSelect.innerHTML = '<option value="">All Open WZ Archives</option>';
            for (const ar of archives) {
                const opt = document.createElement('option');
                opt.value = ar.archiveId;
                opt.textContent = `${ar.name} (v${ar.version})`;
                this.scopeSelect.appendChild(opt);
            }
            this.scopeSelect.value = prev;
        }

        focus(): void {
            this.app.workbench.activateTab(RepackerWorkbenchTab.Search);
            this.queryInput.focus();
            this.queryInput.select();
        }

        private async run(): Promise<void> {
            const kind = this.kindSelect.value;
            const res = await rpcCall<SearchResponseDto>(RpcMethod.SearchQuery, {
                archiveId: this.scopeSelect.value || undefined,
                query: this.queryInput.value,
                valueQuery: this.valueInput.value,
                propertyKindFilter: kind ? (kind as RepackerPropertyKind) : undefined,
                exactMatch: this.exactCheckbox.checked,
                maxResults: SEARCH_MAX_RESULTS,
            });

            const hits = res.ok && res.data?.results ? res.data.results : [];
            this.countPill.textContent = String(hits.length);
            this.resultsBody.innerHTML = '';
            for (const h of hits) {
                const tr = document.createElement('tr');
                tr.innerHTML = `
                    <td>${escapeHtml(h.archiveName)}</td>
                    <td>${escapeHtml(h.path)}</td>
                    <td>${typeBadgeHtml(h.nodeKind, h.propertyKind)}</td>
                    <td>${escapeHtml(h.displayValue)}</td>
                `;
                tr.addEventListener('click', () => void this.app.selectAndInspectNode(h.archiveId, h.path, true));
                this.resultsBody.appendChild(tr);
            }
        }
    }
}
