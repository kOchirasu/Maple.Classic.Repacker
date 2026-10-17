namespace MapleRepacker {
    export enum ModalButtonVariant {
        Default = '',
        Primary = 'primary',
        Accent = 'accent',
        Danger = 'danger',
    }

    export interface ModalAction {
        label: string;
        variant?: ModalButtonVariant;
        /** The modal stays open until the handler calls `modal.close()`, so failed actions keep the user's input. */
        onClick: (modal: OpenModal) => void | Promise<void>;
    }

    export interface ModalOptions {
        title: string;
        /** Trusted HTML; build it with `ModalHtml` and escape any user data. */
        body: string;
        actions: ModalAction[];
        /** Runs when the user dismisses the modal (Cancel, close button, backdrop click or Escape). */
        onDismiss?: () => void;
    }

    export interface OpenModal {
        /** Looks up an element inside the modal body. */
        field<T extends HTMLElement = HTMLInputElement>(id: string): T;
        /** Closes without running `onDismiss`. */
        close(): void;
        /** Disables the action buttons while a long-running action is in flight. */
        setBusy(busy: boolean): void;
    }

    export interface ModalRowOptions {
        id?: string;
        hidden?: boolean;
        labelId?: string;
        hint?: string;
        hintId?: string;
    }

    export interface ModalInputOptions {
        type?: 'text' | 'number' | 'file';
        value?: string | number;
        placeholder?: string;
        accept?: string;
    }

    /** Small HTML builders for modal bodies, so dialogs share markup and CSS classes instead of inline styles. */
    export const ModalHtml = {
        row(label: string, control: string, opts: ModalRowOptions = {}): string {
            const idAttr = opts.id ? ` id="${opts.id}"` : '';
            const labelIdAttr = opts.labelId ? ` id="${opts.labelId}"` : '';
            const hint =
                opts.hint !== undefined || opts.hintId
                    ? `<div class="form-hint"${opts.hintId ? ` id="${opts.hintId}"` : ''}>${escapeHtml(opts.hint)}</div>`
                    : '';
            return `<div class="form-row${opts.hidden ? ' hidden' : ''}"${idAttr}><label${labelIdAttr}>${escapeHtml(label)}</label>${control}${hint}</div>`;
        },

        input(id: string, opts: ModalInputOptions = {}): string {
            const attrs = [
                `type="${opts.type ?? 'text'}"`,
                `id="${id}"`,
                'class="input-full"',
                opts.value !== undefined ? `value="${escapeHtml(String(opts.value))}"` : '',
                opts.placeholder ? `placeholder="${escapeHtml(opts.placeholder)}"` : '',
                opts.accept ? `accept="${escapeHtml(opts.accept)}"` : '',
            ];
            return `<input ${attrs.filter((a) => a).join(' ')} />`;
        },

        select(id: string, options: ReadonlyArray<readonly [value: string, label: string]>): string {
            const opts = options.map(([value, label]) => `<option value="${escapeHtml(value)}">${escapeHtml(label)}</option>`).join('');
            return `<select id="${id}" class="select-full">${opts}</select>`;
        },

        /** Input with a trailing button (e.g. a Browse... button). */
        inputWithButton(input: string, button: string): string {
            return `<div class="input-with-button">${input}${button}</div>`;
        },

        /** Paragraph of trusted HTML. */
        text(html: string, style: 'primary' | 'secondary' | 'warning' = 'primary'): string {
            return `<p class="modal-text${style === 'primary' ? '' : ` ${style}`}">${html}</p>`;
        },

        /** List of trusted HTML items. */
        list(itemsHtml: string[]): string {
            return `<ul class="modal-list">${itemsHtml.map((item) => `<li>${item}</li>`).join('')}</ul>`;
        },

        /** Read-only monospace box, e.g. the archive/path an operation applies to. */
        scope(text: string): string {
            return `<div class="modal-scope">${escapeHtml(text)}</div>`;
        },
    };

    export class ModalController {
        private readonly backdrop = byId('modalBackdrop');
        private readonly titleEl = byId('modalTitle');
        private readonly bodyEl = byId('modalBody');
        private readonly footerEl = byId('modalFooter');
        private onDismiss: (() => void) | null = null;

        constructor() {
            byId('btnCloseModal').addEventListener('click', () => this.dismiss());
            this.backdrop.addEventListener('click', (ev) => {
                if (ev.target === this.backdrop) {
                    this.dismiss();
                }
            });
        }

        get isOpen(): boolean {
            return !this.backdrop.classList.contains('hidden');
        }

        open(options: ModalOptions): OpenModal {
            this.onDismiss = options.onDismiss ?? null;
            this.titleEl.textContent = options.title;
            this.bodyEl.innerHTML = options.body;
            this.footerEl.innerHTML = '';

            const actionButtons: HTMLButtonElement[] = [];
            const modal: OpenModal = {
                field: <T extends HTMLElement>(id: string): T => {
                    const el = this.bodyEl.querySelector<T>(`#${CSS.escape(id)}`);
                    if (!el) {
                        throw new Error(`Missing modal field #${id}`);
                    }
                    return el;
                },
                close: () => this.close(),
                setBusy: (busy) => actionButtons.forEach((btn) => (btn.disabled = busy)),
            };

            this.footerEl.appendChild(this.createButton('Cancel', ModalButtonVariant.Default, () => this.dismiss()));
            for (const action of options.actions) {
                const btn = this.createButton(action.label, action.variant ?? ModalButtonVariant.Default, () => void action.onClick(modal));
                actionButtons.push(btn);
                this.footerEl.appendChild(btn);
            }

            this.backdrop.classList.remove('hidden');
            return modal;
        }

        close(): void {
            this.onDismiss = null;
            this.backdrop.classList.add('hidden');
        }

        /** User-initiated close: hides the modal, then runs its `onDismiss`. */
        dismiss(): void {
            if (!this.isOpen) return;
            const onDismiss = this.onDismiss;
            this.close();
            onDismiss?.();
        }

        private createButton(label: string, variant: ModalButtonVariant, onClick: () => void): HTMLButtonElement {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = variant ? `cmd-btn ${variant}` : 'cmd-btn';
            btn.textContent = label;
            btn.addEventListener('click', onClick);
            return btn;
        }
    }
}
