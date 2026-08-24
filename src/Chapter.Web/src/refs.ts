import { call } from './bridge'
import { confirm } from './confirm'
import { icons } from './icons'
import type { Branch, MutationPayload, RefsPayload, Stash, Tag, Worktree } from './protocol'

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

type Section = 'branches' | 'worktrees' | 'stashes' | 'tags'

/** What the caller has to do after a mutation: refresh the rest of the window. */
type MutatedHandler = () => void | Promise<void>

/** Lets a row hand the user to the worktree that already has a branch open. */
type WorktreeHandler = (worktreePath: string) => void | Promise<void>

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

let section: Section = 'branches'
let refs: RefsPayload | null = null
let rows: Row[] = []
let selected = 0
let worktreePath: string | null = null
let busy = false

let onMutated: MutatedHandler = () => {}
let onGoToWorktree: WorktreeHandler = () => {}
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
  primary?: () => void | Promise<void>
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
      <div class="refs-list"></div>
      <div class="refs-foot"></div>
      <div class="refs-hint">
        <kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Enter</kbd> use · <kbd>Tab</kbd> section · <kbd>Esc</kbd> dismiss
      </div>
    </div>`

  document.body.appendChild(overlay)

  filter = overlay.querySelector('.refs-filter')!
  list = overlay.querySelector('.refs-list')!
  subtitle = overlay.querySelector('.refs-subtitle')!
  footer = overlay.querySelector('.refs-foot')!
  notice = overlay.querySelector('.refs-notice')!

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
    if (button) void runFooterAction(button.dataset.foot!)
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
    onWorktreeGone: WorktreeGoneHandler
    toast: (message: string, detail?: string, kind?: 'info' | 'error') => void
  },
  startSection: Section = 'branches',
): Promise<void> {
  if (!overlay) build()

  worktreePath = worktree
  onMutated = handlers.onMutated
  onGoToWorktree = handlers.onGoToWorktree
  onWorktreeGone = handlers.onWorktreeGone
  onToast = handlers.toast

  section = startSection
  selected = 0
  filter.value = ''

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
  } catch (error) {
    refs = null
    list.innerHTML = `<div class="refs-empty">${esc(message(error))}</div>`
    return
  }

  render()
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
}

function syncSectionButtons(): void {
  for (const button of overlay!.querySelectorAll<HTMLElement>('[data-section]'))
    button.classList.toggle('on', button.dataset.section === section)

  filter.placeholder = `Filter ${section}…`
}

/* ==========================================================================
   Rendering
   ========================================================================== */

function render(): void {
  if (!refs) return

  subtitle.innerHTML = refs.current
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
          : tagRows(query)

  // Both the list and the footer are replaced wholesale below, which detaches whatever
  // inside them had focus — a row's action button, most often. Focus then falls to
  // <body>, outside the overlay the key handler is bound to, and the arrows and Enter go
  // dead again: exactly the failure moving the handler onto the panel was meant to end,
  // one keystroke later. Note it here and hand focus back afterwards.
  const focused = document.activeElement
  const losesFocus = focused instanceof HTMLElement && (list.contains(focused) || footer.contains(focused))

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

  // The filter is the one thing in here that survives a render, so it is where focus goes.
  if (losesFocus && !overlay!.contains(document.activeElement)) filter.focus()
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

      footer.innerHTML =
        `<button class="btn small" data-foot="new-worktree">${icons.plus}<span>New worktree</span></button>` +
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

  // Stated as of the last fetch rather than silently implied: nothing in this app talks to
  // a remote, so these counts are exactly as old as the last thing that did.
  return `<span class="refs-track" title="against ${esc(branch.upstream)}, as of the last fetch">${parts}</span>`
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
      primary: () => useWorktree(worktree),
    }))
}

function worktreeHtml(worktree: Worktree, id: number): string {
  const isCurrent = samePath(worktree.path, worktreePath ?? '')

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
        : '<span class="refs-badge-none"></span>'

  // The main worktree is the repository; git refuses to remove, move, lock or unlock it, so
  // the row offers none of those rather than four buttons that each fail the same way.
  const actions = worktree.isMain
    ? isCurrent
      ? ''
      : `<button class="icon-btn" data-row="${id}" data-action="go-worktree"
                 title="Go to this worktree">${icons.external}</button>`
    : `${
        isCurrent
          ? ''
          : `<button class="icon-btn" data-row="${id}" data-action="go-worktree"
                     title="Go to this worktree">${icons.external}</button>`
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
    </span>`
}

/* ==========================================================================
   Keyboard
   ========================================================================== */

function onKey(event: KeyboardEvent): void {
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
      // Cycles the three sections rather than moving focus. Focus has nowhere useful to go
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

function move(delta: number): void {
  if (rows.length === 0) return

  selected = (selected + delta + rows.length) % rows.length
  render()
  list.querySelector('.refs-row.selected')?.scrollIntoView({ block: 'nearest' })
}

function usePrimary(): void | Promise<void> {
  return rows[selected]?.primary?.()
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
  if (busy) return null
  busy = true
  overlay?.classList.add('busy')

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
    overlay?.classList.remove('busy')
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

    case 'go-worktree': {
      const worktree = refs.worktrees[id]
      if (worktree) await useWorktree(worktree)
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
    detail: [worktree.path],
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
    detail: [worktree.path],
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

/** Both fields together — see the protocol note on why the sha travels with the index. */
const entry = (stash: Stash) => ({ worktreePath: worktreePath!, index: stash.index, sha: stash.sha })

async function applyStash(stash: Stash): Promise<void> {
  await run(() => call('stashApply', entry(stash)))
}

async function deleteBranch(branch: Branch): Promise<void> {
  const ok = await confirm({
    title: `Delete ${branch.name}?`,
    body: `The branch is removed from this repository. Its commits stay reachable from anything else that points at them.`,
    confirmLabel: 'Delete',
    // Earned rather than assumed: the tip is captured before the delete and undo recreates
    // the branch at exactly that commit.
    recovery: 'undoable',
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

  const force = await confirm({
    title: `${branch.name} has commits that are on no other branch`,
    body:
      'Deleting it leaves those commits unreachable. Undo puts the branch back at the same ' +
      'commit, and until then git keeps them, but nothing else refers to them.',
    confirmLabel: 'Delete anyway',
    recovery: 'undoable',
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
