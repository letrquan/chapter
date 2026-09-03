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
Claude will write the commit message if you would rather not. Branches, stashes, tags and
remotes are one keystroke away: fetch, pull and push keep their progress in the window, and
the branch list knows which worktree already has a branch open, so switching to it takes you
there rather than failing. Each worktree also has a paginated commit history (`Ctrl` `H`),
with a branch graph, full messages and merge-parent metadata; click a changed file to inspect
that commit in the same Monaco diff view without losing the worktree you were reviewing. The
history detail can also cherry-pick or revert the selected commit, including a chosen merge
parent; `C` and `R` are contextual shortcuts while that timeline is focused. Clean mutations
are undoable, while a conflict stays visible for you to resolve. The open file has its own
history action, which follows renames, and Code mode can add line-level blame markers with
commit details on hover.

**Compare agents side by side.** From the worktree refs panel, choose another usable
worktree to open a read-only comparison of their live snapshots. Tracked and non-ignored
untracked files appear in the shared list, ignored output stays out, exact renames show both
paths, and selecting a text file opens both checkouts in Monaco without changing the active
worktree or its tabs. Close the comparison to return exactly where you were.

**Session links stay local.** When Claude Code, Book or Codex has a session log on disk for a
worktree, the refs panel marks it with an `agent` badge and offers an external-link action.
Chapter reads only bounded metadata prefixes to match the worktree; transcript text never
crosses the bridge. Opening a result performs a fresh provider/id lookup and lets the host
open only a validated `.jsonl` file inside the known local session store.

It is a review cockpit, not an IDE.

## Install

Chapter is in **beta**. Grab `Chapter-win-Setup.exe` from the
[latest release](https://github.com/letrquan/chapter/releases) and run it.

It installs per-user into `%LOCALAPPDATA%\Chapter` — no administrator prompt, no
system-wide footprint — and puts shortcuts on the desktop and in the Start menu. The .NET
runtime is bundled, so nothing else has to be installed first. Uninstall through Windows'
own Apps list.

### Updates

Chapter updates itself. On launch it asks GitHub whether a newer release exists and, if one
does, downloads it in the background while you carry on. Nothing is replaced until you
restart: when a build is waiting, an arrow appears in the rail's footer, and pressing it
restarts into the new version. Ignoring it costs nothing.

The version you are running, and the state of any update, are in the help panel (`?`), which
also has a **Check for updates** button for when you would rather not wait for the next launch.

While the version you run is a prerelease — anything with a `-beta.n` suffix — you are
offered prereleases. Install a stable build and you stop seeing them, without changing a
setting.

Only the first download is large. Updates after that are deltas against the build you have,
which are a few megabytes rather than seventy.

A copy built from source, or unzipped from `Chapter-win-Portable.zip`, has no installation to
replace and does not update itself. The help panel says so rather than claiming to be current.

## Requirements

| | |
|---|---|
| .NET SDK | 10.0+ |
| Node.js | 20+ (build only — the app does not run Node) |
| WebView2 runtime | Ships with Windows 11; otherwise install the Evergreen runtime |
| git | On `PATH`; Git Credential Manager is used when the configured remote needs sign-in |
| A model | Optional — only for generated commit messages. A Claude key, an OpenAI-compatible key, or a local endpoint such as Ollama, which needs no key at all. |

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

## Releasing

The tag is the trigger, and the version in `Directory.Build.props` is the truth. Bump the
file, commit it, then tag the same version:

```bash
git tag v0.1.0-beta.2
git push origin main --tags
```

`.github/workflows/release.yml` takes it from there: it refuses a tag that disagrees with
`Directory.Build.props`, runs the tests, builds through `build/pack.ps1`, and publishes a
GitHub release carrying the installer, the portable zip, and the packages the running app
downloads. A version with a prerelease suffix is published as a prerelease, which is also
what decides whether existing beta users are offered it.

The GitHub release *is* the update server — `VelopackUpdater` reads this repository's
releases and nothing else — so deleting one takes it back from everybody who has not
installed it yet.

To build the same packages locally, without publishing anything:

```powershell
dotnet tool install -g vpk --version 1.2.0
pwsh build/pack.ps1
```

They land in `artifacts/releases`. Deltas are built against whatever earlier packages are
already in that directory, which is why CI downloads the previous release before packing —
an empty directory produces a correct release in which every update is a full download.

Nothing is code-signed yet, so Windows SmartScreen warns on first run. That is the next
thing worth buying.

## How it fits together

```
src/Chapter.Core/     Everything that is not UI. Fully testable without a window.
  Git/                git.exe plumbing: worktrees, status, diffs, base resolution
  Indexing/           Roslyn syntactic index, file watcher, fuzzy search
  Editors/            Rider / VS Code detection and launching
  Ai/                 Commit messages: the keys, the diff budget, the prompt
    Providers/        The seam, and the two dialects behind it
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
from a folder mapped onto a virtual host, so there is no local web server, and the page itself
never makes a network request. Git remote sync and commit-message generation both run in the
backend; remote sign-in is handed to Git Credential Manager rather than a terminal prompt.

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

**Two Chapter windows do not race each other.** Every mutation takes a short repository-wide
lease, held from re-reading the repository's state to classifying git's result, and linked
worktrees resolve to their common git directory so they all queue on the same one. Git's
`index.lock` is not enough on its own: it serializes one low-level write, while the failure
that matters here is two windows each reading the same branch and stash list and then acting
on a snapshot the other has already invalidated — both writes succeed individually. The lease
is a byte-range lock rather than a lock file, so the operating system releases it if Chapter
is killed and there is no stale marker for the next window to break. Waiting is capped at two
seconds and then refused by name — *another Chapter instance is writing this repository* —
because a button that silently blocks looks exactly like one that has hung. It says nothing
about other programs: against Rider or a terminal, the fingerprint checks above are still what
stands between an agent's write and yours.

**The stash is repository-wide, and this app is the one that notices.** `refs/stash` lives in
the *common* git directory, so every worktree in a repository shares a single stash list: an
entry made in one appears in all of them, and `stash@{0}` renumbers whenever any of them
stashes. Every other git client can treat `stash@{n}` as an identity because only one thing
is ever stashing; here an agent in another worktree can renumber the list between it being
drawn and a button being pressed. So every stash action carries the commit sha the list
displayed, and the backend refuses when the entry at that index is no longer that object —
the same move the hunk fingerprint makes, for the same reason. `apply` is then named by sha
outright; `pop` and `drop` cannot be, since removing an entry is inherently positional and
git rejects a raw commit for both.

**Switching branches is attempted, not pre-checked.** Git carries uncommitted changes across
whenever no file differs between the two branches — which is most of the time, and is exactly
the "carry my changes" option other clients offer as a choice. Asking "shall I stash first?"
whenever the tree is dirty would put a dialog in front of a switch that was going to work, so
Chapter tries it and turns only git's actual refusal into the question. Answering it stashes,
switches, restores and drops the stash.

That path is the one place in the app where the user's work exists in exactly one place for a
moment, so none of its failures are allowed to end quietly. If the switch fails the stash is
popped back; if *that* pop also fails — an agent writing to the same files in that second,
which here is ordinary rather than exotic — the message names the stash the work is sitting
in. And a restore that conflicts is reported as a successful switch whose changes are still
stashed, because "could not switch" would send you looking for them on the wrong branch.

**The branch list knows where a branch already is.** `git for-each-ref` reports
`%(worktreepath)` in the same invocation as the name and the tracking counts, so the list
knows which worktree has each branch checked out before anything is attempted. Switching to a
branch another worktree holds is refused by git — `fatal: 'x' is already used by worktree at
…` — and the useful response is not an error but a destination, so that row takes you to that
worktree instead. It has its own failure kind (`CheckedOutElsewhere`) rather than being filed
under "would lose changes", because nothing is at risk and there is nothing to force.

**Remote sync stays in the window.** The Remotes section lists each configured fetch and push
endpoint, with embedded URL credentials hidden. Fetch, pull and push return immediately and
stream Git's transfer output into a progress strip; a slow operation can be cancelled without
extending the bridge timeout. Pull asks whether to merge, rebase or require fast-forward, and
the force action always uses `--force-with-lease`. Ahead/behind badges are refreshed after a
successful sync and describe the local tracking refs, not an unqueried live server state.

**Pull requests are `gh`, kept at arm's length.** The refs panel's sixth section lists a
repository's pull requests, opens the selected one, checks it out, and creates one from the
current branch. Chapter does not embed a GitHub client: GitHub CLI already owns
authentication, host selection and GitHub Enterprise, so the app supplies non-interactive
arguments and reads the bounded JSON it asked for. Checking a PR out is routed through the
same writer as every other mutation, because it moves the local worktree. If `gh` is missing
or signed out, the section repeats what the CLI said rather than showing an empty list.

**Cloning is a first-class operation, not a prerequisite.** `Ctrl` `Shift` `O` clones a
repository into a folder you choose and streams the transfer into the same progress strip as
a fetch. The destination is checked before Git starts — it must not already exist and its
parent must — because the alternative is a partial directory that neither the app nor the
user asked for. The finished clone joins the workspace without disturbing the worktree you
were reviewing.

**History can follow a file.** The clock in the editor header opens a newest-first timeline for
the active path and uses `git log --follow`, so a rename does not cut the story in half. Selecting
a row still opens the commit's parent comparison; when the file had an earlier name, the
historical diff opens that name rather than pretending the current path existed then. The
timeline is anchored to the `HEAD` it first read, so loading older pages remains stable while
an agent commits new work.

**History is searchable without leaving the timeline.** `Ctrl` `F` focuses the history search
field, where Message, Author, Path and Content modes keep the query's meaning explicit. Message
and author searches are literal and case-insensitive; path search accepts a repository-relative
substring; Content uses Git's `-S` pickaxe to find commits that add or remove exact text. Search
results use the same commit detail, historical diff, cherry-pick and revert actions as the
unfiltered timeline. A mutation checks that the full selected object is still reachable from
the current worktree `HEAD`; this prevents a stale overlay from applying an unrelated object.
For a merge, the parent picker is zero-based in the UI and becomes Git's one-based `-m` value.
Git's `--no-edit` keeps the operation inside the desktop app. If Git reports a conflict,
Chapter does not abort it: the operation marker and conflicted files remain available in the
shared resolution banner.

**Blame stays attached to the code you can see.** In Code mode, the attribution button places a
small gutter marker on each line; hovering it shows the short commit id, author and subject.
New or otherwise uncommitted lines use a separate marker. Blame is offered for the working-tree
scopes only, and a dirty Monaco buffer has to be saved first: Git can attribute the bytes on disk,
not text that exists only in the editor. A new untracked text file is shown as entirely
uncommitted; binary files remain reviewable but have no line attribution.

**Conflicts stay in the window.** Merge, interactive rebase, cherry-pick, revert, mailbox
apply and stash-apply conflicts use one persistent banner. Open a file to see Base, Ours,
Theirs and an editable Result; choose a side, combine both sides, or resolve an individual
marker region. Saving a manual result is fingerprint-checked against the bytes that were
shown, and Stage resolved then enables the operation-specific Continue, Skip or Abort action.
Binary conflicts offer exact Ours/Theirs byte choices, while a stash restore keeps its stash
entry until you explicitly continue. `git rerere` can be enabled, applied, inspected and
forgotten from the bridge.

**Worktrees are managed from the same panel, and never from inside themselves.** Adding,
removing, moving, locking and pruning all run in the repository's *main* worktree, whichever
one you are looking at. `git worktree remove` is perfectly willing to delete the directory the
command is running in, which leaves git standing in a deleted working directory — undefined
on POSIX, and impossible on Windows, where a directory in use cannot be deleted at all. The
main worktree is the one git refuses to remove or move, so the host of the command is never
its target. Removing the worktree you are standing in is an ordinary thing to want here, so
the app lets go of its file watcher and symbol index before git runs rather than after, and
moves you to a sibling in the same repository afterwards.

A new worktree's path is suggested by following whatever layout the repository already uses —
`.worktrees/` nested inside it, or siblings beside it — and where there is no precedent it
suggests a sibling, because a worktree nested inside the main one appears in that worktree's
own `git status` as an untracked directory. In this app that means the repository you are
reviewing grows a phantom change that is really another agent's entire checkout.

**Accepting agent work is a guarded handoff.** In the Worktrees section, select a linked
worktree and press `A` (or its check button) to bring its committed branch into the repository's
main worktree. The source must have no tracked, staged, or ordinary untracked changes and must
not have an operation in progress. Merge keeps the agent boundary with `--no-ff`; cherry-pick
applies the source's linear commits in order, while a source merge history must be accepted with
merge mode. A conflict is left active in the main worktree's existing resolution banner rather
than being discarded. A clean integration records one undo point, including a multi-commit
cherry-pick. Optional source-directory removal is a separate guarded step: the branch, tip and
working-tree status are rechecked, ignored files block removal too, and a source that gained new
work is left in place. If cleanup is refused, the integration still stands and can be undone.

**Rejecting agent work is explicit and permanent.** In the Worktrees section, press `R` (or
the reset button) to preview the branch's commits, tracked changes, ordinary untracked files
and ignored files. The confirmation lists those paths before it deletes them and resets the
branch to its merge base with the repository default branch. The committed tip can be restored
with Undo; files that were never committed cannot. The preview's source/base heads and content
fingerprint are checked again immediately before reset, so a concurrent agent write leaves the
new work in place and refuses the operation.

**Nothing destructive happens without saying whether it can be undone.** One confirmation
dialog covers all of it, and it states recoverability every time rather than relying on a
red button to imply it. Discarding is *permanent* and says so — working-tree content that
was never staged is in no git object, so the reflog cannot bring it back. Committing is not:
undo is offered straight afterwards, labelled with what it would reverse, and it refuses if
anything else has committed in that worktree since.

Deleting a branch and dropping a stash both say *undoable*, and both earned it rather than
being assumed: the branch's tip is resolved before the delete so undo recreates it at exactly
that commit, and a dropped stash is only unreferenced, so `git stash store` puts the entry
back with its contents intact. Those inverses name a ref rather than a commit, which makes
them correct however far HEAD has moved — so unlike the commit undo they are not refused when
an agent commits in between, which in this app is the expected case rather than a rare one.

Removing a worktree is *permanent* both times it asks, and the second question exists because
the two removals lose different things. Git's own check before it refuses is `status`, which
says nothing about ignored files — so a worktree whose only untracked content is a `.env` and
a `node_modules` is "clean" to it and is deleted without a murmur. Nothing in the app puts a
directory back, which is why removal records no undo point at all. Pruning gets the third
answer the dialog can give — nothing is lost, because the directories it forgets are already
gone — and shows `git worktree prune --dry-run` in the dialog, since it is the one action
here that names nothing on screen. A *locked* worktree is asked about separately rather than
overridden: git wants `--force --force` there and the app passes one, because a lock is
somebody's explicit instruction and the way past it is to unlock it.

**Every destructive dialog says what it is about to touch, and asks git rather than guessing.**
Removing a worktree lists its uncommitted, untracked and ignored content before either
question is asked — the path was never the thing at risk. Force-pushing runs `git push
--dry-run` against the remote and names the commits the server would stop having, because
`--force-with-lease` is decided against the server's current tip and a preview computed from
local tracking refs would be confident and wrong in exactly the case the lease exists to
catch. Pruning a remote shows the tracking refs that have gone from it. Deleting a branch
names the commits that would be left with nothing pointing at them — a different question from
the one `git branch -d` asks, which is why the app reports what it measured and still lets git
decide.

A preview is asked, not done: none of them go through the writer, so none appear in the
operation log. The two that contact a server do so with the transfer left out and with
credentials allowed, since a preview that only works against public remotes fails precisely
when someone wanted to check. If one cannot run — no network, no permission — the dialog says
so and stands on its own words rather than blocking the action.

Discarding and dropping a stash get no preview and need none: the diff is already on screen for
one, and the other restores exactly from its undo point.

**Editing is conditional, and unsaved work is never overwritten.** The diff stays read-only
— its left pane is a commit — while the code view becomes editable when the backend confirms
the file can be written back with its encoding and line endings intact. The app reloads the
open file whenever the watcher fires, which was correct while nothing was editable and
destructive the moment something was; a model with unsaved edits is now left alone and you
are told the file changed underneath you.

**A model writes the message, and the app says what that cost.** The commit box has a button
above it that fills the message from what is staged. It is a shortcut to filling a textarea,
never a replacement for one: with no key it offers to take one, with no network it says so
once, and if it refuses or returns nonsense the box is exactly as typeable as it was before.

**Two providers, one seam.** `anthropic` talks to the Claude API through its own SDK;
`openai` talks the `chat/completions` dialect, which means OpenAI-*compatible* rather than
OpenAI — Azure, Ollama, LM Studio, vLLM, llama.cpp, OpenRouter, Together, Groq and the rest.
The second is written against the wire by hand rather than through a vendor SDK, because the
target is the dialect and an SDK tracks one implementation of it.

The dialect is not one thing, though, and two fields have no universally safe answer:
`max_completion_tokens` is required by OpenAI's reasoning models and unknown to older
servers, and `response_format: json_schema` is rejected outright by plenty. So the request
goes out fully featured and steps down when told to — a rejection naming a field drops that
field and retries, at most twice, with every concession recorded in the operation log. That
is cheaper than a table of every server anybody might run, and it self-heals when one of them
changes.

Two consequences worth knowing. A `baseUrl` means no key is required, because Ollama and LM
Studio have no authentication and demanding one would lock out the people most likely to want
this; no `Authorization` header is sent at all rather than an empty one. And reasoning on
these endpoints cannot be switched off the way Anthropic's thinking can while still being
charged against the same ceiling as the reply, so the ceiling is raised there — an unused
allowance costs nothing, a reply cut in half costs the feature.

Only the Anthropic path can count tokens before sending; the dialect has no such endpoint, so
the budget falls back to a local estimate rather than borrowing a tokeniser from another model
family, whose counts would simply be wrong. Prices are listed for Claude models only, so an
OpenAI-compatible generation reports tokens and no dollars — the same rule as any unrecognised
model, and for the same reason.

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
documented as hand-editable, and this README already tells you to open it — so keys live in
`credentials.dat` beside it, encrypted with DPAPI under your Windows account, one entry per
provider. Chapter looks for a key you typed into it first, then the provider's environment
variable (`ANTHROPIC_API_KEY` or `OPENAI_API_KEY`), then — for Anthropic only — an
`ant auth login` profile. The commit box names which one it is using. That last part matters:
Chapter is often launched by an agent harness, and an inherited environment variable belonging
to a different account should be visible rather than inferred.

**Adding another language** means implementing `ILanguageIndexer` and registering Monaco
providers for it. Nothing above that seam is C#-specific.

## Keyboard

| | |
|---|---|
| `Ctrl` `1`–`9` | Switch worktree |
| `Ctrl` `Tab` | Cycle worktrees |
| `Ctrl` `P` | Go to file |
| `Ctrl` `T` | Go to symbol |
| `Ctrl` `B` | Branches, stashes and tags |
| `Ctrl` `Shift` `B` | Worktrees: add, move, lock, remove, prune |
| `Ctrl` `Shift` `O` | Clone a repository |
| `Ctrl` `O` | Add a repository already on disk |
| `Tab` (refs panel) | Next section — branches, worktrees, stashes, tags, remotes, pull requests |
| `→` `←` (refs panel) | The selected row's actions, then the buttons below it |
| `A` (Worktrees panel) | Accept the selected agent worktree into main |
| `R` (Worktrees panel) | Reject and reset the selected agent worktree |
| `[` / `]` | Batch review: previous / next usable worktree |
| `Ctrl` `Alt` `M` | Mark the current worktree reviewed |
| `Ctrl` `H` | Commit history for this worktree |
| `Ctrl` `F` | Search the open history timeline |
| `Ctrl` `Shift` `H` | History for the open file |
| `C` / `R` (history overlay) | Cherry-pick / revert the selected commit |
| `Ctrl` `Alt` `B` | Toggle line blame in Code mode |
| `F12` | Go to definition (C#) |
| `Shift` `F12` | Find usages (C#) |
| `Ctrl` `D` | Toggle diff / code |
| `Ctrl` `\` | Toggle inline / side-by-side diff |
| `Ctrl` `Shift` `E` | Open the current file in Rider or VS Code |
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

`C` and `R` are contextual: they act only while the worktree history overlay has focus, so
`R` remains the normal refresh shortcut everywhere else. Both actions ask for confirmation
and show the selected commit before Git runs.

A few of these are registered on the editor as well as on the window, because the editor
otherwise swallows them: `Ctrl` `H` is Monaco's Replace, `Ctrl` `G` its Go to Line and
`Ctrl` `Shift` `O` its Go to Symbol, while `Ctrl` `B` and `Ctrl` `Shift` `B` simply never
arrived with the caret in a pane. Chapter's meanings win — the app has its own symbol search
on `Ctrl` `T`, and an editor here is a review surface before it is a text editor. `Ctrl` `F`
is the exception, left to Monaco's Find, which nothing else here replaces.

**Every action in the refs panel has a key path.** `↑` `↓` move the selection and `Enter` uses
the row, but a row also carries buttons — check out, compare, lock, push, delete — and `Tab` is
spent cycling the panel's sections. So `→` steps out of the filter into the selected row's
actions and then the buttons beneath the list, `←` walks back, and going off the front returns
you to the filter with the caret where you left it. That is also what makes `A`, `R` and `L`
usable: they are letters, the filter is a text box, and until there was a way to leave it they
only worked after a mouse click had moved focus elsewhere.

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
  "provider": "anthropic",      // or "openai" for anything speaking chat/completions
  "baseUrl": "",                // openai only; empty is api.openai.com
  "model": "claude-opus-5",     // a string, because the model list moves faster than this app
  "effort": "low",              // right for a short scoped task, not merely cheap
  "maxTokens": 1024,            // a commit message is short by definition
  "optionCount": 3,             // how many alternatives the options button asks for, 2–5
  "inputTokenBudget": 24000     // ceiling on the whole request; the diff is cut to fit
}
```

`enabled: false` removes the button entirely rather than showing a disabled one, and stops
the app reading a credential at all. Set `model` to `claude-haiku-4-5` if you would rather
spend a tenth as much; Chapter will not pick cheap on your behalf.

`model` has to match `provider` — switching to `openai` and leaving a Claude model here gets
a "model not found" from the endpoint. Chapter does not validate the pairing, because the
compatible providers serve every model between them and OpenRouter genuinely serves
`anthropic/claude-*` through the OpenAI dialect, so any rule about which names are legal
would be wrong for somebody.

Ollama, entirely local and needing no key at all:

```jsonc
"ai": {
  "provider": "openai",
  "baseUrl": "http://localhost:11434",
  "model": "qwen2.5-coder"
}
```

`effort` is only sent to the Anthropic provider. The dialect's own reasoning controls are not
portable across the servers that implement it, and sending a field one of them rejects would
fail the whole request.

## Not in this version

No build or test runner · no semantic (MSBuild) engine · no semantic navigation for languages
other than C# — diff and browse work for everything Monaco tokenises.

See [docs/ROADMAP.md](docs/ROADMAP.md) for where those sit.
