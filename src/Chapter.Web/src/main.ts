import {
  initEditors,
  showDiff,
  showCode,
  showMode,
  setTheme,
  setSideBySide,
  saveViewState,
  restoreViewState,
  revealPosition,
  currentLine,
  focusEditor,
  disposeWorktreeModels,
  onDiffSelectionChanged,
  onDirtyChanged,
  isDirty,
  currentText,
  markSaved,
  setSaveHandler,
  setModeToggleHandler,
  type ViewState,
} from './editor'
import './styles.css'

import { call, on, isHosted } from './bridge'
import * as updates from './update'
import {
  initCommitPanel,
  refreshCommitPanel,
  resetCommitPanel,
  clearCommitSelection,
  forgetDraft,
  writeMessage,
} from './commit'
import { isConfirmOpen } from './confirm'
import { close as closeHelp, isOpen as isHelpOpen, toggle as toggleHelp } from './help'
import { initHunkBar, showHunkBar, hideHunkBar, stepHunk, updateSelectionState } from './hunks'
import { icons, kindLetter } from './icons'
import { registerCSharpNavigation, setNavigateHandler } from './navigation'
import { openPalette, close as closePalette, isOpen as isPaletteOpen } from './palette'
import { renderPreview, cancelPreview } from './preview'
import { openRefs, close as closeRefs, isOpen as isRefsOpen } from './refs'
import type {
  ChangedFile,
  DiffBase,
  DiffScope,
  DiffSide,
  RepoInfo,
  UndoPayload,
  Worktree,
  WorktreeChanges,
} from './protocol'

/* ==========================================================================
   State

   Tabs, the active file and view state are held per worktree. That is the whole
   point of the app: switching worktrees must not disturb what you had open.
   ========================================================================== */

type Mode = 'diff' | 'code' | 'preview' | 'image'

/** Preview only applies where there is something to render. */
const isPreviewable = (path: string): boolean => /\.(md|markdown|mdx)$/i.test(path)

/**
 * Images Chapter renders itself, rather than reporting as binary.
 *
 * Mirrors the backend's InlineableImages list — anything matched here must be inlined
 * there too, or opening the file would promise a picture and deliver a refusal.
 */
const isImage = (path: string): boolean =>
  /\.(png|jpe?g|gif|webp|avif|bmp|ico|svg)$/i.test(path)

interface TabState {
  path: string
  mode: Mode
  /**
   * Which comparison this tab shows. `combined` follows the scope, which is what every
   * review view wants; the commit view opens a file on one side of the index, because
   * "what am I about to commit" and "what am I leaving behind" are different diffs of the
   * same file.
   */
  side: DiffSide
  viewState?: ViewState
}

interface WorktreeState {
  tabs: TabState[]
  activePath: string | null
  changes?: WorktreeChanges
  /**
   * Something changed in this worktree since its scan was taken.
   *
   * Kept as a flag rather than by discarding `changes`, so the rail can go on showing the
   * last known count while the scan itself is deferred until somebody actually looks. A
   * slightly stale badge is worth far more than the git process it would cost to keep
   * exact — see the note in the watcher handler.
   */
  stale?: boolean
  filesScroll: number
  loading: boolean
  error?: string
}

const state = {
  repos: [] as RepoInfo[],
  worktrees: new Map<string, Worktree[]>(),
  collapsed: new Set<string>(),
  active: null as string | null,
  byWorktree: new Map<string, WorktreeState>(),
  theme: 'dark' as 'dark' | 'light',
  sideBySide: true,

  // Applies to every worktree at once. Switching scope is a question you ask about the
  // whole session — "what has nobody committed yet" — not about one branch.
  scope: 'branch' as DiffScope,
}

const SCOPES: { id: DiffScope; label: string; title: string }[] = [
  { id: 'branch', label: 'All', title: 'Everything on this branch, committed or not' },
  { id: 'uncommitted', label: 'Uncommitted', title: 'Only what is not committed yet, including new files' },
  { id: 'committed', label: 'Committed', title: 'Only what has been committed on this branch' },
  // Kept short so "Uncommitted" — the label that matters most — is not the one truncated.
  { id: 'lastCommit', label: 'Last', title: 'Only the most recent commit' },
]

/**
 * Guards against out-of-order responses.
 *
 * Every backend call arrives independently, so a slow reply for a file or worktree the
 * user has already left will resolve after a faster one for the current selection. Without
 * a generation check it paints over the current view, leaving the editor showing one file
 * while the tabs, file list and breadcrumbs all name another — with no error to explain it.
 */
let switchGeneration = 0
let contentGeneration = 0

function worktreeState(path: string): WorktreeState {
  let entry = state.byWorktree.get(path)
  if (!entry) {
    entry = { tabs: [], activePath: null, filesScroll: 0, loading: false }
    state.byWorktree.set(path, entry)
  }
  return entry
}

/** Every usable worktree across all repos, in rail order — the Ctrl+1..9 targets. */
function orderedWorktrees(): Worktree[] {
  const list: Worktree[] = []
  for (const repo of state.repos) {
    if (state.collapsed.has(repo.path)) continue
    for (const worktree of state.worktrees.get(repo.path) ?? []) {
      if (worktree.isUsable) list.push(worktree)
    }
  }
  return list
}

/* ==========================================================================
   Utilities
   ========================================================================== */

const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
}

const esc = (value: string): string => value.replace(/[&<>"']/g, (c) => ESCAPES[c]!)

function splitPath(path: string): { dir: string; name: string } {
  const slash = path.lastIndexOf('/')
  return slash < 0
    ? { dir: '', name: path }
    : { dir: path.slice(0, slash), name: path.slice(slash + 1) }
}

/**
 * Splits a ref at its last slash.
 *
 * Agent worktrees are named `book-agent/<run>/<task-uuid>`, and a single truncating
 * label renders a rail full of them as identical rows reading `book-agent/e7d74e…` —
 * the ellipsis removes precisely the part that tells them apart. The prefix is shared
 * by every sibling, so it identifies nothing and is the half that should give up its
 * width first.
 */
function splitRef(name: string): { prefix: string; leaf: string } {
  const cut = name.lastIndexOf('/')
  return cut === -1
    ? { prefix: '', leaf: name }
    : { prefix: name.slice(0, cut + 1), leaf: name.slice(cut + 1) }
}

/** Truncates a directory from the left, since the tail is the informative half. */
function shortenDir(dir: string, max = 34): string {
  return dir.length <= max ? dir : '…' + dir.slice(dir.length - max + 1)
}

function toast(message: string, detail?: string, kind: 'info' | 'error' = 'info'): void {
  const host = document.getElementById('toasts')!
  const el = document.createElement('div')
  el.className = `toast ${kind}`
  el.innerHTML = `<div>${esc(message)}</div>${detail ? `<div class="toast-detail">${esc(detail)}</div>` : ''}`
  host.appendChild(el)
  setTimeout(() => el.remove(), kind === 'error' ? 7000 : 3500)
}

/* ==========================================================================
   Shell markup
   ========================================================================== */

/**
 * The window is three cards on a shell: worktrees, changed files, editor. Tabs belong
 * to the editor card rather than spanning the window, because that is what they are
 * scoped to — closing one only ever affects the pane below them.
 */
function renderShell(): void {
  document.getElementById('root')!.innerHTML = `
    <div class="app">
      <aside class="rail card" id="rail">
        <div class="rail-body" id="rail-body"></div>
        <div class="rail-foot">
          <button class="btn" id="add-repo">${icons.plus}<span>Add repository</span></button>
          <button class="icon-btn update-ready" id="update" hidden>${icons.download}</button>
          <button class="icon-btn" id="help" title="Keyboard shortcuts (?)">${icons.help}</button>
          <button class="icon-btn" id="theme-toggle" title="Toggle theme"></button>
        </div>
      </aside>

      <section class="files card">
        <div class="files-head">
          <span class="eyebrow">Changes</span>
          <span class="files-count" id="files-count"></span>
          <button class="icon-btn" id="refs"
                  title="Branches, worktrees, stashes and tags (Ctrl+B)">${icons.branch}</button>
          <button class="icon-btn" id="undo" title="Nothing to undo" disabled>${icons.undo}</button>
          <button class="icon-btn" id="refresh" title="Refresh (Ctrl+R)">${icons.refresh}</button>
        </div>
        <div class="segmented scope-switch" id="scope-switch">
          ${SCOPES.map(
            (scope) => `
              <button data-scope="${scope.id}" title="${scope.title}"
                      class="${scope.id === 'branch' ? 'on' : ''}">${scope.label}</button>`,
          ).join('')}
        </div>
        <div class="files-base" id="files-base"></div>
        <div class="files-list" id="files-list"></div>
        <div class="commit-panel" id="commit-panel" hidden></div>
      </section>

      <section class="editor card">
        <div class="tabstrip" id="tabstrip"></div>
        <div class="editor-head">
          <div class="crumbs" id="crumbs"></div>
          <div class="editor-actions">
            <div class="segmented" id="mode-switch">
              <button data-mode="diff" class="on">Diff</button>
              <button data-mode="code">Code</button>
              <button data-mode="preview" id="mode-preview" hidden>Preview</button>
            </div>
            <span class="toolbar-sep"></span>
            <button class="icon-btn" id="split-toggle" title="Toggle inline / side-by-side">${icons.diff}</button>
            <button class="icon-btn" id="open-external" title="Open in external editor">${icons.external}</button>
          </div>
        </div>
        <div class="hunk-bar" id="hunk-bar" hidden></div>
        <div class="editor-host" id="editor-host">
          <div class="markdown-preview" id="preview-host" hidden></div>
          <div class="image-view" id="image-host" hidden></div>
          <!-- Filled by showEmptyState once there is a worktree to describe; the shell is
               built before anything has been read. -->
          <div class="placeholder" id="editor-empty">${restingState()}</div>
        </div>
      </section>

      <div class="splitter" id="split-rail"></div>
      <div class="splitter" id="split-files"></div>
    </div>
  `
}

/* ==========================================================================
   Rail
   ========================================================================== */

function renderRail(): void {
  const body = document.getElementById('rail-body')!

  if (state.repos.length === 0) {
    body.innerHTML = `
      <div class="files-empty" style="padding-top:28px">
        No repositories yet.<br />Add one to see its worktrees.
      </div>`
    return
  }

  let shortcut = 0
  const groups = state.repos.map((repo) => {
    const worktrees = state.worktrees.get(repo.path) ?? []
    const collapsed = state.collapsed.has(repo.path)

    const rows = worktrees
      .map((worktree) => {
        const entry = state.byWorktree.get(worktree.path)
        const count = entry?.changes?.files.length ?? null
        const isActive = state.active === worktree.path
        const key = !collapsed && worktree.isUsable && shortcut < 9 ? ++shortcut : null

        // A stale badge is a count from a moment ago, not a wrong one — the scan behind
        // it is deferred until somebody opens the worktree. Marking it says so rather
        // than pretending the number is live.
        const stale = entry?.stale === true
        const badge =
          count === null
            ? ''
            : `<span class="wt-badge ${count === 0 ? 'zero' : ''} ${stale ? 'stale' : ''}"
                     ${stale ? 'title="changed since this count was taken"' : ''}>${count}</span>`

        const title = worktree.isPrunable
          ? `Unavailable — ${esc(worktree.prunableReason ?? 'worktree directory is missing')}`
          : `${esc(worktree.displayName)}\n${esc(worktree.path)}`

        // Whole name or leaf alone — never a truncated prefix. Letting the prefix ellipsis
        // itself produced `f…model-and-provi…`, which is two ellipses and less legible
        // than either half on its own. The rail's label column is about 22 characters at
        // this width, so a name that fits is shown entire and a name that does not gives
        // up the part every sibling shares. The full name is on the title either way.
        const { prefix, leaf } = splitRef(worktree.displayName)
        const whole = prefix !== '' && worktree.displayName.length <= 22

        return `
          <button class="wt ${isActive ? 'active' : ''} ${worktree.isUsable ? '' : 'unusable'}"
                  data-worktree="${esc(worktree.path)}"
                  title="${title}">
            <span class="wt-icon">${worktree.isUsable ? icons.branch : icons.warning}</span>
            <span class="wt-label">${
              whole ? `<span class="wt-prefix">${esc(prefix)}</span>` : ''
            }<span class="wt-leaf">${esc(leaf)}</span></span>
            ${badge}
            ${key ? `<span class="wt-key">${key}</span>` : ''}
          </button>`
      })
      .join('')

    return `
      <div class="repo-group ${collapsed ? 'collapsed' : ''}" data-repo="${esc(repo.path)}">
        <button class="repo-head" data-toggle-repo="${esc(repo.path)}">
          <span class="chevron">${icons.chevron}</span>
          <span class="repo-name">${esc(repo.name)}</span>
          <span class="repo-action" data-add-worktree="${esc(repo.path)}"
                title="Manage this repository’s worktrees">${icons.plus}</span>
          <span class="repo-action" data-remove-repo="${esc(repo.path)}" title="Close repository">${icons.close}</span>
        </button>
        <div class="repo-worktrees">${rows}</div>
      </div>`
  })

  body.innerHTML = groups.join('')
}

/* ==========================================================================
   Changed-files panel
   ========================================================================== */

/**
 * @param restoreSaved
 *   True only when arriving at a worktree, where the saved offset is the right one.
 *   Every other re-render — clicking a file, a watcher notification — must keep the
 *   position the list is already at, or the row under the cursor jumps away.
 */
/**
 * The Uncommitted scope is where committing happens, so it shows the commit view instead
 * of the flat list.
 *
 * A fifth scope button was the other option and it is the wrong shape: the switch answers
 * "which slice of the work", and staged-versus-unstaged is not another slice — it is the
 * same slice divided by the index. Putting it here also puts the question ("what has
 * nobody committed yet") and the action in the same place.
 */
function isCommitScope(): boolean {
  return state.scope === 'uncommitted'
}

function updatePanelMode(): void {
  const list = document.getElementById('files-list')
  const panel = document.getElementById('commit-panel')
  if (!list || !panel) return

  const commit = isCommitScope()
  list.hidden = commit
  panel.hidden = !commit
}

function renderFiles(restoreSaved = false): void {
  updatePanelMode()

  const list = document.getElementById('files-list')!
  const currentScroll = list.scrollTop
  const count = document.getElementById('files-count')!
  const base = document.getElementById('files-base')!

  // `bare` hides the description, and it is set from the *previous* scan. Cleared up front
  // rather than per branch: left on, it renders "Loading…" into a span with display:none —
  // invisible on exactly the transition it exists for, since arriving from the Uncommitted
  // or Last scope is what set it.
  base.classList.remove('bare')

  if (!state.active) {
    list.innerHTML = ''
    count.textContent = ''
    base.innerHTML = ''
    return
  }

  const entry = worktreeState(state.active)

  if (entry.loading && !entry.changes) {
    list.innerHTML = Array.from({ length: 7 }, () => '<div class="skeleton"></div>').join('')
    count.textContent = ''
    base.innerHTML = '<span class="files-base-what">Loading…</span>'
    refreshRestingState()
    return
  }

  if (entry.error) {
    list.innerHTML = `<div class="files-empty">${esc(entry.error)}</div>`
    count.textContent = ''
    base.innerHTML = ''
    // Without this the pane on the right keeps saying "Reading what changed…" forever:
    // `restingSummary` has a branch for the error and this is the only render that
    // reaches it.
    refreshRestingState()
    return
  }

  const changes = entry.changes
  if (!changes) return

  // The count reads as part of the title — "Changes 12" — rather than as a caps label
  // with a bare integer after it, and it says nothing at all when there is nothing to
  // count. A zero next to a heading is a heading that looks broken.
  count.textContent = changes.files.length === 0 ? '' : String(changes.files.length)

  const label = baseLabel(changes.base)

  base.innerHTML = `
    <span class="files-base-what" title="${esc(changes.base.description)} · ${esc(
      changes.base.sha.slice(0, 10),
    )}">${esc(label)}</span>
    ${
      changes.files.length === 0
        ? ''
        : `<span class="files-stat"><span class="stat-add">+${changes.totalAdded}</span><span class="stat-del">−${changes.totalRemoved}</span></span>`
    }`
  base.classList.toggle('bare', label === '')

  if (changes.files.length === 0) {
    list.innerHTML = `<div class="files-empty">${esc(emptyChangesMessage(changes.base))}</div>`
    refreshRestingState()
    return
  }

  list.innerHTML = changes.files.map((file) => fileRow(file, entry.activePath === file.path)).join('')
  list.scrollTop = restoreSaved ? entry.filesScroll : currentScroll

  // The pane on the right summarises the same scan while nothing is open in it.
  refreshRestingState()
}

/**
 * The short form for the line under the scope switch.
 *
 * Empty for two of the four scopes, because the switch directly above it is already
 * showing the word: `base.description` for the uncommitted scope is the string
 * "uncommitted changes", which under a button labelled *Uncommitted* is a caption
 * repeating its own picture.
 */
function baseLabel(base: DiffBase): string {
  switch (base.scope) {
    case 'uncommitted':
    case 'lastCommit':
      return ''
    case 'committed':
      return base.branchName ? `since ${base.branchName}` : base.description
    default:
      return base.description
  }
}

/**
 * The same fact as a phrase, for the resting pane, which has room for a sentence.
 *
 * Written out per scope rather than dropped into "against ___" for the same reason
 * `emptyChangesMessage` is: only one of the four descriptions survives it. The others
 * produce "against uncommitted changes", which names the thing as its own baseline.
 */
function baseSentence(base: DiffBase): string {
  switch (base.scope) {
    case 'uncommitted':
      return 'in the working tree, staged and unstaged'
    case 'committed':
      return base.branchName ? `committed since ${base.branchName}` : 'committed since the base'
    case 'lastCommit':
      return 'in the most recent commit'
    default:
      return `against ${base.description}`
  }
}

/**
 * What an empty file list says.
 *
 * `base.description` is a label — it sits under the count as "merge-base with main" — and
 * only one of its four forms survives being dropped into "No changes against ___.". The
 * others produced "No changes against committed since main." and "No changes against
 * uncommitted changes.", which are not sentences. Written out per scope instead, the same
 * way the refs panel writes out its own empty lists.
 */
function emptyChangesMessage(base: DiffBase): string {
  const branch = base.branchName

  switch (base.scope) {
    case 'uncommitted':
      return 'Nothing uncommitted — the working tree is clean.'
    case 'committed':
      return branch ? `Nothing committed since ${branch}.` : 'Nothing committed since the base.'
    case 'lastCommit':
      return 'The last commit changed nothing.'
    default:
      return `No changes against ${base.description}.`
  }
}

function fileRow(file: ChangedFile, isActive: boolean): string {
  const { dir, name } = splitPath(file.path)
  const letter = kindLetter(file.kind)
  const kindClass = `k-${file.kind.toLowerCase()}`

  const delta = file.isBinary
    ? '<span class="file-delta">bin</span>'
    : `<span class="file-delta">${file.linesAdded ? `<span class="up">+${file.linesAdded}</span>` : ''}${
        file.linesAdded && file.linesRemoved ? ' ' : ''
      }${file.linesRemoved ? `<span class="down">−${file.linesRemoved}</span>` : ''}</span>`

  const renameNote = file.oldPath ? ` (was ${esc(file.oldPath)})` : ''

  // Only meaningful when the view mixes committed and uncommitted work; in the
  // uncommitted-only view every row would carry it, which says nothing.
  const showDirty = state.scope === 'branch' && file.isUncommitted
  const dirtyTitle = showDirty ? ' · not committed yet' : ''

  return `
    <button class="file-row ${isActive ? 'active' : ''}"
            data-file="${esc(file.path)}"
            title="${esc(file.path)}${renameNote}${dirtyTitle}">
      <span class="file-kind ${kindClass}">${letter}</span>
      <span class="file-dirty ${showDirty ? '' : 'hidden'}"></span>
      <span class="file-name">${esc(name)}</span>
      <span class="file-dir">${esc(shortenDir(dir))}</span>
      ${delta}
    </button>`
}

/* ==========================================================================
   Tabs and editor header
   ========================================================================== */

function renderTabs(): void {
  const strip = document.getElementById('tabstrip')!

  if (!state.active) {
    strip.innerHTML = '<div class="tabstrip-spacer"></div>'
    strip.hidden = true
    return
  }

  const entry = worktreeState(state.active)
  const changes = entry.changes

  // An empty strip is a bar of nothing above a pane of nothing. Collapsing it lets the
  // editor card start at its toolbar, the way it does before anything is opened.
  strip.hidden = entry.tabs.length === 0

  const tabs = entry.tabs
    .map((tab) => {
      const { name } = splitPath(tab.path)
      const file = changes?.files.find((f) => f.path === tab.path)
      const dot = file
        ? `<span class="tab-dot" style="background:var(--${dotColour(file.kind)})"></span>`
        : ''

      return `
        <div class="tab ${entry.activePath === tab.path ? 'active' : ''}" data-tab="${esc(tab.path)}">
          ${dot}
          <span class="tab-name" title="${esc(tab.path)}">${esc(name)}</span>
          <span class="tab-close" data-close="${esc(tab.path)}">${icons.close}</span>
        </div>`
    })
    .join('')

  strip.innerHTML = tabs + '<div class="tabstrip-spacer"></div>'

  // The markers live on the elements this just replaced, so they have to be reapplied.
  // Without it, closing one tab silently clears every other tab's unsaved dot until
  // something happens to change a dirty flag.
  renderDirtyMarkers()
}

function dotColour(kind: string): string {
  switch (kind) {
    case 'added':
    case 'untracked':
      return 'add'
    case 'deleted':
      return 'del'
    case 'modified':
      return 'mod'
    default:
      return 'rename'
  }
}

function renderCrumbs(): void {
  const crumbs = document.getElementById('crumbs')!
  const entry = state.active ? worktreeState(state.active) : null
  const path = entry?.activePath

  if (!path) {
    crumbs.innerHTML = ''
    return
  }

  const { dir, name } = splitPath(path)
  crumbs.innerHTML = dir
    ? `<span class="path">${esc(dir)}</span><span class="crumb-sep">/</span><span class="crumb-file">${esc(name)}</span>`
    : `<span class="crumb-file">${esc(name)}</span>`
}

function renderModeSwitch(): void {
  const entry = state.active ? worktreeState(state.active) : null
  const tab = entry?.tabs.find((t) => t.path === entry.activePath)
  const mode = tab?.mode ?? 'diff'

  // An image has nothing to switch between: git offers no text diff and the code view
  // cannot decode it. Hiding the control beats presenting buttons that can only refuse.
  const segmented = document.getElementById('mode-switch')
  if (segmented) segmented.hidden = tab !== undefined && isImage(tab.path)

  for (const button of document.querySelectorAll<HTMLElement>('#mode-switch button')) {
    button.classList.toggle('on', button.dataset.mode === mode)
  }

  // Preview is offered only where there is something to render, rather than
  // sitting there disabled on every C# file.
  const preview = document.getElementById('mode-preview')
  if (preview) preview.hidden = !(tab && isPreviewable(tab.path))
}

/* ==========================================================================
   Actions
   ========================================================================== */

async function loadRepos(): Promise<void> {
  try {
    state.repos = await call('listRepos')
  } catch (error) {
    // A failure here must still leave a usable window with an "Add repository" button,
    // not an empty shell with no explanation.
    state.repos = []
    toast('Could not load repositories', message(error), 'error')
  }

  renderRail()

  await Promise.all(state.repos.map((repo) => loadWorktrees(repo.path)))

  if (!state.active) {
    const first = orderedWorktrees()[0]
    if (first) await selectWorktree(first.path)
  }
}

const message = (error: unknown): string =>
  error instanceof Error ? error.message : String(error)

async function loadWorktrees(repoPath: string): Promise<void> {
  try {
    state.worktrees.set(repoPath, await call('getWorktrees', { repoPath }))
    renderRail()
  } catch (error) {
    toast('Could not read worktrees', String(error), 'error')
  }
}

async function addRepo(): Promise<void> {
  const picked = await call('pickFolder')
  if (!picked) return

  try {
    const repo = await call('addRepo', { repoPath: picked })
    if (!repo) {
      toast('Not a git repository', picked, 'error')
      return
    }
    if (!state.repos.some((r) => r.path === repo.path)) state.repos.push(repo)

    renderRail()
    await loadWorktrees(repo.path)

    if (!state.active) {
      const first = orderedWorktrees()[0]
      if (first) await selectWorktree(first.path)
    }
  } catch (error) {
    toast('Could not open repository', String(error), 'error')
  }
}

async function selectWorktree(path: string): Promise<void> {
  if (state.active === path) return
  noteInteraction()

  // Whatever the palette is showing was computed against the worktree being left, so
  // acting on it after the switch would open the wrong copy of a file.
  closePalette()

  // Persist what the outgoing worktree looked like, so coming back is exact.
  captureViewState()
  if (state.active) {
    worktreeState(state.active).filesScroll = document.getElementById('files-list')!.scrollTop
  }

  const generation = ++switchGeneration
  state.active = path
  renderRail()

  // Cleared before the new worktree's read lands, so the panel never shows one worktree's
  // staged files under another's name.
  resetCommitPanel()
  clearStaleBanner()
  hideHunkBar()
  void refreshUndo()
  if (isCommitScope()) void refreshCommitPanel()

  // Start the symbol index in the background. The diff view is usable immediately;
  // navigation lights up when this finishes.
  void call('ensureIndex', { worktreePath: path }).catch(() => {})

  const entry = worktreeState(path)

  // Cached worktrees repaint immediately; only a first visit shows the skeleton.
  //
  // A stale entry still repaints from the cache first — the counts are a moment old, not
  // wrong — and then re-reads behind it. Waiting for the scan before painting would put
  // the deferred cost straight back into the switch it was moved out of.
  if (entry.changes) {
    renderFiles(true)
    renderTabs()

    if (entry.stale) {
      void refreshChanges(path).then(() => {
        if (state.active === path) void reloadActiveTab()
      })
    }

    await restoreActiveTab()
    return
  }

  entry.loading = true
  renderFiles(true)
  renderTabs()

  await refreshChanges(path)

  // A faster second switch may have landed while this one was waiting on git.
  if (generation !== switchGeneration) return

  // Landing on a worktree with nothing shown wastes the trip: open the first change so
  // the review starts immediately.
  if (entry.tabs.length > 0) {
    await restoreActiveTab()
    return
  }

  // Re-read rather than reusing the local: refreshChanges populated it after the
  // early-return above already narrowed `entry.changes` to undefined.
  const loaded = worktreeState(path).changes
  const first = loaded?.files.find((file) => !file.isBinary && file.kind !== 'deleted')

  if (first) await openFile(first.path)
  else clearEditor()
}

/**
 * How quiet the app has to be before background scans resume.
 *
 * A fixed gap between scans was the first attempt and it made things worse: spacing
 * fourteen git scans out does not remove the contention, it stretches it over more of the
 * time the user is clicking. Measured, that turned a 118-151ms click into 174-246ms.
 *
 * Badges are background information and a click is not, so the prefetch yields to the
 * user outright — it does no work at all while anything is being clicked, and picks up
 * once the window has been still for this long.
 */
const PREFETCH_IDLE_MS = 400

/** When the user last did something the app had to respond to. */
let lastInteraction = 0

function noteInteraction(): void {
  lastInteraction = performance.now()
}

/** Resolves once the user has stopped clicking for {@link PREFETCH_IDLE_MS}. */
async function waitForQuiet(): Promise<void> {
  for (;;) {
    const since = performance.now() - lastInteraction
    if (since >= PREFETCH_IDLE_MS) return
    await new Promise((resolve) => setTimeout(resolve, PREFETCH_IDLE_MS - since))
  }
}

/**
 * Fills in every worktree's change count in the background.
 *
 * Without this the rail only shows a badge once you have visited a worktree, which
 * defeats the point — the counts are how you tell at a glance which agent did what.
 */
async function prefetchBadges(): Promise<void> {
  // The scope this run belongs to. Changing scope clears every cached change set and
  // starts a fresh prefetch, so a run that outlives its scope must stop writing — its
  // results would otherwise be cached as if they belonged to the new scope, and
  // selectWorktree trusts that cache without refetching.
  const scope = state.scope
  const targets = orderedWorktrees().filter((w) => {
    const entry = state.byWorktree.get(w.path)
    return !entry?.changes || entry.stale
  })

  for (const worktree of targets) {
    if (scope !== state.scope) return
    if (worktree.path === state.active) continue

    // Stand aside while the user is working. Each of these is several git processes over
    // a whole tree; fourteen of them back to back is what made a click during the
    // prefetch three times slower than the same click a second later.
    await waitForQuiet()

    // Re-checked after the wait, not just before it: the user may have switched to this
    // worktree while the prefetch was standing aside, and it is refreshed by the switch.
    if (scope !== state.scope) return
    if (worktree.path === state.active) continue

    try {
      const changes = await call('getChanges', { worktreePath: worktree.path, scope })
      if (scope !== state.scope) return

      const entry = worktreeState(worktree.path)
      entry.changes = changes
      entry.stale = false
      renderRail()
    } catch {
      // A worktree we cannot read simply keeps no badge; the rail still works.
    }
  }
}

async function refreshChanges(worktreePath: string): Promise<void> {
  const entry = worktreeState(worktreePath)

  try {
    entry.changes = await call('getChanges', { worktreePath, scope: state.scope })
    entry.error = undefined
    entry.stale = false
  } catch (error) {
    entry.error = String(error instanceof Error ? error.message : error)
  } finally {
    entry.loading = false
  }

  if (state.active === worktreePath) {
    renderFiles()
    renderTabs()
  }
  renderRail()
}

/**
 * Handles a watcher notification.
 *
 * Repainting the file list is not enough: if the agent just edited the file being read,
 * the diff on screen is now stale — which is precisely the case this app exists for. So
 * the open tab is reloaded too, with its scroll position preserved so the view does not
 * jump out from under whoever is reading it.
 */
async function onFilesChanged(worktreePath: string): Promise<void> {
  // A worktree nobody is looking at is marked stale, not re-read.
  //
  // This used to re-scan unconditionally, and the cost is not theoretical: every open
  // repository an agent happens to be writing in bought a full git scan — several
  // processes over the whole tree, plus a read of every untracked file — per watcher
  // batch, forever, for a file list that is not on screen. Measured on this machine while
  // the window sat idle: one repository alone fired ten of them in forty seconds.
  //
  // The scan is deferred to the moment somebody switches to it, which is the only moment
  // its result can be seen.
  if (worktreePath !== state.active) {
    const other = worktreeState(worktreePath)
    if (!other.stale) {
      other.stale = true
      renderRail()
    }
    return
  }

  scheduleActiveRefresh(worktreePath)
}

/**
 * Coalesces refreshes of the worktree on screen.
 *
 * The active worktree genuinely does have to re-read — showing an agent's change as it
 * lands is the entire point of the app — but an agent mid-edit produces bursts, and each
 * notification costs a git scan plus a reload of the open diff. Without coalescing, a run
 * of writes queues that work several deep and every click the user makes waits behind it.
 *
 * Trailing edge, so a burst settles into exactly one refresh once the writing pauses.
 */
let activeRefreshTimer: ReturnType<typeof setTimeout> | undefined

const ACTIVE_REFRESH_DEBOUNCE_MS = 250

function scheduleActiveRefresh(worktreePath: string): void {
  clearTimeout(activeRefreshTimer)
  activeRefreshTimer = setTimeout(() => {
    void runActiveRefresh(worktreePath)
  }, ACTIVE_REFRESH_DEBOUNCE_MS)
}

async function runActiveRefresh(worktreePath: string): Promise<void> {
  // The user may have moved on during the debounce, in which case this worktree is now
  // one of the ones that only needs marking.
  if (worktreePath !== state.active) {
    worktreeState(worktreePath).stale = true
    return
  }

  await refreshChanges(worktreePath)
  if (worktreePath !== state.active) return

  if (isCommitScope()) await refreshCommitPanel()
  if (worktreePath !== state.active) return

  void refreshUndo()

  const entry = worktreeState(worktreePath)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)
  if (!tab) return

  tab.viewState = saveViewState()
  await loadTabContent(worktreePath, tab)
}

async function openFile(path: string, mode?: Mode, side: DiffSide = 'combined'): Promise<void> {
  if (!state.active) return
  noteInteraction()

  const worktreePath = state.active
  const entry = worktreeState(worktreePath)

  captureViewState()

  let tab = entry.tabs.find((t) => t.path === path)
  if (!tab) {
    // Reviewing starts in the diff; an image has no text diff to show and would land on
    // a refusal instead of its picture, so it opens as what it is.
    tab = { path, mode: mode ?? (isImage(path) ? 'image' : 'diff'), side }
    entry.tabs.push(tab)
  } else {
    if (mode) tab.mode = mode

    // The same file on the other side of the index is the same tab showing a different
    // comparison, not a second tab: two rows in the tab strip with identical names and no
    // way to tell them apart would be worse than switching in place.
    tab.side = side
  }

  entry.activePath = path

  renderTabs()
  renderFiles()
  renderCrumbs()
  renderModeSwitch()

  await loadTabContent(worktreePath, tab)
}

async function loadTabContent(worktreePath: string, tab: TabState): Promise<void> {
  const generation = ++contentGeneration

  /** Whether this load still represents what the user is looking at. */
  const current = (): boolean =>
    generation === contentGeneration &&
    worktreePath === state.active &&
    worktreeState(worktreePath).activePath === tab.path

  try {
    if (tab.mode === 'preview') {
      const file = await call('getFileContent', { worktreePath, path: tab.path, scope: state.scope })
      if (!current()) return

      showPreview(true)
      showImage(false)
      showEmptyState(false)
      showMode('preview')

      await renderPreview(document.getElementById('preview-host')!, {
        worktreePath,
        path: tab.path,
        source: file.text,
        onNavigate: (target) => void navigateTo(worktreePath, target, 1, 1),
      })
      return
    }

    if (tab.mode === 'image') {
      const asset = await call('getAsset', { worktreePath, path: tab.path, scope: state.scope })
      if (!current()) return

      // The asset is inlined as a data URI by the backend: the page cannot touch disk,
      // and a committed-scope review must see the picture as that revision has it.
      const host = document.getElementById('image-host')!

      if (!asset.dataUri) {
        host.hidden = true
        showNotice('Cannot show image', `${tab.path} — ${asset.reason ?? 'could not be read'}.`)
        return
      }

      showPreview(false)
      host.innerHTML = `<img src="${esc(asset.dataUri)}" alt="${esc(tab.path)}" draggable="false">`
      host.hidden = false

      showEmptyState(false)
      showMode(tab.mode)
      clearStaleBanner()

      // The hunk bar belongs to a split diff of the *previous* tab; this file has no
      // hunks, and leaving the bar up would stage against a file no longer on screen.
      void syncHunkBar()
      return
    }

    showPreview(false)
    showImage(false)

    let fresh = true

    if (tab.mode === 'diff') {
      const diff = await call('getDiff', {
        worktreePath,
        path: tab.path,
        scope: state.scope,
        side: tab.side,
      })
      if (!current()) return

      if (diff.isBinary) {
        // Must not return early leaving the previous file's diff on screen: openFile has
        // already repainted the tabs, file list and breadcrumbs for *this* path, so the
        // window would assert that some other file's content belongs to this one.
        showNotice('Binary file', `${tab.path} has no text diff to show.`)
        return
      }

      fresh = showDiff({
        worktreePath,
        path: tab.path,
        baseText: diff.baseText,
        workingText: diff.workingText,
        language: diff.language,
      })
    } else {
      const file = await call('getFileContent', { worktreePath, path: tab.path, scope: state.scope })
      if (!current()) return

      if (file.isBinary) {
        showNotice('Binary file', `${tab.path} cannot be displayed as text.`)
        return
      }

      // The backend decides what may be written back: never a file read at a commit, never
      // a binary, and never one whose encoding or line endings would not survive the round
      // trip. The editor only obeys.
      fresh = showCode(worktreePath, tab.path, file.text, file.language, file.isEditable)
    }

    showEmptyState(false)
    showMode(tab.mode)

    // A model with unsaved edits is left as it is, so restoring a view state captured
    // against different text would put the caret somewhere arbitrary.
    if (fresh) {
      clearStaleBanner()
      restoreViewState(tab.viewState)
    } else {
      showStaleBanner(tab.path)
    }

    renderDirtyMarkers()
    void syncHunkBar()
  } catch (error) {
    if (!current()) return
    showNotice('Could not open file', message(error))
    toast('Could not open file', message(error), 'error')
  }
}

/**
 * Everything that has to happen after the app changes the repository.
 *
 * One function rather than a list at each call site: staging renumbers hunks, moves the
 * file between the two groups, changes both sides of its diff, and gives undo something
 * new to offer. Forgetting any one of those leaves part of the window describing a state
 * that no longer exists, and which part is forgotten depends on which caller was written
 * last.
 */
async function afterMutation(): Promise<void> {
  if (state.active) await refreshChanges(state.active)

  await refreshCommitPanel()
  await reloadActiveTab()

  void refreshUndo()
  void syncHunkBar()
}

/**
 * Shows the hunk bar for the open file when its hunks can be staged, and hides it
 * otherwise — a diff of committed work has nothing to stage.
 */
async function syncHunkBar(): Promise<void> {
  if (!state.active || !isCommitScope()) {
    hideHunkBar()
    return
  }

  const entry = worktreeState(state.active)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)

  if (!tab || tab.mode !== 'diff' || tab.side === 'combined') {
    hideHunkBar()
    return
  }

  await showHunkBar({ worktreePath: state.active, path: tab.path, side: tab.side })
}

/**
 * Opens the refs overlay on the active worktree.
 *
 * Every operation in it is scoped to a worktree — which branch this one is on, which stash
 * to restore into it, which repository's worktrees to manage — so there is nothing to show
 * without one.
 */
function showRefs(section?: 'branches' | 'worktrees' | 'stashes' | 'tags'): void {
  if (!state.active) {
    toast('Open a worktree first.')
    return
  }

  // Captured rather than read later, for the reason the palette captures it: the panel's
  // contents were read against this worktree, and acting on them after the user has moved
  // on would switch a branch in a worktree they are no longer looking at.
  const worktree = state.active

  openRefs(
    worktree,
    {
      // A branch mutation changes the file list, the commit view and what undo offers — the
      // same four things staging changes, so it ends in the same place.
      onMutated: () => afterMutation(),
      onGoToWorktree: (path) => selectWorktree(path),
      onWorktreeGone: (path) => worktreeGone(path),
      toast,
    },
    section,
  ).catch((error: unknown) => {
    // Not `void`: this is opened from a click handler, so a rejection has nowhere to go and
    // the button would simply appear dead — which is exactly how it failed once.
    toast('Could not read this worktree’s refs', message(error), 'error')
  })
}

/**
 * Lets go of a worktree that no longer exists — removed, pruned, or moved elsewhere.
 *
 * Everything the window holds is keyed by path: editor models, the commit draft, the tab
 * list, the change cache, the active selection. None of it survives the directory going, and
 * none of it notices on its own — the first sign would be a bridge call failing against a
 * path nothing on screen admits is dead.
 */
async function worktreeGone(path: string): Promise<void> {
  const wasActive = state.active === path

  disposeWorktreeModels(path)
  forgetDraft(path)
  state.byWorktree.delete(path)

  const repo = [...state.worktrees.entries()].find(([, list]) =>
    list.some((worktree) => worktree.path === path),
  )?.[0]

  const before = new Set((repo ? state.worktrees.get(repo) : [])?.map((w) => w.path) ?? [])

  if (wasActive) {
    // Cleared before anything is re-read, so no panel is left describing the worktree that
    // has gone while the window moves to another one.
    state.active = null
    resetCommitPanel()
    clearStaleBanner()
    hideHunkBar()
  }

  if (repo) await loadWorktrees(repo)

  if (!wasActive) {
    renderRail()
    return
  }

  // Everything below is scoped to the repository the worktree belonged to. Both questions —
  // "did one appear?" and "where do we go now?" — are answered wrongly by looking at the
  // whole rail: every worktree of every *other* repository is also one that was not in
  // `before`, so the answer would be whichever project happens to be listed first.
  const siblings = repo ? state.worktrees.get(repo) ?? [] : []
  const here = orderedWorktrees().filter((worktree) =>
    siblings.some((sibling) => sibling.path === worktree.path),
  )

  // A move leaves exactly one worktree in this repository that was not there before, and
  // that is where the user's work went. Worked out by comparing the lists rather than taken
  // from what they typed into the move box: git resolves that string — against the main
  // worktree, and into the platform's separators — so the typed form frequently is not the
  // path that now exists, and following it would land nowhere.
  const arrived = here.find((worktree) => !before.has(worktree.path))

  const next = arrived ?? here[0] ?? orderedWorktrees()[0]

  if (next) {
    await selectWorktree(next.path)
    return
  }

  renderRail()
  renderFiles()
  renderTabs()
  clearEditor()
}

/**
 * Re-reads whatever tab is open. Called after a mutation, where the diff on screen is now
 * describing a state the repository has left — staging a file changes both sides of it.
 */
async function reloadActiveTab(): Promise<void> {
  if (!state.active) return

  const entry = worktreeState(state.active)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)
  if (!tab) return

  tab.viewState = saveViewState()
  await loadTabContent(state.active, tab)
}

async function restoreActiveTab(): Promise<void> {
  const entry = worktreeState(state.active!)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)

  if (!tab) {
    clearEditor()
    return
  }

  renderCrumbs()
  renderModeSwitch()
  await loadTabContent(state.active!, tab)
}

function clearEditor(): void {
  renderCrumbs()
  renderModeSwitch()
  showEmptyState(true)
}

function showEmptyState(visible: boolean): void {
  const empty = document.getElementById('editor-empty')
  if (!empty) return

  if (visible) {
    empty.innerHTML = restingState()
    showPreview(false)
    showImage(false)
  }
  empty.style.display = visible ? 'grid' : 'none'
}

/**
 * Repaints the resting summary when the scan behind it changes.
 *
 * Guarded on what is actually on screen rather than on a flag: the same element also
 * carries notices ("Binary file", "Could not open file"), and a watcher notification
 * arriving while one is up must not replace somebody's explanation with a summary of a
 * worktree they can already see.
 */
function refreshRestingState(): void {
  const empty = document.getElementById('editor-empty')
  if (!empty || empty.style.display === 'none') return
  if (!empty.querySelector('[data-resting]')) return

  empty.innerHTML = restingState()
}

function showPreview(visible: boolean): void {
  const host = document.getElementById('preview-host')
  if (!host) return

  host.hidden = !visible

  // Abandon any in-flight image resolution or syntax colouring; its DOM is gone.
  if (!visible) {
    cancelPreview()
    host.innerHTML = ''
  }
}

/** The image view is rebuilt on every load, so hiding it also discards the last picture. */
function showImage(visible: boolean): void {
  const host = document.getElementById('image-host')
  if (!host) return

  host.hidden = !visible
  if (!visible) host.innerHTML = ''
}

/**
 * Replaces the editor with an explanation. Used wherever content cannot be shown — a
 * binary file, a failed read — so the pane never keeps displaying the previous file while
 * every other part of the window names the new one.
 */
function showNotice(title: string, detail: string): void {
  const empty = document.getElementById('editor-empty')
  if (!empty) return

  empty.innerHTML = `
    <div class="placeholder-body">
      <div class="placeholder-title">${esc(title)}</div>
      <div class="placeholder-hint">${esc(detail)}</div>
    </div>`
  empty.style.display = 'grid'
}

/**
 * What the editor pane shows when nothing is open in it.
 *
 * This is the app's resting face, and it was the mark at 44px over "Nothing open" over a
 * ten-row keyboard legend, all centred in the largest surface in the window. None of that
 * is about the work: it is the app introducing itself, permanently, to somebody who has
 * been using it all afternoon. The legend now lives behind F1 — see `help.ts`.
 *
 * What it says instead is which worktree you are standing in, since that is the question
 * this app exists to answer and the rail can only show a truncated name of it. Every
 * field comes from the scan already in hand — no call is made to fill this in, because a
 * summary that costs a git process is a summary that arrives after you have stopped
 * looking at it.
 */
function restingState(): string {
  return `<div class="placeholder-body" data-resting>${restingSummary()}</div>`
}

function restingSummary(): string {
  if (!state.active) {
    return `
      <div class="placeholder-title">No worktree open</div>
      <div class="placeholder-hint">${
        state.repos.length === 0
          ? 'Add a repository to see the worktrees its agents are working in.'
          : 'Pick one from the rail on the left.'
      }</div>`
  }

  const entry = worktreeState(state.active)
  const changes = entry.changes
  const worktree = changes?.worktree ?? findWorktree(state.active)
  const repo = repoOf(state.active)

  // The branch, or the name the rail shows when HEAD is detached and there is no branch
  // to name.
  const { prefix, leaf } = splitRef(worktree?.branch ?? worktree?.displayName ?? state.active)

  const meta = [
    repo ? esc(repo.name) : null,
    worktree ? `<span class="sha">${esc(worktree.shortHead)}</span>` : null,
    worktree?.isDetached ? 'detached HEAD' : null,
    worktree?.isMain ? 'main worktree' : null,
  ].filter((part): part is string => part !== null)

  // Ordered by how much they change what you can do next, and only one is ever shown:
  // stacking amber panels in an empty pane is how a warning stops being read.
  const flag = !worktree
    ? null
    : worktree.isPrunable
      ? `This worktree’s directory is gone — ${esc(worktree.prunableReason ?? 'git still has a record of it')}. Ctrl+Shift+B to prune it.`
      : worktree.isLocked
        ? `Locked${worktree.lockReason ? ` — ${esc(worktree.lockReason)}` : ''}. It cannot be moved or pruned until it is unlocked.`
        : null

  const body =
    entry.error !== undefined
      ? `<div class="rest-hint">${esc(entry.error)}</div>`
      : !changes
        ? `<div class="rest-hint">Reading what changed…</div>`
        : // Both cases end on the same "against …" line rather than the empty one
          // borrowing `emptyChangesMessage`, which the file list three inches to the left
          // is already showing word for word.
          `<div class="rest-stats">
             ${
               changes.files.length === 0
                 ? '<span>Nothing changed</span>'
                 : `<span>${changes.files.length} file${
                     changes.files.length === 1 ? '' : 's'
                   } changed</span>
                    <span class="stat-add">+${changes.totalAdded}</span>
                    <span class="stat-del">−${changes.totalRemoved}</span>`
             }
           </div>
           <div class="rest-base">${esc(baseSentence(changes.base))}</div>
           ${
             changes.files.length === 0
               ? ''
               : '<div class="rest-hint">Pick one from the list to see its diff.</div>'
           }`

  return `
    ${prefix ? `<div class="rest-prefix">${esc(prefix)}</div>` : ''}
    <div class="rest-branch">${esc(leaf)}</div>
    ${
      meta.length === 0
        ? ''
        : `<div class="rest-meta">${meta.join('<span class="rest-sep">·</span>')}</div>`
    }
    ${body}
    ${flag ? `<div class="rest-flag">${icons.warning}<span>${flag}</span></div>` : ''}`
}

function findWorktree(path: string): Worktree | null {
  for (const list of state.worktrees.values()) {
    const found = list.find((worktree) => worktree.path === path)
    if (found) return found
  }
  return null
}

function repoOf(worktreePath: string): RepoInfo | null {
  for (const repo of state.repos) {
    if ((state.worktrees.get(repo.path) ?? []).some((w) => w.path === worktreePath)) return repo
  }
  return null
}

/* ==========================================================================
   Saving, and the edits the app must not throw away
   ========================================================================== */

/**
 * Writes the open file back.
 *
 * The text comes from the model rather than from anything the app cached: the model is
 * what the user has been typing into, and it is the only copy of their edit.
 */
async function saveActiveFile(): Promise<void> {
  if (!state.active) return

  const worktreePath = state.active
  const entry = worktreeState(worktreePath)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)
  if (!tab || tab.mode !== 'code') return

  if (!isDirty(worktreePath, tab.path)) return

  const text = currentText(worktreePath, tab.path)
  if (text === undefined) return

  try {
    const result = await call('saveFile', { worktreePath, path: tab.path, text })

    if (!result.ok) {
      toast('Could not save', result.error ?? undefined, 'error')
      return
    }

    // Cleared only on a confirmed write, and only if the buffer still holds exactly what
    // was written. Keystrokes landing while the bridge call was in flight are not saved,
    // and clearing the flag over them hands the next watcher notification permission to
    // overwrite them — silently, with no undo, which is the one thing the dirty flag
    // exists to prevent.
    if (currentText(worktreePath, tab.path) === text) {
      markSaved(worktreePath, tab.path)
      toast(`Saved ${splitPath(tab.path).name}`)
    } else {
      toast(`Saved ${splitPath(tab.path).name}`, 'You have kept typing since — save again to include it.')
    }
  } catch (error) {
    toast('Could not save', message(error), 'error')
  }
}

/**
 * Says so when the file changed on disk while it was being edited here.
 *
 * The alternative — repainting over the edit — is what the app used to do, and it was
 * correct only while nothing was editable. An agent writing to any file in the worktree
 * triggers the same refresh.
 */
function showStaleBanner(path: string): void {
  const host = document.getElementById('editor-host')
  if (!host || host.querySelector('.stale-banner')) return

  const banner = document.createElement('div')
  banner.className = 'stale-banner'
  banner.innerHTML = `
    <span><strong>${esc(splitPath(path).name)}</strong> changed on disk. Your unsaved edits are
    still here — saving overwrites the newer version.</span>
    <button class="btn small" data-stale="reload">Discard mine and reload</button>
    <button class="icon-btn" data-stale="dismiss" title="Keep editing">${icons.close}</button>`

  host.appendChild(banner)
}

function clearStaleBanner(): void {
  document.querySelector('.stale-banner')?.remove()
}

/** A dot on the tab, the way every editor marks unsaved work. */
function renderDirtyMarkers(): void {
  if (!state.active) return

  const worktreePath = state.active
  for (const element of document.querySelectorAll<HTMLElement>('[data-tab]')) {
    const path = element.dataset.tab!
    element.classList.toggle('dirty', isDirty(worktreePath, path))
  }
}

/* ==========================================================================
   Undo
   ========================================================================== */

/**
 * Labels the undo button with what it would actually do.
 *
 * "Undo" alone is not enough here: the button reverses a git operation, and the difference
 * between undoing a commit and undoing nothing at all has to be visible before it is
 * pressed rather than after.
 */
async function refreshUndo(): Promise<void> {
  const button = document.getElementById('undo') as HTMLButtonElement | null
  if (!button) return

  if (!state.active) {
    button.disabled = true
    button.title = 'Nothing to undo'
    return
  }

  let undo: UndoPayload | null = null
  try {
    undo = await call('getUndo', { worktreePath: state.active })
  } catch {
    // The reflog read can fail in a repository with no commits. No undo offered, no noise.
  }

  const label = undo?.label ?? null
  button.disabled = label === null
  button.title = label ? `Undo ${label} (Ctrl+Alt+Z)` : 'Nothing to undo'
  button.classList.toggle('danger', undo?.isDestructive === true)
}

async function undoLast(): Promise<void> {
  if (!state.active) return

  const worktreePath = state.active

  let undo: UndoPayload
  try {
    undo = await call('getUndo', { worktreePath })
  } catch (error) {
    toast('Could not read the undo history', message(error), 'error')
    return
  }

  if (!undo.label) {
    toast('Nothing to undo', 'No mutation has been made in this worktree yet.')
    return
  }

  const { confirm } = await import('./confirm')
  const ok = await confirm({
    title: `Undo ${undo.label}?`,
    body: undo.warning ?? 'The repository goes back to where it was before that operation.',
    confirmLabel: 'Undo',
    recovery: undo.isDestructive ? 'permanent' : 'undoable',
  })

  if (!ok) return

  try {
    const result = await call('undo', { worktreePath })
    if (!result.ok) toast('Could not undo', result.message, 'error')
    else toast(result.message)
  } catch (error) {
    toast('Could not undo', message(error), 'error')
  }

  await refreshUndo()
}

function captureViewState(): void {
  if (!state.active) return
  const entry = worktreeState(state.active)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)
  if (tab) tab.viewState = saveViewState()
}

function closeTab(path: string): void {
  if (!state.active) return
  const entry = worktreeState(state.active)

  const index = entry.tabs.findIndex((t) => t.path === path)
  if (index < 0) return

  entry.tabs.splice(index, 1)

  if (entry.activePath === path) {
    const next = entry.tabs[Math.min(index, entry.tabs.length - 1)]
    entry.activePath = next?.path ?? null

    // Closing the last tab has to clear the editor too, or the file you just closed stays
    // on screen above an empty tab strip.
    if (next) void loadTabContent(state.active, next)
    else clearEditor()
  }

  renderTabs()
  renderFiles()
  renderCrumbs()
}

async function setMode(mode: Mode): Promise<void> {
  if (!state.active) return
  const entry = worktreeState(state.active)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)
  if (!tab || tab.mode === mode) return

  // Carry the caret across the switch so "jump to code" lands where you were reading.
  const line = currentLine(tab.mode)

  captureViewState()
  tab.mode = mode
  renderModeSwitch()

  await loadTabContent(state.active, tab)
  revealPosition(mode, line)
}

/**
 * Switches which slice of the work is shown, across every worktree at once.
 *
 * Every cached change set is discarded rather than filtered: the scopes are different git
 * comparisons, not subsets of one another, so keeping stale results would show the wrong
 * line counts even where the file lists happen to overlap.
 */
async function setScope(scope: DiffScope): Promise<void> {
  if (scope === state.scope) return

  state.scope = scope

  for (const button of document.querySelectorAll<HTMLElement>('#scope-switch button')) {
    button.classList.toggle('on', button.dataset.scope === scope)
  }

  for (const entry of state.byWorktree.values()) entry.changes = undefined
  renderRail()
  updatePanelMode()

  if (!state.active) return

  // Leaving the commit view drops its selection, or a later return would highlight a row
  // against a file list rebuilt from a different comparison.
  if (!isCommitScope()) {
    clearCommitSelection()
    hideHunkBar()

    // The side has to go back with the view. A tab opened from the commit view carries
    // `staged` or `unstaged`, and GetDiffAsync short-circuits on a named side and ignores
    // the scope entirely — so the pane would go on showing HEAD-to-index while the switch
    // above it claimed to be showing something else.
    for (const entry of state.byWorktree.values()) {
      for (const tab of entry.tabs) tab.side = 'combined'
    }
  }

  await refreshChanges(state.active)
  if (isCommitScope()) await refreshCommitPanel()

  // The open file may not be in the new scope at all — an uncommitted-only view will not
  // contain a file whose changes are all committed. Say so rather than showing an
  // identical-looking diff of nothing.
  const entry = worktreeState(state.active)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)

  if (tab) {
    tab.viewState = saveViewState()
    await loadTabContent(state.active, tab)
  }

  void prefetchBadges()
}

function applyTheme(theme: 'dark' | 'light'): void {
  state.theme = theme
  document.documentElement.dataset.theme = theme
  setTheme(theme)

  const button = document.getElementById('theme-toggle')
  if (button) button.innerHTML = theme === 'dark' ? icons.sun : icons.moon
}

/* ==========================================================================
   Wiring
   ========================================================================== */

function wireEvents(): void {
  document.getElementById('add-repo')!.addEventListener('click', () => void addRepo())

  document.getElementById('help')!.addEventListener('click', () => toggleHelp())

  document.getElementById('theme-toggle')!.addEventListener('click', () => {
    const next = state.theme === 'dark' ? 'light' : 'dark'
    applyTheme(next)

    // Sent here rather than from applyTheme, which startup also calls — with the
    // *resolved* theme, so persisting there would quietly turn a "system" preference
    // into a fixed one on the first launch. The host both stores it and repaints the
    // native caption, which is the one surface the page cannot draw itself.
    void call('setTheme', { theme: next }).catch(() => {})
  })

  document.getElementById('refresh')!.addEventListener('click', () => {
    if (state.active) void refreshChanges(state.active)
    if (isCommitScope()) void refreshCommitPanel()
  })

  document.getElementById('undo')!.addEventListener('click', () => void undoLast())

  document.getElementById('refs')!.addEventListener('click', () => showRefs('branches'))

  document.getElementById('editor-host')!.addEventListener('click', (event) => {
    const action = (event.target as HTMLElement).closest<HTMLElement>('[data-stale]')
    if (!action) return

    clearStaleBanner()
    if (action.dataset.stale !== 'reload' || !state.active) return

    // The user chose the version on disk. Marking it saved is what lets the reload
    // through — the model is only protected while it holds edits nobody has resolved.
    const entry = worktreeState(state.active)
    const tab = entry.tabs.find((t) => t.path === entry.activePath)
    if (!tab) return

    markSaved(state.active, tab.path)
    void loadTabContent(state.active, tab)
  })

  document.getElementById('rail-body')!.addEventListener('click', (event) => {
    const target = event.target as HTMLElement

    const remove = target.closest<HTMLElement>('[data-remove-repo]')
    if (remove) {
      event.stopPropagation()
      void removeRepo(remove.dataset.removeRepo!)
      return
    }

    const manage = target.closest<HTMLElement>('[data-add-worktree]')
    if (manage) {
      event.stopPropagation()
      void manageWorktrees(manage.dataset.addWorktree!)
      return
    }

    const toggle = target.closest<HTMLElement>('[data-toggle-repo]')
    if (toggle) {
      const path = toggle.dataset.toggleRepo!
      if (state.collapsed.has(path)) state.collapsed.delete(path)
      else state.collapsed.add(path)
      renderRail()
      return
    }

    const worktree = target.closest<HTMLElement>('[data-worktree]')
    if (worktree && !worktree.classList.contains('unusable')) {
      void selectWorktree(worktree.dataset.worktree!)
    }
  })

  document.getElementById('files-list')!.addEventListener('click', (event) => {
    const row = (event.target as HTMLElement).closest<HTMLElement>('[data-file]')
    if (row) void openFile(row.dataset.file!)
  })

  document.getElementById('tabstrip')!.addEventListener('click', (event) => {
    const target = event.target as HTMLElement

    const close = target.closest<HTMLElement>('[data-close]')
    if (close) {
      event.stopPropagation()
      closeTab(close.dataset.close!)
      return
    }

    const tab = target.closest<HTMLElement>('[data-tab]')
    if (tab) void openFile(tab.dataset.tab!)
  })

  // Middle-click closes a tab, as it does in every editor.
  document.getElementById('tabstrip')!.addEventListener('auxclick', (event) => {
    if ((event as MouseEvent).button !== 1) return
    const tab = (event.target as HTMLElement).closest<HTMLElement>('[data-tab]')
    if (tab) closeTab(tab.dataset.tab!)
  })

  document.getElementById('mode-switch')!.addEventListener('click', (event) => {
    const button = (event.target as HTMLElement).closest<HTMLElement>('[data-mode]')
    if (button) void setMode(button.dataset.mode as Mode)
  })

  document.getElementById('scope-switch')!.addEventListener('click', (event) => {
    const button = (event.target as HTMLElement).closest<HTMLElement>('[data-scope]')
    if (button) void setScope(button.dataset.scope as DiffScope)
  })

  document.getElementById('split-toggle')!.addEventListener('click', (event) => {
    state.sideBySide = !state.sideBySide
    setSideBySide(state.sideBySide)
    ;(event.currentTarget as HTMLElement).classList.toggle('on', !state.sideBySide)
  })

  document.getElementById('open-external')!.addEventListener('click', () => void openExternally())

  wireSplitter('split-rail', '--rail-w', 160, 420)
  wireSplitter('split-files', '--files-w', 200, 600)
  wireKeyboard()
}

/**
 * Opens the worktree section for one repository, from its header in the rail.
 *
 * The panel is scoped to a worktree throughout — it is where the branch list, the stash and
 * the repository all come from — so managing another repository's worktrees means being in
 * one of them first. Switching is done here, visibly, rather than by handing the panel a
 * path from a repository the rest of the window is not looking at: every action in it would
 * then apply somewhere other than where the user believes they are.
 */
async function manageWorktrees(repoPath: string): Promise<void> {
  const worktrees = state.worktrees.get(repoPath) ?? []
  const here = worktrees.some((worktree) => worktree.path === state.active)

  if (!here) {
    const target = worktrees.find((worktree) => worktree.isUsable)
    if (!target) {
      toast('No usable worktree in this repository', 'Open one before managing the rest.', 'error')
      return
    }

    await selectWorktree(target.path)
  }

  showRefs('worktrees')
}

async function removeRepo(repoPath: string): Promise<void> {
  await call('removeRepo', { repoPath })

  for (const worktree of state.worktrees.get(repoPath) ?? []) {
    disposeWorktreeModels(worktree.path)
    forgetDraft(worktree.path)
    state.byWorktree.delete(worktree.path)
    if (state.active === worktree.path) state.active = null
  }

  resetCommitPanel()

  state.worktrees.delete(repoPath)
  state.repos = state.repos.filter((r) => r.path !== repoPath)

  renderRail()

  const first = orderedWorktrees()[0]
  if (first) await selectWorktree(first.path)
  else {
    renderFiles()
    renderTabs()
    clearEditor()
  }
}

async function openExternally(): Promise<void> {
  if (!state.active) return
  const entry = worktreeState(state.active)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)
  if (!tab) return

  try {
    const opened = await call('openInEditor', {
      worktreePath: state.active,
      path: tab.path,
      line: currentLine(tab.mode),
      column: 1,
      editor: '',
    })
    if (!opened) toast('No external editor found', 'Set one in settings.json', 'error')
  } catch (error) {
    toast('Could not open editor', String(error), 'error')
  }
}

function wireSplitter(id: string, variable: string, min: number, max: number): void {
  const handle = document.getElementById(id)
  if (!handle) return

  handle.addEventListener('pointerdown', (event) => {
    event.preventDefault()
    handle.setPointerCapture(event.pointerId)
    handle.classList.add('dragging')
    document.body.classList.add('resizing')

    const startX = event.clientX
    const startWidth = parseInt(
      getComputedStyle(document.documentElement).getPropertyValue(variable),
      10,
    )

    const onMove = (move: PointerEvent): void => {
      const next = Math.min(max, Math.max(min, startWidth + move.clientX - startX))
      document.documentElement.style.setProperty(variable, `${next}px`)
    }

    const onUp = (): void => {
      handle.classList.remove('dragging')
      document.body.classList.remove('resizing')
      handle.removeEventListener('pointermove', onMove)
      handle.removeEventListener('pointerup', onUp)
    }

    handle.addEventListener('pointermove', onMove)
    handle.addEventListener('pointerup', onUp)
  })
}

/**
 * Flips the open tab between its diff and its code.
 *
 * Lives here rather than inline in the key handler because there are two ways in: the
 * window shortcut, and Monaco's own binding for when the caret is in the editor and the
 * window never sees the keystroke at all.
 */
function toggleMode(): void {
  const entry = state.active ? worktreeState(state.active) : null
  const tab = entry?.tabs.find((t) => t.path === entry.activePath)

  // An image has no diff or code view to flip to.
  if (!tab || isImage(tab.path)) return

  void setMode(tab?.mode === 'diff' ? 'code' : 'diff')
}

/** Whether the caret is in something that takes text — a field, a box, or Monaco. */
function isTyping(): boolean {
  const element = document.activeElement as HTMLElement | null
  if (!element) return false

  return (
    element.tagName === 'INPUT' || element.tagName === 'TEXTAREA' || element.isContentEditable
  )
}

function wireKeyboard(): void {
  window.addEventListener('keydown', (event) => {
    const ctrl = event.ctrlKey || event.metaKey

    // A modal question is on screen; it owns the keyboard until it is answered.
    if (isConfirmOpen()) return

    // The refs overlay handles its own keys on its filter field, including Escape and Tab.
    // Letting the shortcuts below run behind it would switch worktrees out from under a
    // panel whose rows were read against the one it was opened on.
    if (isRefsOpen()) {
      if (event.key === 'Escape') closeRefs()
      return
    }

    // ? opens the keyboard reference, and it has to be the primary binding rather than
    // the courtesy one: F1 never arrives. Chromium claims it for its own help before the
    // page sees a keydown, and WebView2 inherits that — the listener below simply does not
    // run. It is still accepted here for the host that one day does deliver it.
    //
    // ? is the only unmodified key the app binds, so it is the only one that can be taken
    // out of somebody's typing: without the guard, a question mark in a commit message
    // opens this panel instead of reaching the box. Monaco's caret is a textarea too, so
    // the same test covers the editor. Toggling, so the key that opened it closes it.
    if (event.key === 'F1' || (event.key === '?' && !ctrl && !event.altKey && !isTyping())) {
      event.preventDefault()
      toggleHelp()
      return
    }

    if (isHelpOpen()) {
      if (event.key === 'Escape') closeHelp()
      return
    }

    // Ctrl+S saves, from anywhere outside the editor. Monaco has its own binding for when
    // the caret is inside it, because it swallows the keystroke before this listener runs.
    if (ctrl && !event.shiftKey && event.key.toLowerCase() === 's') {
      event.preventDefault()
      void saveActiveFile()
      return
    }

    // Deliberately not Ctrl+Z: Monaco owns that, and it means "undo my typing". This undoes
    // a git operation, which is a different and much larger thing to be reversing.
    if (ctrl && event.altKey && event.key.toLowerCase() === 'z') {
      event.preventDefault()
      void undoLast()
      return
    }

    // Ctrl+G writes the commit message. Reaching for the mouse to fill in a text box rather
    // defeats the point of a keyboard-first app, and pressing it again stops the generation.
    if (ctrl && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'g') {
      event.preventDefault()
      writeMessage()
      return
    }

    // Alt+Up/Down walks git's hunks. Plain arrows belong to the editor, and Ctrl+Up/Down
    // is Monaco's own scroll, so the modifier that is left is the one used here.
    if (event.altKey && !ctrl && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
      event.preventDefault()
      stepHunk(event.key === 'ArrowDown' ? 1 : -1)
      return
    }

    // Ctrl+B opens branches, stashes and tags. B for branch, and it is the only unclaimed
    // letter that names the thing — Monaco takes Ctrl+K and Ctrl+L, and Ctrl+T is symbols.
    //
    // Shift lands on the worktree section of the same panel. A separate key would have been
    // the alternative and there is not one to spare, whereas the shifted form of the panel's
    // own key is where anybody would look for its other half.
    if (ctrl && !event.altKey && event.key.toLowerCase() === 'b') {
      event.preventDefault()
      showRefs(event.shiftKey ? 'worktrees' : 'branches')
      return
    }

    // Ctrl+1..9 jumps straight to a worktree — the app's primary motion.
    // altKey is excluded because AltGr reports as Ctrl+Alt on non-US layouts, where the
    // digit row carries characters people actually type.
    if (ctrl && !event.altKey && !event.shiftKey && event.code.startsWith('Digit')) {
      const index = Number(event.code.slice(5)) - 1
      const target = orderedWorktrees()[index]
      if (target) {
        event.preventDefault()
        void selectWorktree(target.path)
      }
      return
    }

    if (ctrl && event.key.toLowerCase() === 'r') {
      event.preventDefault()
      if (state.active) void refreshChanges(state.active)
      return
    }

    if (ctrl && event.key.toLowerCase() === 'w') {
      event.preventDefault()
      const entry = state.active ? worktreeState(state.active) : null
      if (entry?.activePath) closeTab(entry.activePath)
      return
    }

    if (ctrl && !event.shiftKey && event.key.toLowerCase() === 'd') {
      event.preventDefault()
      toggleMode()
      return
    }

    // Ctrl+Shift+V — the shortcut people already have in their fingers from VS Code.
    if (ctrl && event.shiftKey && event.key.toLowerCase() === 'v') {
      event.preventDefault()
      const entry = state.active ? worktreeState(state.active) : null
      const tab = entry?.tabs.find((t) => t.path === entry.activePath)
      if (tab && isPreviewable(tab.path)) void setMode(tab.mode === 'preview' ? 'diff' : 'preview')
      return
    }

    // Ctrl+Tab cycles worktrees rather than tabs: switching context is the common move.
    if (ctrl && event.key === 'Tab') {
      event.preventDefault()
      const all = orderedWorktrees()
      if (all.length === 0) return

      const index = all.findIndex((w) => w.path === state.active)
      const next = all[(index + (event.shiftKey ? -1 : 1) + all.length) % all.length]
      if (next) void selectWorktree(next.path)
      return
    }

    if (ctrl && (event.key === 'PageDown' || event.key === 'PageUp')) {
      event.preventDefault()
      cycleTab(event.key === 'PageDown' ? 1 : -1)
      return
    }

    // Ctrl+P files, Ctrl+T symbols — the two motions worth having a palette for.
    if (ctrl && (event.key.toLowerCase() === 'p' || event.key.toLowerCase() === 't')) {
      event.preventDefault()
      if (!state.active) return

      // Captured, not read at Enter-time: the results were computed against this
      // worktree, so opening them against whatever is selected later would land in a
      // different worktree's copy of the file — silently, when the path exists in both.
      const searched = state.active

      openPalette(event.key.toLowerCase() === 'p' ? 'files' : 'symbols', searched, (result) => {
        void navigateTo(searched, result.path, result.line, result.column)
      })
      return
    }

    if (event.key === 'Escape' && isPaletteOpen()) closePalette()
  })
}

/**
 * Opens a file and puts the caret on a position — the single path every navigation takes,
 * whether it came from F12, a peek result or the palette.
 */
async function navigateTo(
  worktreePath: string,
  path: string,
  line: number,
  column: number,
): Promise<void> {
  if (worktreePath !== state.active) await selectWorktree(worktreePath)

  const entry = worktreeState(worktreePath)
  const existing = entry.tabs.find((t) => t.path === path)

  // Navigating to a file is about reading it, not reviewing a change — so land in code
  // view, the rendered document for Markdown, or the picture itself for an image.
  // Opening the same file from the changed list still starts in diff, which is the right
  // default when reviewing.
  const fresh: Mode = isImage(path) ? 'image' : isPreviewable(path) ? 'preview' : 'code'
  await openFile(path, existing?.mode ?? fresh)

  const tab = entry.tabs.find((t) => t.path === path)
  revealPosition(tab?.mode ?? 'code', line, column)
}

function cycleTab(delta: number): void {
  if (!state.active) return
  const entry = worktreeState(state.active)
  if (entry.tabs.length === 0) return

  const index = entry.tabs.findIndex((t) => t.path === entry.activePath)
  const next = entry.tabs[(index + delta + entry.tabs.length) % entry.tabs.length]
  if (next) void openFile(next.path)
}

/* ==========================================================================
   Startup
   ========================================================================== */

async function start(): Promise<void> {
  renderShell()

  initEditors(document.getElementById('editor-host')!)

  registerCSharpNavigation()
  setNavigateHandler((worktreePath, path, line, column) => {
    void navigateTo(worktreePath, path, line, column)
  })

  setSaveHandler(() => void saveActiveFile())
  setModeToggleHandler(() => toggleMode())
  onDiffSelectionChanged(() => updateSelectionState())

  // The tab's dot has to track the model, not the load: a keystroke makes a file dirty
  // without anything else in the app being told.
  onDirtyChanged(() => renderDirtyMarkers())

  initCommitPanel(document.getElementById('commit-panel')!, {
    activeWorktree: () => state.active,
    // The commit view is about one comparison, but an image still has no diff to sit on.
    openFile: (path, side) => void openFile(path, isImage(path) ? 'image' : 'diff', side),
    onMutated: () => void afterMutation(),
    toast,
  })

  initHunkBar(document.getElementById('hunk-bar')!, {
    onMutated: () => void afterMutation(),
    toast,
  })

  const settings = await call('getSettings').catch(() => null)
  const preference = settings?.theme ?? 'system'
  const systemDark = window.matchMedia('(prefers-color-scheme: dark)').matches
  applyTheme(preference === 'system' ? (systemDark ? 'dark' : 'light') : preference)

  wireEvents()

  // The backend watches each worktree and pushes here when an agent edits a file.
  on('filesChanged', ({ worktreePath }) => {
    if (state.byWorktree.has(worktreePath)) void onFilesChanged(worktreePath)
  })

  on('worktreesChanged', ({ repoPath }) => void loadWorktrees(repoPath))

  wireUpdates()

  await loadRepos()

  renderFiles()
  renderTabs()
  focusEditor('diff')

  // Deliberately not awaited: badges fill in behind the already-usable UI.
  void prefetchBadges()
}

/**
 * The rail's update button: present only while a new build is actually waiting.
 *
 * A button that is there permanently and does nothing on most days is furniture; this one
 * appearing is the whole message. The detail — which version, and the option to check by
 * hand — lives in the help panel, so this needs to carry only "there is one, press to take
 * it", which a tooltip does.
 *
 * `initUpdates` is not awaited: it makes a bridge call, and the rail should not wait on the
 * network to finish drawing. The subscription is in place first, so a status arriving before
 * the call returns still paints.
 */
function wireUpdates(): void {
  const button = document.getElementById('update') as HTMLButtonElement

  updates.subscribe((status) => {
    button.hidden = status.state !== 'ready'
    button.title = `Chapter ${status.availableVersion ?? ''} is ready — restart to update`.replace('  ', ' ')
  })

  button.addEventListener('click', () => void updates.apply())

  void updates.initUpdates()
}

function fatal(title: string, detail: string): void {
  document.getElementById('root')!.innerHTML = `
    <div class="placeholder">
      <div class="placeholder-body">
        <div class="placeholder-title">${esc(title)}</div>
        <div class="placeholder-error">${esc(detail)}</div>
      </div>
    </div>`
}

if (!isHosted) {
  fatal('Chapter', 'This page has to run inside the Chapter desktop app.')
} else {
  // Anything escaping startup would otherwise leave a blank shell with no clue why.
  start().catch((error: unknown) => fatal('Chapter failed to start', message(error)))

  window.addEventListener('unhandledrejection', (event) => {
    toast('Unexpected error', message(event.reason), 'error')
  })
}
