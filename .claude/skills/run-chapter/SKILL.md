---
name: run-chapter
description: Build, launch, screenshot and drive the Chapter desktop app (WPF + WebView2, Windows-only). Use when asked to run or start Chapter, take a screenshot of it, click through its UI, or confirm a change works in the real app rather than only in tests.
---

# Running Chapter

Chapter is a WPF shell hosting a WebView2 control. There is no automation
surface — nothing outside the process can reach the front-end's DOM — so the app
is driven at the Win32 level by `driver.ps1`: capture with `PrintWindow`, click
with `mouse_event`, type with `SendInput`.

All paths below are relative to the repo root (the directory holding
`chapter.slnx`). The driver lives at
`.claude/skills/run-chapter/driver.ps1`.

**Windows only.** This is WPF plus the Edge WebView2 runtime, and neither runs
anywhere else.

On Linux, use the `run-chapter-linux` skill. It cannot run this executable
either — it replaces the window with Electron and the WebView2 message pipe with
a WebSocket, and drives the same `Chapter.Core` and the same front-end
underneath. Prefer this skill wherever you have Windows: it is the real app.

## Prerequisites

Verified on Windows 11 with:

| | |
|---|---|
| .NET SDK | `10.0.303` (`dotnet --version`) |
| Node.js | `v24.16.0` (`node --version`), npm `11.13.0` |
| WebView2 runtime | ships with Windows 11 |
| git | on `PATH` |

## Build

The front-end is a separate build step that `dotnet build` does **not** run. Do
it first — a Debug build serves the UI straight from `src/Chapter.Web/dist`, so
if that folder is empty the app launches to a blank window and the build emits
its `CHAPTER001` warning.

```powershell
Set-Location src\Chapter.Web
npm install
npm run build          # ~7s -> dist\app.js, editor.worker.js, app.css
Set-Location ..\..
dotnet build chapter.slnx -v m --nologo
```

`dotnet build` finishes in about 2s incrementally and should report
`0 Warning(s)`. A `CHAPTER001` warning means you skipped `npm run build`.

Optional checks:

```powershell
Set-Location src\Chapter.Web; npm run typecheck; Set-Location ..\..
dotnet test chapter.slnx --nologo -v q      # 346 passed, ~44s
```

## Run — agent path

The loop is **`shot` → look at the PNG → `click` the pixel you saw**. The
captured bitmap is exactly the window rect, so coordinates read off the
screenshot are the coordinates you pass to `click`. No scaling, no offset.

```powershell
$d = ".\.claude\skills\run-chapter\driver.ps1"

& $d status                                   # is it already running? (READ THIS FIRST)
& $d launch -Repo I:\path\to\some\repo        # -Repo optional; omit to open with remembered repos
& $d shot -Out shot.png                       # then actually open shot.png and look
& $d click -X 134 -Y 230                      # window-relative pixels
& $d key -Combo ctrl+p                        # ctrl+p, ctrl+shift+v, f12, alt+down, escape, 1-9
& $d type -Text vitest                        # literal text into the focused field
& $d quit
```

**Check `status` before you inject anything.** Chapter is a review cockpit the
user may be sitting in front of, with `Discard hunk` (permanent, per the README)
and `Commit` on screen. If an instance is already running, it is probably
theirs — ask before clicking into it.

**Click into the web content before sending keys.** Keyboard events go to
whatever the WebView2 render widget considers focused; straight after launch
that is nothing, and keys are dropped silently. One `click` on a read-only area
(the left diff pane is safe) fixes it for the rest of the session.

This exact sequence is verified end to end:

```powershell
& $d status
& $d shot  -Out before.png
& $d click -X 800 -Y 650      # focus: left (read-only) diff pane
& $d key   -Combo ctrl+p      # "Go to file..." palette opens
& $d type  -Text vitest       # fuzzy filter narrows the list
& $d shot  -Out after.png
& $d key   -Combo escape      # dismiss; app returns to its previous state
```

Useful stable coordinates: the worktree rail is a fixed-width column at
`x < 250` whose rows do **not** move when the window is resized — the first
worktree row sits at `y = 134`, subsequent rows every ~29px, with group headers
between repos. Clicking a rail row switches worktree and re-reads git. Everything
right of the rail reflows with window size, so re-`shot` before clicking there.

## Run — human path

```powershell
.\src\Chapter.App\bin\Debug\net10.0-windows\Chapter.App.exe I:\path\to\repo
```

A window opens; close it to exit. Repos are remembered in
`%LOCALAPPDATA%\Chapter\settings.json`. While working on the front-end,
`npm run watch` in `src/Chapter.Web` plus `Ctrl` `R` in the app is enough — no
C# rebuild.

## Gotchas

Every one of these cost time in a real session.

- **`PrintWindow` needs flag `2` (`PW_RENDERFULLCONTENT`).** Without it the WPF
  chrome captures fine and the entire WebView2 client area comes back white,
  which looks exactly like "the UI failed to load". The driver always passes it.
- **`WScript.Shell` `SendKeys` does nothing.** It emits virtual-key events with
  no scan code, and Chromium's input pipeline in the WebView2 render widget
  drops them. You get no error — the app simply ignores the keystroke. Use
  `SendInput` with a `MapVirtualKey` scan code, which is what `driver.ps1 key`
  does.
- **`SetForegroundWindow` fails silently from an agent shell.** Windows'
  foreground lock refuses activation requests from a background process, and the
  call just returns without raising. Input then goes to whatever window really
  is in front, so `click` and `key` print their success message while the app
  never sees them — and your next screenshot is byte-identical to the last one,
  which reads as "the app ignored my keystroke" rather than "my keystroke went
  somewhere else". The driver attaches to the foreground thread's input queue to
  lift the lock, then *verifies* activation and throws if it did not take.
  Identical consecutive screenshots are the tell.
- **`MainWindowHandle` is cached on the `Process` object.** Polling
  `$p.MainWindowHandle` on a stale object returns `0` forever. Re-`Get-Process`
  on every poll.
- **A minimized window reports rect `(-32000,-32000)` and captures blank.**
  Indistinguishable from a crashed render process unless you check. The driver
  restores via `ShowWindow(9)` first and throws if it can't.
- **Re-read the window rect before every click.** Each agent tool call is a
  fresh PowerShell process, and between two of your calls the user can move,
  resize, maximize or minimize the window. A click computed from a stale rect
  lands outside the app — on the desktop, on the taskbar, or on whatever is
  behind it. The driver refuses out-of-bounds points for this reason.
- **The window appears ~7s before WebView2 has painted.** `launch` waits for
  `MainWindowHandle` and then sleeps; capturing earlier gives a white frame.
- **Switching worktree restores that worktree's previously-open tabs
  asynchronously.** A capture taken immediately after the click can still show
  "Nothing open" while tabs are on their way in. Wait, then re-shot.
- **Opening a `.ts`/`.tsx` file used to raise stacked error toasts** reading
  `Missing requestHandler or method: getSyntacticDiagnostics`. Fixed:
  `src/Chapter.Web/src/editor.ts` now turns off the worker-backed providers for
  TypeScript, JSON, CSS and HTML, since this build ships only the generic
  `editor.worker.js` and semantic navigation here is C#-only anyway.
  Tokenisation is unaffected, so every language still highlights. Any file type
  is safe to open.
- **`quit` force-kills after a 3s grace period.** If a file is open with unsaved
  edits, that loses them.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Capture is white below the title bar | Missing `PW_RENDERFULLCONTENT`, or captured before WebView2 painted. Sleep and re-shot. |
| Capture is blank with a small strip at top-left | Window is minimized. `driver.ps1 shot` restores it; if it still fails the process is wedged. |
| `key` / `type` do nothing | Nothing in the web content has focus. `click` a read-only spot first. If you wrote your own sender, you are probably using `SendKeys` — it does not work here. |
| Commands print success but the screenshot never changes | The window was not actually foregrounded, so the input went elsewhere. The driver throws rather than reporting a false success; if it does, click the Chapter window once by hand and retry. |
| `click` reports "outside the window" | Your coordinates came from an older screenshot; re-`shot` and re-read them. |
| `launch` throws "Not built" | Run the Build section. |
| App opens to a blank window | `src/Chapter.Web/dist` is empty — `npm run build`. |
| Blue-on-blue unreadable diff, wrong theme | Theme follows the OS (`prefers-color-scheme`); the app's default preference is `system`. Not a driver problem. |
