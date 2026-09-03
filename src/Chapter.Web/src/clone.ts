import { call, on } from './bridge'
import { icons } from './icons'
import type { CloneProgress } from './protocol'

/**
 * Clone is the one repository action that starts without an existing worktree. Keep its
 * form and its detached progress view together so a slow network transfer never turns into
 * an unlabelled spinner in the main shell.
 */

const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
}

const esc = (value: string): string => value.replace(/[&<>"']/g, (c) => ESCAPES[c]!)

interface CloneOptions {
  toast: (message: string, detail?: string, kind?: 'info' | 'error') => void
  pickFolder: () => Promise<string | null>
  defaultDestination: () => string
  onRepository: (path: string) => void | Promise<void>
}

let options: CloneOptions | null = null
let overlay: HTMLElement | null = null
let form: HTMLElement | null = null
let progress: HTMLElement | null = null
let sourceInput: HTMLInputElement
let destinationInput: HTMLInputElement
let recursiveInput: HTMLInputElement
let bareInput: HTMLInputElement
let active: CloneProgress | null = null
let startedId: string | null = null
let settled = false

// A local clone can complete before the response to startClone reaches the page. Keep the
// terminal event until the id is known rather than painting a fresh spinner over the result.
const events = new Map<string, CloneProgress>()
const MAX_EVENTS = 32

function cache(value: CloneProgress): void {
  events.set(value.id, value)
  while (events.size > MAX_EVENTS) {
    const oldest = events.keys().next().value
    if (oldest === undefined) break
    events.delete(oldest)
  }
}

export function initClone(next: CloneOptions): void {
  options = next
  on('cloneProgress', handleProgress)
  on('cloneFinished', handleFinished)
}

export function isOpen(): boolean {
  return overlay?.classList.contains('open') === true
}

export function open(): void {
  if (!options) return
  if (!overlay) build()

  active = null
  startedId = null
  settled = false
  sourceInput.value = ''
  destinationInput.value = ''
  recursiveInput.checked = true
  bareInput.checked = false
  showForm()
  overlay!.classList.add('open')
  sourceInput.focus()
}

export function close(): void {
  if (active?.state === 'running') {
    void cancelOrClose()
    return
  }
  overlay?.classList.remove('open')
  active = null
  startedId = null
  settled = false
}

function build(): void {
  overlay = document.createElement('div')
  overlay.className = 'clone-backdrop'
  overlay.innerHTML = `
    <div class="clone-dialog" role="dialog" aria-modal="true" aria-labelledby="clone-title">
      <div class="clone-head">
        <span class="clone-title" id="clone-title">Clone repository</span>
        <span class="clone-subtitle">Copy a remote or local repository into a new folder.</span>
      </div>
      <div class="clone-form">
        <label class="clone-label" for="clone-source">Source</label>
        <div class="clone-input-row">
          <input id="clone-source" class="clone-input" type="text" spellcheck="false"
                 autocomplete="off" placeholder="https://github.com/org/project.git" />
          <button class="btn small" data-clone-browse-source>${icons.folder}<span>Browse</span></button>
        </div>
        <label class="clone-label" for="clone-destination">Destination folder</label>
        <div class="clone-input-row">
          <input id="clone-destination" class="clone-input" type="text" spellcheck="false"
                 autocomplete="off" placeholder="C:\\work\\project" />
          <button class="btn small" data-clone-browse-destination>${icons.folder}<span>Parent</span></button>
        </div>
        <div class="clone-options">
          <label><input id="clone-recursive" type="checkbox" checked /> Include submodules</label>
          <label><input id="clone-bare" type="checkbox" /> Bare repository</label>
        </div>
        <div class="clone-error" data-clone-error hidden></div>
      </div>
      <div class="clone-progress" data-clone-progress hidden></div>
      <div class="clone-actions">
        <button class="btn" data-clone-cancel>Cancel</button>
        <button class="btn pop" data-clone-start>${icons.download}<span>Clone</span></button>
      </div>
      <div class="clone-hint"><kbd>Enter</kbd> start · <kbd>Esc</kbd> cancel</div>
    </div>`

  document.body.appendChild(overlay)
  form = overlay.querySelector('.clone-form')!
  progress = overlay.querySelector('[data-clone-progress]')!
  sourceInput = overlay.querySelector<HTMLInputElement>('#clone-source')!
  destinationInput = overlay.querySelector<HTMLInputElement>('#clone-destination')!
  recursiveInput = overlay.querySelector<HTMLInputElement>('#clone-recursive')!
  bareInput = overlay.querySelector<HTMLInputElement>('#clone-bare')!

  sourceInput.addEventListener('input', () => {
    if (!destinationInput.value.trim()) destinationInput.value = destinationForSource(sourceInput.value)
  })

  overlay.addEventListener('mousedown', (event) => {
    if (event.target === overlay && active?.state !== 'running') close()
  })
  overlay.addEventListener('click', (event) => {
    const target = (event.target as HTMLElement).closest<HTMLElement>(
      '[data-clone-start], [data-clone-cancel], [data-clone-browse-source], ' +
      '[data-clone-browse-destination], [data-clone-dismiss]',
    )
    if (!target) return

    switch (true) {
      case target.hasAttribute('data-clone-start'):
        void start()
        break
      case target.hasAttribute('data-clone-cancel'):
        void cancelOrClose()
        break
      case target.hasAttribute('data-clone-browse-source'):
        void browseSource()
        break
      case target.hasAttribute('data-clone-browse-destination'):
        void browseDestination()
        break
      case target.hasAttribute('data-clone-dismiss'):
        close()
        break
    }
  })
  overlay.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      event.preventDefault()
      void cancelOrClose()
      return
    }
    if (event.key === 'Enter' && event.target instanceof HTMLInputElement &&
        event.target.type !== 'checkbox') {
      event.preventDefault()
      void start()
    }
  })
}

async function browseSource(): Promise<void> {
  if (!options || active?.state === 'running') return
  const picked = await options.pickFolder()
  if (!picked) return
  sourceInput.value = picked
  if (!destinationInput.value.trim()) destinationInput.value = destinationForSource(picked)
}

async function browseDestination(): Promise<void> {
  if (!options || active?.state === 'running') return
  const parent = await options.pickFolder()
  if (!parent) return
  destinationInput.value = joinPath(parent, sourceName(sourceInput.value))
}

async function start(): Promise<void> {
  if (!options || active?.state === 'running') return

  const source = sourceInput.value.trim()
  const destination = destinationInput.value.trim()
  const error = overlay?.querySelector<HTMLElement>('[data-clone-error]')
  if (!source) {
    showError(error ?? null, 'Enter a repository URL or local path.')
    sourceInput.focus()
    return
  }
  if (!destination) {
    showError(error ?? null, 'Choose a new destination folder.')
    destinationInput.focus()
    return
  }

  if (error) {
    error.hidden = true
    error.textContent = ''
  }

  settled = false
  showProgress({
    id: '',
    source,
    destination,
    state: 'running',
    phase: 'starting',
    message: 'Starting clone…',
  })

  try {
    const started = await call('startClone', {
      source,
      destination,
      recursive: recursiveInput.checked,
      bare: bareInput.checked,
    })
    startedId = started.id

    const buffered = events.get(started.id)
    if (buffered && buffered.state !== 'running') {
      events.delete(started.id)
      handleFinished(buffered)
      return
    }

    active = buffered && buffered.state === 'running'
      ? buffered
      : {
          id: started.id,
          source: started.source,
          destination: started.destination,
          state: 'running',
          phase: 'starting',
          message: 'Waiting for git…',
        }
    if (buffered) events.delete(started.id)
    renderProgress()
  } catch (caught) {
    active = {
      id: '',
      source,
      destination,
      state: 'failed',
      phase: 'failed',
      message: caught instanceof Error ? caught.message : String(caught),
    }
    settled = true
    renderProgress()
    options.toast('Could not start clone', active.message, 'error')
  }
}

async function cancelOrClose(): Promise<void> {
  if (!options) return
  if (!active || active.state !== 'running') {
    close()
    return
  }
  if (!active.id) return

  try {
    const cancelled = await call('cancelClone', { id: active.id })
    if (!cancelled) options.toast('Clone has already finished')
    else {
      active = { ...active, phase: 'cancelling', message: 'Cancelling…' }
      renderProgress()
    }
  } catch (caught) {
    options.toast('Could not cancel clone', caught instanceof Error ? caught.message : String(caught), 'error')
  }
}

function handleProgress(next: CloneProgress): void {
  cache(next)
  if (!startedId || next.id !== startedId) return
  if (next.state !== 'running') return
  active = next
  renderProgress()
}

function handleFinished(next: CloneProgress): void {
  cache(next)
  if (!startedId || next.id !== startedId) return
  active = next
  settled = true
  renderProgress()

  if (next.state === 'completed') {
    const path = next.repositoryPath || next.destination
    options?.toast('Repository cloned', path)
    if (next.repositoryPath) void options?.onRepository(next.repositoryPath)
  } else if (next.state === 'cancelled') {
    options?.toast('Clone cancelled', next.message || next.destination)
  } else {
    options?.toast('Clone failed', next.message || 'Git could not clone that repository.', 'error')
  }
}

function showForm(): void {
  if (form) form.hidden = false
  if (progress) progress.hidden = true
  const actions = overlay?.querySelector<HTMLElement>('.clone-actions')
  if (actions) actions.innerHTML =
    '<button class="btn" data-clone-cancel>Cancel</button>' +
    `<button class="btn pop" data-clone-start>${icons.download}<span>Clone</span></button>`
  const hint = overlay?.querySelector<HTMLElement>('.clone-hint')
  if (hint) hint.innerHTML = '<kbd>Enter</kbd> start · <kbd>Esc</kbd> cancel'
}

function showProgress(next: CloneProgress): void {
  active = next
  if (form) form.hidden = true
  if (progress) progress.hidden = false
  renderProgress()
}

function renderProgress(): void {
  if (!progress || !active) return
  const value = active.percent == null ? null : Math.max(0, Math.min(100, active.percent))
  const terminal = active.state !== 'running'
  const title = active.state === 'completed'
    ? 'Clone complete'
    : active.state === 'cancelled'
      ? 'Clone cancelled'
      : active.state === 'failed' ? 'Clone failed' : 'Cloning repository'
  progress.innerHTML = `
    <div class="clone-progress-title">${esc(title)}</div>
    <div class="clone-progress-path" title="${esc(active.destination)}">${esc(active.destination)}</div>
    <div class="clone-progress-bar"><span style="width:${value ?? 0}%"></span></div>
    <div class="clone-progress-meta">
      <span>${esc(active.phase || 'working')}</span>
      <span>${value == null ? '' : `${value}%`}</span>
    </div>
    <div class="clone-progress-message">${esc(active.message || 'Waiting for git…')}</div>`

  const actions = overlay?.querySelector<HTMLElement>('.clone-actions')
  if (actions) {
    actions.innerHTML = terminal
      ? '<button class="btn pop" data-clone-dismiss>Done</button>'
      : '<button class="btn danger" data-clone-cancel>Cancel clone</button>'
  }
  const hint = overlay?.querySelector<HTMLElement>('.clone-hint')
  if (hint) hint.innerHTML = terminal ? '<kbd>Esc</kbd> close' : 'The clone can take a while · <kbd>Esc</kbd> cancel'

  if (terminal && !settled) settled = true
}

function showError(element: HTMLElement | null, text: string): void {
  if (!element) return
  element.hidden = false
  element.textContent = text
}

function sourceName(source: string): string {
  const trimmed = source.trim().replace(/[\\/]+$/, '')
  const slash = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'))
  let name = slash >= 0 ? trimmed.slice(slash + 1) : trimmed
  name = name.replace(/\.git$/i, '')
  return name || 'repository'
}

function joinPath(parent: string, child: string): string {
  const separator = parent.includes('\\') || /^[A-Za-z]:/.test(parent) ? '\\' : '/'
  return parent.replace(/[\\/]+$/, '') + separator + (child || 'repository')
}

function destinationForSource(source: string): string {
  const base = options?.defaultDestination() ?? ''
  return base ? joinPath(base, sourceName(source)) : ''
}
