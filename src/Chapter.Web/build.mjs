import * as esbuild from 'esbuild'
import { rm, mkdir, cp } from 'node:fs/promises'

const watch = process.argv.includes('--watch')

await rm('dist', { recursive: true, force: true })
await mkdir('dist', { recursive: true })
await cp('index.html', 'dist/index.html')

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
