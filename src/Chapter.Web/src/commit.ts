/**
 * The commit view: what a commit would take, what it would leave, and the box that makes
 * one.
 *
 * Lives inside the Uncommitted scope rather than behind a fifth scope button. The scope
 * switch already answers "which slice of the work", and "what is staged" is not another
 * slice — it is the same slice split by the index. Putting it here also means the question
 * the user is asking ("what has nobody committed yet") and the action they are about to
 * take are in the same place.
 */

import { call } from './bridge'
import { confirm } from './confirm'
import { icons, kindLetter } from './icons'
import type {
  ChangedFile,
  CommitViewPayload,
  DiffSide,
  MessageReviewPayload,
  MutationPayload,
} from './protocol'

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

const shortenDir = (dir: string, max = 26): string =>
  dir.length <= max ? dir : '…' + dir.slice(dir.length - max + 1)

/** What the user has typed but not yet committed, kept per worktree like everything else. */
interface CommitDraft {
  message: string
  amend: boolean
  signOff: boolean
  /** Whether the draft was seeded from HEAD, so toggling amend twice does not re-seed. */
  amendSeeded: boolean
  /** The message before amend replaced it, restored when amend is turned back off. */
  savedMessage: string | null
}

const drafts = new Map<string, CommitDraft>()

function draftFor(worktreePath: string): CommitDraft {
  let draft = drafts.get(worktreePath)
  if (!draft) {
    draft = { message: '', amend: false, signOff: false, amendSeeded: false, savedMessage: null }
    drafts.set(worktreePath, draft)
  }
  return draft
}

export function forgetDraft(worktreePath: string): void {
  drafts.delete(worktreePath)
}

/** Which file and side the commit view has open, so the diff matches the row. */
export interface CommitSelection {
  path: string
  side: DiffSide
}

interface Deps {
  activeWorktree: () => string | null
  /** Opens a file's diff on one side of the index. */
  openFile: (path: string, side: DiffSide) => void
  /** Something changed the repository; re-read everything. */
  onMutated: () => void
  toast: (message: string, detail?: string, kind?: 'info' | 'error') => void
}

let deps: Deps
let host: HTMLElement
let view: CommitViewPayload | null = null
let review: MessageReviewPayload | null = null
let selection: CommitSelection | null = null

/** Guards against a slow read for a worktree the user has already left. */
let generation = 0

export function initCommitPanel(element: HTMLElement, dependencies: Deps): void {
  host = element
  deps = dependencies
  wire()
}

export function commitSelection(): CommitSelection | null {
  return selection
}

export function clearCommitSelection(): void {
  selection = null
}

/** Drops the cached view so the panel shows its skeleton rather than another worktree's files. */
export function resetCommitPanel(): void {
  view = null
  review = null
  selection = null
}

export async function refreshCommitPanel(): Promise<void> {
  const worktreePath = deps.activeWorktree()
  if (!worktreePath) {
    view = null
    render()
    return
  }

  const mine = ++generation

  try {
    const next = await call('getCommitView', { worktreePath })
    if (mine !== generation) return
    view = next
  } catch (error) {
    if (mine !== generation) return
    view = null
    deps.toast('Could not read the index', message(error), 'error')
  }

  render()

  // The message review needs the view rendered first — it only annotates.
  void reviewDraft()
}

const message = (error: unknown): string =>
  error instanceof Error ? error.message : String(error)

/* ==========================================================================
   Rendering
   ========================================================================== */

function render(): void {
  if (!view) {
    host.innerHTML = `<div class="commit-empty">Select a worktree to stage and commit.</div>`
    return
  }

  const draft = draftFor(view.worktreePath)

  // Where the caret was, if it was in the message box.
  //
  // This repaint is triggered by the watcher, which fires whenever an agent writes
  // anything in the worktree — so without this, typing a commit message while an agent
  // works loses focus and caret mid-word, over and over. That is not an edge case here;
  // it is the situation the app was built for.
  const previous = host.querySelector<HTMLTextAreaElement>('#commit-message')
  const focused = previous !== null && document.activeElement === previous
  const caret = focused ? { start: previous.selectionStart, end: previous.selectionEnd } : null
  const scrolled = host.querySelector('.commit-scroll')?.scrollTop ?? 0

  // The groups scroll; the box stays put. A commit button that scrolls off the bottom of a
  // long list of changes is a commit button nobody can find.
  host.innerHTML = `
    <div class="commit-scroll">
      ${renderGroup('staged', view.staged)}
      ${renderGroup('unstaged', view.unstaged)}
    </div>
    ${renderBox(view, draft)}`

  const scroller = host.querySelector('.commit-scroll')
  if (scroller) scroller.scrollTop = scrolled

  const textarea = host.querySelector<HTMLTextAreaElement>('#commit-message')
  if (!textarea) return

  autosize(textarea)

  if (!caret) return

  textarea.focus()
  // Clamped, because the draft can have been shortened between the two renders — a commit
  // clears it — and an out-of-range offset silently sends the caret to the end.
  const limit = textarea.value.length
  textarea.setSelectionRange(Math.min(caret.start, limit), Math.min(caret.end, limit))
}

function renderGroup(side: 'staged' | 'unstaged', files: ChangedFile[]): string {
  const isStaged = side === 'staged'
  const title = isStaged ? 'Staged' : 'Not staged'

  const bulkLabel = isStaged ? 'Unstage all' : 'Stage all'
  const bulkAction = isStaged ? 'unstage-all' : 'stage-all'

  const bulk =
    files.length === 0
      ? ''
      : `<button class="group-action" data-action="${bulkAction}" title="${bulkLabel}">
           ${isStaged ? icons.unstage : icons.stage}<span>${bulkLabel}</span>
         </button>`

  const body =
    files.length === 0
      ? `<div class="group-empty">${
          isStaged ? 'Nothing staged yet.' : 'Everything is staged.'
        }</div>`
      : files.map((file) => renderRow(file, side)).join('')

  return `
    <section class="commit-group ${side}">
      <header class="group-head">
        <span class="group-title">${title}</span>
        <span class="group-count">${files.length}</span>
        ${bulk}
      </header>
      <div class="group-files">${body}</div>
    </section>`
}

function renderRow(file: ChangedFile, side: 'staged' | 'unstaged'): string {
  const { dir, name } = splitPath(file.path)
  const isStaged = side === 'staged'

  // The kind shown is the one belonging to this side. A file staged as an addition and
  // then modified on disk is `A` above and `M` below, and showing the combined kind in
  // both places would misdescribe one of them.
  const kind = (isStaged ? file.stagedKind : file.unstagedKind) ?? file.kind
  const letter = kindLetter(kind)

  const active = selection?.path === file.path && selection.side === side

  const delta = file.isBinary
    ? '<span class="file-delta">bin</span>'
    : `<span class="file-delta">${
        file.linesAdded ? `<span class="up">+${file.linesAdded}</span>` : ''
      }${file.linesAdded && file.linesRemoved ? ' ' : ''}${
        file.linesRemoved ? `<span class="down">−${file.linesRemoved}</span>` : ''
      }</span>`

  const conflicted = file.isConflicted
    ? `<span class="file-conflict" title="Unresolved merge conflict">!</span>`
    : ''

  // Discard is offered only on the unstaged side. On the staged side the equivalent is
  // unstage-then-discard, and a one-click button that throws away staged work as well is
  // exactly the kind of thing this app should not have.
  const actions = isStaged
    ? `<button class="row-action" data-act="unstage" data-path="${esc(file.path)}"
               title="Unstage ${esc(name)}">${icons.unstage}</button>`
    : `<button class="row-action" data-act="stage" data-path="${esc(file.path)}"
               title="Stage ${esc(name)}">${icons.stage}</button>
       <button class="row-action danger" data-act="discard" data-path="${esc(file.path)}"
               data-untracked="${file.kind === 'untracked'}"
               title="Discard changes to ${esc(name)}">${icons.discard}</button>`

  return `
    <div class="commit-row ${active ? 'active' : ''}" data-row="${esc(file.path)}" data-side="${side}">
      <button class="row-open" data-open="${esc(file.path)}" data-side="${side}"
              title="${esc(file.path)}">
        <span class="file-kind k-${kind.toLowerCase()}">${letter}</span>
        ${conflicted}
        <span class="file-name">${esc(name)}</span>
        <span class="file-dir">${esc(shortenDir(dir))}</span>
        ${delta}
      </button>
      <div class="row-actions">${actions}</div>
    </div>`
}

function renderBox(state: CommitViewPayload, draft: CommitDraft): string {
  // Escaped like any other repository content. `user.name` comes from a config file inside
  // a worktree an agent has been writing to, which makes it exactly as untrusted as the
  // Markdown the preview already sanitises.
  const identity =
    state.authorName && state.authorEmail
      ? `${esc(state.authorName)} &lt;${esc(state.authorEmail)}&gt;`
      : null

  // Worth saying before the commit rather than after it fails: a repository with no
  // user.email is common on a fresh machine.
  const identityRow = identity
    ? `<span class="commit-identity" title="The identity git will record">${identity}</span>`
    : `<span class="commit-identity missing" title="git has no user.name or user.email here">
         no git identity configured
       </span>`

  const target = state.isUnborn
    ? `first commit on <strong>${esc(state.branch ?? 'main')}</strong>`
    : state.branch
      ? `on <strong>${esc(state.branch)}</strong>`
      : 'on a <strong>detached HEAD</strong>'

  // Which readiness applies depends on the toggle, and they genuinely differ: an amend
  // with nothing staged is a reword, which is the commonest reason to amend at all.
  const canGo = draft.amend ? state.canAmend : state.canCommit
  const blockedReason = draft.amend ? state.amendBlockedReason : state.blockedReason
  const note = draft.amend ? state.amendNote : state.note

  const problems = (review?.problems ?? [])
    .map(
      (problem) =>
        `<li class="${problem.severity}">${esc(problem.message)}</li>`,
    )
    .join('')

  const blocked = !canGo
  const label = draft.amend ? 'Amend' : 'Commit'

  return `
    <section class="commit-box">
      <div class="commit-head">
        <span class="commit-target">${target}</span>
        ${identityRow}
      </div>

      <textarea id="commit-message" class="commit-message" rows="3"
                spellcheck="true"
                placeholder="${
                  draft.amend ? 'Amend the message…' : 'Summary, then a blank line, then why.'
                }">${esc(draft.message)}</textarea>

      ${problems ? `<ul class="commit-problems">${problems}</ul>` : ''}

      ${note ? `<div class="commit-note">${esc(note)}</div>` : ''}
      ${
        blocked && blockedReason
          ? `<div class="commit-blocked">${esc(blockedReason)}</div>`
          : ''
      }

      <div class="commit-options">
        <label title="Replace the previous commit instead of adding one">
          <input type="checkbox" data-opt="amend" ${draft.amend ? 'checked' : ''}
                 ${state.isUnborn ? 'disabled' : ''} />
          <span>Amend</span>
        </label>
        <label title="Add a Signed-off-by trailer">
          <input type="checkbox" data-opt="signoff" ${draft.signOff ? 'checked' : ''} />
          <span>Sign off</span>
        </label>
      </div>

      <button class="btn pop commit-submit" data-action="commit"
              ${blocked ? 'disabled' : ''}
              title="${blocked ? esc(state.blockedReason ?? '') : `${label} (Ctrl+Enter)`}">
        ${icons.commit}<span>${label}${
          state.staged.length > 0
            ? ` ${state.staged.length} file${state.staged.length === 1 ? '' : 's'}`
            : ''
        }</span>
      </button>
    </section>`
}

/** Grows the message box with its content, up to a point, rather than scrolling at 3 rows. */
function autosize(textarea: HTMLTextAreaElement): void {
  textarea.style.height = 'auto'
  textarea.style.height = `${Math.min(220, Math.max(58, textarea.scrollHeight))}px`
}

/* ==========================================================================
   Actions
   ========================================================================== */

function wire(): void {
  host.addEventListener('click', (event) => {
    const target = event.target as HTMLElement

    const open = target.closest<HTMLElement>('[data-open]')
    if (open) {
      const side = open.dataset.side as 'staged' | 'unstaged'
      selection = { path: open.dataset.open!, side }
      deps.openFile(selection.path, side)
      render()
      return
    }

    const rowAction = target.closest<HTMLElement>('[data-act]')
    if (rowAction) {
      const path = rowAction.dataset.path!
      switch (rowAction.dataset.act) {
        case 'stage':
          void stage([path])
          break
        case 'unstage':
          void unstage([path])
          break
        case 'discard':
          void discard([path], rowAction.dataset.untracked === 'true')
          break
      }
      return
    }

    const action = target.closest<HTMLElement>('[data-action]')
    if (!action || !view) return

    switch (action.dataset.action) {
      case 'stage-all':
        void stage(view.unstaged.map((f) => f.path))
        break
      case 'unstage-all':
        void unstage(view.staged.map((f) => f.path))
        break
      case 'commit':
        void submit()
        break
    }
  })

  host.addEventListener('input', (event) => {
    const target = event.target as HTMLElement
    if (target.id !== 'commit-message' || !view) return

    const textarea = target as HTMLTextAreaElement
    draftFor(view.worktreePath).message = textarea.value
    autosize(textarea)
    scheduleReview()
  })

  host.addEventListener('change', (event) => {
    const target = event.target as HTMLInputElement
    if (!target.dataset.opt || !view) return

    const draft = draftFor(view.worktreePath)

    if (target.dataset.opt === 'amend') {
      draft.amend = target.checked
      applyAmendMessage(draft)
      render()
      void reviewDraft()
      return
    }

    draft.signOff = target.checked
  })

  // Ctrl+Enter commits from inside the message box, which is where the hands already are.
  host.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' || !(event.ctrlKey || event.metaKey)) return
    event.preventDefault()
    void submit()
  })
}

/**
 * Swaps the message in and out when amend is toggled.
 *
 * Turning amend on replaces an empty draft with HEAD's message, because that is what
 * amending starts from. It never overwrites something already typed, and turning amend off
 * puts back whatever was there before — losing a half-written message to a checkbox is
 * exactly the kind of small betrayal that stops people trusting a tool.
 */
function applyAmendMessage(draft: CommitDraft): void {
  if (!view) return

  if (draft.amend) {
    if (!draft.amendSeeded) {
      draft.savedMessage = draft.message
      if (draft.message.trim().length === 0 && view.headMessage) {
        draft.message = view.headMessage
      }
      draft.amendSeeded = true
    }
    return
  }

  if (draft.amendSeeded) {
    // Only restore when the seeded message is untouched; anything else is the user's.
    if (draft.message === view.headMessage) draft.message = draft.savedMessage ?? ''
    draft.amendSeeded = false
    draft.savedMessage = null
  }
}

let reviewTimer: number | undefined

/** The review shells out to `git log`, so it waits for a pause rather than firing per key. */
function scheduleReview(): void {
  window.clearTimeout(reviewTimer)
  reviewTimer = window.setTimeout(() => void reviewDraft(), 250)
}

async function reviewDraft(): Promise<void> {
  if (!view) return

  const worktreePath = view.worktreePath
  const draft = draftFor(worktreePath)
  const mine = generation

  try {
    const next = await call('reviewMessage', { worktreePath, message: draft.message })
    if (mine !== generation || !view || view.worktreePath !== worktreePath) return

    review = next
    paintProblems()
  } catch {
    // Message advice is a nicety; failing to fetch it must not disturb the panel.
  }
}

/**
 * Repaints only the problem list.
 *
 * A full render would rebuild the textarea and take the caret with it — the message box is
 * being typed in, and this runs a quarter-second after every pause.
 */
function paintProblems(): void {
  const box = host.querySelector('.commit-box')
  if (!box) return

  const existing = box.querySelector('.commit-problems')
  const problems = review?.problems ?? []

  if (problems.length === 0) {
    existing?.remove()
    return
  }

  const html = problems
    .map((problem) => `<li class="${problem.severity}">${esc(problem.message)}</li>`)
    .join('')

  if (existing) {
    existing.innerHTML = html
    return
  }

  const list = document.createElement('ul')
  list.className = 'commit-problems'
  list.innerHTML = html
  box.querySelector('#commit-message')!.after(list)
}

async function stage(paths: string[]): Promise<void> {
  if (!view || paths.length === 0) return
  await run(() => call('stage', { worktreePath: view!.worktreePath, paths }))
}

async function unstage(paths: string[]): Promise<void> {
  if (!view || paths.length === 0) return
  await run(() => call('unstage', { worktreePath: view!.worktreePath, paths }))
}

/**
 * Discards a file's uncommitted changes, after saying plainly what that means.
 *
 * An untracked file is deleted rather than restored, and the confirmation has to say so —
 * "discard changes" reads as "put it back how it was", which for a file that has never
 * been committed means removing it entirely.
 */
async function discard(paths: string[], untracked: boolean): Promise<void> {
  if (!view) return

  const worktreePath = view.worktreePath
  const name = paths.length === 1 ? paths[0]! : `${paths.length} files`

  // This button lives only on the "Not staged" row, so it throws away working-tree edits
  // and nothing else. Sending `everything` here was a real bug: it restores from HEAD and
  // takes the staged version with it, so staging a good version of a file and then
  // discarding a bad edit destroyed the good one — unrecoverably, since it was never
  // committed. `unstaged` restores from the index, which is what the row means.
  const staged = view.staged.some((f) => paths.includes(f.path))

  const ok = await confirm({
    title: untracked ? 'Delete this file?' : 'Discard changes?',
    body: untracked
      ? `${name} is not tracked by git, so discarding it deletes the file.`
      : staged
        ? `Unstaged changes to ${name} will be thrown away. The staged version is kept.`
        : `Uncommitted changes to ${name} will be thrown away.`,
    detail: paths.length > 1 ? paths : undefined,
    confirmLabel: untracked ? 'Delete' : 'Discard',
    // Working-tree content that was never staged is in no git object. The reflog cannot
    // bring it back, and saying otherwise would be a lie the user only discovers once.
    recovery: 'permanent',
  })

  if (!ok) return

  await run(() =>
    call('discard', {
      worktreePath,
      paths: untracked ? [] : paths,
      untracked: untracked ? paths : [],
      target: 'unstaged',
    }),
  )
}

async function submit(): Promise<void> {
  if (!view) return

  const worktreePath = view.worktreePath
  const draft = draftFor(worktreePath)

  // Mirrors the button: an amend is allowed with an empty index, a plain commit is not.
  if (!(draft.amend ? view.canAmend : view.canCommit)) return

  if (draft.message.trim().length === 0) {
    deps.toast('Nothing to commit with', 'A commit needs a message.', 'error')
    host.querySelector<HTMLTextAreaElement>('#commit-message')?.focus()
    return
  }

  // Amending a commit that is already pushed rewrites history somebody else may have.
  // Not detectable without a remote — Phase 5 — so the warning is about the local fact.
  if (draft.amend) {
    const ok = await confirm({
      title: 'Amend the previous commit?',
      body: 'The existing commit is replaced by a new one. Anyone who already has the old '
        + 'commit will see the two diverge.',
      confirmLabel: 'Amend',
      // The replaced commit stays in the reflog, and undo puts it straight back.
      recovery: 'undoable',
    })

    if (!ok) return
  }

  const result = await run(() =>
    call('commit', {
      worktreePath,
      message: draft.message,
      amend: draft.amend,
      signOff: draft.signOff,
    }),
  )

  if (!result?.ok) return

  // Cleared only on success, so a rejected commit does not also lose the message.
  draft.message = ''
  draft.amend = false
  draft.amendSeeded = false
  draft.savedMessage = null
}

/**
 * Runs a mutation and reports it the same way every time.
 *
 * The failure path is the one that matters: git's own message is already the best sentence
 * available for most of these, and `MutationPayload.message` has done the work of picking
 * it. Lock contention is called out separately because it is the failure that is worth
 * retrying and the user cannot tell that from the text alone.
 */
async function run(mutate: () => Promise<MutationPayload>): Promise<MutationPayload | null> {
  try {
    const result = await mutate()

    if (!result.ok) {
      deps.toast(result.message, result.commandLine || undefined, 'error')
    } else if (result.attempts > 1) {
      // Succeeded, but only after waiting for somebody else's git to finish.
      deps.toast(result.message, `after ${result.attempts} attempts — another process held the lock`)
    }

    deps.onMutated()
    return result
  } catch (error) {
    deps.toast('That did not work', message(error), 'error')
    deps.onMutated()
    return null
  }
}
