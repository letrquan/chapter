/**
 * The keyboard reference, as something you ask for.
 *
 * These bindings used to be printed permanently across the empty editor pane, which made
 * the app's resting face a cheat sheet: material you read once, in front of you every time
 * you closed a file for the rest of the day. It is still the only place the app documents
 * itself, so nothing is dropped — it moved behind ? and the button in the rail foot, which
 * is where somebody looking for it would look.
 *
 * Built lazily and kept, the same way the palette is: the markup is static, and rebuilding
 * it per open would throw away the scroll position for no gain.
 */

const SHORTCUTS: [keys: string, what: string][] = [
  ['<kbd>Ctrl</kbd> <kbd>1</kbd>–<kbd>9</kbd>', 'Switch worktree'],
  ['<kbd>Ctrl</kbd> <kbd>Tab</kbd>', 'Next / previous worktree'],
  ['<kbd>Ctrl</kbd> <kbd>P</kbd>', 'Find file'],
  ['<kbd>Ctrl</kbd> <kbd>T</kbd>', 'Find symbol'],
  ['<kbd>Ctrl</kbd> <kbd>B</kbd>', 'Branches, stashes and tags'],
  ['<kbd>Ctrl</kbd> <kbd>Shift</kbd> <kbd>B</kbd>', 'Worktrees: add, remove, prune'],
  ['<kbd>Ctrl</kbd> <kbd>D</kbd>', 'Diff or code'],
  ['<kbd>Ctrl</kbd> <kbd>Shift</kbd> <kbd>V</kbd>', 'Markdown preview'],
  ['<kbd>Ctrl</kbd> <kbd>PgUp</kbd> <kbd>PgDn</kbd>', 'Previous / next tab'],
  ['<kbd>Ctrl</kbd> <kbd>W</kbd>', 'Close the open file'],
  ['<kbd>Ctrl</kbd> <kbd>S</kbd>', 'Save the open file'],
  ['<kbd>Alt</kbd> <kbd>↑</kbd> <kbd>↓</kbd>', 'Previous / next hunk'],
  ['<kbd>Ctrl</kbd> <kbd>G</kbd>', 'Write the commit message'],
  ['<kbd>Ctrl</kbd> <kbd>Enter</kbd>', 'Commit'],
  ['<kbd>Ctrl</kbd> <kbd>Alt</kbd> <kbd>Z</kbd>', 'Undo the last git operation'],
  ['<kbd>Ctrl</kbd> <kbd>R</kbd>', 'Refresh'],
  ['<kbd>?</kbd>', 'This list'],
]

let overlay: HTMLElement | null = null

function build(): void {
  overlay = document.createElement('div')
  overlay.className = 'help-backdrop'
  overlay.innerHTML = `
    <div class="help" role="dialog" aria-modal="true" aria-label="Keyboard shortcuts">
      <div class="help-head">
        <span class="help-title">Keyboard</span>
        <span class="help-subtitle">Every binding the app has.</span>
      </div>
      <div class="shortcuts">
        ${SHORTCUTS.map(
          ([keys, what]) => `<span class="keys">${keys}</span><span class="what">${what}</span>`,
        ).join('')}
      </div>
      <div class="help-hint"><kbd>Esc</kbd> close</div>
    </div>`

  document.body.appendChild(overlay)

  // Backdrop only — a mousedown that started inside the card and ended on the backdrop is
  // a text selection being dragged, not a dismissal.
  overlay.addEventListener('mousedown', (event) => {
    if (event.target === overlay) close()
  })
}

export function open(): void {
  if (!overlay) build()
  overlay!.classList.add('open')
}

export function close(): void {
  overlay?.classList.remove('open')
}

export function isOpen(): boolean {
  return overlay?.classList.contains('open') === true
}

export function toggle(): void {
  if (isOpen()) close()
  else open()
}
