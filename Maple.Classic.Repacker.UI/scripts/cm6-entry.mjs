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
import { json } from '@codemirror/lang-json';
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
            backgroundColor: '#1e1e1e',
            height: '100%',
            fontSize: '12.5px',
            fontFamily: 'var(--font-mono, "Cascadia Code", "JetBrains Mono", Consolas, monospace)',
        },
        '.cm-content': {
            caretColor: '#aeafad',
            padding: '8px 0',
        },
        '.cm-cursor, .cm-dropCursor': {
            borderLeftColor: '#aeafad',
            borderLeftWidth: '2px',
        },
        '&.cm-focused .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection': {
            backgroundColor: '#264f78 !important',
        },
        '.cm-activeLine': {
            backgroundColor: 'rgba(255, 255, 255, 0.04)',
        },
        '.cm-selectionMatch': {
            backgroundColor: 'rgba(55, 148, 255, 0.22)',
        },
        '.cm-gutters': {
            backgroundColor: '#1e1e1e',
            color: '#858585',
            borderRight: '1px solid #2b2b2b',
        },
        '.cm-activeLineGutter': {
            backgroundColor: 'rgba(255, 255, 255, 0.05)',
            color: '#c6c6c6',
        },
        '&.cm-focused .cm-matchingBracket': {
            backgroundColor: 'rgba(0, 122, 204, 0.3)',
            outline: '1px solid #007acc',
            color: '#ffffff !important',
        },
        '.cm-foldPlaceholder': {
            backgroundColor: '#252526',
            border: '1px solid #3c3c3c',
            color: '#9cdcfe',
        },
        '.cm-tooltip': {
            border: '1px solid #454545',
            backgroundColor: '#252526',
            color: '#cccccc',
            borderRadius: '5px',
            boxShadow: '0 6px 20px rgba(0, 0, 0, 0.5)',
        },
        '.cm-tooltip-autocomplete > ul > li[aria-selected]': {
            backgroundColor: '#04395e',
            color: '#ffffff',
        },
    },
    { dark: true }
);

const mapleHighlightStyle = HighlightStyle.define([
    { tag: tags.keyword, color: '#c586c0', fontWeight: '600' },
    { tag: [tags.name, tags.deleted, tags.character, tags.propertyName, tags.macroName, tags.variableName], color: '#9cdcfe' },
    { tag: [tags.string, tags.special(tags.string)], color: '#ce9178' },
    { tag: [tags.number, tags.integer, tags.float], color: '#b5cea8' },
    { tag: [tags.bool, tags.null, tags.atom, tags.self], color: '#569cd6' },
    { tag: [tags.comment], color: '#6a9955', fontStyle: 'italic' },
    { tag: [tags.punctuation, tags.bracket, tags.brace, tags.operator], color: '#d4d4d4' },
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
        json(),
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
