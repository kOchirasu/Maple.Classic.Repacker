import { EditorState } from '@codemirror/state';
import {
    EditorView,
    keymap,
    lineNumbers,
    highlightActiveLine,
    highlightActiveLineGutter,
    drawSelection,
    dropCursor,
    rectangularSelection,
    crosshairCursor,
} from '@codemirror/view';
import {
    syntaxHighlighting,
    HighlightStyle,
    indentOnInput,
    bracketMatching,
    foldGutter,
    foldKeymap,
    indentUnit,
} from '@codemirror/language';
import {
    defaultKeymap,
    history,
    historyKeymap,
    indentMore,
    indentLess,
} from '@codemirror/commands';
import { searchKeymap, highlightSelectionMatches } from '@codemirror/search';
import {
    autocompletion,
    completionKeymap,
    closeBrackets,
    closeBracketsKeymap,
    acceptCompletion,
} from '@codemirror/autocomplete';
import { python } from '@codemirror/lang-python';
import { tags } from '@lezer/highlight';

const WZ_JSON_COMPLETIONS = [
    { label: '"_type"', type: 'keyword', detail: 'WzValue discriminator (vector, canvas, sound, convex, raw)' },
    { label: '"origin"', type: 'property', detail: 'Sprite anchor vector (x, y)' },
    { label: '"lt"', type: 'property', detail: 'Top-left bounding box vector (x, y)' },
    { label: '"rb"', type: 'property', detail: 'Bottom-right bounding box vector (x, y)' },
    { label: '"head"', type: 'property', detail: 'Character/Mob head offset vector (x, y)' },
    { label: '"delay"', type: 'property', detail: 'Animation frame duration in milliseconds' },
    { label: '"z"', type: 'property', detail: 'Z-Map rendering layer name (e.g. mailChest)' },
    { label: '"_outlink"', type: 'property', detail: 'Cross-archive Canvas sprite reference path' },
    { label: '"_inlink"', type: 'property', detail: 'Intra-image Canvas sprite reference path' },
    { label: '"width"', type: 'property', detail: 'Canvas width in pixels' },
    { label: '"height"', type: 'property', detail: 'Canvas height in pixels' },
    { label: '"format"', type: 'property', detail: 'WZ pixel format (1, 2, 513, 517, 1026, 2050)' },
    { label: '"properties"', type: 'property', detail: 'Nested child properties of a Canvas node' },
    { label: '"a0"', type: 'property', detail: 'Initial frame alpha (0..255)' },
    { label: '"a1"', type: 'property', detail: 'Ending frame alpha (0..255)' },
];

function wzJsonCompletionSource(context) {
    const word = context.matchBefore(/"?[A-Za-z0-9_]*$/);
    if (!word || (word.from === word.to && !context.explicit)) {
        return null;
    }
    return {
        from: word.from,
        options: WZ_JSON_COMPLETIONS,
    };
}

const mapleDarkTheme = EditorView.theme(
    {
        '&': {
            color: '#d4d4d4',
            backgroundColor: '#16181d',
            height: '100%',
            fontSize: '12.5px',
            fontFamily: '"JetBrains Mono", "Cascadia Mono", Consolas, monospace',
        },
        '.cm-content': {
            caretColor: '#e5c07b',
            padding: '8px 0',
        },
        '.cm-cursor, .cm-dropCursor': {
            borderLeftColor: '#e5c07b',
        },
        '&.cm-focused .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection': {
            backgroundColor: '#264f78',
        },
        '.cm-activeLine': {
            backgroundColor: 'rgba(255, 255, 255, 0.04)',
        },
        '.cm-selectionMatch': {
            backgroundColor: 'rgba(229, 192, 123, 0.22)',
        },
        '.cm-gutters': {
            backgroundColor: '#131519',
            color: '#6b7280',
            borderRight: '1px solid #262930',
        },
        '.cm-activeLineGutter': {
            backgroundColor: '#1c1f26',
            color: '#e5c07b',
        },
        '.cm-foldPlaceholder': {
            backgroundColor: '#252932',
            border: '1px solid #3b4048',
            color: '#d19a66',
        },
        '.cm-tooltip': {
            border: '1px solid #333842',
            backgroundColor: '#1e222a',
            color: '#d4d4d4',
        },
    },
    { dark: true }
);

const mapleHighlightStyle = HighlightStyle.define([
    { tag: tags.keyword, color: '#e06c75', fontWeight: '600' },
    { tag: [tags.name, tags.deleted, tags.character, tags.propertyName, tags.macroName], color: '#61afef' },
    { tag: [tags.string, tags.special(tags.string)], color: '#98c379' },
    { tag: [tags.number, tags.integer, tags.float, tags.bool, tags.null], color: '#d19a66' },
    { tag: [tags.comment], color: '#5c6370', fontStyle: 'italic' },
    { tag: [tags.punctuation, tags.bracket, tags.brace], color: '#abb2bf' },
]);

function buildExtensions(onChangeCallback, onSaveCallback) {
    return [
        lineNumbers(),
        highlightActiveLineGutter(),
        history(),
        foldGutter(),
        drawSelection(),
        dropCursor(),
        EditorState.allowMultipleSelections.of(true),
        indentOnInput(),
        indentUnit.of('  '),
        syntaxHighlighting(mapleHighlightStyle, { fallback: true }),
        bracketMatching(),
        closeBrackets(),
        autocompletion({
            override: [wzJsonCompletionSource],
        }),
        rectangularSelection(),
        crosshairCursor(),
        highlightActiveLine(),
        highlightSelectionMatches(),
        python(),
        mapleDarkTheme,
        keymap.of([
            {
                key: 'Mod-s',
                preventDefault: true,
                run: () => {
                    if (typeof onSaveCallback === 'function') {
                        onSaveCallback();
                    }
                    return true;
                },
            },
            {
                key: 'Tab',
                run: (view) => {
                    if (acceptCompletion(view)) return true;
                    if (view.state.selection.ranges.some((r) => !r.empty)) {
                        return indentMore(view);
                    }
                    view.dispatch(
                        view.state.update(view.state.replaceSelection('  '), {
                            scrollIntoView: true,
                            userEvent: 'input',
                        })
                    );
                    return true;
                },
                shift: indentLess,
            },
            ...closeBracketsKeymap,
            ...defaultKeymap,
            ...searchKeymap,
            ...historyKeymap,
            ...foldKeymap,
            ...completionKeymap,
        ]),
        EditorView.updateListener.of((update) => {
            if (update.docChanged && typeof onChangeCallback === 'function') {
                onChangeCallback();
            }
        }),
    ];
}

class Cm6EditorInstance {
    constructor(hostElement, options = {}) {
        this._changeListeners = [];
        this._onSave = options.onSave || null;
        this._extensions = buildExtensions(() => {
            for (const fn of this._changeListeners) {
                fn();
            }
        }, this._onSave);

        const initialText = options.initialValue || '';
        this._view = new EditorView({
            state: EditorState.create({
                doc: String(initialText),
                extensions: this._extensions,
            }),
            parent: hostElement,
        });
    }

    getValue() {
        return this._view.state.doc.toString();
    }

    setValue(content) {
        const str = String(content ?? '');
        const cur = this._view.state.doc.toString();
        if (cur !== str) {
            this._view.dispatch({
                changes: { from: 0, to: cur.length, insert: str },
            });
        }
    }

    focus() {
        this._view.focus();
    }

    refresh() {
        this._view.requestMeasure();
    }

    on(event, handler) {
        if (event === 'change' && typeof handler === 'function') {
            this._changeListeners.push(handler);
        }
    }
}

window.MapleCM6 = {
    createEditor(hostElement, options) {
        return new Cm6EditorInstance(hostElement, options);
    },
};
