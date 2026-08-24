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

import { call, on } from './bridge'
import { confirm } from './confirm'
import { icons, kindLetter } from './icons'
import type {
  AiAvailability,
  ChangedFile,
  CommitViewPayload,
  DiffSide,
  GeneratedMessage,
  GenerationCost,
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

/* ==========================================================================
   Generated messages
   ========================================================================== */

/**
 * A generation in flight, or null. Only ever one at a time.
 *
 * `replaced` is whatever was in the box when it started, kept so a refusal or a dead network
 * gives it back. Clearing the box to make room for a message that never arrives is the same
 * small betrayal as losing a half-written message to the amend checkbox.
 */
let writing: { id: string; worktreePath: string; replaced: string } | null = null

/** Cached because the button is rendered on every repaint and the answer rarely changes. */
let ai: AiAvailability | null = null

/** What the last generation cost, and anything it wants to say about itself. */
let lastCost: GenerationCost | null = null
let lastNote: string | null = null

/** Alternatives from a "give me options" run. Empty for an ordinary generation. */
let choices: GeneratedMessage[] = []

/** Whether the inline key prompt is open. */
let askingForKey = false

/**
 * Set the instant a generation is asked for, before the round trip that returns its id.
 *
 * `writing` cannot do this job: it needs the id, which only exists once the call comes back,
 * and the whole point of the call returning immediately is that there is a gap. Two presses
 * of Ctrl+G inside that gap would otherwise both pass the guard, start two generations, and
 * leave the first one running unwatched — streaming, billed, and dropped.
 */
let starting = false

/**
 * What was staged when the message was written, so the panel can notice it moving.
 *
 * The situation this app is built for: an agent stages something else while the generated
 * message sits in the box unsent. Nothing about the message would otherwise reveal that it
 * describes work which is no longer what is about to be committed.
 */
let writtenAgainst: string | null = null

/** A cheap identity for the staged set — paths and their line counts, in order. */
const stagedSignature = (state: CommitViewPayload): string =>
  state.staged.map((f) => `${f.path}:${f.linesAdded}:${f.linesRemoved}`).join('\n')

export function initCommitPanel(element: HTMLElement, dependencies: Deps): void {
  host = element
  deps = dependencies
  wire()

  // The text comes back on the event channel rather than as the call's return value: a
  // model call can outlast the bridge's 60s ceiling, so the call returns an id and the
  // words follow.
  on('messageDelta', (payload) => {
    if (writing?.id !== payload.id || !view || view.worktreePath !== payload.worktreePath) return

    // Through the draft, never straight into the DOM. This panel repaints on every watcher
    // notification, and an agent writing in the worktree while a message is being generated
    // is the ordinary case here — a direct write would be erased by the next repaint.
    draftFor(payload.worktreePath).message = payload.message
    paintMessage()
  })

  on('messageGenerated', (result) => {
    if (writing?.id !== result.id) return

    const { replaced } = writing
    writing = null

    if (!result.ok) {
      // Never fatal, and never lossy. "Exactly as usable as it was before the button was
      // pressed" has to include what was in the box, so the message the generation cleared
      // to make room for itself goes back.
      //
      // Unconditional here on purpose: every user-initiated stop — typing, the Stop button,
      // leaving the worktree — nulls `writing` first, so those results never reach this
      // branch. What does reach it is a refusal or a failure, with the box untouched.
      draftFor(result.worktreePath).message = replaced
      writtenAgainst = null

      if (result.error) deps.toast('No message was written', result.error, 'error')
      render()
      return
    }

    const draft = draftFor(result.worktreePath)
    draft.message = result.options[0]?.message ?? draft.message

    choices = result.options.length > 1 ? result.options : []
    lastCost = result.cost
    lastNote = result.note

    render()
    void reviewDraft()
  })
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

  // A generation belongs to the worktree it was started in. Leaving that worktree abandons
  // it rather than letting it finish and drop a message into a box nobody is looking at.
  stopWriting()

  choices = []
  lastCost = null
  lastNote = null
  writtenAgainst = null
  askingForKey = false
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

  // Asked once and remembered. The answer changes only when a key is stored, and that path
  // updates it itself.
  if (!ai) {
    ai = await call('getAiStatus').catch(() => null)
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

  // The key box is never bound to a draft — nothing in this app holds a credential in
  // module state — so a repaint mid-paste would drop it on the floor unless it is carried
  // across by hand. Same watcher, same repaint, worse thing to lose.
  const keyBox = host.querySelector<HTMLInputElement>('#api-key')
  const key = keyBox === null ? null : { value: keyBox.value, focused: document.activeElement === keyBox }

  // The groups scroll; the box stays put. A commit button that scrolls off the bottom of a
  // long list of changes is a commit button nobody can find.
  // Both groups empty is not two empty groups.
  //
  // Rendered separately they say "Nothing staged yet." and "Everything is staged." at the
  // same time. Each is true on its own and together they read as a bug: the panel appears
  // to be contradicting itself about a working tree where nothing has happened at all.
  const nothing = view.staged.length === 0 && view.unstaged.length === 0

  host.innerHTML = `
    <div class="commit-scroll">
      ${
        nothing
          ? `<div class="commit-empty">Nothing to commit — the working tree is clean.</div>`
          : `${renderGroup('staged', view.staged)}${renderGroup('unstaged', view.unstaged)}`
      }
    </div>
    ${renderBox(view, draft)}`

  const scroller = host.querySelector('.commit-scroll')
  if (scroller) scroller.scrollTop = scrolled

  if (key !== null) {
    const restored = host.querySelector<HTMLInputElement>('#api-key')
    if (restored) {
      restored.value = key.value
      if (key.focused) restored.focus()
    }
  }

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

  // Only ever reached with the other group non-empty — `render` handles the both-empty
  // case itself — so both of these say something the other group does not already say.
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

  // The leaf of the branch name, not the whole thing. An agent branch is long enough to
  // wrap this line to two and shift the entire box — and the rail three inches to the left
  // is already showing which worktree this is. The full name is on the title.
  //
  // `== null`, not `=== null`: a detached HEAD sends no branch at all — the backend omits
  // null members — so the field arrives `undefined` and strict equality waves it straight
  // through to `.slice()`. See the note at the top of protocol.ts.
  const branchLeaf = state.branch == null ? null : state.branch.slice(state.branch.lastIndexOf('/') + 1)
  const branchTitle = state.branch == null ? '' : ` title="${esc(state.branch)}"`
  const named = `<strong${branchTitle}>${esc(branchLeaf ?? 'main')}</strong>`

  const target = state.isUnborn
    ? `first commit on ${named}`
    : state.branch
      ? `on ${named}`
      : 'on a <strong>detached HEAD</strong>'

  // Which readiness applies depends on the toggle, and they genuinely differ: an amend
  // with nothing staged is a reword, which is the commonest reason to amend at all.
  const canGo = draft.amend ? state.canAmend : state.canCommit
  const blockedReason = draft.amend ? state.amendBlockedReason : state.blockedReason
  const note = draft.amend ? state.amendNote : state.note

  // Nothing to say about a message nobody has written. The backend answers honestly that
  // an empty string has no subject line, and printing that under an untouched box tells
  // the user off for not having typed yet — advice arriving before the thing it advises on.
  const problems =
    draft.message.trim().length === 0
      ? ''
      : (review?.problems ?? [])
          .map((problem) => `<li class="${problem.severity}">${esc(problem.message)}</li>`)
          .join('')

  const blocked = !canGo
  const label = draft.amend ? 'Amend' : 'Commit'

  // Nothing changed, nothing staged, nothing typed — so every remaining row of this box
  // is about work that does not exist. Collapsed to the one line that is still true, plus
  // the way back in: an amend needs nothing staged (rewording the last commit is the
  // commonest reason to amend at all), and ticking it renders the box in full.
  //
  // A generation in flight is not idle, whatever the tree looks like. `write` clears the
  // draft before the first token arrives, so a repaint during those seconds sees an empty
  // message — and if an agent commits the staged set in the same moment, which is this
  // app's ordinary case, the collapse would take away the Stop button and the box the
  // text is streaming into. Same for the key prompt: it is a half-finished question, and
  // a watcher notification must not close it.
  const idle =
    !draft.amend &&
    !writing &&
    !askingForKey &&
    state.staged.length === 0 &&
    state.unstaged.length === 0 &&
    draft.message.trim().length === 0

  if (idle) {
    return `
      <section class="commit-box idle">
        <div class="commit-head">
          <span class="commit-target">${target}</span>
          ${identityRow}
        </div>
        ${
          state.isUnborn
            ? ''
            : `<div class="commit-options">
                 <label title="Reword or add to the previous commit">
                   <input type="checkbox" data-opt="amend" />
                   <span>Amend the last commit</span>
                 </label>
               </div>`
        }
      </section>`
  }

  return `
    <section class="commit-box">
      <div class="commit-head">
        <span class="commit-target">${target}</span>
        ${identityRow}
      </div>

      ${renderWrite(state, draft)}

      <!-- Never readonly, not even mid-generation. A readonly textarea fires no input
           event, which would make "typing cancels the generation" quietly untrue: the
           user would be locked out of the box until the model finished. -->
      <textarea id="commit-message" class="commit-message" rows="3"
                spellcheck="true"
                placeholder="${
                  draft.amend ? 'Amend the message…' : 'Summary, then a blank line, then why.'
                }">${esc(draft.message)}</textarea>

      ${renderChoices()}

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

      <!-- Filled only when it can actually fire. A full-width saturated primary that
           refuses the click makes the loudest thing in the window the one thing that does
           nothing, and teaches the eye to stop reading it — including on the occasions
           when it would have worked. -->
      <button class="btn ${blocked ? '' : 'pop'} commit-submit" data-action="commit"
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

/**
 * The row above the message box: write one, stop writing one, or say why neither is on offer.
 *
 * Above the box rather than beside the commit button, because it acts on the box. Nothing
 * here is ever the only way to get a message — the textarea is the feature and this is a
 * shortcut to filling it, so every state below still leaves it typeable.
 */
function renderWrite(state: CommitViewPayload, draft: CommitDraft): string {
  if (askingForKey) return renderKeyPrompt()

  // Off in settings.json, or a build with no credential story at all. Say nothing and take
  // up no space: an error banner above every commit box would be its own kind of error spam.
  if (!ai || (!ai.available && !ai.needsKey)) return ''

  if (writing) {
    return `
      <div class="commit-write busy">
        <button class="btn small" data-action="stop-writing" title="Stop generating">
          ${icons.stop}<span>Stop</span>
        </button>
        <span class="write-status">Writing a message…</span>
      </div>`
  }

  const needsKey = ai.needsKey

  // An amend can be described even with nothing staged — that is the reword case. A plain
  // commit cannot: there is no diff to write about.
  const hasSomethingToDescribe = draft.amend ? !state.isUnborn : state.staged.length > 0

  // Named rather than assumed. Somebody pointed at a local endpoint should not be told to
  // fetch a Claude key, and somebody with two accounts should be able to see which is in use.
  const where = ai.baseUrl ? `${ai.model} at ${ai.baseUrl}` : ai.model

  const disabled = !needsKey && !hasSomethingToDescribe
  const title = needsKey
    ? `Add an API key (${ai.environmentVariable}) to write commit messages here`
    : disabled
      ? 'Stage something first — there is nothing to describe'
      : `Write a message with ${where} (Ctrl+G)`

  const again = draft.message.trim().length > 0

  return `
    <div class="commit-write">
      <button class="btn small write-go" data-action="write" ${disabled ? 'disabled' : ''}
              title="${esc(title)}">
        ${needsKey ? icons.key : icons.spark}<span>${
          needsKey ? 'Add a key' : again ? 'Rewrite' : 'Write it for me'
        }</span>
      </button>

      ${
        needsKey || disabled
          ? ''
          : `<button class="btn small" data-action="write-options"
                     title="Ask for ${ai.optionCount} different framings of the same change">
               <span>${ai.optionCount} options</span>
             </button>`
      }

      <span class="write-status">${renderCost()}</span>
    </div>
    ${lastNote ? `<div class="commit-note">${esc(lastNote)}</div>` : ''}
    ${renderMoved()}`
}

/**
 * What the last generation cost.
 *
 * Shown rather than buried, because without it nobody can tell whether this feature is
 * cheap or quietly expensive — and the answer differs by two orders of magnitude between
 * models. Cached input is called out separately: it is why regenerating costs almost
 * nothing, and that is invisible if the tokens are simply summed.
 */
function renderCost(): string {
  if (!lastCost) return ''

  const tokens = `${lastCost.inputTokens.toLocaleString()} in · ${lastCost.outputTokens.toLocaleString()} out`
  const cached = lastCost.cacheReadTokens > 0 ? ` · ${lastCost.cacheReadTokens.toLocaleString()} cached` : ''

  // Absent for a model that is not in the price table — which is every OpenAI-compatible
  // generation, since the table is Claude-only. Tokens are still true; an invented price
  // would not be.
  //
  // Tested with `typeof`, not against null. The backend omits null members rather than
  // writing them, so this arrives as `undefined` and a `=== null` check waves it through to
  // `.toFixed()` — which throws inside the template, so `host.innerHTML` is never assigned
  // and the panel freezes on whatever it was showing.
  const money =
    typeof lastCost.usd !== 'number'
      ? ''
      : lastCost.usd < 0.01
        ? ` · &lt;$0.01`
        : ` · $${lastCost.usd.toFixed(2)}`

  return `<span title="${esc(tokens + cached)}">${tokens}${cached}${money}</span>`
}

/** Says when the staged set has moved out from under a generated message. */
function renderMoved(): string {
  if (!view || writtenAgainst === null) return ''
  if (writtenAgainst === stagedSignature(view)) return ''

  return `<div class="commit-note moved">${
    icons.warning
  }<span>What is staged has changed since this message was written.</span></div>`
}

/** The alternatives from a "3 options" run, until one is picked. */
function renderChoices(): string {
  if (choices.length === 0 || writing) return ''

  const rows = choices
    .map((choice, index) => {
      const summary = choice.message.split('\n')[0] ?? ''
      const rest = choice.body.trim().split('\n')[0] ?? ''

      return `
        <button class="commit-choice" data-choice="${index}" title="Use this message">
          <span class="choice-subject">${esc(summary)}</span>
          ${rest ? `<span class="choice-body">${esc(rest)}</span>` : ''}
        </button>`
    })
    .join('')

  return `<div class="commit-choices">${rows}</div>`
}

/**
 * Asks for the key, inline.
 *
 * Inline rather than in a settings screen because there is no settings screen, and inline
 * rather than a modal because pasting a key does not warrant one. The sentence underneath is
 * the important part: people are right to be wary of typing a credential into a window, and
 * the honest answer to "where does this go" is short enough to just say.
 */
function renderKeyPrompt(): string {
  const stored = ai?.source === 'stored'
  const variable = ai?.environmentVariable ?? 'ANTHROPIC_API_KEY'

  return `
    <div class="commit-key">
      <div class="key-row">
        <input type="password" id="api-key" class="key-input" spellcheck="false"
               autocomplete="off" placeholder="${ai?.provider === 'openai' ? 'sk-…' : 'sk-ant-…'}" />
        <button class="btn small pop" data-action="save-key">Save</button>
        <button class="btn small" data-action="cancel-key">Cancel</button>
      </div>
      <div class="key-note">
        Stored for <strong>${esc(ai?.provider ?? 'anthropic')}</strong>, encrypted for your
        Windows account in <code>credentials.dat</code> — never written to
        <code>settings.json</code>. <code>${esc(variable)}</code> is used instead when this is
        left empty.${stored ? ' Saving an empty box forgets the key already stored.' : ''}
      </div>
    </div>`
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

    const choice = target.closest<HTMLElement>('[data-choice]')
    if (choice && view) {
      const picked = choices[Number(choice.dataset.choice)]
      if (picked) {
        draftFor(view.worktreePath).message = picked.message
        choices = []
        render()
        void reviewDraft()
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
      case 'write':
        if (ai?.needsKey) {
          askingForKey = true
          render()
          host.querySelector<HTMLInputElement>('#api-key')?.focus()
        } else {
          void write(1)
        }
        break
      case 'write-options':
        // The configured number, not a hardcoded one — the button labelled itself with it.
        void write(ai?.optionCount ?? 3)
        break
      case 'stop-writing':
        stopWriting()
        render()
        break
      case 'save-key':
        void saveKey()
        break
      case 'cancel-key':
        askingForKey = false
        render()
        break
    }
  })

  host.addEventListener('input', (event) => {
    const target = event.target as HTMLElement
    if (target.id !== 'commit-message' || !view) return

    // Typing wins. A user who starts writing while the model is still going has answered
    // the question themselves, and racing them for the same box would be indefensible.
    if (writing) stopWriting()

    const textarea = target as HTMLTextAreaElement
    draftFor(view.worktreePath).message = textarea.value
    autosize(textarea)
    scheduleReview()

    // The alternatives described a message that has now been edited away from.
    choices = []
  })

  // Enter saves the key from inside its own box, and Escape closes the prompt without
  // touching what is already stored.
  host.addEventListener('keydown', (event) => {
    const target = event.target as HTMLElement
    if (target.id !== 'api-key') return

    if (event.key === 'Enter') {
      event.preventDefault()
      void saveKey()
    } else if (event.key === 'Escape') {
      event.preventDefault()
      askingForKey = false
      render()
    }
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

/* ==========================================================================
   Writing a message
   ========================================================================== */

/**
 * Asks for a message and lets it stream into the box.
 *
 * Exported so the keyboard can reach it: the roadmap's cross-cutting rule is that every new
 * action gets a binding, and reaching for the mouse to fill in a commit message rather
 * defeats the point of a keyboard-first app.
 */
export function writeMessage(): void {
  if (!view || !ai) return

  if (ai.needsKey) {
    askingForKey = true
    render()
    host.querySelector<HTMLInputElement>('#api-key')?.focus()
    return
  }

  // The same key stops it. Pressing it twice should not queue a second generation.
  if (writing) {
    stopWriting()
    render()
    return
  }

  void write(1)
}

async function write(count: number): Promise<void> {
  if (!view || writing || starting) return

  const worktreePath = view.worktreePath
  const draft = draftFor(worktreePath)

  starting = true

  choices = []
  lastCost = null
  lastNote = null

  // Captured now, not when the message comes back. The diff the model is about to be given
  // is the index as it stands at this moment; by the time the words arrive an agent may have
  // staged something else, and recording the staging *then* would compare the new state
  // against itself and never notice — which is precisely the case this exists for.
  writtenAgainst = stagedSignature(view)

  try {
    const started = await call('generateCommitMessage', {
      worktreePath,
      amend: draft.amend,
      count,
    })

    // The user can have moved on during the round trip; the generation is then already
    // pointless and the backend is told so rather than left running.
    if (!view || view.worktreePath !== worktreePath) {
      void call('cancelGeneration', { id: started.id }).catch(() => {})
      return
    }

    writing = { id: started.id, worktreePath, replaced: draft.message }

    // Cleared only now, so a refused start leaves whatever was typed alone — and kept above,
    // so a refused *generation* gives it back.
    if (count === 1) draft.message = ''

    render()
  } catch (error) {
    writtenAgainst = null
    deps.toast('Could not start writing', message(error), 'error')

    // The status is the likeliest thing to have gone stale — a key removed, the feature
    // switched off in settings.json since the panel last asked.
    ai = await call('getAiStatus').catch(() => ai)
    render()
  } finally {
    starting = false
  }
}

/** Abandons the generation in flight, if any. Safe to call when there is none. */
function stopWriting(): void {
  if (!writing) return

  const { id } = writing
  writing = null

  // Whatever ends up in the box is now the user's, not something written against a
  // particular staging — so there is nothing left for the "this has moved" note to be about.
  writtenAgainst = null

  // Failure here is not worth reporting: the front-end has already stopped listening for
  // this id, so the worst case is a request that finishes and is ignored.
  void call('cancelGeneration', { id }).catch(() => {})
}

/**
 * Stores the key and re-asks what that changed.
 *
 * The value is read straight out of the input and sent; it is never put in the draft, a
 * toast, or the operation log. An empty box forgets the stored key rather than storing an
 * empty one, which is how "sign out" works without a second button.
 */
async function saveKey(): Promise<void> {
  const input = host.querySelector<HTMLInputElement>('#api-key')
  if (!input) return

  const key = input.value
  input.value = ''

  try {
    const result = await call('setApiKey', { key })
    ai = result.status

    if (!result.ok) {
      deps.toast('The key was not saved', result.error ?? undefined, 'error')
      return
    }

    askingForKey = false
    render()

    deps.toast(
      key.trim().length === 0 ? 'Key forgotten' : 'Key saved',
      key.trim().length === 0
        ? `Chapter will fall back to ${result.status.environmentVariable} if it is set.`
        : 'Encrypted for your Windows account.',
    )
  } catch (error) {
    deps.toast('The key was not saved', message(error), 'error')
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

  // Nothing to advise on, so nothing is asked and nothing is shown. The backend answers
  // truthfully that an empty string has no subject line, and printing that under a box
  // nobody has typed in yet tells the user off for not having started — advice arriving
  // before the thing it advises on. Cleared here rather than only in `renderBox`, because
  // `paintProblems` writes the list into the DOM directly and would otherwise leave the
  // last message's warnings standing over an empty box.
  if (draft.message.trim().length === 0) {
    review = null
    paintProblems()
    return
  }

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
 * Repaints only the message box.
 *
 * A full render per delta would rebuild the whole panel several times a second and throw
 * away the scroll position of the file list above it. This is the same targeted-update
 * reasoning as `paintProblems`, for the same reason.
 */
function paintMessage(): void {
  const textarea = host.querySelector<HTMLTextAreaElement>('#commit-message')
  if (!textarea || !view) return

  textarea.value = draftFor(view.worktreePath).message
  autosize(textarea)

  // Keeps the tail of a long message in view as it is written, which is where the words are
  // appearing.
  textarea.scrollTop = textarea.scrollHeight
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

  // The generation's leftovers describe the commit that has just been made.
  choices = []
  lastNote = null
  writtenAgainst = null
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
