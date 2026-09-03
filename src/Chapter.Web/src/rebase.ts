import { call, on } from './bridge'
import { confirm } from './confirm'
import { icons } from './icons'
import type {
  ConflictFile,
  ConflictResolutionAction,
  ConflictState,
  MutationPayload,
  RebasePlan,
} from './protocol'

/**
 * The planner and the paused-operation banner share one small module. The planner is opened
 * from a history row; the banner stays visible in the main shell while Git waits for a
 * conflict, an edit, or a commit message. Keeping the latter outside the history overlay is
 * intentional: closing history must never hide the only way out of a paused operation.
 */

const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
}

const esc = (value: string): string => value.replace(/[&<>"']/g, (c) => ESCAPES[c]!)

interface BannerHost {
  activeWorktree: () => string | null
  conflictDirty: (worktreePath: string, path: string) => boolean
  toast: (message: string, detail?: string, kind?: 'info' | 'error') => void
  afterMutation: () => Promise<void>
  openConflictFile: (path: string) => void
}

let host: BannerHost | null = null
let banner: HTMLElement | null = null
let requestGeneration = 0
let message = ''
let currentState: ConflictState | null = null
let resolving = false

export function initRebaseBanner(options: BannerHost): void {
  host = options
  banner = document.getElementById('operation-banner')
  if (!banner) return

  banner.addEventListener('click', (event) => {
    const target = (event.target as HTMLElement).closest<HTMLElement>(
      '[data-rebase-banner-action], [data-conflict-action], [data-conflict-path]',
    )
    if (!target || !currentState || !host) return

    const conflictPath = target.dataset.conflictPath
    if (conflictPath) {
      host.openConflictFile(conflictPath)
      return
    }

    const conflictAction = target.dataset.conflictAction as ConflictResolutionAction | undefined
    if (conflictAction) {
      const path = target.dataset.path
      if (!path) return
      if (target.dataset.conflictAction === 'mark') {
        if (host.conflictDirty(currentState.worktreePath, path)) {
          host.toast('Save or discard the conflict edit first', 'The working result has unsaved changes.', 'error')
          return
        }
        void markConflict(path)
        return
      }
      if (conflictAction === 'manual') {
        host.openConflictFile(path)
      } else {
        if (host.conflictDirty(currentState.worktreePath, path)) {
          host.toast('Save or discard the conflict edit first', 'The working result has unsaved changes.', 'error')
          return
        }
        void resolveConflict(path, conflictAction,
          currentState.files.find((file) => file.path === path)?.fingerprint ?? '')
      }
      return
    }

    const action = target.dataset.rebaseBannerAction
    if (action === 'continue') void continuePaused()
    if (action === 'skip') void resolvePaused('skip')
    if (action === 'abort') void resolvePaused('abort')
  })

  banner.addEventListener('input', (event) => {
    const input = (event.target as HTMLTextAreaElement).closest<HTMLTextAreaElement>('[data-rebase-message]')
    if (input) message = input.value
  })

  // One mutation raises filesChanged, rebaseChanged, conflictChanged and often
  // historyChanged, and every one of them means the same thing to this banner: read the
  // paused operation again. Coalesced into a single call per turn of the event loop rather
  // than one per event — the generation counter already discarded the extra *renders*, but
  // the bridge calls behind them were all made.
  // A timer rather than a microtask: each event arrives from the host as its own message,
  // so a microtask flushes between them and coalesces nothing. One frame is long enough to
  // gather the burst and short enough that the banner still appears at once.
  let pending: string | null = null
  let scheduled = 0

  const refreshFor = (worktreePath: string): void => {
    if (host?.activeWorktree() !== worktreePath) return

    pending = worktreePath
    if (scheduled) return

    scheduled = window.setTimeout(() => {
      scheduled = 0
      const path = pending
      pending = null
      if (path && host?.activeWorktree() === path) void refreshRebaseBanner(path)
    }, 16)
  }

  on('filesChanged', ({ worktreePath }) => refreshFor(worktreePath))
  on('rebaseChanged', ({ worktreePath }) => refreshFor(worktreePath))
  on('conflictChanged', ({ worktreePath }) => refreshFor(worktreePath))
  on('historyChanged', ({ worktreePath }) => refreshFor(worktreePath))
}

/** Reads and paints the paused operation for the selected worktree. */
export async function refreshRebaseBanner(worktreePath: string | null): Promise<void> {
  if (!banner || !host || !worktreePath) {
    hideBanner()
    return
  }

  const generation = ++requestGeneration
  try {
    const state = await call('getConflictState', { worktreePath })
    if (generation !== requestGeneration || host.activeWorktree() !== worktreePath) return
    if (currentState?.currentCommit !== state.currentCommit) message = ''
    currentState = state
    if (!state.isPaused) {
      hideBanner()
      return
    }
    renderBanner(state)
  } catch {
    if (generation === requestGeneration) hideBanner()
  }
}

function hideBanner(): void {
  currentState = null
  message = ''
  resolving = false
  if (banner) {
    banner.hidden = true
    banner.innerHTML = ''
  }
}

function renderBanner(state: ConflictState): void {
  if (!banner) return

  const conflictCount = state.conflictedPaths.length
  const operation = state.isStashRestore
    ? `Stash ${state.stashVerb ?? 'restore'}`
    : state.operation === 'rebaseInteractive'
      ? 'Interactive rebase'
      : state.operation === 'none'
        ? 'Conflict resolution'
        : state.operation === 'applyMailbox'
          ? 'Patch application'
          : state.operation === 'cherryPick'
            ? 'Cherry-pick'
            : state.operation === 'revert'
              ? 'Revert'
              : state.operation === 'merge'
                ? 'Merge'
                : 'Rebase'
  const progress = state.step != null && state.totalSteps != null
    ? `Step ${state.step} of ${state.totalSteps}`
    : conflictCount > 0 ? `${conflictCount} conflict${conflictCount === 1 ? '' : 's'}` : 'Paused'
  const subject = state.currentSubject || state.currentCommit?.slice(0, 12) || state.description
  const canReplaceMessage = state.currentAction === 'edit' ||
    state.currentAction === 'reword' || state.currentAction === 'squash'
  const files = state.files.length > 0
    ? `<div class="operation-conflicts conflict-files">${state.files.slice(0, 12).map(conflictFileHtml).join('')}${state.files.length > 12 ? `<span>and ${state.files.length - 12} more</span>` : ''}</div>`
    : ''
  const stashNote = state.isStashRestore
    ? '<span class="operation-note">The stash entry is kept until you verify the result.</span>'
    : ''

  banner.hidden = false
  banner.innerHTML = `
    <div class="operation-banner-main">
      <span class="operation-icon">${icons.warning}</span>
      <div class="operation-copy">
        <strong>${operation} paused</strong>
        <span>${esc(progress)} · ${esc(subject)}</span>
      </div>
      ${canReplaceMessage
        ? `<textarea data-rebase-message rows="2" placeholder="Optional replacement commit message" aria-label="Commit message">${esc(message)}</textarea>`
        : ''}
      <button class="btn small pop" data-rebase-banner-action="continue"
        ${state.canContinue && !resolving ? '' : 'disabled'}>${icons.check}<span>Continue</span></button>
      <button class="btn small" data-rebase-banner-action="skip" ${state.canSkip && !resolving ? '' : 'disabled'}>Skip</button>
      <button class="btn small danger" data-rebase-banner-action="abort" ${state.canAbort && !resolving ? '' : 'disabled'}>Abort</button>
    </div>
    ${files}
    ${stashNote}`
}

function conflictFileHtml(file: ConflictFile): string {
  const side = (action: ConflictResolutionAction, label: string, enabled: boolean): string =>
    enabled
      ? `<button class="btn tiny" data-conflict-action="${action}" data-path="${esc(file.path)}">${label}</button>`
      : ''

  // A modify/delete conflict has no blob at one stage, but choosing that side is still a
  // meaningful action: it removes the working file. Keep the control visible and say so;
  // hiding it leaves the user with no way to resolve a perfectly ordinary conflict.
  const canChooseSide = file.hasOurs || file.hasTheirs
  const oursLabel = file.hasOurs ? 'Ours' : 'Ours (delete)'
  const theirsLabel = file.hasTheirs ? 'Theirs' : 'Theirs (delete)'

  return `<div class="conflict-file-row">
    <button class="conflict-file-path" data-conflict-path="${esc(file.path)}" title="Open conflict in the editor">${esc(file.path)}</button>
    <span class="conflict-file-actions">
      ${side('ours', oursLabel, canChooseSide)}
      ${side('theirs', theirsLabel, canChooseSide)}
      ${side('both', 'Both', !file.isBinary && file.canRoundTrip !== false && file.hasOurs && file.hasTheirs)}
      ${side('manual', 'Edit', !file.isBinary && (file.canRoundTrip !== false || !file.workingFileExists))}
      <button class="btn tiny pop" data-conflict-action="mark" data-path="${esc(file.path)}">Stage resolved</button>
    </span>
  </div>`
}

async function continuePaused(): Promise<void> {
  if (!host || !currentState || !currentState.canContinue || resolving) return
  const path = currentState.worktreePath
  resolving = true
  renderBanner(currentState)
  try {
    const result = await call('continueOperation', { worktreePath: path, message })
    await handleResult(result, path, 'Continue')
  } catch (error) {
    host.toast('Could not continue operation', error instanceof Error ? error.message : String(error), 'error')
  } finally {
    resolving = false
    if (currentState?.worktreePath === path && currentState?.isPaused) renderBanner(currentState)
  }
}

async function resolvePaused(action: 'skip' | 'abort'): Promise<void> {
  if (!host || !currentState || resolving) return
  const path = currentState.worktreePath
  if (action === 'abort') {
    const name = operationLabel(currentState)
    const approved = await confirm({
      title: `Abort this ${name.toLowerCase()}?`,
      body: `Git will restore the branch and working tree to the state from before the ${name.toLowerCase()}.`,
      confirmLabel: `Abort ${name.toLowerCase()}`,
      recovery: 'undoable',
      detail: currentState.originalHead ? [`Original HEAD: ${currentState.originalHead.slice(0, 12)}`] : undefined,
    })
    if (!approved) return
  }

  if (host.activeWorktree() !== path || !currentState) return
  resolving = true
  renderBanner(currentState)

  try {
    const result = action === 'skip'
      ? await call('skipOperation', { worktreePath: path })
      : await call('abortOperation', { worktreePath: path })
    await handleResult(result, path, action === 'skip' ? 'Skip' : 'Abort')
  } catch (error) {
    host.toast(`Could not ${action} operation`, error instanceof Error ? error.message : String(error), 'error')
  } finally {
    resolving = false
    if (currentState?.worktreePath === path && currentState?.isPaused) renderBanner(currentState)
  }
}

async function handleResult(result: MutationPayload, path: string, verb: string): Promise<void> {
  if (!host) return
  if (result.ok) {
    message = ''
    host.toast(result.message)
  } else {
    host.toast(`${verb} operation did not complete`, result.message, 'error')
  }

  if (host.activeWorktree() === path) await host.afterMutation()
  await refreshRebaseBanner(path)
}

function operationLabel(state: ConflictState): string {
  if (state.isStashRestore) return `stash ${state.stashVerb ?? 'restore'}`
  switch (state.operation) {
    case 'merge': return 'merge'
    case 'cherryPick': return 'cherry-pick'
    case 'revert': return 'revert'
    case 'applyMailbox': return 'patch application'
    case 'rebaseInteractive': return 'interactive rebase'
    case 'rebase': return 'rebase'
    default: return 'operation'
  }
}

async function resolveConflict(
  path: string,
  action: ConflictResolutionAction,
  fingerprint = '',
): Promise<void> {
  if (!host || !currentState || resolving) return
  const worktreePath = currentState.worktreePath
  resolving = true
  try {
    const result = await call('resolveConflict', { worktreePath, path, action, fingerprint })
    await handleResult(result, worktreePath, action === 'ours' ? 'Take ours' : action === 'theirs' ? 'Take theirs' : 'Take both')
  } catch (error) {
    host.toast('Could not resolve conflict', error instanceof Error ? error.message : String(error), 'error')
  } finally {
    resolving = false
  }
}

async function markConflict(path: string): Promise<void> {
  if (!host || !currentState || resolving) return
  const worktreePath = currentState.worktreePath
  resolving = true
  try {
    const result = await call('markResolved', { worktreePath, path })
    await handleResult(result, worktreePath, 'Stage resolved file')
  } catch (error) {
    host.toast('Could not mark conflict resolved', error instanceof Error ? error.message : String(error), 'error')
  } finally {
    resolving = false
  }
}

/** Small helper for history callers that want to inspect a plan without owning its UI. */
export async function readPlan(worktreePath: string, upstream: string): Promise<RebasePlan> {
  return call('getRebasePlan', { worktreePath, upstream })
}
