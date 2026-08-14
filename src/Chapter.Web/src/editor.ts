import * as monaco from 'monaco-editor'

/**
 * Monaco setup: worker wiring, themes matched to the app palette, and model reuse.
 *
 * Models are cached per (worktree, path) so that switching tabs — or switching worktrees
 * and coming back — restores the exact scroll offset and selection rather than jumping to
 * the top of the file.
 */

// Monaco tokenises and computes diffs off the main thread. Without a worker the diff
// editor renders two plain panes with no change highlighting at all.
self.MonacoEnvironment = {
  getWorker: () => new Worker('/editor.worker.js', { type: 'module' }),
}

const DARK = 'chapter-dark'
const LIGHT = 'chapter-light'

monaco.editor.defineTheme(DARK, {
  base: 'vs-dark',
  inherit: true,
  rules: [],
  colors: {
    'editor.background': '#0b0d10',
    'editorGutter.background': '#0b0d10',
    'editor.lineHighlightBackground': '#141922',
    'editor.lineHighlightBorder': '#00000000',
    'editorLineNumber.foreground': '#414b5a',
    'editorLineNumber.activeForeground': '#8b95a7',
    'editorIndentGuide.background1': '#1b2129',
    'editorIndentGuide.activeBackground1': '#2b3441',
    'editorWidget.background': '#151a21',
    'editorWidget.border': '#29323d',
    'editorHoverWidget.background': '#151a21',
    'editorHoverWidget.border': '#29323d',
    'editorSuggestWidget.background': '#151a21',
    'editorSuggestWidget.border': '#29323d',
    'editorSuggestWidget.selectedBackground': '#1d2530',
    'peekViewResult.background': '#0d1014',
    'peekViewEditor.background': '#0b0d10',
    'peekViewTitle.background': '#151a21',
    'peekView.border': '#7c9cff',
    'peekViewResult.selectionBackground': '#1d2530',
    'diffEditor.insertedTextBackground': '#3fb95022',
    'diffEditor.removedTextBackground': '#f8514922',
    'diffEditor.insertedLineBackground': '#3fb95014',
    'diffEditor.removedLineBackground': '#f8514914',
    'diffEditorGutter.insertedLineBackground': '#3fb95022',
    'diffEditorGutter.removedLineBackground': '#f8514922',
    'scrollbarSlider.background': '#2a323d80',
    'scrollbarSlider.hoverBackground': '#3a4552b0',
    'scrollbarSlider.activeBackground': '#3a4552',
    'editorOverviewRuler.border': '#00000000',
    focusBorder: '#7c9cff66',
  },
})

monaco.editor.defineTheme(LIGHT, {
  base: 'vs',
  inherit: true,
  rules: [],
  colors: {
    'editor.background': '#ffffff',
    'editorGutter.background': '#ffffff',
    'editor.lineHighlightBackground': '#f4f6f9',
    'editor.lineHighlightBorder': '#00000000',
    'editorLineNumber.foreground': '#b4bcc8',
    'editorLineNumber.activeForeground': '#5f6977',
    'editorIndentGuide.background1': '#e9edf2',
    'editorWidget.background': '#ffffff',
    'editorWidget.border': '#cdd4dd',
    'peekViewResult.background': '#fbfcfd',
    'peekView.border': '#3b62d9',
    'diffEditor.insertedTextBackground': '#1a7f371f',
    'diffEditor.removedTextBackground': '#cf222e1f',
    'scrollbarSlider.background': '#cdd4dd80',
    focusBorder: '#3b62d966',
  },
})

const commonOptions: monaco.editor.IStandaloneEditorConstructionOptions = {
  automaticLayout: false, // we drive layout from a ResizeObserver; cheaper and no polling
  fontFamily: "'Cascadia Code', 'Cascadia Mono', 'JetBrains Mono', Consolas, monospace",
  fontSize: 12.5,
  lineHeight: 19,
  fontLigatures: true,
  minimap: { enabled: false },
  scrollBeyondLastLine: false,
  smoothScrolling: true,
  cursorBlinking: 'smooth',
  renderLineHighlight: 'line',
  roundedSelection: false,
  padding: { top: 10, bottom: 24 },
  scrollbar: {
    verticalScrollbarSize: 11,
    horizontalScrollbarSize: 11,
    useShadows: false,
  },
  overviewRulerBorder: false,
  guides: { indentation: true },
  bracketPairColorization: { enabled: true },
  contextmenu: true,
  // This is a review tool: nothing here writes to disk.
  readOnly: true,
  domReadOnly: true,
  renderWhitespace: 'selection',
  stickyScroll: { enabled: true },
}

let codeEditor: monaco.editor.IStandaloneCodeEditor | null = null
let diffEditor: monaco.editor.IStandaloneDiffEditor | null = null
let codeHost: HTMLElement
let diffHost: HTMLElement

const models = new Map<string, monaco.editor.ITextModel>()

/**
 * Maps a model URI back to the worktree and file it came from.
 *
 * Navigation providers are handed a model, not our own state, so this is how a definition
 * request knows which worktree to ask about. Parsing it out of the URI would be fragile —
 * Windows paths contain a drive colon that URI parsing treats as an authority separator.
 */
export interface ModelOrigin {
  worktreePath: string
  path: string
  /** Which pane of a diff this model backs. */
  side: 'base' | 'work'
}

const modelOrigins = new Map<string, ModelOrigin>()

function modelKey(worktreePath: string, path: string, side: 'base' | 'work'): string {
  return `${side}:${worktreePath}:${path}`
}

export function modelUri(worktreePath: string, path: string, side: 'base' | 'work' = 'work'): monaco.Uri {
  return monaco.Uri.parse(`chapter://${side}/${encodeURI(worktreePath)}/${encodeURI(path)}`)
}

export function resolveModelOrigin(uri: monaco.Uri): ModelOrigin | undefined {
  return modelOrigins.get(uri.toString())
}

/**
 * Creates a model for a file without displaying it. Peek widgets render from models, so
 * a definition in an unopened file shows nothing unless one exists first.
 */
export function ensureModel(
  worktreePath: string,
  path: string,
  text: string,
  language: string,
): monaco.editor.ITextModel {
  return getModel(worktreePath, path, 'work', text, language)
}

export function hasModel(worktreePath: string, path: string): boolean {
  const existing = models.get(modelKey(worktreePath, path, 'work'))
  return Boolean(existing && !existing.isDisposed())
}

/**
 * Gets or creates a model. Monaco keys models by URI, and reusing the same URI is what
 * preserves undo history, folding and — via saved view state — scroll position.
 */
function getModel(
  worktreePath: string,
  path: string,
  side: 'base' | 'work',
  text: string,
  language: string,
): monaco.editor.ITextModel {
  const key = modelKey(worktreePath, path, side)
  const existing = models.get(key)

  if (existing && !existing.isDisposed()) {
    if (existing.getValue() !== text) existing.setValue(text)
    return existing
  }

  const uri = modelUri(worktreePath, path, side)
  const model = monaco.editor.createModel(text, language, uri)
  models.set(key, model)
  modelOrigins.set(uri.toString(), { worktreePath, path, side })
  return model
}

/** Drops every model belonging to a worktree — used when a repo is closed. */
export function disposeWorktreeModels(worktreePath: string): void {
  for (const [key, model] of models) {
    if (key.includes(`:${worktreePath}:`)) {
      modelOrigins.delete(model.uri.toString())
      model.dispose()
      models.delete(key)
    }
  }
}

export function initEditors(container: HTMLElement): void {
  codeHost = document.createElement('div')
  diffHost = document.createElement('div')
  for (const host of [codeHost, diffHost]) {
    host.style.position = 'absolute'
    host.style.inset = '0'
    container.appendChild(host)
  }

  codeEditor = monaco.editor.create(codeHost, commonOptions)
  diffEditor = monaco.editor.createDiffEditor(diffHost, {
    ...commonOptions,
    renderSideBySide: true,
    ignoreTrimWhitespace: false,
    renderOverviewRuler: true,
    diffWordWrap: 'off',
    renderGutterMenu: false,
    hideUnchangedRegions: { enabled: true, contextLineCount: 3, minimumLineCount: 6 },
  })

  // automaticLayout polls on a timer; a ResizeObserver reacts immediately and only when
  // the pane actually changes size, which matters when dragging the splitters.
  const observer = new ResizeObserver(() => {
    codeEditor?.layout()
    diffEditor?.layout()
  })
  observer.observe(container)

  showMode('diff')
}

export function showMode(mode: 'diff' | 'code'): void {
  codeHost.style.display = mode === 'code' ? 'block' : 'none'
  diffHost.style.display = mode === 'diff' ? 'block' : 'none'
  // A hidden Monaco instance skips layout, so it needs one on the way back in.
  if (mode === 'code') codeEditor?.layout()
  else diffEditor?.layout()
}

export function setTheme(theme: 'dark' | 'light'): void {
  monaco.editor.setTheme(theme === 'dark' ? DARK : LIGHT)
}

export interface DiffInput {
  worktreePath: string
  path: string
  baseText: string
  workingText: string
  language: string
}

export function showDiff(input: DiffInput): void {
  if (!diffEditor) return

  diffEditor.setModel({
    original: getModel(input.worktreePath, input.path, 'base', input.baseText, input.language),
    modified: getModel(input.worktreePath, input.path, 'work', input.workingText, input.language),
  })
  diffEditor.layout()
}

export function showCode(worktreePath: string, path: string, text: string, language: string): void {
  if (!codeEditor) return
  codeEditor.setModel(getModel(worktreePath, path, 'work', text, language))
  codeEditor.layout()
}

/** Moves the caret to a line and scrolls it into view, centred. */
export function revealPosition(mode: 'diff' | 'code', line: number, column = 1): void {
  const target = mode === 'diff' ? diffEditor?.getModifiedEditor() : codeEditor
  if (!target) return

  target.setPosition({ lineNumber: line, column })
  target.revealLineInCenter(line)
  target.focus()
}

/** Line the caret currently sits on, for jumping from the diff into the full file. */
export function currentLine(mode: 'diff' | 'code'): number {
  const target = mode === 'diff' ? diffEditor?.getModifiedEditor() : codeEditor
  return target?.getPosition()?.lineNumber ?? 1
}

export type ViewState = {
  code: monaco.editor.ICodeEditorViewState | null
  diff: monaco.editor.IDiffEditorViewState | null
}

export function saveViewState(): ViewState {
  return {
    code: codeEditor?.saveViewState() ?? null,
    diff: diffEditor?.saveViewState() ?? null,
  }
}

export function restoreViewState(state: ViewState | undefined): void {
  if (!state) return
  if (state.code) codeEditor?.restoreViewState(state.code)
  if (state.diff) diffEditor?.restoreViewState(state.diff)
}

export function setSideBySide(sideBySide: boolean): void {
  diffEditor?.updateOptions({ renderSideBySide: sideBySide })
}

export function focusEditor(mode: 'diff' | 'code'): void {
  if (mode === 'diff') diffEditor?.getModifiedEditor().focus()
  else codeEditor?.focus()
}

export { monaco }
