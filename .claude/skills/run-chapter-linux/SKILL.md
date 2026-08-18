---
name: run-chapter-linux
description: Build, launch, screenshot and drive the Chapter desktop app on Linux, where WPF and WebView2 cannot run. Use when asked to run or start Chapter, take a screenshot of it, click through its UI, or confirm a change works in the real app rather than only in tests, on a Linux machine. On Windows use run-chapter instead.
---

# Running Chapter on Linux

`src/Chapter.App` is a WPF window hosting a WebView2 control. Neither runs on Linux, and
there is no shim that makes them. **On Windows, use the `run-chapter` skill instead** —
it drives the real executable and is the better path when you have it.

What this skill replaces is only the window. Everything below it is the real app:

| Windows | Linux |
|---|---|
| WPF `Window` | Electron `BrowserWindow` (`shell/main.js`) |
| WebView2 control | Chromium, same engine family |
| WebView2 `postMessage` pipe | WebSocket (`host/wwwroot-shim/webview.js`) |
| `Chapter.Core` + `BridgeDispatcher` | **unchanged** — real code, real git |
| `src/Chapter.Web` | **unchanged** — served as built |

The seam is `BridgeDispatcher.HandleAsync`, which its own doc comment describes as taking
a JSON string and returning one, "so the whole protocol can be exercised without a window".
`host/Program.cs` is the Linux counterpart of `MainWindow.xaml.cs`: it serves `dist/` over
HTTP and pumps the same three things the WPF window does — `HandleAsync`, `EventRaised`,
and the folder picker.

All paths below are relative to the repo root (the directory holding `chapter.slnx`).

## Prerequisites

Verified on Ubuntu with a Wayland session (`XDG_SESSION_TYPE=wayland`, XWayland at `:0`):

| | |
|---|---|
| .NET SDK | `10.0.400` — `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash -s -- --channel 10.0` installs to `~/.dotnet` |
| Node.js | `v24.14.0`, npm `11.9.0` |
| git | on `PATH` |
| zenity | for the "Add repository" folder chooser (`apt install zenity`) |
| a display | `$DISPLAY` or `$WAYLAND_DISPLAY` must be set; there is no headless path to a window |

`Chapter.Core` targets `net10.0-windows` but builds and runs clean on Linux — that TFM is
only a platform annotation, and the Windows-only calls behind it (registry, DPAPI) are
lazy and never reached at startup.

## Build

Three steps, in this order. The front-end is not built by `dotnet build`.

```bash
export PATH="$HOME/.dotnet:$PATH"

cd src/Chapter.Web && npm install && npm run build && cd ../..   # ~1s -> dist/
dotnet build .claude/skills/run-chapter-linux/host                # builds Chapter.Core too
cd .claude/skills/run-chapter-linux/shell && npm install && cd -  # electron, ~100MB
```

Optional checks:

```bash
cd src/Chapter.Web && npm run typecheck && cd ../..
dotnet test tests/Chapter.Core.Tests
```

The test suite reports **396 passed, 14 failed, 24 skipped** on Linux. All 14 failures are
platform artefacts, not regressions: `ApiKeyStoreTests` and `AiBridgeTests` need DPAPI, and
the `RegressionTests` path cases assert on `C:\Windows\...` and backslash separators. If
you see a different count, something really did break.

## Run

```bash
cd .claude/skills/run-chapter-linux/shell
npx electron .                       # opens the repos already in settings.json
npx electron . /path/to/some/repo    # the equivalent of `chapter.exe <path>`
```

A window titled "Chapter" opens. Six seconds after load the shell writes
`shell/window.png` — a capture of the window's own contents — and logs its size and
visibility. **Look at that PNG.** A blank frame is a failure to launch, and on a Wayland
session there may be no other screenshot tool to check with.

Set `CHAPTER_PORT` to move off 5099; the shell passes it to the host and both ends agree
on the origin the page's CSP has to allow.

## Drive it

Playwright's `_electron` attaches to the real window, so this drives the app rather than a
browser tab.

```bash
cd .claude/skills/run-chapter-linux/shell && npm install --no-save playwright
```

```js
import { _electron as electron } from 'playwright'

const app = await electron.launch({
  args: ['.', '/tmp/chapter-testrepo'],
  cwd: '.claude/skills/run-chapter-linux/shell',
  timeout: 60000,
})
app.process().stdout?.on('data', d => process.stdout.write(`| ${d}`))   // host output
const page = await app.firstWindow({ timeout: 60000 })
await page.waitForTimeout(4000)                                        // WebView + git
```

**Pin the worktree before asserting anything.** Which one is active is restored from
`settings.json`, not taken from argv, so passing a repo on the command line adds it to the
rail without necessarily selecting it — and the assertions then run against whichever repo
was open last:

```js
await page.click(`.wt[data-worktree="/tmp/chapter-testrepo"]`)
await page.waitForTimeout(4000)   // switching re-reads git and restores that worktree's tabs
```

Useful selectors, all stable: `.wt[data-worktree="<abs path>"]` (worktree rows), `.file-row`, `#scope-switch
button[data-scope=...]`, `#mode-switch button[data-mode=...]`, `.commit-row`,
`.row-action[data-act="stage"|"unstage"|"discard"]`, `[data-hunk="next"|"apply"|"discard"]`,
`#commit-message`, `.commit-submit`, `.refs-row`, `.refs-sections button[data-section=...]`,
`[data-foot="new-branch"]`, `.palette-row`, `[data-confirm-ok]`, `#add-repo`,
`#theme-toggle`, `#undo`, `#hunk-bar`.

**Run mutations against a scratch repo.** `./testrepo.sh` builds one at
`/tmp/chapter-testrepo` with every state Chapter draws — staged and unstaged edits,
untracked, a deletion, a rename, a two-hunk file, extra branches, a stash, a tag and a
linked worktree. Re-run it to reset. Chapter has `Discard hunk` and `Commit` on screen and
the README calls discard permanent; do not point a click-driver at a real repository.

## Gotchas

Every one of these cost time in a real session.

- **The web root must not be derived from the repo path.** `WEB_ROOT` comes from
  `CHAPTER_SRC` (where Chapter lives), not from `REPO` (what is being reviewed). Deriving
  it from `REPO` means the app opens with no UI the moment you point it at another project.
- **`~/.dotnet` is not on `PATH`.** `dotnet-install.sh` does not add it, and a GUI launcher
  inherits even less of a `PATH` than a shell. `main.js` prefers the absolute path. A child
  process that cannot spawn raises an unhandled `'error'` and the window never appears with
  nothing printed — hence the explicit handler.
- **Playwright's `:has-text("Committed")` also matches "Uncommitted".** The match is a
  case-insensitive substring, so the scope switch silently clicks the wrong button and the
  filter appears not to work. Use `[data-scope="committed"]`.
- **With Code showing there are still three `.monaco-editor` nodes** — the code editor and
  both panes of the hidden diff. `document.querySelector('.monaco-editor textarea')` returns
  whichever is first in the DOM, which is often a read-only diff pane. Filter on
  `offsetParent !== null`.
- **Monaco's hidden textarea reports `readOnly: true` regardless** of the editor's actual
  `readOnly` option. It proves nothing. To test editability, type and check the text landed.
- **`refs.close()` only removes the `open` class.** The overlay stays in the DOM forever, so
  `document.querySelector('.refs')` is truthy whether the panel is open or shut. Test
  `.refs-backdrop.open`. The same is true of the palette (`.palette-backdrop.open`).
- **The hunk bar belongs to the commit view.** It is correctly hidden in the All / Committed
  / Last scopes; switch to Uncommitted and open a file from a `.row-open` to see it.
- **Tabs, modes and scroll positions persist between runs**, per worktree, in
  `~/.local/share/Chapter/settings.json`. A file can reopen in Preview mode from a previous
  session, so a Ctrl+Shift+V that "did nothing" may have toggled preview *off*. Assert the
  mode before and after rather than assuming a direction.
- **Undo raises a confirmation.** `Ctrl+Alt+Z` opens a dialog; nothing moves until
  `[data-confirm-ok]` is clicked. Same for discards.
- **`pkill -f ChapterHost` kills your own shell.** The pattern matches the command line of
  the very command running it, and the tool call dies with exit 144. Use a pattern that
  cannot match itself, like `pkill -f 'ChapterHost[.]dll'`.
- **A stale host holds port 5099** after a killed Electron, and the next launch happily
  connects to *it* — showing the previous repo. Check with `curl -s -o /dev/null -w '%{http_code}'
  http://127.0.0.1:5099/` before concluding anything about a fresh launch.
- **The first `npx electron .` downloads ~115MB** before it does anything, with no output
  for a minute or so. It is not hung.

## Two deviations from the Windows app

Both are forced by the transport, and both are visible in `host/Program.cs`:

- The served `index.html` gets `ws://127.0.0.1:<port>` added to `connect-src`, and a
  `<script>` tag for the shim inserted before `app.js`. The page's CSP predates there being
  a socket to connect to. The file on disk is untouched — the rewrite happens per request.
- The folder picker is `zenity --file-selection --directory` standing in for
  `Microsoft.Win32.OpenFolderDialog`.

## What does not work on Linux, by design

These are Windows APIs, not gaps in the port:

| Feature | Why |
|---|---|
| AI commit messages | `ApiKeyStore` encrypts with DPAPI |
| "Open in external editor" | `EditorLauncher` reads the Windows registry. Reports "No external editor found — set one in settings.json", which is a clean failure |
| Dark title bar | The caption is tinted through `dwmapi.dll`; you get your desktop's standard frame |

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| No window, empty log | The host could not spawn. Check `dotnet` resolves and that `host/bin/.../ChapterHost.dll` exists. |
| `Not built` / blank window | `src/Chapter.Web/dist` is empty — `npm run build` in `src/Chapter.Web`. |
| Window shows the wrong repository | A stale host is still on the port; kill it and relaunch. |
| `firstWindow` times out | The shell waits for the host before creating the window. Read the host lines in the app's stdout. |
| Front-end connects but nothing renders | Look for `[host] front-end connected` in the log. If it is missing, the shim did not load — check the CSP rewrite still matches the string in `index.html`. |
| Theme is light when the desktop is dark | The app's preference is `system` and the page reads `prefers-color-scheme`. Headless Chromium always reports light; a real window follows the desktop. |
