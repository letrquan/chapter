import { call, on } from './bridge'
import { confirm } from './confirm'
import { icons } from './icons'
import type {
  Branch,
  AcceptWorkPayload,
  AgentSession,
  RejectWorkPayload,
  RejectWorkPreviewPayload,
  MutationPayload,
  PullStrategy,
  RefsPayload,
  Remote,
  RemoteProgress,
  RemoteOperationStarted,
  PullRequest,
  Stash,
  Tag,
  Worktree,
} from './protocol'

/**
 * Branches, worktrees, stashes and tags — one overlay, four sections.
 *
 * Built alongside `palette.ts` rather than inside it. The palette is shaped around a result
 * that is a place in a file (`path`, `line`, `column`) and does one thing to it; this is a
 * list of refs where every row has several actions and one of them destroys something. The
 * two share an idiom — backdrop, capture-phase keys, arrows and Enter — and nothing else,
 * which is the same relationship `confirm.ts` has to both.
 *
 * Everything is read through one `getRefs` call. The four lists are shown together and
 * every mutation refreshes all of them, so fetching them separately would only give the
 * panel more ways to contradict itself.
 *
 * Worktrees sit here rather than in the rail, which is the other place they could have gone.
 * The rail is the list of worktrees, but it is a hundred and sixty pixels wide and every row
 * is a single button — there is no room for the four actions each worktree needs, and no
 * honest way to ask "where should this one go?" in it. This overlay already has the row
 * grid, the filter, the inline prompt, the confirmation and the keyboard, all of which
 * worktree management needs and none of which the rail has.
 */

export type Section = 'branches' | 'worktrees' | 'stashes' | 'tags' | 'remotes' | 'pullRequests'

/** What the caller has to do after a mutation: refresh the rest of the window. */
type MutatedHandler = () => void | Promise<void>

/** Lets a row hand the user to the worktree that already has a branch open. */
type WorktreeHandler = (worktreePath: string) => void | Promise<void>

/** Starts a read-only comparison with the currently selected worktree. */
type CompareWorktreeHandler = (worktreePath: string) => void | Promise<void>

/** Refreshes the active window after accepting another worktree's branch. */
type AcceptWorktreeHandler = (payload: AcceptWorkPayload) => void | Promise<void>

/** Refreshes the source window after rejecting and resetting an agent worktree. */
type RejectWorktreeHandler = (payload: RejectWorkPayload) => void | Promise<void>

/**
 * Says a worktree the app was holding open no longer exists — removed, pruned, or moved
 * elsewhere. Editor models, tabs and the active selection all key off the path, so the
 * window has to be told rather than left to discover it by failing to read one.
 *
 * Where it went is deliberately not a parameter. A move is given a destination the user
 * typed, which git resolves — against the main worktree, and into the platform's own
 * separators — so the string this panel holds is not reliably the path that now exists. The
 * window re-reads the list and can see for itself.
 */
type WorktreeGoneHandler = (worktreePath: string) => void | Promise<void>

const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
}

const esc = (value: string): string => value.replace(/[&<>"']/g, (c) => ESCAPES[c]!)

let overlay: HTMLElement | null = null
let filter: HTMLInputElement
let list: HTMLElement
let subtitle: HTMLElement
let footer: HTMLElement
let notice: HTMLElement
let progress: HTMLElement
let hint: HTMLElement

let section: Section = 'branches'
let worktreePurpose: 'manage' | 'compare' = 'manage'
let refs: RefsPayload | null = null
let pullRequests: PullRequest[] = []
let pullRequestDetail = ''
let pullRequestsLoading = false
let rows: Row[] = []
let selected = 0
let worktreePath: string | null = null
let busy = false
let remoteStarting = false
let remoteOperation: RemoteProgress | null = null

  // A detached operation can finish between the postMessage and the response carrying its id.
  // Keep those terminal events briefly so the UI never paints a spinner for work that is already
  // over, and so a fast local remote does not lose its completion toast.
const remoteProgressById = new Map<string, RemoteProgress>()
const remoteFinishedById = new Map<string, RemoteProgress>()
const remoteStartedByWorktree = new Map<string, RemoteOperationStarted>()
const MAX_REMOTE_EVENT_CACHE = 64

/** Keep a bounded race buffer; a terminal event is only needed until its start reply arrives. */
function cacheRemoteEvent(cache: Map<string, RemoteProgress>, value: RemoteProgress): void {
  cache.set(value.id, value)
  while (cache.size > MAX_REMOTE_EVENT_CACHE) {
    const oldest = cache.keys().next().value
    if (oldest === undefined) break
    cache.delete(oldest)
  }
}

let onMutated: MutatedHandler = () => {}
let onGoToWorktree: WorktreeHandler = () => {}
let onCompareWorktree: CompareWorktreeHandler = () => {}
let onAcceptWorktree: AcceptWorktreeHandler = () => {}
let onRejectWorktree: RejectWorktreeHandler = () => {}
let onWorktreeGone: WorktreeGoneHandler = () => {}
let onToast: (message: string, detail?: string, kind?: 'info' | 'error') => void = () => {}

/**
 * A rendered row and what Enter does to it.
 *
 * The primary action is carried per row rather than derived from the section, because
 * within one section it genuinely differs: Enter switches to a branch, but for a branch
 * another worktree already has open it goes to that worktree instead.
 */
interface Row {
  html: string
  worktree?: Worktree
  primary?: () => void | Promise<void>
  /** Contextual action available from the selected row in the current section. */
  accept?: () => void | Promise<void>
  reject?: () => void | Promise<void>
}

/* ==========================================================================
   Shell
   ========================================================================== */

const SECTIONS: { id: Section; label: string }[] = [
  { id: 'branches', label: 'Branches' },
  // Second rather than last: this is the app's home turf, and it is the section most likely
  // to be the reason the panel was opened at all.
  { id: 'worktrees', label: 'Worktrees' },
  { id: 'stashes', label: 'Stashes' },
  { id: 'tags', label: 'Tags' },
  { id: 'remotes', label: 'Remotes' },
  { id: 'pullRequests', label: 'Pull requests' },
]

function build(): void {
  overlay = document.createElement('div')
  overlay.className = 'refs-backdrop'
  overlay.innerHTML = `
    <div class="refs" role="dialog" aria-modal="true" aria-labelledby="refs-title">
      <div class="refs-head">
        <span class="refs-title" id="refs-title">Refs and worktrees</span>
        <span class="refs-subtitle"></span>
      </div>
      <div class="segmented refs-sections">
        ${SECTIONS.map(
          (s) => `<button data-section="${s.id}" class="${s.id === 'branches' ? 'on' : ''}">${s.label}</button>`,
        ).join('')}
      </div>
      <input class="refs-filter" type="text" spellcheck="false" autocomplete="off" />
      <div class="refs-notice" hidden></div>
      <div class="refs-progress" hidden></div>
      <div class="refs-list"></div>
      <div class="refs-foot"></div>
      <div class="refs-hint">
        <kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Enter</kbd> use · <kbd>→</kbd> actions ·
        <kbd>A</kbd> accept · <kbd>R</kbd> reject (Worktrees) ·
        <kbd>Tab</kbd> section · <kbd>Esc</kbd> dismiss
      </div>
    </div>`

  document.body.appendChild(overlay)

  filter = overlay.querySelector('.refs-filter')!
  list = overlay.querySelector('.refs-list')!
  subtitle = overlay.querySelector('.refs-subtitle')!
  footer = overlay.querySelector('.refs-foot')!
  notice = overlay.querySelector('.refs-notice')!
  progress = overlay.querySelector('.refs-progress')!
  hint = overlay.querySelector('.refs-hint')!

  // These listeners live for the page lifetime, not the overlay lifetime. Events can arrive
  // while the panel is closed, and retaining the small terminal cache lets reopening it show
  // the operation's actual state instead of inventing a fresh one.
  on('remoteProgress', handleRemoteProgress)
  on('remoteFinished', handleRemoteFinished)

  overlay.addEventListener('mousedown', (event) => {
    if (event.target === overlay) close()
  })

  overlay.querySelector('.refs-sections')!.addEventListener('click', (event) => {
    const button = (event.target as HTMLElement).closest<HTMLElement>('[data-section]')
    if (button) setSection(button.dataset.section as Section)
  })

  filter.addEventListener('input', () => {
    selected = 0
    render()
  })

  // Bound to the panel rather than to the filter field, so the keyboard keeps working
  // wherever focus has landed inside it. Pressing an action button moves focus onto that
  // button, and with the handler on the input alone the arrows and Enter went dead until
  // the user clicked back into the field — in an app whose whole point is the keyboard.
  // The inline prompt still gets first refusal: it stops the event before it reaches here.
  overlay!.addEventListener('keydown', onKey)

  // Delegated, because the list is rebuilt on every render and per-row listeners would be
  // re-attached each time — and would keep firing against rows that no longer exist.
  list.addEventListener('click', (event) => {
    const target = event.target as HTMLElement

    if (target.closest('a')) {
      event.stopPropagation()
      return
    }

    const action = target.closest<HTMLElement>('[data-action]')
    if (action) {
      event.stopPropagation()
      void runRowAction(Number(action.dataset.row), action.dataset.action!)
      return
    }

    const row = target.closest<HTMLElement>('[data-index]')
    if (!row) return

    selected = Number(row.dataset.index)
    void usePrimary()
  })

  footer.addEventListener('click', (event) => {
    const button = (event.target as HTMLElement).closest<HTMLElement>('[data-foot]')
    if (!button) return
    event.stopPropagation()
    void runFooterAction(button.dataset.foot!)
  })

  progress.addEventListener('click', (event) => {
    const button = (event.target as HTMLElement).closest<HTMLElement>('[data-foot="cancel-remote"]')
    if (button) {
      event.stopPropagation()
      void runFooterAction('cancel-remote')
    }
  })
}

export function isOpen(): boolean {
  return overlay?.classList.contains('open') ?? false
}

export function close(): void {
  overlay?.classList.remove('open')
}

export async function openRefs(
  worktree: string,
  handlers: {
    onMutated: MutatedHandler
    onGoToWorktree: WorktreeHandler
    onCompareWorktree: CompareWorktreeHandler
    onAcceptWorktree: AcceptWorktreeHandler
    onRejectWorktree: RejectWorktreeHandler
    onWorktreeGone: WorktreeGoneHandler
    toast: (message: string, detail?: string, kind?: 'info' | 'error') => void
  },
  startSection: Section = 'branches',
  purpose: 'manage' | 'compare' = 'manage',
): Promise<void> {
  if (!overlay) build()

  worktreePath = worktree
  onMutated = handlers.onMutated
  onGoToWorktree = handlers.onGoToWorktree
  onCompareWorktree = handlers.onCompareWorktree
  onAcceptWorktree = handlers.onAcceptWorktree
  onRejectWorktree = handlers.onRejectWorktree
  onWorktreeGone = handlers.onWorktreeGone
  onToast = handlers.toast

  section = startSection
  worktreePurpose = purpose
  selected = 0
  filter.value = ''
  pullRequests = []
  pullRequestDetail = ''

  const started = remoteStartedByWorktree.get(worktree)
  remoteOperation = started
    ? remoteProgressById.get(started.id) ?? {
        id: started.id,
        worktreePath: worktree,
        operation: started.operation,
        state: 'running',
        phase: 'starting',
        message: 'Starting…',
      }
    : null

  syncSectionButtons()
  overlay!.classList.add('open')
  filter.focus()

  list.innerHTML = '<div class="refs-empty">Reading refs…</div>'
  await refresh()
}

/** Re-reads everything and repaints. Every mutation ends here. */
async function refresh(): Promise<void> {
  if (!worktreePath) return

  try {
    refs = await call('getRefs', { worktreePath })
    if (section === 'pullRequests') await refreshPullRequests()
  } catch (error) {
    refs = null
    list.innerHTML = `<div class="refs-empty">${esc(message(error))}</div>`
    return
  }

  render()
}

async function refreshPullRequests(): Promise<void> {
  if (!worktreePath) return
  pullRequestsLoading = true
  pullRequestDetail = ''
  try {
    const result = await call('getPullRequests', { worktreePath, limit: 100 })
    pullRequests = result.pullRequests
    if (!result.success) pullRequestDetail = result.detail || 'GitHub CLI could not list pull requests.'
  } catch (error) {
    pullRequests = []
    pullRequestDetail = message(error)
  } finally {
    pullRequestsLoading = false
  }
}

const message = (error: unknown): string =>
  error instanceof Error ? error.message : String(error)

function setSection(next: Section): void {
  section = next
  selected = 0
  filter.value = ''
  syncSectionButtons()
  render()
  filter.focus()
  if (next === 'pullRequests') void refreshPullRequests().then(render)
}

function syncSectionButtons(): void {
  for (const button of overlay!.querySelectorAll<HTMLElement>('[data-section]'))
    button.classList.toggle('on', button.dataset.section === section)

  filter.placeholder = `Filter ${section}…`
  hint.innerHTML =
    '<kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Enter</kbd> use · <kbd>→</kbd> actions · ' +
    (section === 'worktrees' && worktreePurpose === 'manage'
      ? '<kbd>A</kbd> accept · <kbd>R</kbd> reject selected · '
      : '') +
    (section === 'worktrees' ? '<kbd>L</kbd> open session log · ' : '') +
    '<kbd>Tab</kbd> section · <kbd>Esc</kbd> dismiss'
}

/* ==========================================================================
   Rendering
   ========================================================================== */

function render(): void {
  if (!refs) return

  subtitle.innerHTML = worktreePurpose === 'compare'
    ? '<strong>Choose a worktree</strong> to compare with the current one'
    : refs.current
      ? `on <strong>${esc(refs.current)}</strong>`
      : '<strong>detached HEAD</strong> — not on any branch'

  // Said once, at the top, rather than on each disabled button: during a merge or rebase
  // every switch is refused for the same reason, and repeating it per row is noise.
  const blocked = !refs.canSwitch && refs.blockedReason
  notice.hidden = !blocked
  if (blocked) notice.textContent = `Switching is unavailable: ${refs.blockedReason}`

  const query = filter.value.trim().toLowerCase()

  rows =
    section === 'branches'
      ? branchRows(query)
      : section === 'worktrees'
        ? worktreeRows(query)
        : section === 'stashes'
          ? stashRows(query)
          : section === 'tags'
          ? tagRows(query)
          : section === 'remotes'
            ? remoteRows(query)
            : pullRequestRows(query)

  renderRemoteProgress()

  // Both the list and the footer are replaced wholesale below, which detaches whatever
  // inside them had focus — a row's action button, most often. Focus then falls to
  // <body>, outside the overlay the key handler is bound to, and the arrows and Enter go
  // dead again: exactly the failure moving the handler onto the panel was meant to end,
  // one keystroke later. Note it here and hand focus back afterwards.
  const focused = document.activeElement
  const losesFocus =
    focused instanceof HTMLElement &&
    (list.contains(focused) || footer.contains(focused) || progress.contains(focused))

  // Which action the keyboard was holding, so it can be handed back the same one. Without
  // this, ↓ while on a row action — or a progress tick arriving mid-navigation, which
  // renders too — drops the user back into the filter one keystroke into a sequence.
  const hold = heldAction(focused)

  if (rows.length === 0) {
    list.innerHTML = `<div class="refs-empty">${esc(emptyMessage(query))}</div>`
  } else {
    if (selected >= rows.length) selected = rows.length - 1

    list.innerHTML = rows
      .map(
        (row, index) =>
          `<div class="refs-row ${index === selected ? 'selected' : ''}" data-index="${index}">${row.html}</div>`,
      )
      .join('')
  }

  renderFooter()

  // The filter is the one thing in here that survives a render, so it is where focus goes —
  // but only once the action it was on has been given a chance to come back.
  if (losesFocus && !overlay!.contains(document.activeElement)) {
    if (!(hold && restoreAction(hold))) filter.focus()
  }
}

/**
 * What an empty list says.
 *
 * Written out per section rather than assembled from the section name: "the stash" is
 * singular in git's own language and "Nothing in the tags" is not a sentence anybody would
 * write on purpose. An empty list is also the first thing a new user sees here, so it is
 * worth it being a sentence.
 */
function emptyMessage(query: string): string {
  if (query.length > 0) return 'No matches'

  switch (section) {
    case 'stashes':
      return 'The stash is empty'
    case 'tags':
      return 'No tags yet'
    case 'remotes':
      return 'No remotes configured'
    case 'pullRequests':
      return pullRequestsLoading
        ? 'Loading pull requests…'
        : pullRequestDetail || 'No pull requests found'
    case 'worktrees':
      // Not a state that can occur — a repository always has its main worktree — but the
      // list is rendered from a payload, and an empty one should read as a sentence rather
      // than as blank space.
      return 'No worktrees in this repository'
    default:
      return 'No branches in this repository'
  }
}

function renderFooter(): void {
  switch (section) {
    case 'branches':
      footer.innerHTML =
        `<button class="btn small" data-foot="new-branch">${icons.plus}<span>New branch</span></button>`
      return

    case 'worktrees': {
      // Prune is offered only when git has something to forget. A permanently visible button
      // that usually says "nothing to do" trains people to ignore it, and the count is the
      // whole of what makes it worth pressing.
      const missing = refs?.worktrees.filter((w) => w.isPrunable).length ?? 0

      footer.innerHTML = worktreePurpose === 'compare'
        ? `<button class="btn small" data-foot="cancel-compare"><span>Cancel</span></button>`
        : `<button class="btn small" data-foot="new-worktree">${icons.plus}<span>New worktree</span></button>` +
        (missing > 0
          ? `<button class="btn small" data-foot="prune">
               <span>Prune ${missing} missing</span>
             </button>`
          : '')
      return
    }

    case 'stashes':
      footer.innerHTML =
        `<button class="btn small" data-foot="stash">${icons.plus}<span>Stash changes</span></button>
         <button class="btn small" data-foot="stash-untracked">
           <span>Stash, including untracked</span>
         </button>`
      return

    case 'remotes':
      footer.innerHTML =
        `<button class="btn small" data-foot="new-remote">${icons.plus}<span>Add remote</span></button>
         <button class="btn small pop" data-foot="fetch-all">${icons.refresh}<span>Fetch all</span></button>`
      return

    case 'pullRequests':
      footer.innerHTML =
        `<button class="btn small pop" data-foot="new-pull-request">${icons.plus}<span>Create pull request</span></button>
         <button class="btn small" data-foot="refresh-pull-requests">${icons.refresh}<span>Refresh</span></button>`
      return

    default:
      footer.innerHTML =
        `<button class="btn small" data-foot="new-tag">${icons.plus}<span>New tag</span></button>`
  }
}

/**
 * The ahead/behind pair, or the reason there is not one.
 *
 * A branch with no upstream and a branch exactly level with its upstream both have nothing
 * to count, and showing "0 ↑ 0 ↓" for the first would claim a relationship it does not have.
 */
function trackingHtml(branch: Branch): string {
  if (branch.isUpstreamGone)
    return `<span class="refs-track gone" title="${esc(branch.upstream ?? '')} no longer exists">gone</span>`

  if (branch.upstream == null) return '<span class="refs-track"></span>'

  const ahead = branch.ahead ?? 0
  const behind = branch.behind ?? 0

  if (ahead === 0 && behind === 0)
    return `<span class="refs-track level" title="level with ${esc(branch.upstream)}">✓</span>`

  const parts = [
    ahead > 0 ? `<span class="refs-ahead">↑${ahead}</span>` : '',
    behind > 0 ? `<span class="refs-behind">↓${behind}</span>` : '',
  ].join('')

  // Stated as of the last fetch rather than silently implied: tracking refs are local data,
  // and a fetch is the event that makes their relationship with the server current.
    return `<span class="refs-track" title="against ${esc(branch.upstream)}, as of the last remote sync">${parts}</span>`
}

function branchRows(query: string): Row[] {
  if (!refs) return []

  const matching = refs.branches.filter((b) => b.name.toLowerCase().includes(query))

  // Locals first: they are what the user acts on. Remote rows exist to be checked out from,
  // which is a rarer move than switching between branches that already exist here.
  const ordered = [...matching.filter((b) => !b.isRemote), ...matching.filter((b) => b.isRemote)]

  return ordered.map((branch) => ({
    html: branchHtml(branch, refs!.branches.indexOf(branch)),
    primary: () => useBranch(branch),
  }))
}

function branchHtml(branch: Branch, id: number): string {
  const elsewhere = branch.isCheckedOutElsewhere
  const where = elsewhere ? branch.checkedOutIn!.replace(/[\\/]$/, '').split(/[\\/]/).pop() ?? '' : ''

  // Always an element, even when there is nothing to say. The row is a grid with fixed
  // columns, so an omitted cell does not leave a gap — it shifts every cell after it one
  // column left, and the subject and sha then land in a different place on each row.
  const badge = branch.isCurrent
    ? '<span class="refs-badge current">current</span>'
    : elsewhere
      ? `<span class="refs-badge elsewhere" title="${esc(branch.checkedOutIn!)}">in ${esc(where)}</span>`
      : branch.isRemote
        ? '<span class="refs-badge remote">remote</span>'
        : '<span class="refs-badge-none"></span>'

  // A branch open in another worktree cannot be switched to or deleted here, and both of
  // those are git's rules rather than ours — so the row offers the thing that does work.
  const actions = branch.isRemote
    ? ''
    : elsewhere
      ? `<button class="icon-btn" data-row="${id}" data-action="go"
                 title="Go to the worktree that has it open">${icons.external}</button>`
      : `<button class="icon-btn" data-row="${id}" data-action="upstream" title="Set upstream">${icons.branch}</button>
         <button class="icon-btn" data-row="${id}" data-action="rename" title="Rename">${icons.pencil}</button>
         ${
           branch.isCurrent
             ? ''
             : `<button class="icon-btn danger" data-row="${id}" data-action="delete"
                        title="Delete branch">${icons.discard}</button>`
         }`

  return `
    <span class="refs-icon">${icons.branch}</span>
    <span class="refs-name">${esc(branch.name)}</span>
    ${badge}
    ${trackingHtml(branch)}
    <span class="refs-meta">${esc(branch.subject)}</span>
    <span class="refs-sha">${esc(branch.shortSha)}</span>
    <span class="refs-actions">${actions}</span>`
}

/**
 * Shortens a path from the left, which is the half that repeats down the list — every
 * worktree in a repository shares most of its prefix, and the tail is what tells them apart.
 * The full path is on the row's title either way.
 */
function shortPath(path: string, max = 42): string {
  if (path.length <= max) return path

  const parts = path.split(/[\\/]/).filter(Boolean)
  const separator = path.includes('\\') ? '\\' : '/'

  let tail = parts.pop() ?? path
  while (parts.length > 0) {
    const next = `${parts[parts.length - 1]}${separator}${tail}`
    if (next.length + 1 > max) break
    tail = next
    parts.pop()
  }

  return `…${separator}${tail}`
}

function worktreeRows(query: string): Row[] {
  if (!refs) return []

  // Matched on path as well as branch: two agents on branches called `fix-1` and `fix-2`
  // are told apart by their name, but a worktree the user knows by its directory is not.
  return refs.worktrees
    .filter((w) => `${w.displayName} ${w.branch ?? ''} ${w.path}`.toLowerCase().includes(query))
    .map((worktree) => ({
      html: worktreeHtml(worktree, refs!.worktrees.indexOf(worktree)),
      worktree,
      primary: () => worktreePurpose === 'compare'
        ? compareWorktree(worktree)
        : useWorktree(worktree),
      accept: worktreePurpose === 'manage' && !worktree.isMain && worktree.isUsable && Boolean(worktree.branch)
        ? () => acceptWorktree(worktree)
        : undefined,
      reject: worktreePurpose === 'manage' && !worktree.isMain && worktree.isUsable && Boolean(worktree.branch)
        ? () => rejectWorktree(worktree)
        : undefined,
    }))
}

function sessionsFor(worktree: Worktree): AgentSession[] {
  if (!refs) return []

  // Git, the settings file and the session stores disagree on drive-letter casing and
  // trailing separators. Resolve the object key with the same path rule as the rest of
  // this panel instead of assuming a JSON object's spelling is stable.
  const entry = Object.entries(refs.agentSessions ?? {})
    .find(([path]) => samePath(path, worktree.path))
  return entry?.[1] ?? []
}

function sessionLabel(session: AgentSession): string {
  const provider = session.provider[0]!.toUpperCase() + session.provider.slice(1)
  return `${provider}${session.name ? ` — ${session.name}` : ''}`
}

function worktreeHtml(worktree: Worktree, id: number): string {
  const isCurrent = samePath(worktree.path, worktreePath ?? '')
  const sessions = sessionsFor(worktree)

  // Missing beats locked in the icon, because it is the one that says the row cannot be
  // used. Both are still spelled out in the badges.
  const icon = worktree.isPrunable || !worktree.isUsable ? icons.warning : icons.worktree

  const badge = isCurrent
    ? '<span class="refs-badge current">current</span>'
    : worktree.isMain
      ? '<span class="refs-badge">main</span>'
      : '<span class="refs-badge-none"></span>'

  const state = worktree.isPrunable
    ? `<span class="refs-badge missing" title="${esc(worktree.prunableReason ?? 'the directory is gone')}">missing</span>`
    : worktree.isLocked
      ? `<span class="refs-badge locked" title="${esc(worktree.lockReason ?? 'locked')}">locked</span>`
      : !worktree.isUsable
        ? '<span class="refs-badge missing">unavailable</span>'
        : sessions.length > 0
          ? `<span class="refs-badge session" title="${esc(sessionTitle(sessions))}">agent${sessions.length > 1 ? ` ×${sessions.length}` : ''}</span>`
          : '<span class="refs-badge-none"></span>'

  // The main worktree is the repository; git refuses to remove, move, lock or unlock it, so
  // the row offers none of those rather than four buttons that each fail the same way.
  if (worktreePurpose === 'compare') {
    const compareAction = !isCurrent && worktree.isUsable
      ? `<button class="icon-btn" data-row="${id}" data-action="compare-worktree"
                 title="Compare with this worktree">${icons.compare}</button>`
      : ''
    const sessionAction = sessions.length > 0
      ? `<button class="icon-btn" data-row="${id}" data-action="open-session"
                 title="Open the agent session log">${icons.external}</button>`
      : ''

    return `
      <span class="refs-icon">${icon}</span>
      <span class="refs-name">${esc(worktree.displayName)}</span>
      ${badge}
      ${state}
      <span class="refs-meta" title="${esc(worktree.path)}">${esc(shortPath(worktree.path))}</span>
      <span class="refs-sha">${esc(worktree.shortHead)}</span>
      <span class="refs-actions">${sessionAction}${compareAction}</span>`
  }

  const sessionAction = sessions.length > 0
    ? `<button class="icon-btn" data-row="${id}" data-action="open-session"
               title="Open the agent session log">${icons.external}</button>`
    : ''

  const actions = worktree.isMain
    ? isCurrent
      ? sessionAction
      : `${sessionAction}<button class="icon-btn" data-row="${id}" data-action="go-worktree"
                 title="Go to this worktree">${icons.external}</button>`
    : `${sessionAction}${
        isCurrent
          ? ''
          : `<button class="icon-btn" data-row="${id}" data-action="go-worktree"
                     title="Go to this worktree">${icons.external}</button>`
      }
       ${
         !isCurrent && worktree.isUsable
           ? `<button class="icon-btn" data-row="${id}" data-action="compare-worktree"
                      title="Compare with this worktree">${icons.compare}</button>`
           : ''
       }
        ${
          worktree.isUsable && worktree.branch
            ? `<button class="icon-btn accept-worktree" data-row="${id}" data-action="accept-worktree"
                       title="Accept ${esc(worktree.branch)} into main (A)">${icons.check}</button>`
            : ''
        }
        ${
          worktree.isUsable && worktree.branch
            ? `<button class="icon-btn danger reject-worktree" data-row="${id}" data-action="reject-worktree"
                       title="Reject and reset ${esc(worktree.branch)} (R)">${icons.reject}</button>`
            : ''
        }
       ${
         worktree.isLocked
           ? `<button class="icon-btn" data-row="${id}" data-action="unlock"
                      title="Unlock">${icons.unlock}</button>`
           : `<button class="icon-btn" data-row="${id}" data-action="lock"
                      title="Lock, so prune and move leave it alone">${icons.lock}</button>`
       }
       <button class="icon-btn" data-row="${id}" data-action="move" title="Move">${icons.move}</button>
       <button class="icon-btn danger" data-row="${id}" data-action="remove-worktree"
               title="Remove worktree">${icons.discard}</button>`

  return `
    <span class="refs-icon">${icon}</span>
    <span class="refs-name">${esc(worktree.displayName)}</span>
    ${badge}
    ${state}
    <span class="refs-meta" title="${esc(worktree.path)}">${esc(shortPath(worktree.path))}</span>
    <span class="refs-sha">${esc(worktree.shortHead)}</span>
    <span class="refs-actions">${actions}</span>`
}

function sessionTitle(sessions: AgentSession[]): string {
  return sessions.map(sessionLabel).join('\n')
}

/**
 * Compares two worktree paths.
 *
 * Case-insensitively and without a trailing separator, because the same worktree reaches the
 * front-end from three places — git's list, the rail's selection and the settings file — and
 * they do not agree on either. Getting this wrong shows a second "current" row, or offers to
 * remove the worktree the user is standing in without saying so.
 */
const samePath = (a: string, b: string): boolean =>
  a.replace(/[\\/]+$/, '').toLowerCase() === b.replace(/[\\/]+$/, '').toLowerCase()

function stashRows(query: string): Row[] {
  if (!refs) return []

  return refs.stashes
    .filter((s) => `${s.message} ${s.branch ?? ''}`.toLowerCase().includes(query))
    .map((stash) => ({
      html: stashHtml(stash),
      // Apply rather than pop: it is the half of the pair that keeps the entry, so a
      // restore that turns out to be wrong has cost nothing. Pop is one button away.
      primary: () => applyStash(stash),
    }))
}

function stashHtml(stash: Stash): string {
  // The branch is worth showing on every row because the stash is shared by every worktree
  // in the repository — so an entry an agent left in another one appears here too, and the
  // branch name is the only thing that says so.
  const from = stash.branch
    ? `<span class="refs-badge">${esc(stash.branch)}</span>`
    : '<span class="refs-badge-none"></span>'

  // Seven cells on every row in every section, so the columns line up down the list. The
  // empty one stands in for the branch rows' ahead/behind count.
  return `
    <span class="refs-icon">${icons.stash}</span>
    <span class="refs-name">${esc(stash.message || stash.selector)}</span>
    ${from}
    <span class="refs-badge-none"></span>
    <span class="refs-meta">${esc(stash.selector)}</span>
    <span class="refs-sha">${esc(stash.shortSha)}</span>
    <span class="refs-actions">
      <button class="icon-btn" data-row="${stash.index}" data-action="pop" title="Apply and remove">${icons.check}</button>
      <button class="icon-btn danger" data-row="${stash.index}" data-action="drop" title="Delete without applying">${icons.discard}</button>
    </span>`
}

function tagRows(query: string): Row[] {
  if (!refs) return []

  // indexOf against the unfiltered list, never the filtered one's position. Every action
  // resolves its row against `refs.tags`, so a filtered position would name a different tag
  // than the row that was clicked — filter to "v0", press the bin, and the tag above it is
  // the one deleted, under a dialog carrying its name.
  return refs.tags
    .filter((t) => t.name.toLowerCase().includes(query))
    .map((tag) => ({ html: tagHtml(tag, refs!.tags.indexOf(tag)) }))
}

function tagHtml(tag: Tag, id: number): string {
  return `
    <span class="refs-icon">${icons.tag}</span>
    <span class="refs-name">${esc(tag.name)}</span>
    ${
      tag.isAnnotated
        ? '<span class="refs-badge">annotated</span>'
        : '<span class="refs-badge-none"></span>'
    }
    <span class="refs-badge-none"></span>
    <span class="refs-meta">${esc(tag.subject)}</span>
    <span class="refs-sha">${esc(tag.shortSha)}</span>
    <span class="refs-actions">
      <button class="icon-btn danger" data-row="${id}" data-action="delete-tag"
              title="Delete tag">${icons.discard}</button>
      ${
        refs?.remotes.length
          ? `<button class="icon-btn" data-row="${id}" data-action="push-tag"
                    title="Push tag to a remote">${icons.upload}</button>`
          : ''
      }
    </span>`
}

function remoteRows(query: string): Row[] {
  if (!refs) return []

  return refs.remotes
    .filter((remote) => `${remote.name} ${remote.fetchUrl} ${remote.pushUrl}`.toLowerCase().includes(query))
    .map((remote) => ({
      html: remoteHtml(remote, refs!.remotes.indexOf(remote)),
      primary: () => startFetch(remote.name),
    }))
}

function pullRequestRows(query: string): Row[] {
  const matching = pullRequests.filter((pr) =>
    `${pr.number} ${pr.title} ${pr.state} ${pr.author} ${pr.headRefName} ${pr.baseRefName}`
      .toLowerCase()
      .includes(query),
  )

  return matching.map((pr) => ({
    html: pullRequestHtml(pr),
    primary: () => viewPullRequest(pr),
  }))
}

function pullRequestHtml(pr: PullRequest): string {
  const state = pr.isDraft ? 'draft' : pr.state.toLowerCase()
  const badgeClass = state === 'open' ? 'current' : state === 'merged' ? 'session' : 'locked'
  const branches = pr.headRefName && pr.baseRefName
    ? `${pr.headRefName} → ${pr.baseRefName}`
    : pr.headRefName || pr.baseRefName

  return `
    <span class="refs-icon">${icons.pullRequest}</span>
    <span class="refs-name" title="${esc(pr.title)}">#${pr.number} ${esc(pr.title)}</span>
    <span class="refs-badge ${badgeClass}">${esc(state)}</span>
    <span class="refs-badge-none"></span>
    <span class="refs-meta" title="${esc(branches)}">${esc(branches)}</span>
    <span class="refs-sha">${esc(pr.author || '')}</span>
    <span class="refs-actions">
      ${safePullRequestUrl(pr.url) ? `<a class="icon-btn" href="${esc(safePullRequestUrl(pr.url))}" target="_blank" rel="noreferrer" title="Open pull request">${icons.external}</a>` : ''}
      <button class="icon-btn" data-row="${pullRequests.indexOf(pr)}" data-action="checkout-pr"
              title="Check out pull request #${pr.number}">${icons.download}</button>
    </span>`
}

function safePullRequestUrl(value: string): string {
  return /^https:\/\/[^\s/]+\/(?:[^\s/]+\/){2,}pull\/\d+\/?$/i.test(value) ? value : ''
}

/** Avoid putting a token from an embedded URL into the visible overlay. */
function safeRemoteUrl(value: string): string {
  const scheme = value.indexOf('://')
  if (scheme >= 0) {
    const start = scheme + 3
    const rest = value.slice(start)
    const boundary = rest.search(/[\\/\s'"<>?]/)
    const end = boundary < 0 ? value.length : start + boundary
    const at = value.lastIndexOf('@', end - 1)
    if (at >= start) return `${value.slice(0, start)}***@${value.slice(at + 1)}`
  }

  // SCP-style URLs (`user:token@host:path`) have no scheme. Keep ordinary email addresses
  // intact by requiring both a credential colon and the path separator after the host.
  const at = value.lastIndexOf('@')
  const path = value.indexOf(':', at + 1)
  const userInfo = at > 0 ? value.slice(0, at) : ''
  if (at > 0 && path > at && userInfo.indexOf(':') > 0 && !/[\\/\s=]/.test(userInfo))
    return `***@${value.slice(at + 1)}`

  return value
}

function remoteHtml(remote: Remote, id: number): string {
  const fetchUrl = safeRemoteUrl(remote.fetchUrl)
  const pushUrl = safeRemoteUrl(remote.pushUrl)

  return `
    <span class="refs-icon">${icons.cloud}</span>
    <span class="refs-name" title="${esc(remote.name)}">${esc(remote.name)}</span>
    <span class="refs-badge remote">remote</span>
    <span class="refs-badge-none"></span>
    <span class="refs-meta refs-remote-url" title="fetch: ${esc(fetchUrl)}\npush: ${esc(pushUrl)}">
      ${esc(fetchUrl)}
    </span>
    <span class="refs-sha refs-remote-push" title="push: ${esc(pushUrl)}">${esc(pushUrl)}</span>
    <span class="refs-actions">
      <button class="icon-btn" data-row="${id}" data-action="fetch" title="Fetch ${esc(remote.name)}">${icons.download}</button>
      <button class="icon-btn" data-row="${id}" data-action="pull" title="Pull from ${esc(remote.name)}">${icons.pull}</button>
      <button class="icon-btn" data-row="${id}" data-action="push" title="Push to ${esc(remote.name)}">${icons.upload}</button>
      <button class="icon-btn" data-row="${id}" data-action="force-push" title="Force push with lease to ${esc(remote.name)}">${icons.uploadForce}</button>
      <button class="icon-btn" data-row="${id}" data-action="prune-remote" title="Prune stale ${esc(remote.name)} branches">${icons.refresh}</button>
      <button class="icon-btn" data-row="${id}" data-action="rename-remote" title="Rename ${esc(remote.name)}">${icons.pencil}</button>
      <button class="icon-btn danger" data-row="${id}" data-action="remove-remote" title="Remove ${esc(remote.name)}">${icons.discard}</button>
    </span>`
}

function renderRemoteProgress(): void {
  if (!progress) return

  if (!remoteOperation || !samePath(remoteOperation.worktreePath, worktreePath ?? '')) {
    progress.hidden = true
    progress.innerHTML = ''
    return
  }

  const operation = remoteOperation.operation === 'forcePush' ? 'Force pushing' :
    remoteOperation.operation === 'pushTag' ? 'Pushing tag' :
      remoteOperation.operation[0]!.toUpperCase() + remoteOperation.operation.slice(1)
  const running = remoteOperation.state === 'running'
  const percent = remoteOperation.percent == null ? '' :
    `<div class="refs-progress-bar"><span style="width:${Math.max(0, Math.min(100, remoteOperation.percent))}%"></span></div>`
  const action = running && remoteOperation.id
    ? `<button class="btn small" data-foot="cancel-remote">${icons.stop}<span>Cancel</span></button>`
    : ''

  progress.hidden = false
  progress.innerHTML = `
    <div class="refs-progress-head">
      <span class="refs-progress-title">${esc(operation)}</span>
      <span class="refs-progress-phase">${esc(remoteOperation.phase || 'working')}</span>
      ${action}
    </div>
    ${percent}
    <div class="refs-progress-message" title="${esc(remoteOperation.message)}">${esc(remoteOperation.message || 'Waiting for git…')}</div>`
}

function handleRemoteProgress(next: RemoteProgress): void {
  cacheRemoteEvent(remoteProgressById, next)
  if (!remoteOperation || remoteOperation.id !== next.id) return

  remoteOperation = next
  syncRemoteBusy()
  renderRemoteProgress()
}

function handleRemoteFinished(next: RemoteProgress): void {
  cacheRemoteEvent(remoteFinishedById, next)
  remoteProgressById.delete(next.id)
  for (const [path, started] of remoteStartedByWorktree) {
    if (started.id === next.id) remoteStartedByWorktree.delete(path)
  }

  if (!remoteOperation || remoteOperation.id !== next.id) {
    // The panel may have been closed while the transfer ran. Still report a result for the
    // worktree the user was looking at, but do not reopen a modal surface unexpectedly.
    if (!remoteStarting && samePath(next.worktreePath, worktreePath ?? '') && next.result) {
      onToast(
        next.state === 'completed' ? next.result.message : `${next.operation} ${next.state}`,
        next.state === 'completed' ? undefined : next.result.message,
        next.state === 'failed' ? 'error' : 'info',
      )
    }
    return
  }

  remoteOperation = next
  syncRemoteBusy()
  renderRemoteProgress()

  const result = next.result
  if (result) {
    if (next.state === 'completed') onToast(result.message)
    else if (next.state === 'cancelled') onToast(`${next.operation} cancelled`, result.message)
    else onToast(`Could not ${next.operation}`, result.message, 'error')
  }

  // Let the terminal state be visible for one paint, then return the footer to its normal
  // controls. The backend also sends filesChanged/worktreesChanged, but this refresh catches
  // remotes and branch tracking immediately when the overlay is still open.
  const id = next.id
  window.setTimeout(() => {
    if (remoteOperation?.id !== id) return
    remoteOperation = null
    remoteStartedByWorktree.delete(next.worktreePath)
    syncRemoteBusy()
    render()
    void refresh().then(() => onMutated())
  }, 350)
}

function syncRemoteBusy(): void {
  const active =
    busy ||
    remoteStarting ||
    (remoteOperation?.state === 'running' && samePath(remoteOperation.worktreePath, worktreePath ?? ''))
  overlay?.classList.toggle('busy', active)
}

/* ==========================================================================
   Keyboard
   ========================================================================== */

/**
 * The row and footer buttons, in the order the eye reads them.
 *
 * Every action in this panel is a real button already — they were simply unreachable, since
 * `Tab` is spent on the sections and the filter holds focus the rest of the time. Roving
 * focus along the strip gives all of them a keyboard path without inventing a mnemonic per
 * action, and it keeps working when a section gains one.
 */
function actionStrip(): HTMLElement[] {
  const row = list.querySelector<HTMLElement>('.refs-row.selected')
  const inRow = row
    ? [...row.querySelectorAll<HTMLElement>('.refs-actions button, .refs-actions a')]
    : []
  return [...inRow, ...footer.querySelectorAll<HTMLElement>('button')]
}

/**
 * What a focused control is, in terms that survive the list being rebuilt.
 *
 * Named by action rather than by position: a worktree row swaps Lock for Unlock, and the
 * footer gains and loses the prune button, so restoring "the third button" after a render
 * would hand the keyboard a different action than the one it was on.
 */
type ActionHold = { strip: 'row' | 'foot'; name: string }

function heldAction(element: Element | null): ActionHold | null {
  if (!(element instanceof HTMLElement)) return null

  const row = element.closest<HTMLElement>('.refs-actions button, .refs-actions a')
  if (row && list.contains(row))
    return { strip: 'row', name: row.dataset.action ?? 'link' }

  const foot = element.closest<HTMLElement>('[data-foot]')
  if (foot && footer.contains(foot)) return { strip: 'foot', name: foot.dataset.foot! }

  return null
}

/** Puts focus back on the same action after a render, or gives up gracefully. */
function restoreAction(hold: ActionHold): boolean {
  const selector = hold.strip === 'foot'
    ? `[data-foot="${hold.name}"]`
    : hold.name === 'link'
      ? '.refs-row.selected .refs-actions a'
      : `.refs-row.selected .refs-actions [data-action="${hold.name}"]`

  const root = hold.strip === 'foot' ? footer : list
  const match = root.querySelector<HTMLElement>(selector)
  if (match) {
    match.focus()
    return true
  }

  // The row moved to one without this action — ↓ from Unlock onto a worktree that is not
  // locked. Staying on the strip is what the user asked for; which button is a detail.
  if (hold.strip === 'row') {
    const first = actionStrip()[0]
    if (first) {
      first.focus()
      return true
    }
  }
  return false
}

/** Moves along the strip. Falling off either end is how you get back to the filter. */
function moveAction(delta: number): void {
  const strip = actionStrip()
  if (strip.length === 0) return

  const current = strip.indexOf(document.activeElement as HTMLElement)
  const next = current < 0 ? (delta > 0 ? 0 : strip.length - 1) : current + delta

  if (next < 0 || next >= strip.length) {
    focusFilter()
    return
  }
  strip[next]!.focus()
}

/** The filter is home, and the caret goes to the end so `←` does not immediately leave again. */
function focusFilter(): void {
  filter.focus()
  const end = filter.value.length
  filter.setSelectionRange(end, end)
}

function onKey(event: KeyboardEvent): void {
  const target = event.target instanceof HTMLElement ? event.target : null
  const typing = Boolean(target?.closest('input, textarea, [contenteditable="true"]'))
  const onAction = heldAction(target) !== null

  // A focused button activates itself. The panel's own Enter runs the *row's* primary
  // action, which is not what somebody who navigated onto Delete is asking for — and was
  // already wrong for a button reached by clicking it.
  if (onAction && (event.key === 'Enter' || event.key === ' ')) return

  if (
    (event.key === 'ArrowRight' || event.key === 'ArrowLeft') &&
    !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey
  ) {
    const forward = event.key === 'ArrowRight'

    if (onAction) {
      event.preventDefault()
      moveAction(forward ? 1 : -1)
      return
    }

    // From the filter, only at the end of the text: everywhere else this is caret movement,
    // and a filter you cannot move the caret in is worse than actions you cannot reach.
    if (
      target === filter &&
      forward &&
      filter.selectionStart === filter.value.length &&
      filter.selectionEnd === filter.value.length
    ) {
      event.preventDefault()
      moveAction(1)
      return
    }
  }

  // `A` is contextual: it works only in the worktrees management section and never steals
  // a character from the filter or one of the inline prompts.
  if (
    section === 'worktrees' &&
    worktreePurpose === 'manage' &&
    !typing &&
    !target?.closest('.refs-prompt') &&
    !event.ctrlKey &&
    !event.metaKey &&
    !event.altKey &&
    !event.shiftKey &&
    event.key.toLowerCase() === 'a'
  ) {
    const accept = rows[selected]?.accept
    if (accept) {
      event.preventDefault()
      void accept()
    }
    return
  }

  // `L` opens the newest matching agent log for the selected worktree. It is contextual
  // for the same reason as accept/reject: while typing a filter or prompt it must remain a
  // literal character, and outside the worktree section it has unrelated meanings.
  if (
    section === 'worktrees' &&
    !typing &&
    !target?.closest('.refs-prompt') &&
    !event.ctrlKey &&
    !event.metaKey &&
    !event.altKey &&
    !event.shiftKey &&
    event.key.toLowerCase() === 'l'
  ) {
    const selectedWorktree = rows[selected]?.worktree
    if (selectedWorktree && sessionsFor(selectedWorktree).length > 0) {
      event.preventDefault()
      void openSession(selectedWorktree)
    }
    return
  }

  // `R` is the destructive counterpart to `A`, kept contextual so the normal refresh
  // shortcut elsewhere in the app remains untouched.
  if (
    section === 'worktrees' &&
    worktreePurpose === 'manage' &&
    !typing &&
    !target?.closest('.refs-prompt') &&
    !event.ctrlKey &&
    !event.metaKey &&
    !event.altKey &&
    !event.shiftKey &&
    event.key.toLowerCase() === 'r'
  ) {
    const reject = rows[selected]?.reject
    if (reject) {
      event.preventDefault()
      void reject()
    }
    return
  }

  switch (event.key) {
    case 'Escape':
      event.preventDefault()
      close()
      return

    case 'ArrowDown':
      event.preventDefault()
      move(1)
      return

    case 'ArrowUp':
      event.preventDefault()
      move(-1)
      return

    case 'Enter':
      event.preventDefault()
      void usePrimary()
      return

    case 'Tab': {
      // Cycles the sections rather than moving focus. Focus has nowhere useful to go
      // inside this overlay — the filter is the only text field, and the rows are driven by
      // the arrows.
      event.preventDefault()
      const index = SECTIONS.findIndex((s) => s.id === section)
      const next = SECTIONS[(index + (event.shiftKey ? -1 : 1) + SECTIONS.length) % SECTIONS.length]!
      setSection(next.id)
      return
    }
  }
}

/**
 * Shows a complete, permanent preview before moving an agent branch back to its merge base.
 * The snapshot values are sent back to the backend so a late agent write turns into a refusal,
 * never an accidental reset of newer work.
 */
async function rejectWorktree(worktree: Worktree): Promise<void> {
  if (!refs || !worktreePath || busy || remoteStarting || remoteOperation) return
  if (worktree.isMain || !worktree.isUsable || !worktree.branch) return

  let preview: RejectWorkPreviewPayload
  try {
    preview = await call('previewRejectWorktree', {
      worktreePath,
      target: worktree.path,
    })
  } catch (error) {
    onToast('Could not preview this rejection', message(error), 'error')
    return
  }

  if (!preview.ok) {
    onToast('Cannot reject this worktree', preview.message, 'error')
    return
  }

  const changed = preview.paths.map((path) => {
    const suffix = path.oldPath ? ` (renamed from ${path.oldPath})` : ''
    return `${path.path}${suffix}`
  })
  const ignored = preview.ignoredPaths.map((path) => `ignored: ${path}`)
  const detail = [
    `${preview.sourceBranch} → ${preview.baseBranch}`,
    `${preview.commitCount} commit(s) to discard`,
    ...changed,
    ...ignored,
  ]

  const ok = await confirm({
    title: `Reject and reset ${preview.sourceBranch}?`,
    body:
      'The branch is moved to its merge base and every listed changed, untracked, and ignored path is permanently deleted. ' +
      'The committed tip can be restored with Undo; discarded files cannot.',
    confirmLabel: 'Reject and reset',
    recovery: 'permanent',
    detail,
  })
  if (!ok) return

  busy = true
  syncRemoteBusy()
  try {
    const result = await call('rejectWorktree', {
      worktreePath,
      target: worktree.path,
      expectedSourceHead: preview.sourceHead,
      expectedBaseHead: preview.baseHead,
      expectedSnapshotFingerprint: preview.snapshotFingerprint,
    })

    if (result.ok) onToast(result.message)
    else onToast('Rejection was refused', result.message, 'error')

    await onRejectWorktree(result)
    await refresh()
  } catch (error) {
    onToast('Could not reject this worktree', message(error), 'error')
  } finally {
    busy = false
    syncRemoteBusy()
  }
}

function move(delta: number): void {
  if (rows.length === 0) return

  selected = (selected + delta + rows.length) % rows.length
  render()
  list.querySelector('.refs-row.selected')?.scrollIntoView({ block: 'nearest' })
}

function usePrimary(): void | Promise<void> {
  return rows[selected]?.primary?.()
}

type RemoteStart = () => Promise<RemoteOperationStarted>

/** Starts a detached fetch/pull/push and keeps the cancel affordance alive until it ends. */
async function startRemote(start: RemoteStart): Promise<void> {
  if (!refs || !worktreePath || busy || remoteStarting || remoteOperation) return

  const path = worktreePath
  remoteStarting = true
  remoteOperation = {
    id: '',
    worktreePath: path,
    operation: 'remote',
    state: 'running',
    phase: 'starting',
    message: 'Starting…',
  }
  syncRemoteBusy()
  renderRemoteProgress()

  try {
    const started = await start()
    remoteStartedByWorktree.set(path, started)
    const finished = remoteFinishedById.get(started.id)
    remoteOperation = finished ?? remoteProgressById.get(started.id) ?? {
      id: started.id,
      worktreePath: started.worktreePath,
      operation: started.operation,
      state: 'running',
      phase: 'starting',
      message: 'Starting…',
    }

    remoteStarting = false
    syncRemoteBusy()
    renderRemoteProgress()
    if (finished) handleRemoteFinished(finished)
  } catch (error) {
    onToast('Could not start remote operation', message(error), 'error')
    remoteOperation = null
    remoteStarting = false
    syncRemoteBusy()
    renderRemoteProgress()
  }
}

async function startFetch(remote = '', prune = false, all = false): Promise<void> {
  await startRemote(() => call('fetch', { worktreePath: worktreePath!, remote, prune, all }))
}

function currentBranch(): Branch | null {
  if (!refs?.current) return null
  return refs.branches.find((branch) => !branch.isRemote && branch.name === refs!.current) ?? null
}

function upstreamParts(branch: Branch | null): { remote: string; branch: string } | null {
  if (!branch?.upstream) return null
  const slash = branch.upstream.indexOf('/')
  if (slash <= 0 || slash === branch.upstream.length - 1) return null
  return { remote: branch.upstream.slice(0, slash), branch: branch.upstream.slice(slash + 1) }
}

async function pullRemote(remoteName: string): Promise<void> {
  const branch = currentBranch()
  if (!branch) {
    onToast('Cannot pull from detached HEAD', 'Check out a branch first.', 'error')
    return
  }

  const upstream = upstreamParts(branch)
  const branchName = upstream?.remote === remoteName ? upstream.branch : ''
  const strategyInput = await prompt(
    `Pull ${branch.name} from ${remoteName} using (merge, rebase, or ff-only)`,
    'merge',
    ['merge', 'rebase', 'ff-only'],
  )
  if (!strategyInput) return

  const normal = strategyInput.toLowerCase()
  const strategy: PullStrategy = normal === 'rebase'
    ? 'rebase'
    : normal === 'ff-only' || normal === 'fast-forward-only'
      ? 'ff-only'
      : 'merge'

  await startRemote(() =>
    call('pull', { worktreePath: worktreePath!, remote: remoteName, branch: branchName, strategy }),
  )
}

async function pushRemote(remoteName: string, forceWithLease: boolean): Promise<void> {
  const branch = currentBranch()
  if (!branch) {
    onToast('Cannot push detached HEAD', 'Check out a branch first.', 'error')
    return
  }

  if (forceWithLease) {
    const detail = await previewPush(remoteName, branch.name)

    const ok = await confirm({
      title: `Force-push ${branch.name} to ${remoteName}?`,
      body:
        'This uses --force-with-lease and may replace the remote branch history if it has not ' +
        'changed since your last fetch. Anyone who based work on that history may need to recover it.',
      confirmLabel: 'Force push with lease',
      recovery: 'remote',
      detail,
    })
    if (!ok) return
  }

  const upstream = upstreamParts(branch)
  await startRemote(() =>
    call('push', {
      worktreePath: worktreePath!,
      remote: remoteName,
      branch: branch.name,
      forceWithLease,
      setUpstream: upstream == null,
    }),
  )
}

async function pushTag(tag: Tag): Promise<void> {
  if (!refs || refs.remotes.length === 0) {
    onToast('No remote configured', 'Add a remote before pushing a tag.', 'error')
    return
  }

  const remote = await prompt(
    'Which remote should receive this tag?',
    refs.remotes[0]!.name,
    refs.remotes.map((entry) => entry.name),
  )
  if (!remote) return

  await startRemote(() => call('pushTag', { worktreePath: worktreePath!, remote, tag: tag.name }))
}

/**
 * Asks the server what a force push would replace, and says so in the dialog.
 *
 * The dry run is the whole point: `--force-with-lease` is decided against the remote's
 * current tip, which is a fact only the remote has. A preview computed from local tracking
 * refs would be confident and stale in exactly the case the lease exists to catch.
 */
async function previewPush(remoteName: string, branchName: string): Promise<string[]> {
  try {
    const preview = await call('previewPush', {
      worktreePath: worktreePath!,
      remote: remoteName,
      branch: branchName,
      forceWithLease: true,
    })

    if (!preview.ok)
      return [`${remoteName}/${branchName}`, `Could not preview: ${preview.message}`]

    const lines: string[] = []
    for (const update of preview.updates) {
      if (update.isRejected) {
        lines.push(`${update.toRef}: refused — ${update.summary || 'the remote moved since your last fetch'}`)
        continue
      }

      if (update.isDeleted) {
        lines.push(`${update.toRef}: deleted from the remote`)
        continue
      }

      lines.push(`${update.toRef}: ${update.summary || 'updated'}`)
      if (!update.isForced) continue

      if (update.droppedUnknown) {
        lines.push(`The remote's current tip ${update.oldSha} is not in this repository — fetch to see what it holds.`)
        continue
      }

      if (update.dropped.length === 0) lines.push('No commits are removed from the remote.')
      else lines.push(`${update.dropped.length} commit(s) the remote would no longer have:`, ...update.dropped)
    }

    return lines.length > 0 ? lines : [`${remoteName}/${branchName}`]
  } catch (error) {
    // A preview that cannot run is not a reason to block the push: say so and let the
    // ordinary confirmation stand on its own words.
    return [`${remoteName}/${branchName}`, `Could not preview: ${message(error)}`]
  }
}

async function pruneRemote(remoteName: string): Promise<void> {
  let detail = [remoteName]
  try {
    const preview = await call('previewPruneRemote', { worktreePath: worktreePath!, name: remoteName })
    if (!preview.ok) detail = [remoteName, `Could not preview: ${preview.message}`]
    else if (preview.refs.length === 0) detail = [remoteName, 'Nothing on the server has gone: no refs would be pruned.']
    else detail = [`${preview.refs.length} tracking ref(s) would be removed:`, ...preview.refs]
  } catch (error) {
    detail = [remoteName, `Could not preview: ${message(error)}`]
  }

  const ok = await confirm({
    title: `Prune stale branches from ${remoteName}?`,
    body:
      'Remote-tracking branches that no longer exist on the server are removed locally. ' +
      'This does not delete branches on the remote.',
    confirmLabel: 'Prune',
    recovery: 'local',
    detail,
  })
  if (ok) await run(() => call('pruneRemote', { worktreePath: worktreePath!, name: remoteName }))
}

async function removeRemote(remote: Remote): Promise<void> {
  const ok = await confirm({
    title: `Remove remote ${remote.name}?`,
    body:
      'The local configuration and remote-tracking refs are removed. Nothing is deleted from the server.',
    confirmLabel: 'Remove remote',
    recovery: 'local',
    detail: [remote.name, safeRemoteUrl(remote.fetchUrl)],
  })
  if (ok) await run(() => call('removeRemote', { worktreePath: worktreePath!, name: remote.name }))
}

async function viewPullRequest(pr: PullRequest): Promise<void> {
  if (!worktreePath) return
  try {
    const result = await call('viewPullRequest', { worktreePath, selector: String(pr.number) })
    if (!result.success) {
      onToast('Could not view pull request', result.message, 'error')
      return
    }

    const detail = result.pullRequest ?? pr
    const body = detail.body ? `\n\n${detail.body.slice(0, 1800)}` : ''
    onToast(`#${detail.number} ${detail.title}`, `${detail.url}${body}`)
  } catch (error) {
    onToast('Could not view pull request', message(error), 'error')
  }
}

async function checkoutPullRequest(pr: PullRequest): Promise<void> {
  if (!worktreePath) return
  const ok = await confirm({
    title: `Check out pull request #${pr.number}?`,
    body:
      `GitHub CLI will fetch ${pr.headRefName || `PR #${pr.number}`} and create or switch the local branch. ` +
      'Any uncommitted work that conflicts with the checkout stays protected by git.',
    confirmLabel: 'Check out PR',
    recovery: 'undoable',
    detail: [pr.title, pr.url],
  })
  if (!ok) return

  const result = await run(() => call('checkoutPullRequest', {
    worktreePath: worktreePath!,
    selector: String(pr.number),
  }))
  if (result?.ok) {
    await onMutated()
    await refresh()
  }
}

async function createPullRequest(): Promise<void> {
  if (!worktreePath || busy || remoteStarting || remoteOperation) return
  const title = await prompt('Pull-request title', '')
  if (!title) return
  const body = await prompt('Pull-request body (optional)', '')
  if (body == null) return

  const base = await prompt('Base branch (optional)', currentBranch()?.name ?? 'main')
  if (base == null) return
  const draftInput = await prompt('Create as draft? (yes or no)', 'no', ['yes', 'no'])
  if (draftInput == null) return

  busy = true
  syncRemoteBusy()
  try {
    const result = await call('createPullRequest', {
      worktreePath,
      title,
      body,
      baseBranch: base,
      draft: /^(y|yes|true)$/i.test(draftInput.trim()),
    })
    if (result.success) {
      onToast('Pull request created', result.pullRequest?.url || result.url || result.message)
      await refreshPullRequests()
      render()
    } else {
      onToast('Could not create pull request', result.message, 'error')
    }
  } catch (error) {
    onToast('Could not create pull request', message(error), 'error')
  } finally {
    busy = false
    syncRemoteBusy()
  }
}

/* ==========================================================================
   Actions
   ========================================================================== */

/**
 * Runs a mutation, reports it, and refreshes both this panel and the window behind it.
 *
 * `busy` is not cosmetic. Every action here re-reads the list afterwards and the row
 * indices come from that list, so a second action started before the first has repainted
 * would be aimed at positions that are already stale — which for the stash is precisely the
 * mistake the sha check exists to catch.
 */
async function run(
  action: () => Promise<MutationPayload>,
  /**
   * Failures the caller is about to turn into a question, so they are not also reported as
   * errors. A red toast saying "your changes would be overwritten" beside a dialog offering
   * to stash them reads as two separate events, and the alarming one arrives first.
   */
  expected: MutationPayload['failure'][] = [],
  /**
   * A worktree this action destroys or moves, when it succeeds.
   *
   * Handled here rather than at the call site because the order matters and there is only
   * one right one: the window has to be told the path is gone *before* anything refreshes
   * against it. And when the vanished worktree is the one this panel was opened on, there is
   * nothing left to re-read — the panel closes instead, because `getRefs` against a deleted
   * directory can only produce an error message about the thing the user just asked for.
   */
  gone?: { path: string },
): Promise<MutationPayload | null> {
  if (busy || remoteStarting || remoteOperation) return null
  busy = true
  syncRemoteBusy()

  try {
    const result = await action()

    if (result.ok) onToast(result.message)
    else if (!expected.includes(result.failure))
      onToast(result.message, result.commandLine || undefined, 'error')

    if (result.ok && gone) {
      const wasThisPanel = samePath(gone.path, worktreePath ?? '')

      if (wasThisPanel) close()
      await onWorktreeGone(gone.path)

      // The handler above re-reads whatever the window moved to, which is everything
      // `onMutated` would have done and more.
      if (wasThisPanel) return result
    }

    await refresh()
    await onMutated()

    return result
  } catch (error) {
    onToast(message(error), undefined, 'error')
    return null
  } finally {
    busy = false
    syncRemoteBusy()
  }
}

/** Enter on a branch row. */
async function useBranch(branch: Branch): Promise<void> {
  if (branch.isCurrent) return

  // The affordance the whole `checkedOutIn` field exists for. Git would refuse the switch,
  // and the thing the user actually wants is one worktree away.
  if (branch.isCheckedOutElsewhere) {
    close()
    await onGoToWorktree(branch.checkedOutIn!)
    return
  }

  await switchTo(branch.name)
}

/**
 * Switches, and offers to stash when git refuses because the tree would be overwritten.
 *
 * Attempted rather than pre-checked, deliberately: git carries uncommitted changes across
 * whenever no file differs between the two branches, which is the common case. Asking
 * "stash first?" whenever the tree is dirty would put a dialog in front of a switch that
 * was going to work.
 */
async function switchTo(branch: string): Promise<void> {
  // Neither of these is reported by `run`, because both are answered below in words chosen
  // for them: one becomes the stash question, the other a single sentence. Left in, each
  // would arrive first as a red toast carrying git's raw stderr — so the alarming version
  // of the news would beat the useful one to the screen.
  const result = await run(
    () => call('switchBranch', { worktreePath: worktreePath!, branch }),
    ['wouldLoseChanges', 'checkedOutElsewhere'],
  )

  if (!result || result.ok) return

  if (result.failure === 'checkedOutElsewhere') {
    // The list was stale — something checked it out between the read and the click.
    onToast('That branch is now open in another worktree.', undefined, 'error')
    return
  }

  if (result.failure !== 'wouldLoseChanges') return

  const ok = await confirm({
    title: `Stash your changes and switch to ${branch}?`,
    body:
      'Some uncommitted changes conflict with that branch, so git will not carry them over. ' +
      'They can be stashed, restored on the other side of the switch, and the stash removed.',
    confirmLabel: 'Stash and switch',
    // Nothing is thrown away: the work moves through a stash, and if the restore does not
    // apply cleanly the stash is kept and the message says so.
    recovery: 'undoable',
  })

  if (!ok) return

  await run(() =>
    call('switchBranch', { worktreePath: worktreePath!, branch, strategy: 'stashAndSwitch' }),
  )
}

/**
 * Runs a row's secondary action.
 *
 * `id` is always an index into the **unfiltered** list the row came from — `refs.branches`,
 * `refs.tags`, or a stash's own `index`. Filtering changes what is on screen and never what
 * a row is called, because everything here resolves against those arrays: a filtered
 * position would silently name a different ref than the row the user clicked, which for the
 * delete actions means destroying the wrong one under a dialog carrying the right one's name.
 */
async function runRowAction(id: number, action: string): Promise<void> {
  if (!refs || !worktreePath) return

  switch (action) {
    case 'go': {
      const branch = refs.branches[id]
      if (!branch?.checkedOutIn) return
      close()
      await onGoToWorktree(branch.checkedOutIn)
      return
    }

    case 'rename': {
      const branch = refs.branches[id]
      if (!branch) return

      const to = await prompt(`Rename ${branch.name} to`, branch.name)
      if (to == null || to === branch.name) return

      await run(() => call('renameBranch', { worktreePath: worktreePath!, from: branch.name, to }))
      return
    }

    case 'upstream': {
      const branch = refs.branches[id]
      if (!branch) return

      const upstream = await prompt(
        `Track which remote branch with ${branch.name}? Leave empty to stop tracking`,
        branch.upstream ?? '',
      )

      if (upstream == null) return
      await run(() => call('setUpstream', { worktreePath: worktreePath!, branch: branch.name, upstream }))
      return
    }

    case 'delete': {
      const branch = refs.branches[id]
      if (branch) await deleteBranch(branch)
      return
    }

    case 'pop': {
      const stash = refs.stashes.find((s) => s.index === id)
      if (stash) await run(() => call('stashPop', entry(stash)))
      return
    }

    case 'drop': {
      const stash = refs.stashes.find((s) => s.index === id)
      if (stash) await dropStash(stash)
      return
    }

    case 'delete-tag': {
      const tag = refs.tags[id]
      if (tag) await deleteTag(tag)
      return
    }

    case 'push-tag': {
      const tag = refs.tags[id]
      if (tag) await pushTag(tag)
      return
    }

    case 'fetch': {
      const remote = refs.remotes[id]
      if (remote) await startFetch(remote.name)
      return
    }

    case 'pull': {
      const remote = refs.remotes[id]
      if (remote) await pullRemote(remote.name)
      return
    }

    case 'push': {
      const remote = refs.remotes[id]
      if (remote) await pushRemote(remote.name, false)
      return
    }

    case 'force-push': {
      const remote = refs.remotes[id]
      if (remote) await pushRemote(remote.name, true)
      return
    }

    case 'prune-remote': {
      const remote = refs.remotes[id]
      if (remote) await pruneRemote(remote.name)
      return
    }

    case 'rename-remote': {
      const remote = refs.remotes[id]
      if (!remote) return

      const to = await prompt(`Rename ${remote.name} to`, remote.name)
      if (to == null || to === remote.name) return
      await run(() => call('renameRemote', { worktreePath: worktreePath!, from: remote.name, to }))
      return
    }

    case 'remove-remote': {
      const remote = refs.remotes[id]
      if (remote) await removeRemote(remote)
      return
    }

    case 'checkout-pr': {
      const pr = pullRequests[id]
      if (pr) await checkoutPullRequest(pr)
      return
    }

    case 'go-worktree': {
      const worktree = refs.worktrees[id]
      if (worktree) await useWorktree(worktree)
      return
    }

    case 'compare-worktree': {
      const worktree = refs.worktrees[id]
      if (!worktree || !worktree.isUsable || samePath(worktree.path, worktreePath)) return
      close()
      await onCompareWorktree(worktree.path)
      return
    }

    case 'open-session': {
      const worktree = refs.worktrees[id]
      if (worktree) await openSession(worktree)
      return
    }

    case 'accept-worktree': {
      const worktree = refs.worktrees[id]
      if (worktree) await acceptWorktree(worktree)
      return
    }

    case 'lock': {
      const worktree = refs.worktrees[id]
      if (!worktree) return

      const reason = await prompt(`Why is ${worktree.displayName} locked? (optional)`, '')
      if (reason == null) return

      await run(() => call('lockWorktree', { worktreePath: worktreePath!, target: worktree.path, reason }))
      return
    }

    case 'unlock': {
      const worktree = refs.worktrees[id]
      if (worktree)
        await run(() => call('unlockWorktree', { worktreePath: worktreePath!, target: worktree.path }))
      return
    }

    case 'move': {
      const worktree = refs.worktrees[id]
      if (worktree) await moveWorktree(worktree)
      return
    }

    case 'remove-worktree': {
      const worktree = refs.worktrees[id]
      if (worktree) await removeWorktree(worktree)
      return
    }
  }
}

/** Enter on a worktree row: go there. Everything else about it is a button. */
async function useWorktree(worktree: Worktree): Promise<void> {
  if (samePath(worktree.path, worktreePath ?? '')) return

  if (!worktree.isUsable) {
    onToast(
      `${worktree.displayName} has no working directory`,
      worktree.prunableReason ?? 'The directory it named is gone.',
      'error',
    )
    return
  }

  close()
  await onGoToWorktree(worktree.path)
}

/** Opens the newest high-confidence session matched to a worktree. */
async function openSession(worktree: Worktree): Promise<void> {
  if (!worktreePath || !worktree.isUsable) return

  try {
    // Refresh before opening: a session can finish, be compacted, or be deleted while the
    // refs overlay stays open. The backend resolves the id again and never trusts a path from
    // this response as an arbitrary shell target.
    const payload = await call('getAgentSessions', { worktreePath: worktree.path })
    const session = payload.sessions[0]
    if (!session) {
      onToast('No agent session found', 'No local session log matches this worktree.')
      return
    }

    const opened = await call('openAgentSession', {
      worktreePath: worktree.path,
      provider: session.provider,
      sessionId: session.id,
    })

    if (opened.success) {
      onToast(`Opened ${sessionLabel(session)}`)
    } else {
      onToast('Could not open the agent session', opened.detail, 'error')
    }
  } catch (error) {
    onToast('Could not read the agent session', message(error), 'error')
  }
}

async function compareWorktree(worktree: Worktree): Promise<void> {
  if (samePath(worktree.path, worktreePath ?? '')) return
  if (!worktree.isUsable) {
    onToast(
      `${worktree.displayName} has no working directory`,
      worktree.prunableReason ?? 'The directory it named is gone.',
      'error',
    )
    return
  }

  close()
  await onCompareWorktree(worktree.path)
}

/**
 * Brings a clean agent branch into the repository's main worktree.
 *
 * The two confirmations are intentionally separate. Integrating is recoverable through the
 * undo stack; removing the source directory is not, and may delete ignored build output.
 * Asking the latter only after the strategy is known keeps the irreversible choice visible
 * without making the ordinary merge confirmation sound more dangerous than it is.
 */
async function acceptWorktree(worktree: Worktree): Promise<void> {
  if (!refs || !worktreePath || busy || remoteStarting || remoteOperation) return

  if (worktree.isMain || samePath(worktree.path, worktreePath)) {
    // The active linked worktree is a valid source. The main worktree is the one exception:
    // accepting it into itself has no useful meaning and the backend rejects it too.
    if (worktree.isMain) {
      onToast('The main worktree is already the target', 'Choose a linked agent worktree to accept.', 'error')
      return
    }
  }

  if (!worktree.isUsable) {
    onToast(
      `${worktree.displayName} has no working directory`,
      worktree.prunableReason ?? 'The directory it named is gone.',
      'error',
    )
    return
  }

  if (!worktree.branch) {
    onToast('Detached worktrees cannot be accepted', 'Check out a branch in that worktree first.', 'error')
    return
  }

  const strategyInput = await prompt(
    `Accept ${worktree.branch} into main using (merge or cherry-pick)`,
    'merge',
    ['merge', 'cherry-pick'],
  )
  if (strategyInput == null) return

  const normal = strategyInput.trim().toLowerCase()
  const strategy = normal === 'cherry-pick' || normal === 'cherrypick' || normal === 'cherry_pick'
    ? 'cherryPick'
    : normal === 'merge'
      ? 'merge'
      : null
  if (!strategy) {
    onToast('Choose merge or cherry-pick', 'The branch was not changed.', 'error')
    return
  }

  const removeInput = await prompt(
    `Remove ${worktree.displayName} after accepting? (yes or no)`,
    'no',
    ['yes', 'no'],
  )
  if (removeInput == null) return
  const removeChoice = removeInput.trim().toLowerCase()
  if (removeChoice !== 'yes' && removeChoice !== 'y' && removeChoice !== 'no' && removeChoice !== 'n') {
    onToast('Choose yes or no', 'The branch was not changed.', 'error')
    return
  }
  const removeAfter = removeChoice === 'yes' || removeChoice === 'y'

  const integrated = await confirm({
    title: `${strategy === 'merge' ? 'Merge' : 'Cherry-pick'} ${worktree.branch} into main?`,
    body: removeAfter
      ? strategy === 'merge'
        ? 'The branch is accepted into main with a merge commit, then its source directory is deleted. The integration can be undone; files not committed in the source directory cannot be recovered.'
        : 'The branch commits are applied to main, then its source directory is deleted. The integration can be undone; files not committed in the source directory cannot be recovered.'
      : strategy === 'merge'
        ? 'The branch is accepted into the repository main worktree with a merge commit, preserving the agent boundary. The integration can be undone afterwards.'
        : 'The branch commits are applied in order to the repository main worktree. The integration can be undone afterwards.',
    confirmLabel: strategy === 'merge' ? 'Merge into main' : 'Cherry-pick into main',
    recovery: removeAfter ? 'mixed' : 'undoable',
    detail: [worktree.branch, worktree.path],
  })
  if (!integrated) return

  const expectedTargetHead = refs.worktrees.find((candidate) => candidate.isMain)?.head ?? ''
  busy = true
  syncRemoteBusy()

  try {
    const result = await call('acceptWorktree', {
      worktreePath: worktreePath!,
      target: worktree.path,
      strategy,
      removeAfter,
      // A merge commit records the agent boundary even when Git could fast-forward.
      noFastForward: strategy === 'merge',
      expectedSourceHead: worktree.head,
      expectedTargetHead,
    })

    if (result.ok) onToast(result.message)
    else if (result.integration.failure === 'conflict') {
      onToast('Acceptance stopped on conflicts', 'Resolve them in the main worktree.', 'error')
    } else {
      onToast(result.message, result.integration.commandLine || undefined, 'error')
    }

    // A source removal closes this panel when it was the panel's worktree. A conflict also
    // hands the user to the main worktree, so leave the refs surface before that banner opens.
    const closesPanel = result.removed && samePath(result.sourceWorktreePath, worktreePath!)
    const conflict = !result.ok && result.integration.failure === 'conflict'
    if (closesPanel || conflict) close()
    await onAcceptWorktree(result)
    if (!closesPanel) await refresh()
  } catch (error) {
    onToast('Could not accept this worktree', message(error), 'error')
  } finally {
    busy = false
    syncRemoteBusy()
  }
}

async function moveWorktree(worktree: Worktree): Promise<void> {
  const destination = await prompt(`Move ${worktree.displayName} to`, worktree.path)
  if (destination == null || samePath(destination, worktree.path)) return

  // No confirmation: a move destroys nothing, and git refuses if the destination is taken.
  // What it does break is every path the window is holding for this worktree, which is why
  // the new location travels with the notification rather than the window being left to
  // discover it.
  await run(
    () => call('moveWorktree', { worktreePath: worktreePath!, target: worktree.path, destination }),
    [],
    { path: worktree.path },
  )
}

/**
 * Removes a worktree, asking twice when the second question is a different one.
 *
 * The first removal is attempted without `--force`, so git decides whether tracked work is
 * at risk — the same shape as deleting a branch. Its refusal is what turns a tidy-up into a
 * question about somebody's work in progress. Both questions are permanent; what differs is
 * what goes, and that is what the two bodies say.
 */
async function removeWorktree(worktree: Worktree): Promise<void> {
  // Locked is asked first because it is a decision, not an obstacle. Somebody locked this
  // deliberately, possibly on another machine, and the reason is the whole of what they left
  // behind to say why.
  if (worktree.isLocked) {
    const unlock = await confirm({
      title: `${worktree.displayName} is locked`,
      body: worktree.lockReason
        ? `It was locked with the reason: "${worktree.lockReason}". Unlock it before removing?`
        : 'It was locked, with no reason recorded. Unlock it before removing?',
      confirmLabel: 'Unlock',
      recovery: 'undoable',
    })

    if (!unlock) return

    const unlocked = await run(() =>
      call('unlockWorktree', { worktreePath: worktreePath!, target: worktree.path }),
    )

    if (!unlocked?.ok) return
  }

  const ok = await confirm({
    title: `Remove ${worktree.displayName}?`,
    body:
      'The whole directory is deleted, including files git ignores — a .env, a node_modules, ' +
      'anything built. The branch and its commits stay in the repository, so committed work ' +
      'is not affected.',
    confirmLabel: 'Remove',
    detail: await previewRemoval(worktree),
    // Permanent, and this was wrong once. The reasoning for "undoable" was that git refuses
    // the unforced removal if anything is uncommitted, so nothing outside git can be lost —
    // and git's check is `status`, which does not report ignored files. A worktree whose only
    // untracked content is a .env and a node_modules is "clean" to that check and is deleted
    // without a murmur. Nothing in the app reverses it either: removal records no undo point,
    // because there is nothing an inverse command could put back.
    recovery: 'permanent',
  })

  if (!ok) return

  const gone = { path: worktree.path }

  const result = await run(
    () => call('removeWorktree', { worktreePath: worktreePath!, target: worktree.path }),
    ['wouldLoseChanges'],
    gone,
  )

  if (!result || result.ok) return
  if (result.failure !== 'wouldLoseChanges') return

  const force = await confirm({
    title: `${worktree.displayName} has uncommitted work in it`,
    body:
      'Removing it now deletes files that were never committed — an agent’s work in progress, ' +
      'or anything not yet staged. Committing or stashing first is the way to keep it.',
    confirmLabel: 'Remove anyway',
    detail: await previewRemoval(worktree),
    // Uncommitted content is in no git object, so neither the reflog nor undo can reach it —
    // the same fact that makes discard permanent, for the same reason.
    recovery: 'permanent',
  })

  if (!force) return

  await run(
    () => call('removeWorktree', { worktreePath: worktreePath!, target: worktree.path, force: true }),
    [],
    gone,
  )
}

/**
 * Names what is inside a worktree before offering to delete the directory.
 *
 * The dialog has always promised to say what it removes, and until now it said a path. The
 * gap it closes is specifically the quiet one: git's own removal check is `status`, which
 * does not report ignored files, so a worktree whose only untracked content is a `.env` and
 * a `node_modules` is clean by that test and would be deleted without either dialog ever
 * mentioning it.
 */
async function previewRemoval(worktree: Worktree): Promise<string[]> {
  try {
    const preview = await call('previewRemoveWorktree', {
      worktreePath: worktreePath!,
      target: worktree.path,
    })

    if (!preview.ok) return [worktree.path, `Could not read the directory: ${preview.message}`]
    if (!preview.exists)
      return [worktree.path, 'The directory is already gone; only git’s record of it is removed.']

    const counts = [
      preview.changedCount > 0 ? `${preview.changedCount} uncommitted` : '',
      preview.untrackedCount > 0 ? `${preview.untrackedCount} untracked` : '',
      preview.ignoredCount > 0 ? `${preview.ignoredCount} ignored` : '',
    ].filter(Boolean)

    const lines = [preview.branch ? `${worktree.path} (${preview.branch})` : worktree.path]

    // The totals come before the examples on purpose. The dialog shows eight lines and then
    // says "and N more", so a preview that leads with paths spends its budget on three of
    // them and truncates away the only number that decides anything.
    if (counts.length === 0) {
      lines.push('Nothing uncommitted, untracked or ignored is in it.')
      return lines
    }

    lines.push(`${counts.join(', ')} — all deleted with the directory`)
    lines.push(
      ...preview.changedPaths.slice(0, 2),
      ...preview.untrackedPaths.slice(0, 2),
      ...preview.ignoredPaths.slice(0, 2).map((path) => `ignored: ${path}`),
    )
    return lines
  } catch (error) {
    return [worktree.path, `Could not read the directory: ${message(error)}`]
  }
}

/** Both fields together — see the protocol note on why the sha travels with the index. */
const entry = (stash: Stash) => ({ worktreePath: worktreePath!, index: stash.index, sha: stash.sha })

async function applyStash(stash: Stash): Promise<void> {
  await run(() => call('stashApply', entry(stash)))
}

/**
 * The commits a delete would leave with nothing pointing at them.
 *
 * Deliberately a different question from the one `git branch -d` asks, and the count is used
 * to word the second dialog rather than to pre-empt git: `-d` refuses an unmerged branch even
 * when every commit on it is also on three others, and the old wording claimed the opposite
 * in that case. Git still decides; this only decides what to say.
 */
async function previewBranchDeletion(name: string): Promise<{ unreachable: number; detail: string[] }> {
  try {
    const preview = await call('previewDeleteBranch', { worktreePath: worktreePath!, name })
    if (!preview.ok) return { unreachable: 0, detail: [`Could not preview: ${preview.message}`] }

    const commits = preview.unreachableCommits
    if (commits.length === 0)
      return { unreachable: 0, detail: ['Every commit on it is reachable from another ref.'] }

    return {
      unreachable: commits.length,
      detail: [`${commits.length} commit(s) reachable from nothing else:`, ...commits],
    }
  } catch (error) {
    return { unreachable: 0, detail: [`Could not preview: ${message(error)}`] }
  }
}

async function deleteBranch(branch: Branch): Promise<void> {
  const preview = await previewBranchDeletion(branch.name)

  const ok = await confirm({
    title: `Delete ${branch.name}?`,
    body: `The branch is removed from this repository. Its commits stay reachable from anything else that points at them.`,
    confirmLabel: 'Delete',
    // Earned rather than assumed: the tip is captured before the delete and undo recreates
    // the branch at exactly that commit.
    recovery: 'undoable',
    detail: preview.detail,
  })

  if (!ok) return

  // Same reasoning as the switch: the unmerged refusal becomes the second question below,
  // so reporting it as an error first would only make that question look like a fault.
  const result = await run(
    () => call('deleteBranch', { worktreePath: worktreePath!, name: branch.name }),
    ['wouldLoseChanges'],
  )

  if (!result || result.ok) return

  // Git refuses `-d` when the branch's commits are on no other branch. That refusal is the
  // only thing separating tidying up from abandoning work, so it gets its own question
  // rather than being pre-empted by passing -D from the start.
  if (result.failure !== 'wouldLoseChanges') return

  // Git refused, which is its own measurement: `-d` asks whether the branch is merged into
  // HEAD or its upstream. The preview asked a different question — what no ref would point
  // at — and the two legitimately disagree, so the wording follows whichever one has
  // something to show rather than asserting both.
  const force = await confirm({
    title: preview.unreachable > 0
      ? `${branch.name} has commits that are on no other branch`
      : `${branch.name} is not merged into this branch or its upstream`,
    body: preview.unreachable > 0
      ? 'Deleting it leaves those commits unreachable. Undo puts the branch back at the same ' +
        'commit, and until then git keeps them, but nothing else refers to them.'
      : 'Every commit on it is also reachable from another ref, so nothing becomes unreachable — ' +
        'git refuses because the branch has not been merged where it is looking. Undo puts the ' +
        'branch back at the same commit either way.',
    confirmLabel: 'Delete anyway',
    recovery: 'undoable',
    detail: preview.detail,
  })

  if (!force) return

  await run(() => call('deleteBranch', { worktreePath: worktreePath!, name: branch.name, force: true }))
}

async function dropStash(stash: Stash): Promise<void> {
  const ok = await confirm({
    title: 'Delete this stash?',
    body: `"${stash.message || stash.selector}" is removed without being applied.`,
    confirmLabel: 'Delete',
    // A dropped stash is unreferenced, not gone: undo puts the entry back with its contents
    // intact. That is why this is not the permanent wording a discard gets.
    recovery: 'undoable',
  })

  if (ok) await run(() => call('stashDrop', entry(stash)))
}

async function deleteTag(tag: Tag): Promise<void> {
  const ok = await confirm({
    title: `Delete ${tag.name}?`,
    body: tag.isAnnotated
      ? 'The tag and its message are removed from this repository.'
      : 'The tag is removed from this repository. The commit it named is untouched.',
    confirmLabel: 'Delete',
    recovery: 'undoable',
  })

  if (ok) await run(() => call('deleteTag', { worktreePath: worktreePath!, name: tag.name }))
}

async function runFooterAction(action: string): Promise<void> {
  if (!worktreePath) return

  switch (action) {
    case 'cancel-compare':
      close()
      return

    case 'new-branch': {
      const name = await prompt('Name the new branch', '')
      if (!name) return

      await run(() => call('createBranch', { worktreePath: worktreePath!, name }))
      return
    }

    case 'stash':
    case 'stash-untracked': {
      const note = await prompt('Describe the stash (optional)', '')
      if (note == null) return

      await run(() =>
        call('stashPush', {
          worktreePath: worktreePath!,
          message: note,
          includeUntracked: action === 'stash-untracked',
        }),
      )
      return
    }

    case 'new-worktree':
      await newWorktree()
      return

    case 'prune':
      await pruneWorktrees()
      return

    case 'new-tag': {
      const name = await prompt('Name the new tag', '')
      if (!name) return

      // A message is what makes a tag annotated — git's own rule, since -m implies -a — so
      // asking for one is also how the choice between the two kinds is offered.
      const note = await prompt(`Describe ${name} (optional — a message makes it annotated)`, '')
      if (note == null) return

      await run(() => call('createTag', { worktreePath: worktreePath!, name, message: note }))
      return
    }

    case 'new-remote': {
      const name = await prompt('Name the new remote', 'origin')
      if (!name) return

      const url = await prompt(`URL for ${name}`, '')
      if (!url) return

      await run(() => call('addRemote', { worktreePath: worktreePath!, name, url }))
      return
    }

    case 'fetch-all':
      await startFetch('', false, true)
      return

    case 'new-pull-request':
      await createPullRequest()
      return

    case 'refresh-pull-requests':
      await refreshPullRequests()
      render()
      return

    case 'cancel-remote': {
      if (!remoteOperation || remoteOperation.state !== 'running') return

      try {
        const cancelled = await call('cancelRemoteOperation', { id: remoteOperation.id })
        if (!cancelled) onToast('That remote operation has already finished.')
      } catch (error) {
        onToast('Could not cancel remote operation', message(error), 'error')
      }
      return
    }
  }
}

/**
 * Adds a worktree: a name, where its branch starts, and a place to put it.
 *
 * Three questions, none of them optional. The name decides the branch, the start point
 * decides what is in it, and the path decides where the checkout lands. Conflating any of
 * them — deriving the path from the name and never showing it, or letting the start point
 * default out of sight — is how a tool ends up creating a directory the user cannot find,
 * holding a branch that begins somewhere they did not choose.
 *
 * Whether the branch is created or checked out is *not* a fourth question. The panel already
 * holds the branch list, so it can tell which of the two this is, and asking the user to
 * classify something the app already knows is a question with a right answer — which is not
 * a question worth asking.
 *
 * The start point is asked only where it has no such right answer, which is the new-branch
 * case alone. An existing branch already begins where it begins. A name matching exactly one
 * remote is git's dwim, where the start point *is* that remote branch and an override could
 * only produce the wrong one. What is left is the case where the answer was silently `HEAD` —
 * and `HEAD` resolves in the repository's *main* worktree, because that is where every
 * worktree mutation runs, so a user standing in a linked worktree got a branch off a commit
 * they were not looking at. The label said "at this HEAD" and meant a different one.
 */
async function newWorktree(): Promise<void> {
  if (!refs) return

  const name = await prompt('Name the branch for the new worktree', '')
  if (!name) return

  const existing = refs.branches.find((b) => !b.isRemote && b.name === name)

  // A name that exists on exactly one remote and nowhere locally is git's own dwim case:
  // `git worktree add <path> <name>` creates a local branch tracking it and says so. Passing
  // `-b` instead produces a branch of the same name that tracks nothing, from the wrong
  // commit — which looks identical in the list and is not the branch the user asked for.
  const tracking = refs.branches.filter(
    (b) => b.isRemote && b.name.slice(b.name.indexOf('/') + 1) === name,
  )

  const dwim = existing == null && tracking.length === 1

  // `checkedOutIn`, not `isCheckedOutElsewhere`: the latter is false for the branch *this*
  // worktree is on, so naming it fell through to git, whose refusal — "that branch is checked
  // out in another worktree" — is untrue and sends the user looking for a worktree that does
  // not exist. Git's rule is one worktree per branch, and this one counts.
  if (existing?.checkedOutIn != null) {
    onToast(
      existing.isCurrent
        ? `You are already on ${name} here`
        : `${name} is already open in another worktree`,
      'Git allows a branch in one worktree at a time. Pick another name, or work in the one that has it.',
      'error',
    )
    return
  }

  // Prefilled with this worktree's HEAD — the answer the old label claimed to be giving, and
  // the one this question usually means — but prefilled rather than assumed, because the
  // other common answer is `main` and reaching it should not mean leaving the app.
  let startPoint = ''
  if (existing == null && !dwim) {
    const from = await prompt(`Where should ${name} start?`, headHere(), startPointOptions())
    if (!from) return

    startPoint = from
  }

  // Asked of the backend rather than assembled here: which layout this repository uses is a
  // fact about the repository, and joining paths is a fact about the platform. Neither is
  // something the window can work out from what it has.
  let suggestion = ''
  try {
    suggestion = await call('suggestWorktreePath', { worktreePath: worktreePath!, name })
  } catch {
    // A suggestion that could not be made is an empty box, not a failure — the user knows
    // where they want it.
  }

  // The label says which of the three things is about to happen, because they differ in what
  // the worktree will contain: an existing branch as it stands, a branch from a remote, or
  // an empty new one at this HEAD.
  const label = existing
    ? `Where should the worktree for ${name} go?`
    : dwim
      ? `Where should ${name} go? It is created from ${tracking[0]!.name}.`
      : `Where should ${name} go? A new branch is created from ${startPoint}.`

  const path = await prompt(label, suggestion)
  if (!path) return

  await run(() =>
    call('addWorktree', {
      worktreePath: worktreePath!,
      path,
      branch: name,
      createBranch: existing == null && !dwim,
      startPoint,
    }),
  )
}

/**
 * What this worktree's HEAD is called, in a form git will take as a start point.
 *
 * The branch name where there is one, and the sha where HEAD is detached — a detached
 * worktree has no name to offer. `HEAD` itself is never the answer: it resolves in the main
 * worktree, which is precisely the confusion this question exists to remove.
 */
function headHere(): string {
  if (!refs) return ''
  if (refs.current) return refs.current

  return refs.worktrees.find((w) => samePath(w.path, worktreePath ?? ''))?.shortHead ?? ''
}

/**
 * Names worth completing in the start point box: every branch and tag the panel already holds.
 *
 * A list to complete from, not a list to pick from. Git takes far more than these — `main~2`,
 * a sha, `origin/main@{yesterday}` — so a picker would have to refuse answers that work.
 * Locals first, since a new branch usually starts from one.
 */
function startPointOptions(): string[] {
  if (!refs) return []

  return [
    ...refs.branches.filter((b) => !b.isRemote).map((b) => b.name),
    ...refs.branches.filter((b) => b.isRemote).map((b) => b.name),
    ...refs.tags.map((t) => t.name),
  ]
}

/**
 * Prunes, showing first what pruning would forget.
 *
 * The dry run is the point. Every other action in this panel names the thing it acts on in
 * the row it was clicked from; prune acts on entries that have no row, because the
 * directories they refer to are gone. Without the preview the button says "do something to
 * an unspecified number of things you cannot see".
 */
async function pruneWorktrees(): Promise<void> {
  let entries: { name: string; reason: string }[]

  try {
    const preview = await call('previewPrune', { worktreePath: worktreePath! })
    entries = preview.entries
  } catch (error) {
    onToast('Could not check what pruning would remove', message(error), 'error')
    return
  }

  if (entries.length === 0) {
    onToast('Nothing to prune', 'Every worktree git knows about is still where it should be.')
    return
  }

  const ok = await confirm({
    title: entries.length === 1 ? 'Forget 1 missing worktree?' : `Forget ${entries.length} missing worktrees?`,
    body:
      'Git stops tracking these. Their directories are already gone — pruning removes the ' +
      'records left behind, and the branches they had checked out are untouched.',
    confirmLabel: 'Prune',
    detail: entries.map((entry) => (entry.reason ? `${entry.name} — ${entry.reason}` : entry.name)),
    recovery: 'harmless',
  })

  if (!ok) return

  await run(() => call('pruneWorktrees', { worktreePath: worktreePath! }))
}

/* ==========================================================================
   Inline prompt

   A one-line question asked inside the overlay rather than through another dialog.
   `confirm.ts` deliberately has no text field — it answers yes-or-no about something
   destructive — and a second modal stacked on this one would take the keyboard from it.
   ========================================================================== */

function prompt(
  question: string,
  initial: string,
  suggestions: string[] = [],
): Promise<string | null> {
  return new Promise((resolve) => {
    const host = document.createElement('div')
    host.className = 'refs-prompt'

    // A datalist rather than rows of our own: it completes without taking the keyboard, which
    // is the whole reason this question is asked here instead of in a second dialog. The id
    // is fixed because the footer holds one prompt at a time — the previous one is replaced,
    // not stacked, so there is never a second datalist to collide with.
    const options =
      suggestions.length > 0
        ? `<datalist id="refs-prompt-options">${suggestions
            .map((value) => `<option value="${esc(value)}"></option>`)
            .join('')}</datalist>`
        : ''

    host.innerHTML = `
      <label class="refs-prompt-label">${esc(question)}</label>
      <div class="refs-prompt-row">
        <input class="refs-prompt-input" type="text" spellcheck="false" autocomplete="off"
               ${options ? 'list="refs-prompt-options"' : ''} />
        <button class="btn small" data-prompt-cancel>Cancel</button>
        <button class="btn small pop" data-prompt-ok>OK</button>
      </div>
      ${options}`

    footer.replaceChildren(host)

    const input = host.querySelector<HTMLInputElement>('.refs-prompt-input')!
    input.value = initial

    let settled = false

    const finish = (value: string | null): void => {
      if (settled) return
      settled = true

      // The footer is rebuilt rather than merely emptied: whatever runs next re-renders,
      // and leaving a dead prompt behind would let a second Enter answer a dead question.
      renderFooter()

      // And focus goes home. Rebuilding the footer destroys the input the keyboard was in,
      // which drops focus onto <body> — outside the element this panel's key handler is bound
      // to, so answering or cancelling a prompt left the whole overlay keyboard-dead until
      // somebody clicked it. Found by cancelling one and finding the filter would not type.
      focusFilter()
      resolve(value)
    }

    input.addEventListener('keydown', (event) => {
      // Stopped here so the overlay's own handler does not also see them: Enter would
      // otherwise act on the selected row at the same time as answering this.
      event.stopPropagation()

      if (event.key === 'Enter') {
        event.preventDefault()
        finish(input.value.trim())
      } else if (event.key === 'Escape') {
        event.preventDefault()
        finish(null)
      }
    })

    host.querySelector('[data-prompt-cancel]')!.addEventListener('click', () => finish(null))
    host.querySelector('[data-prompt-ok]')!.addEventListener('click', () => finish(input.value.trim()))

    input.focus()
    input.select()
  })
}
