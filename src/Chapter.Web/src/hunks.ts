/**
 * Hunk and line staging — the feature that makes a git GUI worth using.
 *
 * The controls are drawn from git's hunks, never Monaco's. Monaco computes its own diff
 * and groups the result differently, so a button anchored to one of its change regions
 * would send an index naming a hunk the user never looked at. The backend hands over the
 * parsed hunks precisely so both sides count the same things.
 */

import { call } from './bridge'
import { confirm } from './confirm'
import { diffSelections, hasLineSelection, revealDiffLine, type LineRange } from './editor'
import { icons } from './icons'
import type { DiffSide, FilePatchPayload, HunkPayload, PatchLineSelection } from './protocol'

const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
}

const esc = (value: string): string => value.replace(/[&<>"']/g, (c) => ESCAPES[c]!)

interface Deps {
  onMutated: () => void
  toast: (message: string, detail?: string, kind?: 'info' | 'error') => void
}

let deps: Deps
let host: HTMLElement

interface Target {
  worktreePath: string
  path: string
  side: DiffSide
}

let target: Target | null = null
let patch: FilePatchPayload | null = null
let cursor = 0
let generation = 0

export function initHunkBar(element: HTMLElement, dependencies: Deps): void {
  host = element
  deps = dependencies
  wire()
}

/** Hides the bar — the open file is not one whose hunks can be staged. */
export function hideHunkBar(): void {
  generation++
  target = null
  patch = null
  host.hidden = true
  host.innerHTML = ''
}

/**
 * Points the bar at a file, on one side of the index.
 *
 * Called every time the commit view opens a file and after every mutation, because both
 * change the hunks — staging one renumbers the rest.
 */
export async function showHunkBar(next: Target): Promise<void> {
  const mine = ++generation

  // A different file starts at its first hunk. Clamping the old cursor instead — which is
  // what this did — opens the next file at hunk 6 of 8 because that is where the previous
  // one was left.
  if (target?.path !== next.path || target.side !== next.side) cursor = 0

  target = next

  try {
    const read = await call('getFilePatch', {
      worktreePath: next.worktreePath,
      path: next.path,
      side: next.side,
    })

    if (mine !== generation) return
    patch = read
  } catch {
    if (mine !== generation) return
    hideHunkBar()
    return
  }

  if (patch.isBinary || patch.hunks.length === 0) {
    hideHunkBar()
    return
  }

  cursor = Math.min(cursor, patch.hunks.length - 1)

  host.hidden = false
  render()
}

/* ==========================================================================
   Rendering
   ========================================================================== */

function render(): void {
  if (!patch || !target || patch.hunks.length === 0) return

  const hunk = patch.hunks[cursor]!
  const staging = target.side === 'unstaged'

  // The two directions have different words. On the unstaged side the buttons move work
  // into the commit; on the staged side they take it back out.
  const primary = staging ? 'Stage' : 'Unstage'
  const primaryIcon = staging ? icons.stage : icons.unstage

  host.innerHTML = `
    <div class="hunk-nav">
      <button class="icon-btn" data-hunk="prev" title="Previous hunk (Alt+Up)"
              ${cursor === 0 ? 'disabled' : ''}>&#9650;</button>
      <span class="hunk-position">
        Hunk <strong>${cursor + 1}</strong> of ${patch.hunks.length}
      </span>
      <button class="icon-btn" data-hunk="next" title="Next hunk (Alt+Down)"
              ${cursor >= patch.hunks.length - 1 ? 'disabled' : ''}>&#9660;</button>
    </div>

    <span class="hunk-stat">
      ${hunk.addedLines ? `<span class="up">+${hunk.addedLines}</span>` : ''}
      ${hunk.removedLines ? `<span class="down">−${hunk.removedLines}</span>` : ''}
    </span>

    ${hunk.section ? `<span class="hunk-section" title="${esc(hunk.section)}">${esc(hunk.section)}</span>` : ''}

    <div class="hunk-actions">
      <button class="btn small" data-hunk="apply" title="${primary} this hunk">
        ${primaryIcon}<span>${primary} hunk</span>
      </button>
      <button class="btn small" data-hunk="apply-lines"
              title="${primary} only the lines selected in the diff">
        ${icons.check}<span>${primary} selection</span>
      </button>
      ${
        staging
          ? `<button class="btn small danger" data-hunk="discard" title="Throw these changes away">
               ${icons.discard}<span data-discard-label>Discard hunk</span>
             </button>`
          : ''
      }
    </div>`

  updateSelectionState()
}

/**
 * Enables the selection button only when there is a selection to act on.
 *
 * Cheap to recompute and worth doing on every selection change: a button that looks
 * available and then reports "nothing was selected" is worse than one that is plainly off.
 */
export function updateSelectionState(): void {
  const button = host.querySelector<HTMLButtonElement>('[data-hunk="apply-lines"]')
  if (!button) return

  const enabled = hasLineSelection() && selectedLines().length > 0
  button.disabled = !enabled
  button.title = enabled
    ? 'Apply only the lines selected in the diff'
    : 'Select changed lines in the diff first'

  // Discard follows the same selection, and says which it means. A single button whose
  // blast radius changes silently with the selection is the one thing a destructive
  // control must not be.
  const label = host.querySelector<HTMLElement>('[data-discard-label]')
  if (label) label.textContent = enabled ? 'Discard selection' : 'Discard hunk'
}

/* ==========================================================================
   Mapping a Monaco selection onto hunk body positions
   ========================================================================== */

/**
 * Works out which changed lines the user has selected.
 *
 * Both panes are walked because they carry different halves of the change: an addition
 * exists only on the right, a deletion only on the left. The walk tracks each side's real
 * line number through the hunk body, which is the only way to line a selection in the
 * editor up with a position in the patch.
 */
function selectedLines(): PatchLineSelection[] {
  if (!patch) return []

  const selection = diffSelections()
  if (!selection.base && !selection.work) return []

  const picked: PatchLineSelection[] = []

  for (const hunk of patch.hunks) {
    let oldLine = hunk.oldStart
    let newLine = hunk.newStart

    hunk.lines.forEach((line, position) => {
      const marker = line[0]

      if (marker === ' ') {
        oldLine++
        newLine++
        return
      }

      // Git's "no newline" note is not a line of the file and has no number.
      if (marker === '\\') return

      if (marker === '+') {
        if (covers(selection.work, newLine)) picked.push({ hunk: hunk.index, line: position })
        newLine++
        return
      }

      if (marker === '-') {
        if (covers(selection.base, oldLine)) picked.push({ hunk: hunk.index, line: position })
        oldLine++
      }
    })
  }

  return picked
}

const covers = (range: LineRange | null, line: number): boolean =>
  range !== null && line >= range.start && line <= range.end

/* ==========================================================================
   Actions
   ========================================================================== */

function wire(): void {
  host.addEventListener('click', (event) => {
    const button = (event.target as HTMLElement).closest<HTMLElement>('[data-hunk]')
    if (!button || !patch) return

    switch (button.dataset.hunk) {
      case 'prev':
        step(-1)
        break
      case 'next':
        step(1)
        break
      case 'apply':
        void apply({ hunks: [patch.hunks[cursor]!.index] })
        break
      case 'apply-lines':
        void apply({ lines: selectedLines() })
        break
      case 'discard':
        void discardHunk()
        break
    }
  })
}

export function stepHunk(delta: number): void {
  if (host.hidden) return
  step(delta)
}

function step(delta: number): void {
  if (!patch || patch.hunks.length === 0) return

  cursor = Math.min(patch.hunks.length - 1, Math.max(0, cursor + delta))
  render()
  scrollTo(patch.hunks[cursor]!)
}

/**
 * Scrolls the diff to a hunk.
 *
 * Aimed at whichever pane actually contains it: a hunk that only removes lines has nothing
 * to show on the right, and revealing its new-side line number would land on unrelated
 * context several lines away.
 */
function scrollTo(hunk: HunkPayload): void {
  if (hunk.addedLines > 0) revealDiffLine(hunk.newStart, 'work')
  else revealDiffLine(hunk.oldStart, 'base')
}

async function apply(selection: { hunks?: number[]; lines?: PatchLineSelection[] }): Promise<void> {
  if (!patch || !target) return

  if (selection.lines && selection.lines.length === 0) {
    deps.toast('Nothing selected', 'Select changed lines in the diff first.', 'error')
    return
  }

  // Unstaged side stages (forward); staged side unstages (reverse).
  const reverse = target.side === 'staged'

  await run(() =>
    call('applyPatch', {
      worktreePath: target!.worktreePath,
      path: target!.path,
      side: target!.side,
      hunks: selection.hunks ?? [],
      lines: selection.lines ?? [],
      reverse,
      applyToWorkingTree: false,
      // Proves the selection was made against the diff the backend is about to re-read.
      fingerprint: patch!.fingerprint,
    }),
  )
}

async function discardHunk(): Promise<void> {
  if (!patch || !target) return

  const hunk = patch.hunks[cursor]!

  // Follows whatever the button says it will: a selection when there is one, the whole
  // hunk otherwise.
  const lines = hasLineSelection() ? selectedLines() : []
  const partial = lines.length > 0

  const ok = await confirm({
    title: partial ? 'Discard the selected lines?' : 'Discard this hunk?',
    body: partial
      ? `${lines.length} selected line(s) in ${target.path} will be thrown away.`
      : `${hunk.addedLines} added and ${hunk.removedLines} removed line(s) in ${target.path} `
        + 'will be thrown away.',
    confirmLabel: 'Discard',
    // Never staged, so it is in no git object and the reflog cannot reach it.
    recovery: 'permanent',
  })

  if (!ok) return

  await run(() =>
    call('applyPatch', {
      worktreePath: target!.worktreePath,
      path: target!.path,
      side: 'unstaged',
      hunks: partial ? [] : [hunk.index],
      lines,
      reverse: true,
      applyToWorkingTree: true,
      fingerprint: patch!.fingerprint,
    }),
  )
}

async function run(mutate: () => Promise<{ ok: boolean; message: string }>): Promise<void> {
  try {
    const result = await mutate()

    if (!result.ok) {
      // The fingerprint mismatch lands here, and its message is the useful one: the file
      // moved under the selection, so the honest answer is to look again.
      deps.toast(result.message, undefined, 'error')
    }
  } catch (error) {
    deps.toast('That did not work', error instanceof Error ? error.message : String(error), 'error')
  }

  deps.onMutated()
}
