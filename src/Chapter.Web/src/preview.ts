import { marked } from 'marked'
import DOMPurify from 'dompurify'

import { monaco } from './editor'
import { call } from './bridge'

/**
 * Rendered Markdown preview.
 *
 * Markdown in a worktree is untrusted input — an agent wrote it, and a document can carry
 * raw HTML, `javascript:` links, and paths pointing anywhere on disk. Rendering is
 * therefore layered: marked produces HTML, DOMPurify strips anything executable, the
 * page's CSP blocks whatever survives, and image paths are resolved by the backend, which
 * refuses anything outside the worktree.
 */

marked.setOptions({
  gfm: true,      // tables, task lists, strikethrough — what people actually write
  breaks: false,  // a single newline is not a line break, per CommonMark
})

/** Fence languages people write, mapped to the ids Monaco actually knows. */
const FENCE_ALIASES: Record<string, string> = {
  ts: 'typescript',
  tsx: 'typescript',
  js: 'javascript',
  jsx: 'javascript',
  mjs: 'javascript',
  cjs: 'javascript',
  cs: 'csharp',
  'c#': 'csharp',
  sh: 'shell',
  bash: 'shell',
  zsh: 'shell',
  console: 'shell',
  ps: 'powershell',
  ps1: 'powershell',
  py: 'python',
  rb: 'ruby',
  yml: 'yaml',
  md: 'markdown',
  jsonc: 'json',
  json5: 'json',
  golang: 'go',
  rs: 'rust',
  kt: 'kotlin',
  h: 'c',
  hpp: 'cpp',
  'c++': 'cpp',
  docker: 'dockerfile',
  text: 'plaintext',
  txt: 'plaintext',
}

/**
 * Renders in-flight work are cancelled by bumping this. Image resolution and syntax
 * colouring are both async, so without it a slow render patches the DOM of a document the
 * user has already navigated away from.
 */
let generation = 0

export function cancelPreview(): void {
  generation++
}

/**
 * Splits leading YAML front matter off a document.
 *
 * Without this, `---` fences render as a horizontal rule followed by whatever the first
 * key happens to look like — which is how most documents in a docs repo would open.
 */
function splitFrontMatter(source: string): { frontMatter: string | null; body: string } {
  if (!source.startsWith('---')) return { frontMatter: null, body: source }

  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/.exec(source)
  if (!match) return { frontMatter: null, body: source }

  return { frontMatter: match[1]!, body: source.slice(match[0].length) }
}

const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
}

const esc = (value: string): string => value.replace(/[&<>"']/g, (c) => ESCAPES[c]!)

/** Whether a link points somewhere outside the document. */
function isExternal(href: string): boolean {
  return /^[a-z][a-z0-9+.-]*:/i.test(href) && !href.startsWith('#')
}

/** Resolves a document-relative path against the directory holding the document. */
function resolveRelative(documentPath: string, relative: string): string {
  const base = documentPath.split('/').slice(0, -1)
  const parts = relative.replace(/^\.\//, '').split('/')

  for (const part of parts) {
    if (part === '.' || part === '') continue
    if (part === '..') base.pop()
    else base.push(part)
  }

  return base.join('/')
}

export interface PreviewRequest {
  worktreePath: string
  /** Repo-relative path of the document, used to resolve its relative links and images. */
  path: string
  source: string
  /** Called when a relative link to another file in the repo is clicked. */
  onNavigate: (path: string) => void
}

export async function renderPreview(host: HTMLElement, request: PreviewRequest): Promise<void> {
  const token = ++generation

  const { frontMatter, body } = splitFrontMatter(request.source)

  // marked is configured synchronously above, so parse returns a string rather than a
  // promise — but assert it rather than trusting the overload.
  const rawHtml = await Promise.resolve(marked.parse(body))

  const clean = DOMPurify.sanitize(rawHtml, {
    // Anything that could reach the network or execute is dropped outright. The CSP would
    // stop most of it anyway; this makes the document safe independent of the CSP.
    FORBID_TAGS: ['script', 'style', 'iframe', 'object', 'embed', 'form', 'link', 'meta', 'base'],
    FORBID_ATTR: ['srcset', 'formaction', 'ping'],
    ALLOW_DATA_ATTR: false,
  })

  if (token !== generation) return

  host.innerHTML =
    (frontMatter ? `<div class="md-frontmatter"><pre>${esc(frontMatter.trim())}</pre></div>` : '') +
    `<div class="md-body">${clean}</div>`

  // Reset after the content lands — resetting an empty container does nothing, and the
  // browser would otherwise keep the previous document's offset.
  host.scrollTop = 0

  addHeadingAnchors(host)
  wireLinks(host, request)

  // Both are async and independent, so let them run together.
  await Promise.all([resolveImages(host, request, token), colorizeCode(host, token)])
}

/** Gives headings ids so in-document `#anchor` links have something to land on. */
function addHeadingAnchors(host: HTMLElement): void {
  const used = new Set<string>()

  for (const heading of host.querySelectorAll('h1, h2, h3, h4, h5, h6')) {
    const base =
      (heading.textContent ?? '')
        .toLowerCase()
        .trim()
        .replace(/[^\w\s-]/g, '')
        .replace(/\s+/g, '-') || 'section'

    let slug = base
    for (let n = 2; used.has(slug); n++) slug = `${base}-${n}`

    used.add(slug)
    heading.id = slug
  }
}

function wireLinks(host: HTMLElement, request: PreviewRequest): void {
  for (const anchor of host.querySelectorAll('a[href]')) {
    const href = anchor.getAttribute('href') ?? ''

    if (isExternal(href)) {
      // target=_blank routes through WebView2's new-window handler, which opens the
      // user's real browser. A plain link would navigate the app itself away from its
      // own page, with no way back.
      anchor.setAttribute('target', '_blank')
      anchor.setAttribute('rel', 'noopener noreferrer')
      continue
    }

    if (href.startsWith('#')) continue // in-document anchor, handled below

    anchor.classList.add('md-internal')
  }

  host.addEventListener('click', (event) => {
    const anchor = (event.target as HTMLElement).closest('a[href]')
    if (!anchor) return

    const href = anchor.getAttribute('href') ?? ''
    if (isExternal(href)) return // let WebView2 open it externally

    event.preventDefault()

    if (href.startsWith('#')) {
      host.querySelector(`#${CSS.escape(href.slice(1))}`)?.scrollIntoView({ behavior: 'smooth' })
      return
    }

    // A relative link to another file in the repo: open it rather than doing nothing.
    const [path] = href.split('#')
    if (path) request.onNavigate(resolveRelative(request.path, decodeURI(path)))
  })
}

/**
 * Replaces image sources with data URIs fetched through the bridge.
 *
 * The page cannot read the filesystem and its CSP allows no remote origins, so every
 * image starts out broken. Local ones are inlined; anything else gets a labelled
 * placeholder, which is more useful than a broken-image icon.
 */
async function resolveImages(host: HTMLElement, request: PreviewRequest, token: number): Promise<void> {
  const images = [...host.querySelectorAll('img')]

  await Promise.all(
    images.map(async (image) => {
      const src = image.getAttribute('src') ?? ''

      if (src.startsWith('data:')) return

      if (isExternal(src)) {
        replaceWithPlaceholder(image, src, 'remote image not loaded')
        return
      }

      const path = resolveRelative(request.path, decodeURI(src.split('#')[0] ?? src))

      try {
        const asset = await call('getAsset', { worktreePath: request.worktreePath, path, scope: 'branch' })
        if (token !== generation) return

        if (asset.dataUri) image.setAttribute('src', asset.dataUri)
        else replaceWithPlaceholder(image, path, asset.reason ?? 'unavailable')
      } catch {
        if (token === generation) replaceWithPlaceholder(image, path, 'could not be read')
      }
    }),
  )
}

function replaceWithPlaceholder(image: HTMLImageElement, label: string, reason: string): void {
  const placeholder = document.createElement('span')
  placeholder.className = 'md-image-missing'
  placeholder.textContent = `${label} — ${reason}`
  placeholder.title = label
  image.replaceWith(placeholder)
}

/** Syntax-highlights fenced code using Monaco, which is already loaded. */
async function colorizeCode(host: HTMLElement, token: number): Promise<void> {
  const blocks = [...host.querySelectorAll('pre > code')]

  await Promise.all(
    blocks.map(async (block) => {
      const declared = [...block.classList]
        .find((name) => name.startsWith('language-'))
        ?.slice('language-'.length)
        .toLowerCase()

      if (!declared) return

      const language = FENCE_ALIASES[declared] ?? declared
      if (!monaco.languages.getLanguages().some((entry) => entry.id === language)) return

      const source = block.textContent ?? ''

      try {
        const html = await monaco.editor.colorize(source, language, { tabSize: 2 })
        if (token !== generation) return

        block.innerHTML = html
        block.classList.add('md-colorized')
      } catch {
        // Leave the block as plain text; unhighlighted code still reads fine.
      }
    }),
  )
}
