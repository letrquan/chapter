<p align="center">
  <img src="assets/mark-1024.png" alt="" width="120" />
</p>

# Chapter

A single-window desktop app for reviewing AI-agent work across git worktrees.

Verifying what an agent did normally means opening a separate Rider or VS Code window per
worktree, each re-indexing the same solution. Chapter puts every worktree in one window:
a persistent rail, instant switching with your tabs and scroll position intact, a real
side-by-side diff, and go-to-definition for C#. When you actually want to *change*
something, one keystroke opens the file at that exact line in Rider or VS Code.

It is a review cockpit, not an IDE. Nothing here writes to your files.

## Requirements

| | |
|---|---|
| .NET SDK | 10.0+ |
| Node.js | 20+ (build only — the app does not run Node) |
| WebView2 runtime | Ships with Windows 11; otherwise install the Evergreen runtime |
| git | On `PATH` |

## Build

The front-end is a separate build step and is **not** run by `dotnet build`. Do it first —
`dotnet build` copies whatever is in `src/Chapter.Web/dist` into the app output and warns
(`CHAPTER001`) if there is nothing there.

```bash
cd src/Chapter.Web
npm install        # first time only
npm run build      # produces src/Chapter.Web/dist

cd ../..
dotnet build
```

Then run `src/Chapter.App/bin/Debug/net10.0-windows/Chapter.App.exe`, optionally with a
repository path:

```bash
Chapter.App.exe I:\path\to\your\repo
```

Repositories you add are remembered in `%LOCALAPPDATA%\Chapter\settings.json`.

### Working on the front-end

In a debug build the app serves the UI straight from `src/Chapter.Web/dist`, so
`npm run watch` plus a page reload is enough — no C# rebuild.

```bash
cd src/Chapter.Web && npm run watch
```

## Tests

```bash
dotnet test
```

Tests that need a specific local repository skip themselves when it is absent, so the
suite stays green on any machine.

## How it fits together

```
src/Chapter.Core/     Everything that is not UI. Fully testable without a window.
  Git/                git.exe plumbing: worktrees, status, diffs, base resolution
  Indexing/           Roslyn syntactic index, file watcher, fuzzy search
  Editors/            Rider / VS Code detection and launching
  Contracts/          The front-end protocol, and the dispatcher that serves it

src/Chapter.App/      Thin WPF shell. Hosts WebView2 and pumps messages. Little else.

src/Chapter.Web/      TypeScript + Monaco front-end, built to static files.
  src/protocol.ts     Mirror of Contracts/Messages.cs — change the two together

tests/Chapter.Core.Tests/
```

### Design decisions worth knowing

**WebView2, not Electron.** The UI is HTML and Monaco, but there is no Node and no bundled
Chromium: WebView2 runs in-process with .NET on the Edge runtime already present on
Windows. The entire backend — git, indexing, watching — is C#. The front-end is served
from a folder mapped onto a virtual host, so there is no local web server and no network
access at runtime.

**Monaco does the diffing.** The backend supplies the base side (`git show <sha>:<path>`)
and the working side; Monaco's diff editor renders it. That is also why every language
looks right even though only C# has semantic navigation.

**Syntactic index, not MSBuild.** Symbols come from `CSharpSyntaxTree.ParseText` alone —
no build, no NuGet restore, nothing that can fail because a project will not load.
Measured on a 12,000-file solution: ~4.8s and ~114MB, in the background, while the diff
view is already usable. A full semantic workspace would be exact but costs 10–60s and
hundreds of MB *per worktree*, which is the very thing that makes switching slow in a real
IDE.

The trade-off: resolution is by name, so overloaded methods and same-named types across
namespaces come back as several candidates. Monaco shows them as a chooser rather than
guessing — a wrong jump is worse than a short list.

**Untracked files are merged into the changed set.** `git diff` alone omits them, and new
files are usually the most important thing an agent produced.

**Markdown gets a third view.** `.md`, `.markdown` and `.mdx` files add a **Preview** mode
beside Diff and Code (`Ctrl` `Shift` `V`). Opening a file from the changed list still starts
in Diff — you are reviewing a change — while navigating to one with `Ctrl` `P` or by
following a link opens the rendered document, because then you are reading it.

Markdown in a worktree is untrusted input: an agent wrote it, and it can carry raw HTML,
`javascript:` links, and paths pointing anywhere on disk. Rendering is layered accordingly
— `marked` produces the HTML, DOMPurify strips anything executable, the page's CSP blocks
whatever survives, and image paths are resolved by the backend, which refuses anything
outside the worktree. Local images are inlined as data URIs; anything that cannot be
supplied renders a labelled placeholder saying why, rather than a broken-image icon.
Fenced code is highlighted by Monaco, which is already loaded.

**Four scopes, not one.** The switch above the file list picks which comparison you are
looking at:

| Scope | Comparison | Untracked |
|---|---|---|
| **All** (default) | merge-base with the default branch → working tree | yes |
| **Uncommitted** | HEAD → working tree | yes |
| **Committed** | merge-base → HEAD | no |
| **Last** | HEAD~1 → HEAD | no |

`All` is the union of `Committed` and `Uncommitted`, so nothing is ever hidden by the
default — but in that view a small amber dot marks the files that are not committed yet,
so you can see what is still dirty without switching. Changing scope re-reads every
worktree, because these are genuinely different git comparisons rather than filters over
one result.

**Adding another language** means implementing `ILanguageIndexer` and registering Monaco
providers for it. Nothing above that seam is C#-specific.

## Keyboard

| | |
|---|---|
| `Ctrl` `1`–`9` | Switch worktree |
| `Ctrl` `Tab` | Cycle worktrees |
| `Ctrl` `P` | Go to file |
| `Ctrl` `T` | Go to symbol |
| `F12` | Go to definition (C#) |
| `Shift` `F12` | Find usages (C#) |
| `Ctrl` `D` | Toggle diff / code |
| `Ctrl` `Shift` `V` | Toggle Markdown preview |
| `Ctrl` `PgUp` `PgDn` | Cycle tabs |
| `Ctrl` `W` | Close tab |
| `Ctrl` `R` | Refresh |

## Not in this version

No editing or saving · no build or test runner · no stage/commit/merge · no worktree
create or delete · no cross-worktree comparison · no semantic (MSBuild) engine · no
semantic navigation for languages other than C# — diff and browse work for everything
Monaco tokenises.
