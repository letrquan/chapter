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

/*
 * Monaco's TypeScript, JSON, CSS and HTML language services answer from workers of their
 * own — ts.worker.js and friends — which this build deliberately does not produce. The one
 * worker above is the generic editor worker, so every model in one of those languages asked
 * it for getSyntacticDiagnostics, provideInlayHints, getNavigationTree and the rest, and
 * each unanswered call arrived as a stacked error toast the moment a .ts file was opened.
 *
 * Turning the worker-backed providers off is the fix rather than shipping the workers:
 * semantic navigation here is C#-only and served by the backend index (see navigation.ts),
 * and a review tool opening one file out of a repository has neither that file's tsconfig
 * nor its node_modules — TypeScript's own diagnostics would be red squiggles under half the
 * imports in the file, every time. Tokenisation is Monarch on the main thread and is not
 * touched, so every language still highlights exactly as before.
 */
const NO_LANGUAGE_SERVICE = {
  completionItems: false,
  hovers: false,
  documentSymbols: false,
  definitions: false,
  references: false,
  documentHighlights: false,
  rename: false,
  colors: false,
  foldingRanges: false,
  diagnostics: false,
  selectionRanges: false,
  documentFormattingEdits: false,
  documentRangeFormattingEdits: false,
  signatureHelp: false,
  onTypeFormattingEdits: false,
  codeActions: false,
  inlayHints: false,
  links: false,
}

// The top-level namespaces, not monaco.languages.*: those moved in 0.56 and the old
// spellings are now stubs typed `{ deprecated: true }` that silently do nothing.
// razor is in LanguageMap too, so .razor and .cshtml reach the HTML service.
for (const defaults of [
  monaco.typescript.typescriptDefaults,
  monaco.typescript.javascriptDefaults,
  monaco.json.jsonDefaults,
  monaco.css.cssDefaults,
  monaco.css.scssDefaults,
  monaco.css.lessDefaults,
  monaco.html.htmlDefaults,
  monaco.html.handlebarDefaults,
  monaco.html.razorDefaults,
]) {
  defaults.setModeConfiguration(NO_LANGUAGE_SERVICE)
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
let mergeHost: HTMLElement
let mergeBaseHost: HTMLElement
let mergeOursHost: HTMLElement
let mergeTheirsHost: HTMLElement
let mergeResultHost: HTMLElement
let mergeBaseEditor: monaco.editor.IStandaloneCodeEditor | null = null
let mergeOursEditor: monaco.editor.IStandaloneCodeEditor | null = null
let mergeTheirsEditor: monaco.editor.IStandaloneCodeEditor | null = null
let mergeResultEditor: monaco.editor.IStandaloneCodeEditor | null = null
let editorHost: HTMLElement
let codeHost: HTMLElement
let diffHost: HTMLElement
let blameDecorations: string[] = []
let codeConflictDecorations: string[] = []
let mergeConflictDecorations: string[] = []
let mergeConflictViewZones: string[] = []

/** Keeps repository-controlled author/message text from becoming Markdown in a hover. */
function blameHoverText(value: string): string {
  let escaped = ''
  for (const character of value.replace(/[\r\n]+/g, ' ')) {
    if ('\\`*_{}[]()#+-.!|>~'.includes(character)) escaped += '\\'
    escaped += character
  }
  return escaped
}

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

function modelKey(
  worktreePath: string,
  path: string,
  side: 'base' | 'work',
  identity = path,
): string {
  return `${side}:${worktreePath}:${identity}`
}

export function modelUri(
  worktreePath: string,
  path: string,
  side: 'base' | 'work' = 'work',
  identity = path,
): monaco.Uri {
  return monaco.Uri.parse(`chapter://${side}/${encodeURI(worktreePath)}/${encodeURI(identity)}`)
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
  identity = path,
): { model: monaco.editor.ITextModel; stale: boolean } {
  const key = modelKey(worktreePath, path, side, identity)
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

  const uri = modelUri(worktreePath, path, side, identity)
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
  mergeHost = document.createElement('div')
  mergeHost.className = 'merge-editor'
  const panes: Array<[string, string]> = [
    ['merge-base', 'Base'],
    ['merge-ours', 'Ours'],
    ['merge-theirs', 'Theirs'],
    ['merge-result', 'Result'],
  ]
  const paneHosts: HTMLElement[] = []
  for (const [className, label] of panes) {
    const pane = document.createElement('section')
    pane.className = `merge-pane ${className}`
    const heading = document.createElement('div')
    heading.className = 'merge-pane-head'
    heading.textContent = label
    const host = document.createElement('div')
    host.className = 'merge-pane-editor'
    pane.append(heading, host)
    mergeHost.appendChild(pane)
    paneHosts.push(host)
  }
  ;[mergeBaseHost, mergeOursHost, mergeTheirsHost, mergeResultHost] = paneHosts as [
    HTMLElement, HTMLElement, HTMLElement, HTMLElement,
  ]
  for (const host of [codeHost, diffHost]) {
    host.style.position = 'absolute'
    host.style.inset = '0'
    container.appendChild(host)
  }
  mergeHost.style.position = 'absolute'
  mergeHost.style.inset = '0'
  container.appendChild(mergeHost)

  // Blame lives only in the code view; keeping the diff gutters unchanged preserves the
  // horizontal space the side-by-side comparison needs.
  codeEditor = monaco.editor.create(codeHost, { ...commonOptions, glyphMargin: true })
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

  const mergeSourceOptions = { ...commonOptions, glyphMargin: false, readOnly: true, domReadOnly: true }
  mergeBaseEditor = monaco.editor.create(mergeBaseHost, mergeSourceOptions)
  mergeOursEditor = monaco.editor.create(mergeOursHost, mergeSourceOptions)
  mergeTheirsEditor = monaco.editor.create(mergeTheirsHost, mergeSourceOptions)
  mergeResultEditor = monaco.editor.create(mergeResultHost, { ...commonOptions, glyphMargin: true })

  // Registered on the editor rather than the window: Monaco swallows Ctrl+S while it has
  // focus, so a global listener never sees the one keystroke that matters most here.
  for (const editor of [codeEditor, mergeResultEditor])
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => saveHandler?.())

  // Ctrl+D needs the same treatment, and needs it on every pane. Monaco binds it to
  // addSelectionToNextFindMatch, so with the caret in the editor the window listener never
  // ran and the Diff/Code toggle went dead — one-way, since switching to Code puts focus
  // in the editor and there is then no way back to the diff without reaching for the mouse.
  for (const editor of [codeEditor, mergeResultEditor, diffEditor.getOriginalEditor(), diffEditor.getModifiedEditor()]) {
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyD, () => modeToggleHandler?.())
  }

  // Three more the window listener never sees, for the same reason and found the same way —
  // by driving the built app and watching a documented shortcut do nothing with the caret in
  // the editor. Monaco's own defaults are what swallow them: Ctrl+H is Replace
  // (`findController`), Ctrl+G is Go to Line and Ctrl+Shift+O is Go to Symbol (both in
  // `standalone/browser/quickAccess`). Chapter's meanings win, as they did for Ctrl+D: the
  // app has its own symbol search on Ctrl+T, and an editor here is a review surface first.
  // Ctrl+F is deliberately not in this list — that one is Monaco's Find, which the app has
  // no replacement for and no business taking.
  for (const editor of [codeEditor, mergeResultEditor, diffEditor.getOriginalEditor(), diffEditor.getModifiedEditor()]) {
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyH, () => historyHandler?.())
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyG, () => generateMessageHandler?.())
    editor.addCommand(
      monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyO,
      () => cloneHandler?.(),
    )

    // And the refs panel, both halves. Monaco's defaults do not list Ctrl+B, but driving the
    // built app says otherwise: with the caret in an editor the panel did not open, and it
    // opens immediately from the file list. Registered here for the same reason as the rest
    // — whatever swallows it, the binding the help panel advertises has to work where the
    // reviewer's cursor actually is.
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyB, () => refsHandler?.(false))
    editor.addCommand(
      monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyB,
      () => refsHandler?.(true),
    )
  }

  // The batch-review keys are plain punctuation, so they belong only to Monaco's read-only
  // diff panes. Binding them in Code or the merge result would eat characters the user is
  // trying to type; leaving them to the window would make them disappear while reviewing,
  // because Monaco owns the focused textarea.
  for (const editor of [diffEditor.getOriginalEditor(), diffEditor.getModifiedEditor()]) {
    editor.addCommand(monaco.KeyCode.BracketLeft, () => batchReviewHandler?.(-1))
    editor.addCommand(monaco.KeyCode.BracketRight, () => batchReviewHandler?.(1))
  }

  // Ctrl+Alt+M is the explicit "mark reviewed" action. Monaco swallows modifier keys in
  // its text areas, so register it on read-only diff panes as well as on the window.
  for (const editor of [codeEditor, mergeResultEditor, diffEditor.getOriginalEditor(), diffEditor.getModifiedEditor()]) {
    editor.addCommand(
      monaco.KeyMod.CtrlCmd | monaco.KeyMod.Alt | monaco.KeyCode.KeyM,
      () => markReviewedHandler?.(),
    )
  }

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

let modeToggleHandler: (() => void) | null = null

export function setModeToggleHandler(handler: () => void): void {
  modeToggleHandler = handler
}

let historyHandler: (() => void) | null = null

export function setHistoryHandler(handler: () => void): void {
  historyHandler = handler
}

let generateMessageHandler: (() => void) | null = null

export function setGenerateMessageHandler(handler: () => void): void {
  generateMessageHandler = handler
}

let cloneHandler: (() => void) | null = null

export function setCloneHandler(handler: () => void): void {
  cloneHandler = handler
}

/** True opens the worktrees half, matching the shifted form of the window binding. */
let refsHandler: ((worktrees: boolean) => void) | null = null

export function setRefsHandler(handler: (worktrees: boolean) => void): void {
  refsHandler = handler
}

let batchReviewHandler: ((delta: 1 | -1) => void) | null = null

export function setBatchReviewHandler(handler: (delta: 1 | -1) => void): void {
  batchReviewHandler = handler
}

let markReviewedHandler: (() => void) | null = null

export function setMarkReviewedHandler(handler: () => void): void {
  markReviewedHandler = handler
}

export type ConflictRegionAction = 'ours' | 'theirs' | 'both'

let conflictResolveHandler: ((region: number, action: ConflictRegionAction) => void) | null = null

/** Wires the first-class region controls without making the editor module own the bridge. */
export function setConflictResolveHandler(
  handler: (region: number, action: ConflictRegionAction) => void,
): void {
  conflictResolveHandler = handler
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
  if (mergeHost.style.display !== 'none') {
    const panes = [mergeBaseHost, mergeOursHost, mergeTheirsHost, mergeResultHost]
    for (const host of panes) {
      const rect = host.getBoundingClientRect()
      if (rect.width > 0 && rect.height > 0) {
        const size = { width: rect.width, height: rect.height }
        if (host === mergeBaseHost) mergeBaseEditor?.layout(size)
        else if (host === mergeOursHost) mergeOursEditor?.layout(size)
        else if (host === mergeTheirsHost) mergeTheirsEditor?.layout(size)
        else mergeResultEditor?.layout(size)
      }
    }
  }
}

/** Drops the read-only model pair created for a cross-worktree comparison. */
export function disposeWorktreeComparisonModels(leftWorktreePath: string, rightWorktreePath: string): void {
  const prefixes = [
    `base:${leftWorktreePath}:comparison:`,
    `work:${rightWorktreePath}:comparison:`,
  ]

  for (const [key, model] of models) {
    if (!prefixes.some((prefix) => key.startsWith(prefix))) continue
    modelOrigins.delete(model.uri.toString())
    model.dispose()
    models.delete(key)
  }
}

/** 'preview' hides all code editors — the Markdown preview owns the pane instead. */
export function showMode(mode: 'diff' | 'code' | 'preview' | 'merge'): void {
  codeHost.style.display = mode === 'code' ? 'block' : 'none'
  diffHost.style.display = mode === 'diff' ? 'block' : 'none'
  mergeHost.style.display = mode === 'merge' ? 'grid' : 'none'

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
  /** Optional model namespace for a historical or otherwise non-working-tree diff. */
  identity?: string
}

export interface WorktreeComparisonDiffInput {
  leftWorktreePath: string
  rightWorktreePath: string
  leftPath: string
  rightPath: string
  leftText: string
  rightText: string
  language: string
  /** Keeps comparison models separate from ordinary worktree and history tabs. */
  identity: string
}

/** @returns whether the pane shows the requested text; false when unsaved edits blocked it. */
export function showDiff(input: DiffInput): boolean {
  if (!diffEditor) return true

  clearConflictDecorations()

  const identity = input.identity ?? input.path
  const base = getModel(
    input.worktreePath,
    input.path,
    'base',
    input.baseText,
    input.language,
    identity,
  )
  const work = getModel(
    input.worktreePath,
    input.path,
    'work',
    input.workingText,
    input.language,
    identity,
  )

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

  clearConflictDecorations()

  const result = getModel(worktreePath, path, 'work', text, language)
  codeEditor.setModel(result.model)
  setEditable(editable)
  layoutEditors()

  return !result.stale
}

/**
 * Shows a read-only diff whose two models belong to different worktrees.
 *
 * The ordinary diff helper assumes both sides share one worktree because its right model
 * may be editable in Code mode later. Cross-worktree comparisons must never inherit that
 * identity: doing so would let a comparison tab reuse (and potentially dirty) the current
 * worktree's model when both checkouts contain a file with the same name.
 */
export function showWorktreeComparisonDiff(input: WorktreeComparisonDiffInput): void {
  if (!diffEditor) return

  clearConflictDecorations()
  const base = getModel(
    input.leftWorktreePath,
    input.leftPath || input.rightPath,
    'base',
    input.leftText,
    input.language,
    `${input.identity}:left`,
  )
  const work = getModel(
    input.rightWorktreePath,
    input.rightPath || input.leftPath,
    'work',
    input.rightText,
    input.language,
    `${input.identity}:right`,
  )

  diffEditor.setModel({ original: base.model, modified: work.model })
  diffEditor.updateOptions({ readOnly: true, renderSideBySide: true })
  setEditable(false)
  layoutEditors()
}

export interface ConflictInput {
  worktreePath: string
  path: string
  baseText: string
  oursText: string
  theirsText: string
  resultText: string
  language: string
  /** False for text that Monaco cannot round-trip without changing its bytes. */
  editable?: boolean
}

/**
 * Shows all three index stages beside the editable working result. The source panes are
 * read-only snapshots; the result reuses the normal working-tree model so save, dirty
 * tracking and Monaco undo continue to mean exactly what they do in Code mode.
 */
export function showConflict(input: ConflictInput): boolean {
  if (!mergeResultEditor) return true

  clearConflictDecorations()

  const base = getModel(input.worktreePath, input.path, 'base', input.baseText, input.language, 'conflict:base')
  const ours = getModel(input.worktreePath, input.path, 'base', input.oursText, input.language, 'conflict:ours')
  const theirs = getModel(input.worktreePath, input.path, 'base', input.theirsText, input.language, 'conflict:theirs')
  const result = getModel(input.worktreePath, input.path, 'work', input.resultText, input.language)

  mergeBaseEditor?.setModel(base.model)
  mergeOursEditor?.setModel(ours.model)
  mergeTheirsEditor?.setModel(theirs.model)
  mergeResultEditor.setModel(result.model)
  const editable = input.editable ?? true
  mergeResultEditor.updateOptions({ readOnly: !editable, domReadOnly: !editable })
  clearBlameDecorations()
  layoutEditors()
  return !result.stale
}

/** Clears blame markers from the code editor, including when its model changes. */
export function clearBlameDecorations(): void {
  if (!codeEditor) {
    blameDecorations = []
    return
  }

  blameDecorations = codeEditor.deltaDecorations(blameDecorations, [])
}

/** Highlights marker-delimited conflict regions in the editable result. */
export function setConflictDecorations(
  regions: readonly { startLine: number; endLine: number; separatorLine: number; baseLine: number | null }[],
): void {
  const decorations: monaco.editor.IModelDeltaDecoration[] = []
  for (const region of regions) {
    decorations.push({
      range: new monaco.Range(region.startLine, 1, region.endLine, 1),
      options: { isWholeLine: true, className: 'chapter-conflict-region' },
    })
    for (const line of [region.startLine, region.baseLine, region.separatorLine, region.endLine]) {
      if (line == null) continue
      decorations.push({
        range: new monaco.Range(line, 1, line, 1),
        options: {
          isWholeLine: true,
          className: 'chapter-conflict-marker',
          glyphMarginClassName: 'chapter-conflict-glyph',
        },
      })
    }
  }

  if (codeEditor) codeConflictDecorations = codeEditor.deltaDecorations(codeConflictDecorations, decorations)
  if (mergeResultEditor) mergeConflictDecorations = mergeResultEditor.deltaDecorations(mergeConflictDecorations, decorations)
  setConflictViewZones(regions)
}

export function clearConflictDecorations(): void {
  codeEditor?.deltaDecorations(codeConflictDecorations, [])
  mergeResultEditor?.deltaDecorations(mergeConflictDecorations, [])
  codeConflictDecorations = []
  mergeConflictDecorations = []
  clearConflictViewZones()
}

function setConflictViewZones(
  regions: readonly { startLine: number }[],
): void {
  if (!mergeResultEditor) return

  mergeResultEditor.changeViewZones((accessor) => {
    for (const id of mergeConflictViewZones) accessor.removeZone(id)
    mergeConflictViewZones = []

    regions.forEach((region, index) => {
      const row = document.createElement('div')
      row.className = 'conflict-region-actions'

      const label = document.createElement('span')
      label.textContent = `Conflict ${index + 1}`
      row.appendChild(label)

      for (const [action, text] of [
        ['ours', 'Ours'],
        ['theirs', 'Theirs'],
        ['both', 'Both'],
      ] as const) {
        const button = document.createElement('button')
        button.type = 'button'
        button.className = 'conflict-region-action'
        button.textContent = text
        button.addEventListener('click', (event) => {
          event.preventDefault()
          event.stopPropagation()
          conflictResolveHandler?.(index, action)
        })
        row.appendChild(button)
      }

      mergeConflictViewZones.push(accessor.addZone({
        afterLineNumber: Math.max(0, region.startLine - 1),
        heightInPx: 28,
        domNode: row,
        suppressMouseDown: false,
      }))
    })
  })
}

function clearConflictViewZones(): void {
  if (!mergeResultEditor || mergeConflictViewZones.length === 0) return
  mergeResultEditor.changeViewZones((accessor) => {
    for (const id of mergeConflictViewZones) accessor.removeZone(id)
  })
  mergeConflictViewZones = []
}

export interface BlameDecoration {
  lineNumber: number
  shortSha: string
  author: string
  subject: string
  uncommitted: boolean
  boundary: boolean
}

/**
 * Paints a compact attribution mark per line. Full commit detail stays in the hover so
 * enabling blame does not turn the editor into a second, permanently wide text column.
 */
export function setBlameDecorations(lines: readonly BlameDecoration[]): void {
  if (!codeEditor) return

  const model = codeEditor.getModel()
  if (!model) {
    clearBlameDecorations()
    return
  }

  const decorations: monaco.editor.IModelDeltaDecoration[] = []
  for (const line of lines) {
    if (line.lineNumber < 1 || line.lineNumber > model.getLineCount()) continue

    const classes = [
      'chapter-blame-glyph',
      line.uncommitted ? 'chapter-blame-uncommitted' : '',
      line.boundary ? 'chapter-blame-boundary' : '',
    ].filter(Boolean).join(' ')
    const author = line.author || 'unknown author'
    const subject = line.subject || '(no subject)'

    decorations.push({
      range: new monaco.Range(line.lineNumber, 1, line.lineNumber, 1),
      options: {
        glyphMarginClassName: classes,
        glyphMarginHoverMessage: {
          value: `**${blameHoverText(line.shortSha || 'uncommitted')}** · ${blameHoverText(author)}\n\n${blameHoverText(subject)}`,
          isTrusted: false,
          supportHtml: false,
        },
      },
    })
  }

  blameDecorations = codeEditor.deltaDecorations(blameDecorations, decorations)
}

/** Moves the caret to a line and scrolls it into view, centred. No-op in preview. */
export function revealPosition(mode: 'diff' | 'code' | 'preview' | 'merge', line: number, column = 1): void {
  if (mode === 'preview') return

  const target = mode === 'diff'
    ? diffEditor?.getModifiedEditor()
    : mode === 'merge' ? mergeResultEditor : codeEditor
  if (!target) return

  target.setPosition({ lineNumber: line, column })
  target.revealLineInCenter(line)
  target.focus()
}

/** Line the caret currently sits on, for jumping from the diff into the full file. */
export function currentLine(mode: 'diff' | 'code' | 'preview' | 'merge'): number {
  if (mode === 'preview') return 1

  const target = mode === 'diff'
    ? diffEditor?.getModifiedEditor()
    : mode === 'merge' ? mergeResultEditor : codeEditor
  return target?.getPosition()?.lineNumber ?? 1
}

export type ViewState = {
  code: monaco.editor.ICodeEditorViewState | null
  diff: monaco.editor.IDiffEditorViewState | null
  merge: monaco.editor.ICodeEditorViewState | null
}

export function saveViewState(): ViewState {
  return {
    code: codeEditor?.saveViewState() ?? null,
    diff: diffEditor?.saveViewState() ?? null,
    merge: mergeResultEditor?.saveViewState() ?? null,
  }
}

export function restoreViewState(state: ViewState | undefined): void {
  if (!state) return
  if (state.code) codeEditor?.restoreViewState(state.code)
  if (state.diff) diffEditor?.restoreViewState(state.diff)
  if (state.merge) mergeResultEditor?.restoreViewState(state.merge)
}

export function setSideBySide(sideBySide: boolean): void {
  diffEditor?.updateOptions({ renderSideBySide: sideBySide })
}

export function focusEditor(mode: 'diff' | 'code'): void {
  if (mode === 'diff') diffEditor?.getModifiedEditor().focus()
  else (mergeHost.style.display !== 'none' ? mergeResultEditor : codeEditor)?.focus()
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
