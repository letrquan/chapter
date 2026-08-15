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
  type ViewState,
} from './editor'
import './styles.css'

import { call, on, isHosted } from './bridge'
import { brandMark, icons, kindLetter } from './icons'
import { registerCSharpNavigation, setNavigateHandler } from './navigation'
import { openPalette, close as closePalette, isOpen as isPaletteOpen } from './palette'
import { renderPreview, cancelPreview } from './preview'
import type { ChangedFile, DiffScope, RepoInfo, Worktree, WorktreeChanges } from './protocol'

/* ==========================================================================
   State

   Tabs, the active file and view state are held per worktree. That is the whole
   point of the app: switching worktrees must not disturb what you had open.
   ========================================================================== */

type Mode = 'diff' | 'code' | 'preview'

/** Preview only applies where there is something to render. */
const isPreviewable = (path: string): boolean => /\.(md|markdown|mdx)$/i.test(path)

interface TabState {
  path: string
  mode: Mode
  viewState?: ViewState
}

interface WorktreeState {
  tabs: TabState[]
  activePath: string | null
  changes?: WorktreeChanges
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
        <div class="rail-head">
          ${brandMark(18)}
          <span class="brand-name">Chapter</span>
          <button class="icon-btn" id="theme-toggle" title="Toggle theme"></button>
        </div>
        <div class="rail-body" id="rail-body"></div>
        <div class="rail-foot">
          <button class="btn" id="add-repo">${icons.plus}<span>Add repository</span></button>
        </div>
      </aside>

      <section class="files card">
        <div class="files-head">
          <span class="eyebrow">Changed</span>
          <span class="files-count" id="files-count"></span>
          <span class="files-stat" id="files-stat"></span>
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
        <div class="editor-host" id="editor-host">
          <div class="markdown-preview" id="preview-host" hidden></div>
          <div class="placeholder" id="editor-empty">${EMPTY_STATE_HTML}</div>
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

        const badge =
          count === null
            ? ''
            : `<span class="wt-badge ${count === 0 ? 'zero' : ''}">${count}</span>`

        const title = worktree.isPrunable
          ? `Unavailable — ${esc(worktree.prunableReason ?? 'worktree directory is missing')}`
          : esc(worktree.path)

        return `
          <button class="wt ${isActive ? 'active' : ''} ${worktree.isUsable ? '' : 'unusable'}"
                  data-worktree="${esc(worktree.path)}"
                  title="${title}">
            <span class="wt-icon">${worktree.isUsable ? icons.branch : icons.warning}</span>
            <span class="wt-label">${esc(worktree.displayName)}</span>
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
          <span class="repo-remove" data-remove-repo="${esc(repo.path)}" title="Close repository">${icons.close}</span>
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
function renderFiles(restoreSaved = false): void {
  const list = document.getElementById('files-list')!
  const currentScroll = list.scrollTop
  const count = document.getElementById('files-count')!
  const stat = document.getElementById('files-stat')!
  const base = document.getElementById('files-base')!

  if (!state.active) {
    list.innerHTML = ''
    count.textContent = ''
    stat.innerHTML = ''
    base.textContent = ''
    return
  }

  const entry = worktreeState(state.active)

  if (entry.loading && !entry.changes) {
    list.innerHTML = Array.from({ length: 7 }, () => '<div class="skeleton"></div>').join('')
    count.textContent = ''
    stat.innerHTML = ''
    base.textContent = 'Loading…'
    return
  }

  if (entry.error) {
    list.innerHTML = `<div class="files-empty">${esc(entry.error)}</div>`
    count.textContent = ''
    stat.innerHTML = ''
    base.textContent = ''
    return
  }

  const changes = entry.changes
  if (!changes) return

  count.textContent = String(changes.files.length)
  base.textContent = changes.base.description
  base.title = `${changes.base.description} · ${changes.base.sha.slice(0, 10)}`
  stat.innerHTML =
    changes.files.length === 0
      ? ''
      : `<span class="stat-add">+${changes.totalAdded}</span><span class="stat-del">−${changes.totalRemoved}</span>`

  if (changes.files.length === 0) {
    list.innerHTML = `<div class="files-empty">No changes against<br />${esc(changes.base.description)}.</div>`
    return
  }

  list.innerHTML = changes.files.map((file) => fileRow(file, entry.activePath === file.path)).join('')
  list.scrollTop = restoreSaved ? entry.filesScroll : currentScroll
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

  // Start the symbol index in the background. The diff view is usable immediately;
  // navigation lights up when this finishes.
  void call('ensureIndex', { worktreePath: path }).catch(() => {})

  const entry = worktreeState(path)

  // Cached worktrees repaint immediately; only a first visit shows the skeleton.
  if (entry.changes) {
    renderFiles(true)
    renderTabs()
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
  const targets = orderedWorktrees().filter((w) => !state.byWorktree.get(w.path)?.changes)

  for (const worktree of targets) {
    if (scope !== state.scope) return
    if (worktree.path === state.active) continue

    try {
      const changes = await call('getChanges', { worktreePath: worktree.path, scope })
      if (scope !== state.scope) return

      worktreeState(worktree.path).changes = changes
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
  await refreshChanges(worktreePath)

  if (worktreePath !== state.active) return

  const entry = worktreeState(worktreePath)
  const tab = entry.tabs.find((t) => t.path === entry.activePath)
  if (!tab) return

  tab.viewState = saveViewState()
  await loadTabContent(worktreePath, tab)
}

async function openFile(path: string, mode?: Mode): Promise<void> {
  if (!state.active) return

  const worktreePath = state.active
  const entry = worktreeState(worktreePath)

  captureViewState()

  let tab = entry.tabs.find((t) => t.path === path)
  if (!tab) {
    tab = { path, mode: mode ?? 'diff' }
    entry.tabs.push(tab)
  } else if (mode) {
    tab.mode = mode
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

    showPreview(false)

    if (tab.mode === 'diff') {
      const diff = await call('getDiff', { worktreePath, path: tab.path, scope: state.scope })
      if (!current()) return

      if (diff.isBinary) {
        // Must not return early leaving the previous file's diff on screen: openFile has
        // already repainted the tabs, file list and breadcrumbs for *this* path, so the
        // window would assert that some other file's content belongs to this one.
        showNotice('Binary file', `${tab.path} has no text diff to show.`)
        return
      }

      showDiff({
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

      showCode(worktreePath, tab.path, file.text, file.language)
    }

    showEmptyState(false)
    showMode(tab.mode)
    restoreViewState(tab.viewState)
  } catch (error) {
    if (!current()) return
    showNotice('Could not open file', message(error))
    toast('Could not open file', message(error), 'error')
  }
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
    empty.innerHTML = EMPTY_STATE_HTML
    showPreview(false)
  }
  empty.style.display = visible ? 'grid' : 'none'
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

/**
 * Replaces the editor with an explanation. Used wherever content cannot be shown — a
 * binary file, a failed read — so the pane never keeps displaying the previous file while
 * every other part of the window names the new one.
 */
function showNotice(title: string, detail: string): void {
  const empty = document.getElementById('editor-empty')
  if (!empty) return

  empty.innerHTML = `
    <div class="placeholder-title">${esc(title)}</div>
    <div class="placeholder-hint">${esc(detail)}</div>`
  empty.style.display = 'grid'
}

/** The keyboard legend doubles as the app's only documentation of itself. */
const SHORTCUTS: [keys: string, what: string][] = [
  ['<kbd>Ctrl</kbd> <kbd>1</kbd>–<kbd>9</kbd>', 'Switch worktree'],
  ['<kbd>Ctrl</kbd> <kbd>P</kbd>', 'Find file'],
  ['<kbd>Ctrl</kbd> <kbd>T</kbd>', 'Find symbol'],
  ['<kbd>Ctrl</kbd> <kbd>D</kbd>', 'Diff or code'],
  ['<kbd>Ctrl</kbd> <kbd>R</kbd>', 'Refresh'],
]

const EMPTY_STATE_HTML = `
  ${brandMark(44)}
  <div class="placeholder-title">Nothing open</div>
  <div class="placeholder-hint">Pick a changed file to see its diff.</div>
  <div class="shortcuts">
    ${SHORTCUTS.map(
      ([keys, what]) => `<span class="keys">${keys}</span><span class="what">${what}</span>`,
    ).join('')}
  </div>`

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

  if (!state.active) return

  await refreshChanges(state.active)

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
  })

  document.getElementById('rail-body')!.addEventListener('click', (event) => {
    const target = event.target as HTMLElement

    const remove = target.closest<HTMLElement>('[data-remove-repo]')
    if (remove) {
      event.stopPropagation()
      void removeRepo(remove.dataset.removeRepo!)
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

async function removeRepo(repoPath: string): Promise<void> {
  await call('removeRepo', { repoPath })

  for (const worktree of state.worktrees.get(repoPath) ?? []) {
    disposeWorktreeModels(worktree.path)
    state.byWorktree.delete(worktree.path)
    if (state.active === worktree.path) state.active = null
  }

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

function wireKeyboard(): void {
  window.addEventListener('keydown', (event) => {
    const ctrl = event.ctrlKey || event.metaKey

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
      const entry = state.active ? worktreeState(state.active) : null
      const tab = entry?.tabs.find((t) => t.path === entry.activePath)
      void setMode(tab?.mode === 'diff' ? 'code' : 'diff')
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
  // view, or the rendered document for Markdown. Opening the same file from the changed
  // list still starts in diff, which is the right default when reviewing.
  const fresh: Mode = isPreviewable(path) ? 'preview' : 'code'
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

  await loadRepos()

  renderFiles()
  renderTabs()
  focusEditor('diff')

  // Deliberately not awaited: badges fill in behind the already-usable UI.
  void prefetchBadges()
}

function fatal(title: string, detail: string): void {
  document.getElementById('root')!.innerHTML = `
    <div class="placeholder">
      <div class="placeholder-title">${esc(title)}</div>
      <div class="placeholder-error">${esc(detail)}</div>
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
