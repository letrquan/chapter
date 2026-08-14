import { call } from './bridge'
import type { SymbolLocation } from './protocol'

/**
 * Quick-open overlay backing Ctrl+P (files) and Ctrl+T (symbols).
 *
 * Queries run against the in-memory index on the backend, so results arrive fast enough
 * to render on every keystroke; the debounce only exists to avoid queueing requests faster
 * than they complete.
 */

export type PaletteMode = 'files' | 'symbols'

export interface PaletteResult {
  primary: string
  secondary: string
  path: string
  line: number
  column: number
}

type PickHandler = (result: PaletteResult) => void

let overlay: HTMLElement | null = null
let input: HTMLInputElement
let list: HTMLElement
let hint: HTMLElement

let mode: PaletteMode = 'files'
let results: PaletteResult[] = []
let selected = 0
let worktreePath: string | null = null
let onPick: PickHandler = () => {}
let queryToken = 0
let debounce: ReturnType<typeof setTimeout> | undefined

const esc = (value: string): string =>
  value.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)

function build(): void {
  overlay = document.createElement('div')
  overlay.className = 'palette-backdrop'
  overlay.innerHTML = `
    <div class="palette" role="dialog" aria-modal="true">
      <input class="palette-input" type="text" spellcheck="false" autocomplete="off" />
      <div class="palette-list"></div>
      <div class="palette-hint"></div>
    </div>`

  document.body.appendChild(overlay)

  input = overlay.querySelector('.palette-input')!
  list = overlay.querySelector('.palette-list')!
  hint = overlay.querySelector('.palette-hint')!

  overlay.addEventListener('mousedown', (event) => {
    if (event.target === overlay) close()
  })

  input.addEventListener('input', () => scheduleQuery())

  input.addEventListener('keydown', (event) => {
    switch (event.key) {
      case 'Escape':
        event.preventDefault()
        close()
        break
      case 'ArrowDown':
        event.preventDefault()
        move(1)
        break
      case 'ArrowUp':
        event.preventDefault()
        move(-1)
        break
      case 'Enter':
        event.preventDefault()
        commit()
        break
    }
  })

  list.addEventListener('click', (event) => {
    const row = (event.target as HTMLElement).closest<HTMLElement>('[data-index]')
    if (!row) return
    selected = Number(row.dataset.index)
    commit()
  })
}

export function openPalette(nextMode: PaletteMode, worktree: string, handler: PickHandler): void {
  if (!overlay) build()

  mode = nextMode
  worktreePath = worktree
  onPick = handler
  results = []
  selected = 0

  input.value = ''
  input.placeholder = mode === 'files' ? 'Go to file…' : 'Go to symbol…'
  hint.innerHTML =
    mode === 'files'
      ? '<kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Enter</kbd> open · <kbd>Esc</kbd> dismiss'
      : '<kbd>↑</kbd><kbd>↓</kbd> navigate · <kbd>Enter</kbd> go to symbol · <kbd>Esc</kbd> dismiss'

  overlay!.classList.add('open')
  input.focus()

  void runQuery('')
}

export function close(): void {
  overlay?.classList.remove('open')
  clearTimeout(debounce)
}

export const isOpen = (): boolean => overlay?.classList.contains('open') ?? false

function scheduleQuery(): void {
  clearTimeout(debounce)
  debounce = setTimeout(() => void runQuery(input.value.trim()), 60)
}

async function runQuery(query: string): Promise<void> {
  if (!worktreePath) return

  // Results can arrive out of order; only the newest query may paint.
  const token = ++queryToken

  let incoming: PaletteResult[]
  try {
    // Held in a local until the staleness check passes. Assigning straight to `results`
    // would let a late reply replace the array that Enter indexes into while leaving the
    // rows on screen unchanged — the user would open a file they never saw.
    incoming =
      mode === 'files'
        ? (await call('searchFiles', { worktreePath, query, limit: 60 })).map(fileResult)
        : (await call('searchSymbols', { worktreePath, query, limit: 60 })).map(symbolResult)
  } catch (error) {
    if (token !== queryToken) return
    results = []
    list.innerHTML = `<div class="palette-empty">${esc(String(error instanceof Error ? error.message : error))}</div>`
    return
  }

  if (token !== queryToken) return

  results = incoming
  selected = 0
  render()
}

function fileResult(path: string): PaletteResult {
  const slash = path.lastIndexOf('/')
  return {
    primary: slash < 0 ? path : path.slice(slash + 1),
    secondary: slash < 0 ? '' : path.slice(0, slash),
    path,
    line: 1,
    column: 1,
  }
}

function symbolResult(symbol: SymbolLocation): PaletteResult {
  return {
    primary: symbol.name,
    secondary: symbol.containerName ?? symbol.path,
    path: symbol.path,
    line: symbol.line,
    column: symbol.column,
  }
}

function render(): void {
  if (results.length === 0) {
    list.innerHTML = '<div class="palette-empty">No matches</div>'
    return
  }

  list.innerHTML = results
    .map(
      (result, index) => `
        <div class="palette-row ${index === selected ? 'selected' : ''}" data-index="${index}">
          <span class="palette-primary">${esc(result.primary)}</span>
          <span class="palette-secondary">${esc(result.secondary)}</span>
        </div>`,
    )
    .join('')
}

function move(delta: number): void {
  if (results.length === 0) return

  selected = (selected + delta + results.length) % results.length
  render()

  list.querySelector('.palette-row.selected')?.scrollIntoView({ block: 'nearest' })
}

function commit(): void {
  const result = results[selected]
  if (!result) return

  close()
  onPick(result)
}
