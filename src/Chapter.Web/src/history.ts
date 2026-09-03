import { call, on } from './bridge'
import { confirm } from './confirm'
import { icons } from './icons'
import type {
  CommitDetail,
  CommitLogEntry,
  CommitLogPage,
  HistorySearchKind,
  MutationPayload,
  RebaseAction,
  RebasePlan,
  RebaseTodoEntry,
} from './protocol'

/**
 * A small, paginated history surface for the active worktree.
 *
 * History is deliberately an overlay rather than another scope in the changed-files card:
 * the scope answers "what changed", while this answers "when and why did it change". The
 * two can be open without making the file list forget which diff it was showing.
 */

const PAGE_SIZE = 50

const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
}

const esc = (value: string): string => value.replace(/[&<>"']/g, (c) => ESCAPES[c]!)

let overlay: HTMLElement | null = null
let panel: HTMLElement
let list: HTMLElement
let detail: HTMLElement
let footer: HTMLElement
let plannerHost: HTMLElement
let title: HTMLElement
let subtitle: HTMLElement
let search: HTMLElement
let searchInput: HTMLInputElement
let searchModes: HTMLElement
let searchClear: HTMLButtonElement
let hint: HTMLElement
let selected = 0
let entries: CommitLogEntry[] = []
let hasMore = false
let anchor = ''
let loading = false
let refreshPending = false
let worktreePath: string | null = null
/** When set, the overlay is scoped to one path instead of the whole worktree. */
let historyPath: string | null = null
let historyBranch: string | null = null
let requestGeneration = 0
let detailGeneration = 0
let refreshTimer: ReturnType<typeof setTimeout> | undefined
let onToast: (message: string, detail?: string, kind?: 'info' | 'error') => void = () => {}
let onOpenCommitFile: (sha: string, parentIndex: number, path: string) => void = () => {}
let onMutateCommit: (
  kind: 'cherryPick' | 'revert',
  sha: string,
  parentIndex: number,
  subject: string,
) => MutationPayload | null | Promise<MutationPayload | null> = () => null
let inspectedParentIndex = 0
let searchKind: HistorySearchKind = 'message'
let searchQuery = ''
let mutationPending = false
let inspectedDetail: CommitDetail | null = null
let actionStatus: { kind: 'success' | 'error'; text: string } | null = null
let rebasePlan: RebasePlan | null = null
let rebaseRows: RebaseTodoEntry[] = []
let rebaseBusy = false
let plannerGeneration = 0
let plannerDetailVisible = false

const SEARCH_LABELS: Record<HistorySearchKind, string> = {
  message: 'message',
  author: 'author',
  path: 'path',
  content: 'content',
}

const SEARCH_PLACEHOLDERS: Record<HistorySearchKind, string> = {
  message: 'Search subject and body…',
  author: 'Search author name or email…',
  path: 'Search changed paths…',
  content: 'Find commits that add or remove exact text…',
}

const GRAPH_ROW_HEIGHT = 50
const GRAPH_LANE_WIDTH = 16
const GRAPH_GUTTER = 8

interface GraphRow {
  before: string[]
  after: string[]
  lane: number
  parentLanes: number[]
}

/**
 * Assigns a lane to each commit in git's newest-first order.
 *
 * The state is a list of parent ids which are still expected below the current row. A
 * merge replaces its lane with the first parent and fans the other parents out beside it;
 * when a parent is already represented by another lane, the duplicate is collapsed. This
 * is intentionally based on full object ids rather than abbreviated hashes, so a long
 * history cannot make two graph lines converge on the wrong commit.
 */
export function buildGraphRows(commits: readonly CommitLogEntry[]): {
  rows: GraphRow[]
  laneCount: number
} {
  const rows: GraphRow[] = []
  let lanes: string[] = commits.length > 0 ? [commits[0]!.sha] : []
  let laneCount = lanes.length

  for (const commit of commits) {
    let lane = lanes.indexOf(commit.sha)
    if (lane < 0) {
      // A date/topology order can expose a commit before the lane which predicted it. Keep
      // the row visible and start a new lane rather than attaching it to an unrelated one.
      lane = 0
      lanes = [commit.sha, ...lanes]
    }

    const before = [...lanes]
    const next = [...lanes]
    if (commit.parents.length === 0) {
      next.splice(lane, 1)
    } else {
      next.splice(lane, 1, commit.parents[0]!)
      next.splice(lane + 1, 0, ...commit.parents.slice(1))
    }

    // A merge whose second parent is already in the graph should continue that existing
    // lane. De-duplicating here also bounds the graph width on criss-cross histories.
    const after: string[] = []
    for (const parent of next) {
      if (!after.includes(parent)) after.push(parent)
    }

    rows.push({
      before,
      after,
      lane,
      parentLanes: commit.parents.map((parent) => after.indexOf(parent)),
    })

    lanes = after
    laneCount = Math.max(laneCount, before.length, after.length, lane + 1)
  }

  return { rows, laneCount }
}

function graphSvg(row: GraphRow, laneCount: number, merge: boolean): string {
  const width = Math.max(1, laneCount) * GRAPH_LANE_WIDTH + GRAPH_GUTTER * 2
  const height = GRAPH_ROW_HEIGHT
  const y = height / 2
  const x = (lane: number): number => GRAPH_GUTTER + lane * GRAPH_LANE_WIDTH + GRAPH_LANE_WIDTH / 2
  const path = (fromLane: number, fromY: number, toLane: number, toY: number): string => {
    const from = x(fromLane)
    const to = x(toLane)
    if (from === to) return `<path d="M${from} ${fromY}V${toY}" />`

    // A shallow cubic keeps a merge leg legible without making rows taller than the text.
    const mid = (fromY + toY) / 2
    return `<path d="M${from} ${fromY}C${from} ${mid},${to} ${mid},${to} ${toY}" />`
  }

  const lines: string[] = []
  for (let index = 0; index < row.before.length; index++) {
    if (index === row.lane) {
      lines.push(path(index, 0, index, y))
    } else {
      lines.push(path(index, 0, index, height))
    }
  }

  for (const parentLane of row.parentLanes) {
    if (parentLane >= 0) lines.push(path(row.lane, y, parentLane, height))
  }

  const nodeClass = merge ? 'merge' : 'commit'
  return `<svg class="history-graph-svg" viewBox="0 0 ${width} ${height}" width="${width}" height="${height}" aria-hidden="true">
    <g class="history-graph-lines">${lines.join('')}</g>
    <circle class="history-graph-node ${nodeClass}" cx="${x(row.lane)}" cy="${y}" r="${merge ? 4 : 3.5}" />
  </svg>`
}

function build(): void {
  overlay = document.createElement('div')
  overlay.className = 'history-backdrop'
  overlay.innerHTML = `
    <div class="history" role="dialog" aria-modal="true" aria-labelledby="history-title" tabindex="-1">
      <div class="history-head">
        <span class="history-title" id="history-title">History</span>
        <span class="history-subtitle"></span>
        <button class="icon-btn" data-history-action="refresh" title="Refresh history">${icons.refresh}</button>
      </div>
      <div class="history-search">
        <button class="icon-btn history-search-submit" data-history-action="search"
          title="Run history search" aria-label="Run history search">${icons.search}</button>
        <input class="history-search-input" type="text" spellcheck="false" autocomplete="off"
          aria-label="Search commit history" />
        <div class="history-search-modes" role="group" aria-label="Search field">
          <button data-history-search-kind="message">Message</button>
          <button data-history-search-kind="author">Author</button>
          <button data-history-search-kind="path">Path</button>
          <button data-history-search-kind="content">Content</button>
        </div>
        <button class="icon-btn history-search-clear" data-history-action="clear-search"
          title="Clear search" aria-label="Clear history search">${icons.close}</button>
      </div>
      <div class="history-list"></div>
      <div class="history-detail" hidden></div>
      <div class="rebase-planner" hidden></div>
      <div class="history-foot"></div>
      <div class="history-hint">
        <kbd>Ctrl</kbd><kbd>F</kbd> search · <kbd>Enter</kbd> run / inspect · <kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Esc</kbd> dismiss
      </div>
    </div>`

  document.body.appendChild(overlay)

  panel = overlay.querySelector('.history')!
  title = overlay.querySelector('.history-title')!
  list = overlay.querySelector('.history-list')!
  detail = overlay.querySelector('.history-detail')!
  plannerHost = overlay.querySelector('.rebase-planner')!
  footer = overlay.querySelector('.history-foot')!
  subtitle = overlay.querySelector('.history-subtitle')!
  search = overlay.querySelector('.history-search')!
  searchInput = overlay.querySelector('.history-search-input')!
  searchModes = overlay.querySelector('.history-search-modes')!
  searchClear = overlay.querySelector('.history-search-clear')!
  hint = overlay.querySelector('.history-hint')!

  overlay.addEventListener('mousedown', (event) => {
    if (event.target === overlay) close()
  })

  overlay.addEventListener('keydown', (event) => {
    if (rebasePlan) {
      if (event.key === 'Escape') {
        event.preventDefault()
        closePlanner()
      }
      if (!event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey && event.key === 'Enter' &&
        !(event.target as HTMLElement).closest('textarea, select, input')) {
        event.preventDefault()
        if (!rebaseBusy) void startRebase()
      }
      return
    }
    const ctrl = event.ctrlKey || event.metaKey

    if (ctrl && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'f' && !historyPath) {
      event.preventDefault()
      searchInput.focus()
      searchInput.select()
      return
    }

    if (event.key === 'Escape') {
      event.preventDefault()
      if (!historyPath && (searchQuery.length > 0 || searchInput.value.length > 0)) {
        clearSearch()
        return
      }
      close()
      return
    }

    if (event.target === searchInput && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
      if (entries.length > 0) {
        event.preventDefault()
        panel.focus({ preventScroll: true })
        move(event.key === 'ArrowDown' ? 1 : -1)
      }
      return
    }

    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      move(event.key === 'ArrowDown' ? 1 : -1)
      return
    }

    if (event.key === 'Enter') {
      event.preventDefault()
      if (event.target === searchInput) {
        if (searchInput.value.trim() !== searchQuery) applySearch(searchInput.value)
        else {
          panel.focus({ preventScroll: true })
          inspect()
        }
        return
      }
      inspect()
      return
    }

    // Contextual single-key actions keep the history surface keyboard-first without
    // stealing letters from a text field (or from an editor behind the overlay). Buttons
    // inside the detail pane remain valid targets, so choosing a merge parent does not
    // silently disable the shortcut.
    const target = event.target as HTMLElement
    const typing = target === searchInput || Boolean(target.closest('input, textarea, [contenteditable="true"]'))
    if (!typing && !ctrl && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'i') {
      const entry = entries[selected]
      if (entry && !historyPath && !searchQuery && !mutationPending) {
        event.preventDefault()
        void openRebasePlanner(entry)
      }
      return
    }

    if (!typing && !ctrl && !event.altKey && !event.shiftKey &&
      (event.key.toLowerCase() === 'c' || event.key.toLowerCase() === 'r')) {
      const entry = entries[selected]
      if (entry && !mutationPending) {
        event.preventDefault()
        void applyHistoryMutation(entry, event.key.toLowerCase() === 'c' ? 'cherryPick' : 'revert')
      }
    }
  })

  searchInput.addEventListener('input', () => {
    searchClear.hidden = searchInput.value.length === 0
  })

  searchModes.addEventListener('click', (event) => {
    const mode = (event.target as HTMLElement).closest<HTMLButtonElement>('[data-history-search-kind]')
    if (!mode) return
    setSearchKind(mode.dataset.historySearchKind as HistorySearchKind)
  })

  list.addEventListener('click', (event) => {
    const row = (event.target as HTMLElement).closest<HTMLElement>('[data-index]')
    if (!row) return

    selected = Number(row.dataset.index)
    render()
    inspect()
  })

  detail.addEventListener('click', (event) => {
    const target = event.target as HTMLElement

    const mutation = target.closest<HTMLElement>('[data-history-mutation]')
    if (mutation) {
      const entry = entries[selected]
      const kind = mutation.dataset.historyMutation
      if (!entry || (kind !== 'cherryPick' && kind !== 'revert') || mutationPending) return

      void applyHistoryMutation(entry, kind)
      return
    }

    const rebase = target.closest<HTMLElement>('[data-history-rebase]')
    if (rebase) {
      const entry = entries[selected]
      if (entry) void openRebasePlanner(entry)
      return
    }

    const parent = target.closest<HTMLElement>('[data-history-parent]')
    if (parent) {
      const entry = entries[selected]
      if (!entry) return

      inspectedParentIndex = Number(parent.dataset.historyParent)
      const generation = ++detailGeneration
      inspectedDetail = null
      actionStatus = null
      renderCommitSummary(entry, null, true)
      void loadDetail(entry, generation, inspectedParentIndex)
      return
    }

    const file = target.closest<HTMLElement>('[data-history-file]')
    if (file) {
      const entry = entries[selected]
      if (!entry || !worktreePath) return
      onOpenCommitFile(entry.sha, inspectedParentIndex, file.dataset.historyFile!)
    }
  })

  footer.addEventListener('click', (event) => {
    const action = (event.target as HTMLElement).closest<HTMLElement>('[data-history-action]')
    if (action?.dataset.historyAction === 'more') void loadMore()
  })

  plannerHost.addEventListener('click', (event) => {
    const target = event.target as HTMLElement
    const move = target.closest<HTMLElement>('[data-rebase-move]')
    if (move) {
      syncPlannerMessages()
      const index = Number(move.dataset.rebaseIndex)
      const delta = move.dataset.rebaseMove === 'up' ? -1 : 1
      const next = index + delta
      if (index >= 0 && next >= 0 && next < rebaseRows.length) {
        const [row] = rebaseRows.splice(index, 1)
        rebaseRows.splice(next, 0, row!)
        renderPlanner()
      }
      return
    }

    const start = target.closest<HTMLElement>('[data-rebase-start]')
    if (start && !rebaseBusy) void startRebase()
    const cancel = target.closest<HTMLElement>('[data-rebase-cancel]')
    if (cancel) closePlanner()
  })

  plannerHost.addEventListener('change', (event) => {
    const action = (event.target as HTMLElement).closest<HTMLSelectElement>('[data-rebase-action]')
    if (!action) return
    const index = Number(action.dataset.rebaseIndex)
    if (Number.isInteger(index) && rebaseRows[index]) {
      syncPlannerMessages()
      rebaseRows[index] = { ...rebaseRows[index]!, action: action.value as RebaseAction }
      renderPlanner()
    }
  })

  overlay.querySelector('[data-history-action="refresh"]')!.addEventListener('click', () =>
    refreshFromHead(),
  )
  overlay.querySelector('[data-history-action="search"]')!.addEventListener('click', () =>
    applySearch(searchInput.value),
  )
  searchClear.addEventListener('click', clearSearch)

  // A commit made by an agent, or by Chapter in another panel, changes the list while it
  // is open. Refresh from the original head rather than appending a second timeline to it.
  on('filesChanged', ({ worktreePath: changed }) => {
    if (isOpen() && changed === worktreePath) scheduleRefresh()
  })

  on('historyChanged', ({ worktreePath: changed }) => {
    if (isOpen() && changed === worktreePath) scheduleRefresh()
  })
}

export function isOpen(): boolean {
  return overlay?.classList.contains('open') ?? false
}

export function close(): void {
  overlay?.classList.remove('open')
  requestGeneration++
  detailGeneration++
  loading = false
  refreshPending = false
  clearTimeout(refreshTimer)
  refreshTimer = undefined
  worktreePath = null
  historyPath = null
  historyBranch = null
  mutationPending = false
  inspectedDetail = null
  actionStatus = null
  closePlanner()
}

function scheduleRefresh(): void {
  if (!isOpen() || !worktreePath || refreshTimer !== undefined) return
  // A commit commonly emits both GitState and filesChanged; one delayed read handles both.
  refreshTimer = setTimeout(() => {
    refreshTimer = undefined
    refreshFromHead()
  }, 40)
}

function refreshFromHead(): void {
  if (!worktreePath) return
  if (loading) {
    refreshPending = true
    return
  }

  refreshPending = false
  entries = []
  hasMore = false
  anchor = ''
  selected = 0
  detailGeneration++
  detail.hidden = true
  renderLoading()
  void loadPage(0, true)
}

export async function open(
  worktree: string,
  branch: string | null,
  toast: (message: string, detail?: string, kind?: 'info' | 'error') => void,
  openCommitFile: (sha: string, parentIndex: number, path: string) => void = () => {},
  path: string | null = null,
  mutateCommit: (
    kind: 'cherryPick' | 'revert',
    sha: string,
    parentIndex: number,
    subject: string,
  ) => MutationPayload | null | Promise<MutationPayload | null> = () => null,
): Promise<void> {
  if (!overlay) build()

  worktreePath = worktree
  historyPath = path
  historyBranch = branch
  onToast = toast
  onOpenCommitFile = openCommitFile
  onMutateCommit = mutateCommit
  selected = 0
  entries = []
  hasMore = false
  anchor = ''
  loading = false
  requestGeneration++
  detailGeneration++
  inspectedParentIndex = 0
  mutationPending = false
  inspectedDetail = null
  actionStatus = null
  closePlanner()
  searchQuery = ''
  searchInput.value = ''
  searchClear.hidden = true
  search.hidden = path !== null
  panel.classList.toggle('file-history', path !== null)
  paintSearchKind()
  detail.hidden = true
  title.textContent = path ? 'File history' : 'History'
  subtitle.textContent = path
    ? `${path} · ${branch ? `on ${branch}` : 'detached HEAD'}`
    : (branch ? `on ${branch}` : 'detached HEAD')
  hint.innerHTML = path
    ? '<kbd>C</kbd> cherry-pick · <kbd>R</kbd> revert · <kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Enter</kbd> inspect · <kbd>Esc</kbd> dismiss'
    : '<kbd>Ctrl</kbd><kbd>F</kbd> search · <kbd>Enter</kbd> run / inspect · <kbd>C</kbd> cherry-pick · <kbd>R</kbd> revert · <kbd>I</kbd> rebase after · <kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Esc</kbd> dismiss'
  overlay!.classList.add('open')
  panel.focus({ preventScroll: true })
  renderLoading()

  await loadPage(0, true)
}

/** Opens the same overlay scoped to the commits which touched one path. */
export function openFile(
  worktree: string,
  branch: string | null,
  path: string,
  toast: (message: string, detail?: string, kind?: 'info' | 'error') => void,
  openCommitFile: (sha: string, parentIndex: number, path: string) => void = () => {},
  mutateCommit: (
    kind: 'cherryPick' | 'revert',
    sha: string,
    parentIndex: number,
    subject: string,
  ) => MutationPayload | null | Promise<MutationPayload | null> = () => null,
): Promise<void> {
  return open(worktree, branch, toast, openCommitFile, path, mutateCommit)
}

function renderLoading(): void {
  list.innerHTML = `<div class="history-empty">${searchQuery ? 'Searching history…' : 'Reading history…'}</div>`
  footer.innerHTML = ''
}

async function loadPage(offset: number, replace: boolean): Promise<void> {
  if (!worktreePath || loading) return

  const generation = ++requestGeneration
  loading = true
  renderFooter()

  let page: CommitLogPage
  try {
    page = historyPath
      ? await call('getFileHistory', {
          worktreePath,
          path: historyPath,
          offset,
          limit: PAGE_SIZE,
          anchor,
        })
      : searchQuery
        ? await call('searchHistory', {
            worktreePath,
            kind: searchKind,
            query: searchQuery,
            offset,
            limit: PAGE_SIZE,
            anchor,
          })
        : await call('getHistory', { worktreePath, offset, limit: PAGE_SIZE, anchor })
  } catch (error) {
    if (generation !== requestGeneration || !isOpen()) {
      if (generation === requestGeneration) loading = false
      if (generation === requestGeneration && refreshPending && isOpen()) refreshFromHead()
      return
    }
    loading = false
    const reason = error instanceof Error ? error.message : String(error)
    if (replace) {
      list.innerHTML = `<div class="history-empty">${esc(reason)}</div>`
      footer.innerHTML = ''
    } else {
      onToast('Could not load older commits', reason, 'error')
      render()
    }
    if (refreshPending && isOpen()) refreshFromHead()
    return
  }

  if (generation !== requestGeneration || !isOpen()) {
    if (generation === requestGeneration) loading = false
    return
  }

  loading = false
  entries = replace ? page.commits : [...entries, ...page.commits]
  anchor = page.anchor
  hasMore = page.hasMore
  if (selected >= entries.length) selected = Math.max(0, entries.length - 1)
  render()

  if (refreshPending && isOpen()) refreshFromHead()
}

async function loadMore(): Promise<void> {
  if (loading || !hasMore) return
  await loadPage(entries.length, false)
}

function render(): void {
  if (entries.length === 0) {
    list.innerHTML = searchQuery
      ? `<div class="history-empty">No ${SEARCH_LABELS[searchKind]} matches for “${esc(searchQuery)}”.</div>`
      : historyPath
        ? '<div class="history-empty">No commits touched this file.</div>'
        : '<div class="history-empty">No commits yet</div>'
    detail.hidden = true
    renderFooter()
    restoreFocus()
    return
  }

  // A path-filtered log omits commits which did not touch the file. Feeding that sparse
  // list into the branch graph would make every omitted parent look like a new lane, so
  // file history gets an honest single timeline instead.
  const graph = historyPath || searchQuery
    ? {
        rows: entries.map((_entry, index): GraphRow => ({
          before: index === 0 ? [] : ['file'],
          after: index === entries.length - 1 ? [] : ['file'],
          lane: 0,
          parentLanes: index === entries.length - 1 ? [] : [0],
        })),
        laneCount: 1,
      }
    : buildGraphRows(entries)
  list.style.setProperty('--history-graph-width', `${Math.max(1, graph.laneCount) * GRAPH_LANE_WIDTH + GRAPH_GUTTER * 2}px`)

  list.innerHTML = entries
    .map((entry, index) => {
      const graphRow = graph.rows[index]!
      const date = formatDate(entry.committedAt)
      const decoration = entry.decorations
        ? `<span class="history-decoration">${esc(entry.decorations)}</span>`
        : ''
      const merge = entry.isMerge ? '<span class="history-merge">merge</span>' : ''

      return `
        <button class="history-row ${index === selected ? 'selected' : ''}" data-index="${index}">
          <span class="history-graph">${graphSvg(graphRow, graph.laneCount, entry.isMerge)}</span>
          <span class="history-sha">${esc(entry.shortSha)}</span>
          <span class="history-row-main">
            <span class="history-subject">${esc(entry.subject || '(no subject)')}</span>
            <span class="history-meta">${esc(entry.authorName || entry.authorEmail || 'unknown author')} · ${esc(date)}${merge ? ` · ${merge}` : ''}</span>
          </span>
          ${decoration}
        </button>`
    })
    .join('')

  renderFooter()
  list.querySelector<HTMLElement>('.history-row.selected')?.scrollIntoView({ block: 'nearest' })
  // Re-rendering replaces the focused row button (mouse click and Load more both do this).
  // Keep focus on the dialog so the arrow/Enter bindings remain usable afterwards.
  restoreFocus()
}

function renderFooter(): void {
  if (loading) {
    footer.innerHTML = '<span class="history-loading">Loading…</span>'
    return
  }

  const noun = searchQuery ? 'match' : 'commit'
  footer.innerHTML = hasMore
    ? `<button class="btn small" data-history-action="more">${icons.chevron}<span>Load more</span></button>`
    : `<span class="history-end">${entries.length > 0 ? `${entries.length} ${noun}${entries.length === 1 ? '' : searchQuery ? 'es' : 's'}` : ''}</span>`
}

function setSearchKind(kind: HistorySearchKind): void {
  if (!Object.hasOwn(SEARCH_LABELS, kind) || kind === searchKind) return
  searchKind = kind
  paintSearchKind()
  searchInput.focus()
  if (searchInput.value.trim().length > 0) applySearch(searchInput.value, true)
}

function paintSearchKind(): void {
  searchInput.placeholder = SEARCH_PLACEHOLDERS[searchKind]
  for (const button of searchModes.querySelectorAll<HTMLButtonElement>('[data-history-search-kind]')) {
    const active = button.dataset.historySearchKind === searchKind
    button.classList.toggle('on', active)
    button.setAttribute('aria-pressed', String(active))
  }
}

function clearSearch(): void {
  searchInput.value = ''
  searchClear.hidden = true
  applySearch('')
  searchInput.focus()
}

function applySearch(value: string, force = false): void {
  const next = value.trim()
  if (!force && next === searchQuery) {
    if (next.length === 0) {
      searchInput.value = ''
      searchClear.hidden = true
    }
    return
  }

  searchQuery = next
  searchClear.hidden = next.length === 0

  // Query changes may overtake a slow pickaxe search. Invalidate its generation and let
  // the new read start immediately; the old reply can no longer paint or own `loading`.
  requestGeneration++
  loading = false
  refreshPending = false
  entries = []
  hasMore = false
  anchor = ''
  selected = 0
  detailGeneration++
  detail.hidden = true
  renderLoading()
  void loadPage(0, true)
}

function restoreFocus(): void {
  if (document.activeElement === searchInput) searchInput.focus({ preventScroll: true })
  else panel.focus({ preventScroll: true })
}

function move(delta: number): void {
  if (entries.length === 0) return

  // Walking off the bottom of a paginated timeline should fetch the next page, not wrap
  // round to the newest commit. Load more was the last thing in this overlay reachable only
  // by clicking it, and pressing ↓ at the end is what somebody reading backwards through
  // history is already doing.
  //
  // A second ↓ while that page is still in flight stays put rather than wrapping. Jumping to
  // the newest commit because the network was slow is the worst of both answers, and it is
  // what this did until a fast double-press showed it.
  if (delta > 0 && selected === entries.length - 1 && hasMore) {
    if (!loading) void loadMore()
    return
  }

  selected = (selected + delta + entries.length) % entries.length
  detail.hidden = true
  render()
}

function inspect(): void {
  const entry = entries[selected]
  if (!entry) return

  const generation = ++detailGeneration
  inspectedParentIndex = 0
  inspectedDetail = null
  actionStatus = null

  renderCommitSummary(entry, null, true)
  void loadDetail(entry, generation, inspectedParentIndex)
}

function renderCommitSummary(
  entry: CommitLogEntry,
  commitDetail: CommitDetail | null,
  isLoading: boolean,
): void {
  const parentButtons = entry.parents.length > 1
    ? `<div class="history-parent-picker"><span>Compare with</span>${entry.parents
        .map((parent, index) => `<button class="btn small ${index === inspectedParentIndex ? 'on' : ''}"
          data-history-parent="${index}" title="Parent ${index + 1}: ${esc(parent)}">${index + 1} · ${esc(parent.slice(0, 12))}</button>`)
        .join('')}</div>`
    : ''

  const parents = entry.parents.length
    ? `<div class="history-detail-line"><span>Parents</span><code>${entry.parents
        .map((parent) => esc(parent.slice(0, 12)))
        .join(' · ')}</code></div>`
    : ''
  const body = entry.body.trim()
    ? `<pre class="history-body">${esc(entry.body.trim())}</pre>`
    : '<div class="history-no-body">No commit body.</div>'

  const files = isLoading
    ? '<div class="history-files-loading">Reading changed files…</div>'
    : commitDetail
      ? renderCommitFiles(commitDetail)
      : '<div class="history-no-body">Could not read changed files.</div>'

  const status = actionStatus
    ? `<div class="history-action-status ${actionStatus.kind}">${esc(actionStatus.text)}</div>`
    : ''

  const actions = `
    <div class="history-actions">
      <button class="btn small" data-history-mutation="cherryPick" ${mutationPending ? 'disabled' : ''}
        title="Apply this commit on the current branch">${icons.commit}<span>Cherry-pick</span></button>
      <button class="btn small" data-history-mutation="revert" ${mutationPending ? 'disabled' : ''}
        title="Create a commit that reverses this change">${icons.undo}<span>Revert</span></button>
      ${!historyPath && !searchQuery
        ? `<button class="btn small" data-history-rebase ${mutationPending ? 'disabled' : ''}
            title="Rewrite commits after this one">${icons.pencil}<span>Rebase after this</span></button>`
        : ''}
      ${mutationPending ? '<span class="history-action-pending">Applying…</span>' : ''}
    </div>${status}`

  detail.innerHTML = `
    <div class="history-detail-head">
      <strong>${esc(entry.subject || '(no subject)')}</strong>
      <code>${esc(entry.sha)}</code>
    </div>
    <div class="history-detail-line"><span>Author</span><span>${esc(entry.authorName)} &lt;${esc(entry.authorEmail)}&gt;</span></div>
    <div class="history-detail-line"><span>Committed</span><span>${esc(formatDate(entry.committedAt))}</span></div>
    ${parents}
    ${parentButtons}
    ${body}
    ${actions}`
    + files
  detail.hidden = false
}

async function openRebasePlanner(base: CommitLogEntry): Promise<void> {
  if (!worktreePath || historyPath || rebaseBusy) return

  const path = worktreePath
  const generation = ++plannerGeneration
  rebaseBusy = true
  rebasePlan = null
  rebaseRows = []
  plannerDetailVisible = !detail.hidden
  detail.hidden = true
  list.hidden = true
  footer.hidden = true
  search.hidden = true
  title.textContent = 'Interactive rebase'
  subtitle.textContent = `after ${base.shortSha}`
  hint.innerHTML = '<kbd>Esc</kbd> cancel planner · reorder with the arrow buttons'
  plannerHost.hidden = false
  panel.classList.add('rebase-planner-open')
  plannerHost.innerHTML = '<div class="history-empty">Reading commits after this base…</div>'
  footer.innerHTML = ''

  try {
    const plan = await call('getRebasePlan', { worktreePath: path, upstream: base.sha })
    if (generation !== plannerGeneration || !isOpen() || worktreePath !== path) return
    rebasePlan = plan
    rebaseRows = plan.entries.map((entry) => ({ ...entry, action: 'pick', message: '' }))
    renderPlanner()
  } catch (error) {
    if (generation === plannerGeneration && worktreePath === path) {
      closePlanner()
      onToast('Could not prepare the rebase', error instanceof Error ? error.message : String(error), 'error')
    }
  } finally {
    rebaseBusy = false
  }
}

function closePlanner(): void {
  plannerGeneration++
  rebasePlan = null
  rebaseRows = []
  rebaseBusy = false
  plannerHost?.setAttribute('hidden', '')
  panel?.classList.remove('rebase-planner-open')
  if (list) list.hidden = false
  if (detail) detail.hidden = !plannerDetailVisible
  if (footer) footer.hidden = false
  if (search) search.hidden = historyPath !== null
  if (title) title.textContent = historyPath ? 'File history' : 'History'
  if (subtitle) {
    subtitle.textContent = historyPath
      ? `${historyPath} · ${historyBranch ?? 'detached HEAD'}`
      : (historyBranch ?? 'detached HEAD')
  }
  if (hint) {
    hint.innerHTML = historyPath
      ? '<kbd>C</kbd> cherry-pick · <kbd>R</kbd> revert · <kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Enter</kbd> inspect · <kbd>Esc</kbd> dismiss'
      : '<kbd>Ctrl</kbd><kbd>F</kbd> search · <kbd>Enter</kbd> run / inspect · <kbd>C</kbd> cherry-pick · <kbd>R</kbd> revert · <kbd>I</kbd> rebase after · <kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Esc</kbd> dismiss'
  }
  plannerDetailVisible = false
}

function syncPlannerMessages(): void {
  if (!plannerHost) return
  for (const field of plannerHost.querySelectorAll<HTMLTextAreaElement>('[data-rebase-message]')) {
    const index = Number(field.dataset.rebaseMessage)
    if (Number.isInteger(index) && rebaseRows[index]) {
      rebaseRows[index] = { ...rebaseRows[index]!, message: field.value }
    }
  }
}

function renderPlanner(): void {
  if (!rebasePlan) return

  const options: Array<[RebaseAction, string]> = [
    ['pick', 'Pick'],
    ['reword', 'Reword'],
    ['edit', 'Edit'],
    ['squash', 'Squash'],
    ['fixup', 'Fixup'],
    ['drop', 'Drop'],
  ]
  const firstKept = rebaseRows.findIndex((entry) => entry.action !== 'drop')
  const invalidFirstAction = firstKept >= 0 &&
    (rebaseRows[firstKept]!.action === 'squash' || rebaseRows[firstKept]!.action === 'fixup')
  const plannerWarning = rebasePlan.unavailableReason ?? (invalidFirstAction
    ? 'The first kept commit cannot be squashed or fixed up. Choose Pick, Reword or Edit first.'
    : null)

  plannerHost.hidden = false
  plannerHost.innerHTML = `
    <div class="rebase-head">
      <div>
        <div class="rebase-title">Rebase commits after <code>${esc(rebasePlan.upstream.slice(0, 12))}</code></div>
        <div class="rebase-subtitle">Oldest first · ${rebaseRows.length} commit${rebaseRows.length === 1 ? '' : 's'} · branch ${esc(rebasePlan.branch ?? 'detached')}</div>
      </div>
      <button class="icon-btn" data-rebase-cancel title="Cancel planner">${icons.close}</button>
    </div>
    ${plannerWarning
      ? `<div class="rebase-warning">${esc(plannerWarning)}</div>`
      : ''}
    <div class="rebase-rows">
      ${rebaseRows.map((entry, index) => `
        <div class="rebase-row ${entry.action === 'drop' ? 'dropped' : ''}" data-rebase-index="${index}">
          <div class="rebase-order">
            <button class="icon-btn" data-rebase-move="up" data-rebase-index="${index}" ${index === 0 ? 'disabled' : ''} title="Move earlier">↑</button>
            <button class="icon-btn" data-rebase-move="down" data-rebase-index="${index}" ${index === rebaseRows.length - 1 ? 'disabled' : ''} title="Move later">↓</button>
          </div>
          <select class="rebase-action" data-rebase-action data-rebase-index="${index}" aria-label="Action for ${esc(entry.shortSha)}">
            ${options.map(([value, label]) => `<option value="${value}" ${entry.action === value ? 'selected' : ''}>${label}</option>`).join('')}
          </select>
          <div class="rebase-commit"><code>${esc(entry.shortSha)}</code><span>${esc(entry.subject || '(no subject)')}</span></div>
          ${entry.action === 'reword' || entry.action === 'edit' || entry.action === 'squash'
            ? `<textarea class="rebase-message" data-rebase-message="${index}" rows="2" placeholder="Replacement commit message (optional)">${esc(entry.message)}</textarea>`
            : ''}
        </div>`).join('')}
    </div>
    <div class="rebase-foot">
      <span class="rebase-foot-note">Drop is explicit. Squash/fixup combine with the row above.</span>
      <div class="history-actions">
        <button class="btn small" data-rebase-cancel>Cancel</button>
        <button class="btn small pop" data-rebase-start ${plannerWarning || rebaseRows.length === 0 ? 'disabled' : ''}>Start rebase</button>
      </div>
    </div>`
}

async function startRebase(): Promise<void> {
  if (!rebasePlan || !worktreePath || rebasePlan.unavailableReason || rebaseRows.length === 0) return

  syncPlannerMessages()
  const firstKept = rebaseRows.find((entry) => entry.action !== 'drop')
  if (firstKept?.action === 'squash' || firstKept?.action === 'fixup') {
    renderPlanner()
    return
  }

  const path = worktreePath
  const approved = await confirm({
    title: 'Rewrite these commits?',
    body: 'Git will replay the selected commits in this order on the current branch. You can abort while it is running, and undo is available after it finishes.',
    confirmLabel: 'Start rebase',
    recovery: 'undoable',
    detail: rebaseRows.map((entry) => `${entry.action} ${entry.shortSha} ${entry.subject}`).slice(0, 8),
  })
  if (!approved || !rebasePlan || worktreePath !== path) return

  rebaseBusy = true
  const plan = rebasePlan
  try {
    const result = await call('startRebase', {
      worktreePath: path,
      upstream: plan.upstream,
      expectedHead: plan.head,
      entries: rebaseRows.map((entry) => ({ sha: entry.sha, action: entry.action, message: entry.message || undefined })),
    })
    if (result.ok) {
      onToast(result.message)
      closePlanner()
      if (isOpen()) refreshFromHead()
    } else {
      onToast(result.failure === 'conflict' ? 'Rebase stopped on conflicts' : 'Could not start rebase', result.message, 'error')
      if (result.failure === 'conflict') closePlanner()
    }
  } catch (error) {
    onToast('Could not start rebase', error instanceof Error ? error.message : String(error), 'error')
  } finally {
    rebaseBusy = false
  }
}

function renderCommitFiles(commitDetail: CommitDetail): string {
  if (commitDetail.files.length === 0)
    return '<div class="history-no-files">This parent comparison has no changed files.</div>'

  const count = commitDetail.files.length
  return `<div class="history-files-head">${count} changed file${count === 1 ? '' : 's'} · click to open diff</div>
    <div class="history-files">${commitDetail.files
      .map((file) => {
        const delta = file.isBinary
          ? '<span class="history-file-delta">binary</span>'
          : `<span class="history-file-delta"><span class="up">+${file.linesAdded}</span> <span class="down">−${file.linesRemoved}</span></span>`
        const old = file.oldPath ? ` <span class="history-file-old">from ${esc(file.oldPath)}</span>` : ''
        return `<button class="history-file" data-history-file="${esc(file.path)}" title="Open ${esc(file.path)}">
          <span class="history-file-name">${esc(file.path)}${old}</span>${delta}</button>`
      })
      .join('')}</div>`
}

async function loadDetail(
  entry: CommitLogEntry,
  generation: number,
  parentIndex: number,
): Promise<void> {
  try {
    const result = await call('getCommitDetail', {
      worktreePath: worktreePath!,
      sha: entry.sha,
      parentIndex,
    })
    if (generation !== detailGeneration || !isOpen() || entries[selected]?.sha !== entry.sha) return
    inspectedDetail = result
    renderCommitSummary(entry, result, false)
  } catch (error) {
    if (generation !== detailGeneration || !isOpen()) return
    onToast('Could not read changed files', error instanceof Error ? error.message : String(error), 'error')
    inspectedDetail = null
    renderCommitSummary(entry, null, false)
  }
}

/** Confirms, runs and keeps the result of a history action visible in the detail pane. */
async function applyHistoryMutation(
  entry: CommitLogEntry,
  kind: 'cherryPick' | 'revert',
): Promise<void> {
  const cherryPick = kind === 'cherryPick'
  const verb = cherryPick ? 'Cherry-pick' : 'Revert'
  const parent = entry.isMerge ? ` using parent ${inspectedParentIndex + 1}` : ''

  const approved = await confirm({
    title: `${verb} “${entry.subject || entry.shortSha}”?`,
    body: cherryPick
      ? `A new commit will apply this change on the current branch${parent}.`
      : `A new commit will reverse this change on the current branch${parent}.`,
    confirmLabel: verb,
    recovery: 'undoable',
    detail: [entry.sha],
  })
  if (!approved || !isOpen() || entries[selected]?.sha !== entry.sha) return

  mutationPending = true
  actionStatus = { kind: 'success', text: `${verb} in progress…` }
  renderCommitSummary(entry, inspectedDetail, false)

  try {
    const result = await onMutateCommit(kind, entry.sha, inspectedParentIndex, entry.subject)

    if (result?.ok) {
      actionStatus = { kind: 'success', text: `${verb} completed. Undo is available in the main window.` }
      onToast(result.message)
    } else if (result) {
      const conflict = result.failure === 'conflict'
      actionStatus = {
        kind: 'error',
        text: conflict
          ? `${verb} stopped on conflicts. The repository was left in that state for resolution.`
          : result.message,
      }
      onToast(
        conflict ? `${verb} stopped on conflicts` : result.message,
        conflict
          ? 'Resolve the conflicted files, then continue or abort from the repository tools.'
          : result.commandLine || undefined,
        'error',
      )
    } else {
      actionStatus = { kind: 'error', text: `${verb} did not complete.` }
    }
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    actionStatus = { kind: 'error', text: reason }
    onToast(`Could not ${verb.toLowerCase()} commit`, reason, 'error')
  } finally {
    mutationPending = false
    if (isOpen() && entries[selected]?.sha === entry.sha) renderCommitSummary(entry, inspectedDetail, false)
  }
}

function formatDate(value: string | null | undefined): string {
  if (!value) return 'unknown date'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value

  return new Intl.DateTimeFormat(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
}
