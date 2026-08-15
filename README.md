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

Review is the point, but you can also act on what you find: stage by file, hunk or line,
discard, edit, and commit — without leaving the window or losing which worktree you were in.
Claude will write the commit message if you would rather not.

It is a review cockpit, not an IDE.

## Requirements

| | |
|---|---|
| .NET SDK | 10.0+ |
| Node.js | 20+ (build only — the app does not run Node) |
| WebView2 runtime | Ships with Windows 11; otherwise install the Evergreen runtime |
| git | On `PATH` |
| Claude API key | Optional — only for generated commit messages. Everything else works without one. |

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
  Ai/                 Commit messages: the key, the diff budget, the call
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
from a folder mapped onto a virtual host, so there is no local web server, and the page
itself reaches the network never: the one feature that leaves the machine is commit-message
generation, and the request is made by the backend.

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

**Committing lives in the Uncommitted scope**, not behind a switch of its own. The scope
selector answers "which slice of the work", and staged-versus-unstaged is not another slice
— it is the same slice divided by the index. Choosing **Uncommitted** turns the file list
into two groups with a commit box below them.

That view is read from `git diff --cached` and `git diff` directly rather than derived from
the review scan, because the two genuinely disagree: a file you staged and then deleted from
disk appears in neither the branch diff nor the working tree, and committing still includes
it. A commit box that inferred its contents would quietly omit it.

**Patches come from git, never from the editor.** Staging a hunk means building a partial
patch, and the only safe source for one is git's own `diff` output. Under `core.autocrlf`
the working tree holds CRLF while the index holds LF, so a patch generated from the text
Monaco is displaying either fails to apply or applies and rewrites every line ending in the
file. For the same reason the staging controls are drawn from git's hunk boundaries rather
than Monaco's — Monaco computes its own diff and groups changes differently, and a button
anchored to the wrong grouping stages something you never looked at.

Because an agent may be writing to the same worktree, a hunk selection carries a fingerprint
of the diff it was made against. If the file changed in between, the stage is refused rather
than applied to whatever hunk now sits at that index.

**Nothing destructive happens without saying whether it can be undone.** One confirmation
dialog covers all of it, and it states recoverability every time rather than relying on a
red button to imply it. Discarding is *permanent* and says so — working-tree content that
was never staged is in no git object, so the reflog cannot bring it back. Committing is not:
undo is offered straight afterwards, labelled with what it would reverse, and it refuses if
anything else has committed in that worktree since.

**Editing is conditional, and unsaved work is never overwritten.** The diff stays read-only
— its left pane is a commit — while the code view becomes editable when the backend confirms
the file can be written back with its encoding and line endings intact. The app reloads the
open file whenever the watcher fires, which was correct while nothing was editable and
destructive the moment something was; a model with unsaved edits is now left alone and you
are told the file changed underneath you.

**Claude writes the message, and says what that cost.** The commit box has a button above it
that fills the message from what is staged. It is a shortcut to filling a textarea, never a
replacement for one: with no key it offers to take one, with no network it says so once, and
if it refuses or returns nonsense the box is exactly as typeable as it was before.

The interesting problem is not the call, it is what to send. A single staged file can be
fourteen thousand lines, and "send the diff" then spends the context window and the bill on a
request whose answer is one sentence. Generated files — lockfiles, minified bundles,
`*.Designer.cs` — are dropped entirely, since nobody reads a lockfile diff to find out what a
commit did. What is left shares a token budget by water-filling: each file gets an equal
share, files that fit release their surplus, and files that do not split what remains. Eight
small files therefore arrive whole beside a truncated giant, rather than the giant arriving
whole and the eight being dropped — which looks like it is working and produces a message
about one file in a nine-file commit. The budget is measured with the API's own token counter
rather than estimated, and whatever was cut is stated in the prompt, because a model shown
half a diff with no warning describes that half as though it were the change.

Messages come back as structured fields rather than prose, so where a repository has opted
into conventional commits its own type list becomes the response schema and the format is
enforced by the API rather than checked afterwards. The last twenty subjects from `git log`
go in the prompt — if this repository does not use type prefixes, the model is told not to
introduce them. The system prompt and those conventions sit behind a cache breakpoint and the
diff after it, which is why regenerating costs almost nothing.

Every generation is recorded in the operation log with its token count and price, and the
price is shown in the commit box too. Without that nobody can tell whether the feature is
cheap or quietly expensive, and the answer differs by an order of magnitude between models.
A model missing from the price table shows tokens and no dollars rather than a made-up
figure.

**The key is never in `settings.json`.** That file is plaintext in `%LOCALAPPDATA%`, it is
documented as hand-editable, and this README already tells you to open it — so the key lives
in `credentials.dat` beside it, encrypted with DPAPI under your Windows account. Chapter looks
for a key you typed into it first, then `ANTHROPIC_API_KEY`, then an `ant auth login` profile,
and the commit box names which one it is using. That last part matters: Chapter is often
launched by an agent harness, and an inherited environment variable belonging to a different
account should be visible rather than inferred.

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
| `Ctrl` `S` | Save the open file |
| `Ctrl` `G` | Write the commit message with Claude (again to stop) |
| `Ctrl` `Enter` | Commit (from the message box) |
| `Alt` `↑` `↓` | Previous / next hunk |
| `Ctrl` `Alt` `Z` | Undo the last git operation |

`Ctrl` `Z` is deliberately left to Monaco, where it means "undo my typing". Rewinding a
commit is a much larger action and gets its own binding.

## Commit message conventions

Off by default beyond the two rules git's own tooling assumes — a short subject and a blank
second line. Conventional-commit validation is opt-in per repository, because it is house
style in some projects and noise in others:

```jsonc
// %LOCALAPPDATA%\Chapter\settings.json
"commitPolicies": {
  "I:\\path\\to\\repo": {
    "requireConventionalCommit": true,
    "types": ["feat", "fix", "docs", "refactor", "test", "chore"],
    "subjectLimit": 72
  }
}
```

Worktrees inherit their repository's entry. Nothing here ever blocks a commit — these are
conventions, and an app that refuses on its own reading of one is an app that stops you
committing during an incident because the subject ran to 74 characters.

Signing is left to the repository's `commit.gpgsign`. Passing `--no-gpg-sign` on your behalf
would produce unsigned commits on a branch that requires them.

## Generated messages

The key is entered in the commit box, not here — see above for where it goes. Everything
else is a setting, and every default is deliberate:

```jsonc
// %LOCALAPPDATA%\Chapter\settings.json
"ai": {
  "enabled": true,
  "model": "claude-opus-5",     // a string, because the model list moves faster than this app
  "effort": "low",              // right for a short scoped task, not merely cheap
  "maxTokens": 1024,            // a commit message is short by definition
  "optionCount": 3,             // how many alternatives "3 options" asks for
  "inputTokenBudget": 24000     // ceiling on the whole request; the diff is cut to fit
}
```

`enabled: false` removes the button entirely rather than showing a disabled one, and stops
the app reading a credential at all. Set `model` to `claude-haiku-4-5` if you would rather
spend a tenth as much; Chapter will not pick cheap on your behalf.

## Not in this version

No build or test runner · no branch, stash or tag management · no fetch/pull/push · no
merge or rebase · no conflict-resolution UI beyond detecting and listing conflicted files ·
no history or blame view · no worktree create or delete · no cross-worktree comparison · no
semantic (MSBuild) engine · no semantic navigation for languages other than C# — diff and
browse work for everything Monaco tokenises.

See [docs/ROADMAP.md](docs/ROADMAP.md) for where those sit.
