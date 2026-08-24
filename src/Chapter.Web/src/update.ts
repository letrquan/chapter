import { call, on } from './bridge'
import { confirm } from './confirm'
import { anyDirty } from './editor'
import type { UpdateStatus } from './protocol'

/**
 * Self-update, as the window presents it.
 *
 * The backend does the work — check, download, stage — and pushes an `updateStatus` event
 * every time it moves. This module holds the latest one and hands it to the two places that
 * draw it: a button in the rail foot that exists only while an update is waiting, and a row
 * in the help panel that always says which build is running.
 *
 * Two surfaces rather than one because they answer different questions. "What version is
 * this?" is asked deliberately, and belongs behind `?` with the rest of the app's account of
 * itself. "There is a new version" is not asked at all — it has to arrive on its own or it
 * never arrives, and a notice that only exists inside a panel nobody opens is not a notice.
 *
 * Neither is a modal. An update is the least urgent thing that will happen all day: nothing
 * breaks by ignoring it, the download already finished in the background, and interrupting a
 * review to announce it would cost more than it is worth.
 */

const UNKNOWN: UpdateStatus = { state: 'unmanaged', currentVersion: '', percent: 0 }

let current: UpdateStatus = UNKNOWN

const listeners = new Set<(status: UpdateStatus) => void>()

/** The latest status. Populated before the first paint, so callers never see a null. */
export function status(): UpdateStatus {
  return current
}

/** Subscribes to status changes. Fires immediately with the current one. */
export function subscribe(listener: (status: UpdateStatus) => void): () => void {
  listeners.add(listener)
  listener(current)
  return () => listeners.delete(listener)
}

function publish(next: UpdateStatus): void {
  current = next
  for (const listener of listeners) listener(next)
}

/**
 * Starts listening and asks for the state as it stands.
 *
 * The backend has already begun a check of its own by the time the page loads, so this is
 * usually answered by a status mid-flight rather than an idle one — which is why the initial
 * read exists at all. Without it a check that finished before the page was ready would leave
 * the window showing nothing, having missed the only event that was ever going to say so.
 */
export async function initUpdates(): Promise<void> {
  on('updateStatus', publish)

  try {
    publish(await call('getUpdateStatus'))
  } catch {
    // An older backend, or one without an updater. Silent: this runs at startup and the
    // absence of an update mechanism is not something to tell somebody about at startup.
  }
}

/** Asks for a check now. Progress arrives as events, so nothing is returned. */
export async function check(): Promise<void> {
  try {
    publish(await call('checkForUpdate'))
  } catch {
    publish({ ...current, state: 'failed', error: 'The update check could not be started.' })
  }
}

/**
 * Restarts into the staged build.
 *
 * Asks first when anything is unsaved, because this is the one action in the app that ends
 * the process on purpose: an unsaved editor buffer is in memory and nowhere else, and a
 * restart takes it with no way back. Everywhere else in Chapter the work is in git or on
 * disk by the time anything dangerous happens; here it need not be.
 */
export async function apply(): Promise<void> {
  if (current.state !== 'ready') return

  if (anyDirty()) {
    const go = await confirm({
      title: 'Restart to update?',
      body:
        'Some files have unsaved edits. Restarting installs ' +
        `${current.availableVersion ?? 'the new version'} and closes the window, and unsaved ` +
        'edits are not kept.',
      confirmLabel: 'Restart anyway',
      recovery: 'permanent',
    })

    if (!go) return
  }

  // Returns only if the backend refuses — on success the process is gone before the reply
  // is written. A refusal means the staged build went away underneath us, so take its word
  // for the new state rather than leaving a button that no longer does anything.
  try {
    publish(await call('applyUpdate'))
  } catch {
    publish({ ...current, state: 'failed', error: 'The update could not be installed.' })
  }
}

/** The sentence for a status, in the help panel's voice. */
export function describe(update: UpdateStatus): string {
  switch (update.state) {
    case 'unmanaged':
      return 'This copy was not installed, so it does not update itself.'
    case 'checking':
      return 'Checking…'
    case 'downloading':
      return `Downloading ${update.availableVersion ?? 'the update'} — ${update.percent}%`
    case 'ready':
      return `${update.availableVersion ?? 'A new version'} is ready.`
    case 'failed':
      return 'Could not reach GitHub to check.'
    case 'upToDate':
    default:
      return 'Up to date.'
  }
}

/** The label on the one button that carries every state. */
export function actionLabel(update: UpdateStatus): string {
  switch (update.state) {
    case 'ready':
      return 'Restart to update'
    case 'checking':
      return 'Checking…'
    case 'downloading':
      return 'Downloading…'
    case 'failed':
      return 'Try again'
    default:
      return 'Check for updates'
  }
}

/** Whether the button does anything in this state. */
export function actionEnabled(update: UpdateStatus): boolean {
  return update.state !== 'checking' && update.state !== 'downloading' && update.state !== 'unmanaged'
}

/** What pressing it does. */
export function act(update: UpdateStatus): Promise<void> {
  return update.state === 'ready' ? apply() : check()
}
