import * as esbuild from 'esbuild'
import { rm, mkdir, cp } from 'node:fs/promises'

const watch = process.argv.includes('--watch')

await rm('dist', { recursive: true, force: true })
await mkdir('dist', { recursive: true })
await cp('index.html', 'dist/index.html')

/**
 * The interface font, copied rather than bundled.
 *
 * Letting esbuild inline it through the woff2 loader would give it a content-hashed
 * name, which the stylesheet cannot reference by hand. Copying to a fixed path keeps
 * the @font-face URL stable, and an absolute `/fonts/...` resolves against the virtual
 * host, so the Content-Security-Policy's `font-src 'self'` covers it.
 *
 * Latin subsets only: the UI draws file paths and English labels, and the Cyrillic,
 * Greek and Vietnamese cuts would be a few hundred kilobytes of glyphs never rendered.
 */
const INTER = 'node_modules/@fontsource-variable/inter/files'

await mkdir('dist/fonts', { recursive: true })
for (const subset of ['latin', 'latin-ext']) {
  const file = `inter-${subset}-wght-normal.woff2`
  await cp(`${INTER}/${file}`, `dist/fonts/${file}`)
}

/**
 * Everything is bundled to static files and served from a folder mapped onto a virtual
 * host by WebView2 — no dev server, no CDN, no network access at runtime.
 */
const options = {
  bundle: true,
  format: 'esm',
  target: 'es2023',
  platform: 'browser',
  minify: !watch,
  sourcemap: watch ? 'inline' : false,
  logLevel: 'info',
  outdir: 'dist',
  // The @font-face URLs above are copied by hand, so esbuild must leave them as
  // written rather than trying to resolve them off the filesystem.
  external: ['/fonts/*'],
  entryPoints: {
    app: 'src/main.ts',
    // Monaco computes diffs and tokenises off the main thread. Without this entry the
    // diff editor silently renders as two plain panes with no change highlighting.
    'editor.worker': 'node_modules/monaco-editor/esm/vs/editor/editor.worker.js',
  },
  loader: {
    '.ttf': 'file',   // codicon.ttf, referenced from Monaco's own CSS
    '.woff': 'file',
    '.woff2': 'file',
    '.svg': 'dataurl',
  },
}

if (watch) {
  const ctx = await esbuild.context(options)
  await ctx.watch()
  console.log('watching for changes…')
} else {
  await esbuild.build(options)
}
