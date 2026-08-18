/*
 * Chapter as a desktop application on Linux.
 *
 * src/Chapter.App is a WPF window hosting a WebView2 control, and neither runs anywhere
 * but Windows. This is the same shape built from the parts that do: a native window
 * owning a Chromium view, pointed at the ChapterHost process which serves the real
 * front-end and pumps the real BridgeDispatcher.
 *
 * The behaviours below are the ones MainWindow.xaml.cs sets on CoreWebView2, kept so the
 * window behaves like the app rather than like a browser.
 *
 *   npx electron .                 # opens the repo this skill lives in
 *   npx electron . /path/to/repo   # the equivalent of `chapter.exe <path>`
 */
const { app, BrowserWindow, shell } = require('electron')
const { spawn } = require('node:child_process')
const { existsSync } = require('node:fs')
const http = require('node:http')
const path = require('node:path')

// .claude/skills/run-chapter-linux/shell -> repo root
const CHAPTER_SRC = path.resolve(__dirname, '../../../..')
const SKILL = path.resolve(__dirname, '..')

// The repository under review. Deliberately separate from CHAPTER_SRC: pointing the app
// at another project must not take the front-end away with it.
const REPO = process.argv[2] && process.argv[2] !== '.' ? path.resolve(process.argv[2]) : CHAPTER_SRC

// dotnet-install.sh puts the SDK in ~/.dotnet, which is not on PATH unless the user has
// added it — and a GUI launcher inherits even less of a PATH than a shell does. Prefer the
// absolute path when it is there, and fall back to whatever PATH offers.
const LOCAL_DOTNET = path.join(process.env.HOME ?? '', '.dotnet/dotnet')
const DOTNET = existsSync(LOCAL_DOTNET) ? LOCAL_DOTNET : 'dotnet'
const HOST_DLL = path.join(SKILL, 'host/bin/Debug/net10.0-windows/ChapterHost.dll')
const WEB_ROOT = path.join(CHAPTER_SRC, 'src/Chapter.Web/dist')

const PORT = process.env.CHAPTER_PORT ?? '5099'
const URL = `http://127.0.0.1:${PORT}/`

// Matches DarkChrome.Shell in MainWindow.xaml.cs, so the frame is already the right
// colour before the page paints its first frame instead of flashing white.
const SHELL_BG = '#050609'

let host = null
let window = null

function startHost() {
  if (!existsSync(HOST_DLL)) {
    console.error(`[shell] no host at ${HOST_DLL}\n[shell] build it: dotnet build ${path.join(SKILL, 'host')}`)
    app.exit(1)
    return
  }

  host = spawn(DOTNET, [HOST_DLL, WEB_ROOT, REPO], {
    stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, CHAPTER_PORT: PORT },
  })
  host.stdout.on('data', (d) => process.stdout.write(`[host] ${d}`))
  host.stderr.on('data', (d) => process.stderr.write(`[host] ${d}`))
  host.on('exit', (code) => console.log(`[host] exited (${code})`))

  // Without this an unspawnable dotnet raises an unhandled 'error' on the child and the
  // window never appears, with nothing printed to say why.
  host.on('error', (error) => {
    console.error(`[shell] could not start ${DOTNET}: ${error.message}`)
    app.exit(1)
  })
}

/** The backend needs a moment; loading before it listens gives a connection-refused page. */
function waitForHost(attempt = 0) {
  return new Promise((resolve, reject) => {
    const probe = http.get(URL, (res) => {
      res.resume()
      resolve()
    })
    probe.on('error', () => {
      if (attempt > 120) return reject(new Error('host did not come up'))
      setTimeout(() => waitForHost(attempt + 1).then(resolve, reject), 250)
    })
  })
}

function createWindow() {
  window = new BrowserWindow({
    width: 1600,
    height: 1000,
    title: 'Chapter',
    icon: path.join(CHAPTER_SRC, 'assets/mark-1024.png'),
    backgroundColor: SHELL_BG,
    autoHideMenuBar: true, // no browser menu; the app draws its own chrome
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
      spellcheck: false,
    },
  })

  // NewWindowRequested: anything outside the app opens in the real browser rather than
  // replacing the UI.
  window.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url)
    return { action: 'deny' }
  })

  // NavigationStarting: same rule for same-window navigation. A link in a rendered
  // Markdown document is content an agent wrote; without this there is no way back.
  window.webContents.on('will-navigate', (event, url) => {
    if (url.startsWith(URL)) return
    event.preventDefault()
    shell.openExternal(url)
  })

  window.loadURL(URL)
  window.on('closed', () => {
    window = null
  })

  // Proof the window painted. There is no guaranteed screenshot tool on a Wayland
  // session, and a blank frame is indistinguishable from a launch failure in `ps`.
  window.webContents.once('did-finish-load', () => {
    setTimeout(async () => {
      const image = await window.webContents.capturePage()
      require('node:fs').writeFileSync(path.join(__dirname, 'window.png'), image.toPNG())
      const bounds = window.getBounds()
      console.log(
        `[shell] window "${window.getTitle()}" ${bounds.width}x${bounds.height} ` +
          `at ${bounds.x},${bounds.y} visible=${window.isVisible()} -> shell/window.png`,
      )
    }, 6000)
  })
}

app.whenReady().then(async () => {
  startHost()
  await waitForHost()
  createWindow()
})

app.on('window-all-closed', () => {
  if (host) host.kill()
  app.quit()
})

app.on('quit', () => {
  if (host) host.kill()
})
