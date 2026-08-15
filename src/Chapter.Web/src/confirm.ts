/**
 * The one affordance every destructive action goes through.
 *
 * Phase 0 asks for this deliberately as a single thing rather than a dialog per feature.
 * The reason is not consistency for its own sake: discard, reset, force-push and worktree
 * removal differ enormously in what they destroy and whether it can be recovered, and a
 * user who has learned that "the red button is recoverable" from four of them will treat
 * the fifth the same way. So the recoverability is stated, in the dialog, every time.
 */

const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
}

const esc = (value: string): string => value.replace(/[&<>"']/g, (c) => ESCAPES[c]!)

export interface ConfirmOptions {
  title: string
  /** What will happen, in one sentence, naming what is affected. */
  body: string
  /** The label on the confirming button. A verb, never "OK". */
  confirmLabel: string
  /**
   * Whether this can be taken back, and how.
   *
   * `undoable` gets a plain note; `permanent` gets the warning treatment and a slower
   * path to the button. Discarding unstaged work is the permanent case that people
   * routinely assume is not, which is the whole reason this parameter exists.
   */
  recovery: 'undoable' | 'permanent'
  /** Extra detail — a file list, a branch name. Rendered smaller, below the body. */
  detail?: string[]
}

let active: (() => void) | null = null

/**
 * Shows the dialog and resolves to whether the user went through with it.
 *
 * Resolves false on Escape, on the backdrop, and on Cancel — every exit that is not the
 * confirm button, so a mis-click never destroys anything.
 */
export function confirm(options: ConfirmOptions): Promise<boolean> {
  // A second dialog would stack invisibly over the first and answer the wrong question.
  active?.()

  return new Promise<boolean>((resolve) => {
    const host = document.createElement('div')
    host.className = 'confirm-backdrop'

    const permanent = options.recovery === 'permanent'

    const detail =
      options.detail && options.detail.length > 0
        ? `<ul class="confirm-detail">${options.detail
            .slice(0, 8)
            .map((line) => `<li>${esc(line)}</li>`)
            .join('')}${
            options.detail.length > 8
              ? `<li class="confirm-more">and ${options.detail.length - 8} more</li>`
              : ''
          }</ul>`
        : ''

    host.innerHTML = `
      <div class="confirm" role="alertdialog" aria-modal="true" aria-labelledby="confirm-title">
        <div class="confirm-title" id="confirm-title">${esc(options.title)}</div>
        <div class="confirm-body">${esc(options.body)}</div>
        ${detail}
        <div class="confirm-recovery ${permanent ? 'permanent' : 'undoable'}">
          ${
            permanent
              ? 'This cannot be undone — the content is not in git, so there is nothing to recover it from.'
              : 'This can be undone afterwards.'
          }
        </div>
        <div class="confirm-actions">
          <button class="btn" data-confirm-cancel>Cancel</button>
          <button class="btn ${permanent ? 'danger' : 'pop'}" data-confirm-ok>
            ${esc(options.confirmLabel)}
          </button>
        </div>
      </div>`

    document.body.appendChild(host)

    const cancel = host.querySelector<HTMLButtonElement>('[data-confirm-cancel]')!
    const ok = host.querySelector<HTMLButtonElement>('[data-confirm-ok]')!

    const finish = (result: boolean): void => {
      if (active !== close) return
      close()
      resolve(result)
    }

    function close(): void {
      window.removeEventListener('keydown', onKey, true)
      host.remove()
      active = null
    }

    function onKey(event: KeyboardEvent): void {
      if (event.key === 'Escape') {
        event.preventDefault()
        event.stopPropagation()
        finish(false)
        return
      }

      // Enter confirms only where confirming is recoverable. Making the permanent case
      // need a deliberate click is the point of separating the two.
      if (event.key === 'Enter' && !permanent) {
        event.preventDefault()
        finish(true)
      }
    }

    active = close

    // Capture phase, so the app's own global shortcuts do not fire behind the dialog.
    window.addEventListener('keydown', onKey, true)

    cancel.addEventListener('click', () => finish(false))
    ok.addEventListener('click', () => finish(true))
    host.addEventListener('mousedown', (event) => {
      if (event.target === host) finish(false)
    })

    // Focus starts on Cancel: the safe option should be the one an accidental Enter hits.
    cancel.focus()
  })
}

export function isConfirmOpen(): boolean {
  return active !== null
}
