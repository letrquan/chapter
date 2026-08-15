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

/*
 * These two themes are the third place the palette lives, after styles.css and the
 * WPF window. The editor sits inside a card, so `editor.background` has to be the
 * card's --bg-surface exactly — anything else draws a visible seam down the pane.
 */

monaco.editor.defineTheme(DARK, {
  base: 'vs-dark',
  inherit: true,
  rules: [],
  colors: {
    'editor.background': '#0d0f15',
    'editorGutter.background': '#0d0f15',
    'editor.lineHighlightBackground': '#151924',
    'editor.lineHighlightBorder': '#00000000',
    'editorLineNumber.foreground': '#3f4757',
    'editorLineNumber.activeForeground': '#8d96a8',
    'editorIndentGuide.background1': '#1b1f29',
    'editorIndentGuide.activeBackground1': '#2a3040',
    'editorWidget.background': '#161a25',
    'editorWidget.border': '#2a3040',
    'editorHoverWidget.background': '#161a25',
    'editorHoverWidget.border': '#2a3040',
    'editorSuggestWidget.background': '#161a25',
    'editorSuggestWidget.border': '#2a3040',
    'editorSuggestWidget.selectedBackground': '#1e2331',
    'editorStickyScroll.background': '#12141c',
    'editorStickyScrollHover.background': '#171b25',
    'peekViewResult.background': '#0a0c11',
    'peekViewEditor.background': '#0d0f15',
    'peekViewTitle.background': '#161a25',
    'peekView.border': '#5b8cff',
    'peekViewResult.selectionBackground': '#1e2331',
    'diffEditor.insertedTextBackground': '#3fb95022',
    'diffEditor.removedTextBackground': '#f8514922',
    'diffEditor.insertedLineBackground': '#3fb95014',
    'diffEditor.removedLineBackground': '#f8514914',
    'diffEditorGutter.insertedLineBackground': '#3fb95022',
    'diffEditorGutter.removedLineBackground': '#f8514922',
    // hideUnchangedRegions is on, so the collapsed band is a permanent fixture.
    'diffEditor.unchangedRegionBackground': '#12141c',
    'diffEditor.unchangedRegionForeground': '#8d96a8',
    'scrollbarSlider.background': '#262d3a80',
    'scrollbarSlider.hoverBackground': '#37404fb0',
    'scrollbarSlider.activeBackground': '#37404f',
    'editorOverviewRuler.border': '#00000000',
    focusBorder: '#5b8cff66',
  },
})

monaco.editor.defineTheme(LIGHT, {
  base: 'vs',
  inherit: true,
  rules: [],
  colors: {
    'editor.background': '#ffffff',
    'editorGutter.background': '#ffffff',
    'editor.lineHighlightBackground': '#f5f7fa',
    'editor.lineHighlightBorder': '#00000000',
    'editorLineNumber.foreground': '#b0b9c6',
    'editorLineNumber.activeForeground': '#5a6474',
    'editorIndentGuide.background1': '#e8ecf2',
    'editorIndentGuide.activeBackground1': '#c7ced9',
    'editorWidget.background': '#ffffff',
    'editorWidget.border': '#c7ced9',
    'editorHoverWidget.background': '#ffffff',
    'editorHoverWidget.border': '#c7ced9',
    'editorSuggestWidget.background': '#ffffff',
    'editorSuggestWidget.border': '#c7ced9',
    'editorSuggestWidget.selectedBackground': '#e4e8f0',
    'editorStickyScroll.background': '#f2f4f8',
    'editorStickyScrollHover.background': '#f0f2f7',
    'peekViewResult.background': '#f2f4f8',
    'peekViewEditor.background': '#ffffff',
    'peekViewTitle.background': '#f2f4f8',
    'peekView.border': '#2f5fe0',
    'peekViewResult.selectionBackground': '#e4e8f0',
    'diffEditor.insertedTextBackground': '#1a7f371f',
    'diffEditor.removedTextBackground': '#cf222e1f',
    'diffEditor.insertedLineBackground': '#1a7f3712',
    'diffEditor.removedLineBackground': '#cf222e12',
    'diffEditor.unchangedRegionBackground': '#f2f4f8',
    'diffEditor.unchangedRegionForeground': '#5a6474',
    'scrollbarSlider.background': '#ccd3dd80',
    'scrollbarSlider.hoverBackground': '#aeb7c4b0',
    'scrollbarSlider.activeBackground': '#aeb7c4',
    'editorOverviewRuler.border': '#00000000',
    focusBorder: '#2f5fe066',
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
  // The editor lives inside a card that clips to its rounded corners, so hover and
  // suggest widgets have to escape to the body or they are cut off at the pane edge.
  fixedOverflowWidgets: true,
  // The default, and still the right one for anything read at a commit or shown in the
  // diff. `setEditable` lifts it for the code view when the backend says the file can be
  // written back — never for the diff, where the left pane is history.
  readOnly: true,
  domReadOnly: true,
  renderWhitespace: 'selection',
  stickyScroll: { enabled: true },
}

let codeEditor: monaco.editor.IStandaloneCodeEditor | null = null
let diffEditor: monaco.editor.IStandaloneDiffEditor | null = null
let editorHost: HTMLElement
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
  return getModel(worktreePath, path, 'work', text, language).model
}

export function hasModel(worktreePath: string, path: string): boolean {
  const existing = models.get(modelKey(worktreePath, path, 'work'))
  return Boolean(existing && !existing.isDisposed())
}

/* --------------------------------------------------------------------------
   Unsaved edits

   The app refreshes the open file whenever the watcher fires, which is correct while
   nothing is editable and actively destructive once something is. An agent touching any
   file in the worktree triggers that refresh, and a refresh that calls setValue throws
   away whatever the user had typed — with no undo, because setValue is not an edit.

   So a dirty model is never overwritten. The reload is refused instead, and the caller
   is told, which turns a silent data loss into a visible "this changed underneath you".
   -------------------------------------------------------------------------- */

const dirty = new Set<string>()

/** Guards the content changes we cause ourselves, which must not count as user edits. */
let applyingOwnEdit = false

const dirtyListeners = new Set<(worktreePath: string, path: string, isDirty: boolean) => void>()

export function onDirtyChanged(
  handler: (worktreePath: string, path: string, isDirty: boolean) => void,
): void {
  dirtyListeners.add(handler)
}

function setDirty(worktreePath: string, path: string, value: boolean): void {
  const key = modelKey(worktreePath, path, 'work')
  if (value === dirty.has(key)) return

  if (value) dirty.add(key)
  else dirty.delete(key)

  for (const listener of dirtyListeners) listener(worktreePath, path, value)
}

export function isDirty(worktreePath: string, path: string): boolean {
  return dirty.has(modelKey(worktreePath, path, 'work'))
}

export function anyDirty(): boolean {
  return dirty.size > 0
}

/** The editor's current text, which is what a save has to write. */
export function currentText(worktreePath: string, path: string): string | undefined {
  const model = models.get(modelKey(worktreePath, path, 'work'))
  return model && !model.isDisposed() ? model.getValue() : undefined
}

/** Clears the dirty flag after a successful save. */
export function markSaved(worktreePath: string, path: string): void {
  setDirty(worktreePath, path, false)
}

/**
 * Whether the code editor accepts typing. The diff editor is never made editable — its
 * left pane is a commit, and the right pane of a scoped comparison is not the working
 * tree either.
 */
export function setEditable(editable: boolean): void {
  codeEditor?.updateOptions({ readOnly: !editable, domReadOnly: !editable })
}

/**
 * Gets or creates a model. Monaco keys models by URI, and reusing the same URI is what
 * preserves undo history, folding and — via saved view state — scroll position.
 *
 * Returns whether the text on screen is the text that was asked for. False means the model
 * held unsaved edits and was left alone.
 */
function getModel(
  worktreePath: string,
  path: string,
  side: 'base' | 'work',
  text: string,
  language: string,
): { model: monaco.editor.ITextModel; stale: boolean } {
  const key = modelKey(worktreePath, path, side)
  const existing = models.get(key)

  if (existing && !existing.isDisposed()) {
    if (existing.getValue() === text) return { model: existing, stale: false }

    // The one case where the app must not repaint: the user has typed here and has not
    // saved. Overwriting is unrecoverable, and Monaco's undo stack does not cover it.
    if (side === 'work' && dirty.has(key)) return { model: existing, stale: true }

    applyingOwnEdit = true
    try {
      existing.setValue(text)
    } finally {
      applyingOwnEdit = false
    }

    return { model: existing, stale: false }
  }

  const uri = modelUri(worktreePath, path, side)
  const model = monaco.editor.createModel(text, language, uri)
  models.set(key, model)
  modelOrigins.set(uri.toString(), { worktreePath, path, side })

  if (side === 'work') {
    model.onDidChangeContent(() => {
      if (!applyingOwnEdit) setDirty(worktreePath, path, true)
    })
  }

  return { model, stale: false }
}

/** Drops every model belonging to a worktree — used when a repo is closed. */
export function disposeWorktreeModels(worktreePath: string): void {
  for (const [key, model] of models) {
    if (key.includes(`:${worktreePath}:`)) {
      modelOrigins.delete(model.uri.toString())
      model.dispose()
      models.delete(key)
      // Or the worktree stays permanently "dirty" to anything asking, and a later
      // repository at the same path inherits a flag about files that no longer exist.
      dirty.delete(key)
    }
  }
}

export function initEditors(container: HTMLElement): void {
  editorHost = container
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
    // Monaco silently drops to a unified diff when the pane is narrower than this, and
    // its 900px default is wider than the editor gets at the app's own default window
    // size — so the side-by-side this tool exists for never appeared, and the toggle
    // button appeared to do nothing. Low enough that the split survives a normal window,
    // high enough that a genuinely cramped pane still falls back rather than showing two
    // useless columns.
    renderSideBySideInlineBreakpoint: 620,
    ignoreTrimWhitespace: false,
    renderOverviewRuler: true,
    diffWordWrap: 'off',
    renderGutterMenu: false,
    hideUnchangedRegions: { enabled: true, contextLineCount: 3, minimumLineCount: 6 },
  })

  // Registered on the editor rather than the window: Monaco swallows Ctrl+S while it has
  // focus, so a global listener never sees the one keystroke that matters most here.
  codeEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => saveHandler?.())

  // automaticLayout polls on a timer; a ResizeObserver reacts immediately and only when
  // the pane actually changes size, which matters when dragging the splitters.
  const observer = new ResizeObserver(() => layoutEditors())
  observer.observe(container)

  showMode('diff')
}

let saveHandler: (() => void) | null = null

export function setSaveHandler(handler: () => void): void {
  saveHandler = handler
}

/**
 * Lays out whichever editor is on screen, at the pane's measured size.
 *
 * The size has to be passed in rather than left for Monaco to measure. Left to itself the
 * diff editor measures its *own* element — an element whose size Monaco also writes — so
 * one layout taken while the pane is `display: none` records zero, stamps that on the
 * element, and every later measurement reads back the value it just wrote. The diff
 * collapses to a few pixels and stays collapsed: switching to Code and back left the pane
 * blank for the rest of the session, with the file list and Code view both fine.
 *
 * The hidden editor is skipped for the same reason — measuring it is what poisons it. It
 * gets a layout from showMode on the way back in, once it has a size to measure.
 */
function layoutEditors(): void {
  const { width, height } = editorHost.getBoundingClientRect()
  if (width === 0 || height === 0) return

  const dimension = { width, height }
  if (codeHost.style.display !== 'none') codeEditor?.layout(dimension)
  if (diffHost.style.display !== 'none') diffEditor?.layout(dimension)
}

/** 'preview' hides both editors — the Markdown preview owns the pane instead. */
export function showMode(mode: 'diff' | 'code' | 'preview'): void {
  codeHost.style.display = mode === 'code' ? 'block' : 'none'
  diffHost.style.display = mode === 'diff' ? 'block' : 'none'

  // A hidden Monaco instance skips layout, so it needs one on the way back in.
  layoutEditors()
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

/** @returns whether the pane shows the requested text; false when unsaved edits blocked it. */
export function showDiff(input: DiffInput): boolean {
  if (!diffEditor) return true

  const base = getModel(input.worktreePath, input.path, 'base', input.baseText, input.language)
  const work = getModel(input.worktreePath, input.path, 'work', input.workingText, input.language)

  diffEditor.setModel({ original: base.model, modified: work.model })
  layoutEditors()

  return !work.stale
}

/** @returns whether the pane shows the requested text; false when unsaved edits blocked it. */
export function showCode(
  worktreePath: string,
  path: string,
  text: string,
  language: string,
  editable = false,
): boolean {
  if (!codeEditor) return true

  const result = getModel(worktreePath, path, 'work', text, language)
  codeEditor.setModel(result.model)
  setEditable(editable)
  layoutEditors()

  return !result.stale
}

/** Moves the caret to a line and scrolls it into view, centred. No-op in preview. */
export function revealPosition(mode: 'diff' | 'code' | 'preview', line: number, column = 1): void {
  if (mode === 'preview') return

  const target = mode === 'diff' ? diffEditor?.getModifiedEditor() : codeEditor
  if (!target) return

  target.setPosition({ lineNumber: line, column })
  target.revealLineInCenter(line)
  target.focus()
}

/** Line the caret currently sits on, for jumping from the diff into the full file. */
export function currentLine(mode: 'diff' | 'code' | 'preview'): number {
  if (mode === 'preview') return 1

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

/** An inclusive run of lines the user has selected in one pane of the diff. */
export interface LineRange {
  start: number
  end: number
  /** True when nothing is dragged — this is just where the caret happens to sit. */
  isCaret: boolean
  /** Whether this pane holds the keyboard focus, which is what breaks a tie between carets. */
  focused: boolean
}

/**
 * What is selected in each pane of the diff.
 *
 * Both panes matter for line staging, and which one is which is not interchangeable: the
 * modified pane can only ever select added lines, because removed lines are not in it.
 * Picking a deletion means selecting it on the left.
 */
export function diffSelections(): { base: LineRange | null; work: LineRange | null } {
  const original = diffEditor?.getOriginalEditor()
  const modified = diffEditor?.getModifiedEditor()

  const read = (editor: monaco.editor.ICodeEditor | undefined): LineRange | null => {
    const selection = editor?.getSelection()
    if (!selection) return null

    // An empty selection is a caret, and a caret is still a line the user is pointing at —
    // "stage this line" with no drag is a reasonable thing to mean.
    return {
      start: selection.startLineNumber,
      end: selection.endLineNumber,
      isCaret: selection.isEmpty(),
      focused: editor?.hasTextFocus() ?? false,
    }
  }

  const base = read(original)
  const work = read(modified)

  // Both panes always report something, and only one of them is what the user meant.
  //
  // The app plants carets itself — stepping between hunks calls setPosition — so a stale
  // caret sits in the pane the user is not working in. Counting it alongside a real drag
  // in the other pane silently widens the selection: a deletion under the forgotten caret
  // gets staged with the additions, or destroyed by "Discard selection".
  //
  // So a real drag wins outright, and when there is none, only the focused pane's caret
  // counts.
  const dragged = (range: LineRange | null): boolean => range !== null && !range.isCaret

  if (dragged(base) || dragged(work)) {
    return { base: dragged(base) ? base : null, work: dragged(work) ? work : null }
  }

  return {
    base: base?.focused ? base : null,
    work: work?.focused ? work : null,
  }
}

/**
 * Whether anything in the diff counts as selected right now.
 *
 * Deliberately answered by `diffSelections` rather than by asking Monaco again: the two
 * must agree, or a button enables itself on a stale caret that the mapping then ignores —
 * or worse, acts on one the button never counted.
 */
export function hasLineSelection(): boolean {
  const selection = diffSelections()
  return selection.base !== null || selection.work !== null
}

/**
 * Notifies when the selection in either diff pane changes, so a "stage selection" control
 * can enable and disable itself as the user drags rather than only on a repaint.
 */
export function onDiffSelectionChanged(handler: () => void): void {
  diffEditor?.getOriginalEditor().onDidChangeCursorSelection(() => handler())
  diffEditor?.getModifiedEditor().onDidChangeCursorSelection(() => handler())
}

/** Scrolls the diff to a line, used when stepping between hunks. */
export function revealDiffLine(line: number, side: 'base' | 'work' = 'work'): void {
  const target = side === 'base' ? diffEditor?.getOriginalEditor() : diffEditor?.getModifiedEditor()
  if (!target) return

  target.revealLineInCenter(line)
  target.setPosition({ lineNumber: line, column: 1 })
}

export { monaco }
