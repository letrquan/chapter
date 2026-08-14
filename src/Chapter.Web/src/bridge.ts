import type { Api, ApiMethod, Events } from './protocol'

/**
 * Request/response transport over the WebView2 message channel.
 *
 * WebView2 gives us a single fire-and-forget pipe in each direction, so calls are
 * correlated by an incrementing id and resolved against a pending-promise map.
 */

interface HostResponse {
  id: number
  ok: boolean
  result?: unknown
  error?: string
}

interface HostEvent {
  event: string
  payload: unknown
}

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: unknown): void
        addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void
      }
    }
  }
}

type Pending = {
  resolve: (value: unknown) => void
  reject: (reason: Error) => void
  method: string
  timer: ReturnType<typeof setTimeout>
}

/**
 * Generous enough for a cold index of a large repository, short enough that a request
 * lost in the plumbing surfaces as an error rather than a permanently frozen UI.
 */
const CALL_TIMEOUT_MS = 60_000

let nextId = 1
const pending = new Map<number, Pending>()
const listeners = new Map<string, Set<(payload: never) => void>>()

const webview = window.chrome?.webview

if (webview) {
  webview.addEventListener('message', (event) => {
    const message = event.data as HostResponse | HostEvent

    if ('event' in message) {
      dispatchEvent(message)
      return
    }

    const entry = pending.get(message.id)
    if (!entry) return
    pending.delete(message.id)
    clearTimeout(entry.timer)

    if (message.ok) {
      entry.resolve(message.result)
    } else {
      entry.reject(new Error(message.error ?? `${entry.method} failed`))
    }
  })
}

function dispatchEvent(message: HostEvent): void {
  const handlers = listeners.get(message.event)
  if (!handlers) return
  for (const handler of handlers) {
    try {
      ;(handler as (payload: unknown) => void)(message.payload)
    } catch (error) {
      console.error(`event handler for '${message.event}' threw`, error)
    }
  }
}

/** Calls a backend method. Rejects with the backend's error text when it fails. */
export function call<M extends ApiMethod>(
  method: M,
  ...args: Api[M]['params'] extends void ? [] : [Api[M]['params']]
): Promise<Api[M]['result']> {
  if (!webview) {
    return Promise.reject(new Error('Not running inside the Chapter host'))
  }

  const id = nextId++
  const params = args[0] ?? null

  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id)
      reject(new Error(`'${method}' timed out after ${CALL_TIMEOUT_MS / 1000}s`))
    }, CALL_TIMEOUT_MS)

    pending.set(id, {
      resolve: resolve as (value: unknown) => void,
      reject,
      method,
      timer,
    })
    webview.postMessage({ id, method, params })
  })
}

/** Subscribes to a backend event. Returns an unsubscribe function. */
export function on<E extends keyof Events>(
  event: E,
  handler: (payload: Events[E]) => void,
): () => void {
  let handlers = listeners.get(event)
  if (!handlers) {
    handlers = new Set()
    listeners.set(event, handlers)
  }
  handlers.add(handler as (payload: never) => void)

  return () => {
    handlers!.delete(handler as (payload: never) => void)
  }
}

export const isHosted = Boolean(webview)
