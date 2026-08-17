import { call } from './bridge'
import { confirm } from './confirm'
import { icons } from './icons'
import type { Branch, MutationPayload, RefsPayload, Stash, Tag } from './protocol'

/**
 * Branches, stashes and tags — one overlay, three sections.
 *
 * Built alongside `palette.ts` rather than inside it. The palette is shaped around a result
 * that is a place in a file (`path`, `line`, `column`) and does one thing to it; this is a
 * list of refs where every row has several actions and one of them destroys something. The
 * two share an idiom — backdrop, capture-phase keys, arrows and Enter — and nothing else,
 * which is the same relationship `confirm.ts` has to both.
 *
 * Everything is read through one `getRefs` call. The three lists are shown together and
 * every mutation refreshes all of them, so fetching them separately would only give the
 * panel more ways to contradict itself.
 */

type Section = 'branches' | 'stashes' | 'tags'

/** What the caller has to do after a mutation: refresh the rest of the window. */
type MutatedHandler = () => void | Promise<void>

/** Lets a row hand the user to the worktree that already has a branch open. */
type WorktreeHandler = (worktreePath: string) => void | Promise<void>

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
  { id: 'stashes', label: 'Stashes' },
  { id: 'tags', label: 'Tags' },
]

function build(): void {
  overlay = document.createElement('div')
  overlay.className = 'refs-backdrop'
  overlay.innerHTML = `
    <div class="refs" role="dialog" aria-modal="true" aria-labelledby="refs-title">
      <div class="refs-head">
        <span class="refs-title" id="refs-title">Refs</span>
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

  filter.addEventListener('keydown', onKey)

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
    toast: (message: string, detail?: string, kind?: 'info' | 'error') => void
  },
  startSection: Section = 'branches',
): Promise<void> {
  if (!overlay) build()

  worktreePath = worktree
  onMutated = handlers.onMutated
  onGoToWorktree = handlers.onGoToWorktree
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

  filter.placeholder =
    section === 'branches' ? 'Filter branches…' : section === 'stashes' ? 'Filter stashes…' : 'Filter tags…'
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
      : section === 'stashes'
        ? stashRows(query)
        : tagRows(query)

  if (rows.length === 0) {
    list.innerHTML = `<div class="refs-empty">${
      query ? 'No matches' : `Nothing in ${section === 'branches' ? 'this repository' : 'the ' + section}`
    }</div>`
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
}

function renderFooter(): void {
  footer.innerHTML =
    section === 'branches'
      ? `<button class="btn small" data-foot="new-branch">${icons.plus}<span>New branch</span></button>`
      : section === 'stashes'
        ? `<button class="btn small" data-foot="stash">${icons.plus}<span>Stash changes</span></button>
           <button class="btn small" data-foot="stash-untracked">
             <span>Stash, including untracked</span>
           </button>`
        : `<button class="btn small" data-foot="new-tag">${icons.plus}<span>New tag</span></button>`
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
): Promise<MutationPayload | null> {
  if (busy) return null
  busy = true
  overlay?.classList.add('busy')

  try {
    const result = await action()

    if (result.ok) onToast(result.message)
    else if (!expected.includes(result.failure))
      onToast(result.message, result.commandLine || undefined, 'error')

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
  }
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

/* ==========================================================================
   Inline prompt

   A one-line question asked inside the overlay rather than through another dialog.
   `confirm.ts` deliberately has no text field — it answers yes-or-no about something
   destructive — and a second modal stacked on this one would take the keyboard from it.
   ========================================================================== */

function prompt(question: string, initial: string): Promise<string | null> {
  return new Promise((resolve) => {
    const host = document.createElement('div')
    host.className = 'refs-prompt'
    host.innerHTML = `
      <label class="refs-prompt-label">${esc(question)}</label>
      <div class="refs-prompt-row">
        <input class="refs-prompt-input" type="text" spellcheck="false" autocomplete="off" />
        <button class="btn small" data-prompt-cancel>Cancel</button>
        <button class="btn small pop" data-prompt-ok>OK</button>
      </div>`

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
