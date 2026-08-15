# Roadmap — from review cockpit to git client

V1 is a **read-only** review tool. That was a deliberate constraint, and it bought a lot:
no save path, no undo, no destructive-action confirmations, no in-progress operation
states, no lock contention with the agents writing in those same worktrees.

Everything below inverts that constraint. That is the real cost of this change — not the
individual features, but the fact that the app becomes a *writer* in worktrees that agents
are also writing to. Phase 0 exists because getting that wrong corrupts someone's work,
and no amount of polish above it compensates.

Ordered so each phase unblocks the next. Items are marked **[BLOCKER]** where later work
depends on them, **[HARD]** where the difficulty is genuinely high, and **[TRAP]** where
the current code will actively fight you.

---

## Phase 0 — Write foundations

Nothing in later phases is safe until these exist.

**Complete.** The backend landed first; the UI half arrived with Phase 1, which is what gave
it something to act on.

- [x] **[BLOCKER]** Mutating git command path. `GitCli` already runs commands, but every
      caller today treats a non-zero exit as "no data". Writes need the opposite: exit
      code, stderr, and *which* mutation failed, surfaced to the user.
      → `GitWriter` + `GitMutation`, with `GitFailureClassifier` mapping git's stderr onto
      the handful of outcomes the UI actually branches on.
- [x] **[BLOCKER][TRAP]** `GIT_OPTIONAL_LOCKS=0` is set on every invocation
      (`GitCli.cs:61`). That's correct for reads — it stops the app taking `index.lock`
      while browsing — and wrong for writes. Split into read and write invocation paths.
      → `GitIntent.Read | Write | Network`; every call declares one.
- [x] **[BLOCKER][TRAP]** `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`
      (`GitCli.cs:60-62`) mean any command needing credentials fails silently rather than
      prompting. Fine for `status`; fatal for `push`. See Phase 5.
      → The seam exists: `GitCli.AllowCredentialPrompts` governs `GCM_INTERACTIVE` for
      network intent alone. Still false, because nothing pushes yet — Phase 5 flips it.
- [x] **[BLOCKER]** `index.lock` contention. An agent running `git add` while you commit
      produces `Unable to create index.lock: File exists`. Detect it, retry with backoff,
      and tell the user *which process* holds it rather than surfacing raw git stderr.
      → Five attempts over ~1.3s, then `GitLock` names the holding process via the Restart
      Manager, and distinguishes a live lock from one a crashed git left behind.
- [x] **[BLOCKER]** Repository operation state. Detect and display in-progress
      merge / rebase / cherry-pick / revert / bisect (`MERGE_HEAD`, `REBASE_HEAD`,
      `CHERRY_PICK_HEAD`, `REVERT_HEAD`, `BISECT_LOG` in the git dir). Half the write
      operations are illegal while one is active, and the UI must say so instead of
      letting git refuse.
      → `RepositoryStateReader`, read from the *worktree's* git dir rather than the common
      one. Wired as a guard on every mutation, with `RunUnguardedAsync` for the commands
      that exist to clear the state the guard notices.
- [x] **[BLOCKER]** Self-write invalidation. The app now mutates the worktree it watches.
      `WorktreeWatcher` will fire on the app's own writes, `WorkspaceService`'s change
      cache will be invalidated by them, and the index will re-parse files the app just
      wrote. Suppress or tag self-originated writes.
      → **Tag, not suppress.** `WorktreeWatcher.BeginSelfWrite` attributes by time window,
      because a mutation's real footprint is unknowable in advance. Suppression was tried
      and reverted: an agent writing inside the window gets the same tag, so dropping
      tagged batches loses the agent's change entirely — which is the one thing this app
      exists to show. One redundant refresh is the cheaper mistake. Phase 1 can coalesce
      by path, where it knows exactly what it wrote.
- [x] **[BLOCKER]** Confirmation model for destructive actions — discard, reset, force
      push, branch delete, worktree remove. One consistent affordance, not per-feature
      dialogs.
      → `confirm.ts`, one dialog for all of them. It states recoverability explicitly on
      every use, because that is the fact that differs between them: a user who learns
      "the red button is undoable" from four actions will assume it of the fifth. Discard
      says *permanent* and means it — working-tree content that was never staged is in no
      git object, so the reflog cannot reach it. Escape and the backdrop both cancel, focus
      starts on Cancel, and Enter only confirms the recoverable cases.
- [x] **[HARD]** Undo, backed by reflog. Almost every git mutation is recoverable
      (`ORIG_HEAD`, reflog, stash); a UI that surfaces "undo that" converts the whole app
      from scary to safe. Do this early — retrofitting undo is much harder.
      → `UndoService`: per-worktree stack of inverse commands, plus the reflog underneath
      it. Refuses when HEAD has moved since — the agent committing between the app's
      mutation and the user's undo is the case that makes this app different.
      → UI landed with Phase 1: a button in the changed-files header labelled with what it
      would actually reverse ("Undo commit \"fix the parser\""), plus `Ctrl` `Alt` `Z`.
      Deliberately not `Ctrl` `Z` — Monaco owns that, and it means "undo my typing", which
      is a very different size of action from rewinding a commit.
- [x] Editable Monaco. Was `readOnly: true, domReadOnly: true`, load-bearing for V1 and now
      conditional rather than deleted — the diff view stays read-only, since its left pane
      is a commit and the right pane of a scoped comparison is not the working tree either.
      → Only the code view lifts it, and only when `FileContentPayload.isEditable` says so.
      **Dirty tracking came with it, and was not optional:** the app reloads the open file
      on every watcher notification, which is correct while nothing is editable and
      destructive the moment something is. An agent touching any file in the worktree fired
      that reload, and `setValue` is not an edit — Monaco's undo stack does not cover it.
      A model with unsaved edits is now never overwritten; the reload is refused and the
      user is told the file changed underneath them, with the choice to keep or reload.
- [x] Save path with encoding preservation. `FileContent.FromBytes` detects UTF-8/UTF-16
      BOMs on read; writes must round-trip the same encoding and line endings, or the app
      silently reformats files.
      → `TextFormat` carries encoding and newline; `WorkingTreeWriter` writes atomically
      through a temp file in the same directory.
- [x] Operation log — what the app did, when, with what git command. The first time it
      does something unexpected, this is the only way to find out what happened.
      → `OperationLog`, in memory for the UI and mirrored to
      `%LOCALAPPDATA%\Chapter\operations.log`, since the question is usually asked after a
      restart.

## Phase 1 — Staging and committing

**Complete.** Chapter is a git client rather than a viewer from here.

- [x] Stage / unstage whole file
      → `StagingService`. Unstage needs two commands, not one: `restore --staged` resolves
      HEAD, and before the first commit there is not one — it exits 128 and leaves the file
      staged. `rm --cached` is the fallback. Paths go to git as `:(literal)` pathspecs,
      because a file genuinely called `a[1].txt` is otherwise a glob that matches nothing
      and stages nothing while reporting success.
- [x] **[HARD]** Stage / unstage **hunk** — the feature that makes a git GUI worth using.
      → `PatchBuilder`, and the important decision is where the patch comes from: git's own
      `diff` output, never the text in the editor. Under `core.autocrlf` the working tree
      holds CRLF and the index holds LF, so a patch generated from what Monaco displays
      fails to apply — or applies and rewrites every line ending in the file. Diffs are
      read as bytes and round-tripped through Latin-1, so a file that is not UTF-8 survives
      byte-for-byte rather than filling with U+FFFD.
      **The controls are drawn from git's hunks, not Monaco's.** Monaco computes its own
      diff and groups it differently, so a button anchored to one of its change regions
      would name a hunk the user never looked at; `getFilePatch` hands the real boundaries
      and bodies to the front-end so both sides count the same things.
- [x] **[HARD]** Stage / unstage **line range** — same mechanism, finer granularity
      → Selection in either diff pane maps onto positions in the hunk body. Both panes are
      read because they carry different halves: an addition exists only on the right, a
      deletion only on the left. The rewrite rules are not symmetric — applying forwards an
      unselected addition is *dropped* and an unselected deletion becomes *context*;
      reversing swaps the two. Getting that backwards yields a patch that applies cleanly
      and stages the opposite of what was asked.
- [x] Discard changes at file / hunk / line level *(destructive — Phase 0 confirmation)*
      → All three. Discarding an untracked file deletes it rather than restoring it, and
      the confirmation says so: "discard changes" reads as "put it back how it was", which
      for a file that was never committed means removing it. The hunk-bar button renames
      itself between *Discard hunk* and *Discard selection* rather than silently changing
      its own blast radius.
- [x] A real staged-vs-unstaged view. The scope switch (All / Uncommitted / Committed /
      Last) had no notion of the index; committing needs one.
      → The Uncommitted scope became it, rather than a fifth button: the switch answers
      "which slice of the work", and staged-versus-unstaged is not another slice but the
      same slice divided by the index. Sourced from `diff --cached` and `diff` directly,
      **not** derived from the review scan — a file staged and then deleted from disk is in
      neither the branch diff nor the working tree, and committing still includes it.
- [x] Commit: message editor, amend, `--signoff`, GPG/SSH signing, co-author trailers
      → The message is passed as a single `-m` argument, newlines and all: it never touches
      a shell, and `-F -` is impossible because `GitCli` closes stdin on every invocation.
      `--cleanup=whitespace` is stated explicitly so a repo's `commit.cleanup=strip` cannot
      eat a line beginning with `#`. Signing defers to the repository's own `commit.gpgsign`
      unless told otherwise — quietly passing `--no-gpg-sign` would produce unsigned commits
      on a branch that requires them. *Signing choice and co-author trailers are on the
      bridge and covered by tests; the commit box currently surfaces amend and sign-off, and
      the other two want a UI.*
- [x] Commit message conventions — subject length, blank second line, conventional-commit
      type/scope validation, configurable per repo
      → `CommitMessagePolicy`, per repository, with worktrees inheriting their repo's entry.
      Nothing here ever blocks a commit: message rules are conventions, and an app that
      refuses on its own reading of one is an app that stops you committing during an
      incident because the subject is 74 characters.
- [x] Guards: nothing staged, detached HEAD, in-progress operation, unresolved conflicts
      → `CommitReadiness`, answered before the message is typed rather than after. A merge
      in progress is a *note*, not a blocker — committing is how a resolved merge concludes,
      and refusing would leave no way out of the state through the app.

## Phase 2 — AI commit messages

Backend is C#, so this lives in `Chapter.Core` using the official SDK
(`dotnet add package Anthropic`). No new process, no Node.

- [ ] **[BLOCKER]** Credential handling. The SDK reads `ANTHROPIC_API_KEY`, or an
      `ant auth login` profile under `~/.config/anthropic/`. For a desktop app, decide:
      reuse an existing profile, or store a key via Windows DPAPI. **Do not** put it in
      `settings.json` — that file is plaintext in `%LOCALAPPDATA%`.
- [ ] **[BLOCKER][HARD]** Diff budgeting. `katclub` has a single staged file at **+14,057
      lines**; a naive "send the diff" blows the context and the bill. Needs:
      - `client.Messages.CountTokens(...)` to measure before sending — **never** estimate
        with a GPT tokenizer, the counts are wrong for Claude
      - a selection strategy: `--stat` first, then full hunks for small files, then
        truncated hunks for large ones, generated/lock files dropped entirely
      - explicit "diff was truncated" signal in the prompt so the model doesn't claim
        completeness it can't have
- [ ] Model: `claude-opus-5` (1M context, $5/$25 per MTok). Expose the choice — some
      people will want `claude-haiku-4-5` for a commit message. Make it a setting; don't
      pick cheap on their behalf.
- [ ] `output_config.effort` — `low` is genuinely right for a short scoped task like this.
      Worth a setting alongside model.
- [ ] `max_tokens` deliberately small (~1024). A commit message is short by definition;
      this is one of the few legitimate reasons to go below the usual default.
- [ ] Streaming (`client.Messages.CreateStreaming`) into the message box, so it feels
      instant rather than a 3-second freeze.
- [ ] **Prompt caching** on the stable prefix — system prompt plus repo conventions.
      Minimum cacheable prefix on Opus 5 is 512 tokens, so a decent convention block
      qualifies. Cache reads are ~0.1× input price; regenerate becomes nearly free.
      Keep the diff *after* the cache breakpoint — it changes every call.
- [ ] **Structured output** (`output_config.format` with a JSON schema) for
      type / scope / subject / body, rather than parsing prose. Makes conventional-commit
      enforcement mechanical.
- [ ] Learn the repo's style — feed the last ~20 subject lines from `git log` so generated
      messages match existing conventions instead of imposing new ones.
- [ ] Regenerate, and "give me 3 options"
- [ ] Handle `stop_reason == "refusal"` and network failure — fall back to a manual
      message box, never block the commit
- [ ] Cost visibility. Show tokens/cost per generation somewhere; without it nobody can
      tell whether this feature is cheap or quietly expensive.
- [ ] Offline: the whole feature must degrade to "type it yourself" with no error spam

## Phase 3 — Branches, stash, refs

- [ ] Branch list per repo (local + remote), with ahead/behind
- [ ] Create / rename / delete branch *(delete is destructive)*
- [ ] **[HARD]** Checkout with a dirty tree — the decision point most GUIs get wrong.
      Offer: stash-and-switch, carry changes, or abort. Never silently discard.
- [ ] Set upstream / track
- [ ] Stash: create (with message, optionally including untracked), list, apply, pop, drop
- [ ] Tags: list, create annotated/lightweight, delete, push

## Phase 4 — History

- [ ] Commit log per worktree, paginated. `git log --format` with a stable field separator.
- [ ] **[HARD]** Graph rendering for branch topology — genuinely fiddly to lay out well
- [ ] Commit detail view — reuses the existing diff view, base = commit's parent
- [ ] File history and blame. Blame maps naturally onto a Monaco gutter decoration.
- [ ] Search history: message, author, path, content (`git log -S`)
- [ ] Cherry-pick and revert *(both can conflict → Phase 6)*
- [ ] **[HARD]** Interactive rebase. This is a project in itself — sequencing, editing,
      conflict handling, abort/continue. Consider deferring past 1.0.

## Phase 5 — Remotes

The credential story is the hard part, not the commands.

- [ ] **[BLOCKER][TRAP]** Undo the no-prompt environment from Phase 0 for network
      operations, and integrate with Git Credential Manager. Right now `GCM_INTERACTIVE=never`
      guarantees an auth prompt can never appear — `push` will fail with an opaque error
      rather than asking for credentials.
- [ ] Fetch / pull / push with progress. These are long-running; the existing 60s bridge
      timeout will cut them off.
- [ ] Pull strategy: merge vs rebase vs fast-forward-only
- [ ] `--force-with-lease` — never plain `--force`
- [ ] Ahead/behind indicators in the worktree rail
- [ ] Remote management: add, rename, remove, prune
- [ ] PR integration via `gh` CLI (create, view, checkout) — optional, high value given the
      agent workflow

## Phase 6 — Conflict resolution

**[HARD]** The hardest UI in the whole roadmap. Budget accordingly.

- [x] Detect conflicted state and list conflicted paths (`git status --porcelain=v2`
      unmerged entries, `u` records — currently skipped by `ParseWorkingState`)
      → Done in Phase 0, because the write guard needs it: `ParseWorkingState` now reads
      `u` records, `ChangedFile.IsConflicted` carries it, and `RepositoryState` lists the
      paths. Everything else in this phase is untouched.
- [ ] **[HARD]** Three-way merge view. Monaco ships a two-way diff editor, not a merge
      editor — ours / base / theirs needs building from multiple editor instances, or
      adopting VS Code's merge-editor approach.
- [ ] Per-conflict actions: take ours, take theirs, take both, edit manually
- [ ] Conflict markers as first-class regions rather than raw `<<<<<<<` text
- [ ] Mark resolved (`git add`), then continue / skip / abort the merge or rebase
- [ ] Conflict resolution during rebase, cherry-pick, revert, and stash-apply — each has
      different continue/abort semantics
- [ ] `git rerere` support, if you hit the same conflicts repeatedly across worktrees

## Phase 7 — Worktree management

The natural home-turf feature — the app is already worktree-shaped.

- [ ] Create worktree from an existing branch or a new one
- [ ] Remove worktree *(destructive)*
- [ ] **Prune stale worktrees** — `heat` has a prunable one right now, and today the app
      can only display it, not fix it
- [ ] Lock / unlock, with reason
- [ ] Move worktree
- [ ] Sensible default paths — support both layouts already handled (`.worktrees/` nested
      and scattered siblings)

## Phase 8 — Agent-workflow differentiators

Nothing else on this list is unique to this app. These are.

- [ ] **Cross-worktree compare** — two agents solved the same task; diff their solutions
      against each other, not just against main. Cut from V1 and still the strongest idea
      here.
- [ ] **Accept this agent's work** — one action: merge or cherry-pick a worktree's branch
      into main, then optionally remove the worktree.
- [ ] **Reject and reset** — discard a worktree's work and reset it to base
- [ ] Batch review — walk every worktree's changes in sequence with a keystroke
- [ ] Link a worktree to the agent session that produced it, if the session log is on disk
- [ ] "What changed since I last looked" — per-worktree review watermark

---

## Cross-cutting

- [x] **Test strategy for mutations.** The current suite is safe because nothing writes.
      Write tests need disposable fixture repos — `RegressionTests` already has the
      `NewRepoAsync` / `Delete` helpers to build on. Never test mutations against the
      real validation repos.
      → `WriteFoundationsTests` creates and destroys its own repo per test and never
      touches the validation repos. `MutationParsingTests` covers the pure half — failure
      classification, operation detection, encoding round-trips — with no repository at all.
      Phase 1 continued the pattern: `StagingTests` (disposable repos, including a
      `core.autocrlf=true` one for hunk patches), `CommitMessageTests` (no repository at
      all) and `CommitBridgeTests` (the JSON seam, where a rename in `Messages.cs` becomes
      a missing field in `protocol.ts` rather than a compile error).
- [ ] **Dry-run / preview** for anything destructive
- [ ] **Long-running operations** — the bridge has a 60s call timeout (`bridge.ts`); clone,
      fetch, and push will exceed it. Needs a progress protocol, not a longer timeout.
- [ ] **Multi-instance safety** — two Chapter windows on the same repo, or Chapter plus
      Rider, both writing
      *(One case is handled: a hunk selection carries a fingerprint of the diff it was made
      against, and the backend refuses when the file changed in between. Without it the user
      approves hunk 2 of one diff and the app stages hunk 2 of another — which, in a
      worktree an agent is actively writing to, is not a hypothetical.)*
- [ ] **Keyboard-first** — the whole point of the app; every new action needs a binding
- [ ] **`.gitattributes`** — still missing, and now that the app writes files, line-ending
      normalization stops being cosmetic

---

## Suggested order

1. ~~**Phase 0**~~ — done. Unavoidable, and the riskiest thing to skip.
2. ~~**Phase 1**~~ — done. Staging, committing. The smallest slice that makes the app a git
   client rather than a viewer.
3. **Phase 2** — AI commit messages. **Next**, and it starts with the one decision the
   roadmap leaves open below: where the API key lives.
4. **Phase 3** — branches and stash, needed before checkout is safe
5. **Phase 7** — worktree management. Cheap, and this app should obviously own it.
6. **Phase 5** — push/pull. Blocked on credentials, so start that spike early.
7. **Phase 4 + 6** — history and conflicts. Both large; conflicts especially.
8. **Phase 8** — the differentiators, once the fundamentals are solid.

Phase 8 is tempting to do first because it's the interesting part. It won't survive
contact with users until Phase 0 exists.
