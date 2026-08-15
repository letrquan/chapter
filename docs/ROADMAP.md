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

**Complete.** The first thing the app does that leaves the machine, which shaped every
decision below more than the feature itself did.

**Since landed: not Anthropic-only.** The phase was written assuming one vendor, and that was
never a design decision — just the shape of the first implementation. There is now a seam
(`Ai/Providers/`) with two things behind it: the Claude API through its SDK, and the
OpenAI-compatible `chat/completions` dialect that Azure, Ollama, LM Studio, vLLM, OpenRouter
and the rest speak. Everything below is unchanged for both, except where noted:

- Token counting is real on Anthropic and estimated on the dialect, which has no counting
  endpoint. Borrowing another family's tokeniser would be worse than an honest over-estimate.
- Prompt caching is Anthropic's explicit breakpoint. The seam still carries the boundary
  between the stable prompt and the diff, which is the only thing a provider with automatic
  prefix caching can use anyway.
- Thinking is suppressed on Anthropic and cannot be on the dialect, so the token ceiling is
  raised there instead. An unused allowance costs nothing; a truncated reply costs the feature.
- The dialect has no single answer for `max_completion_tokens` vs `max_tokens`, or for
  whether `response_format` is understood at all — so the request steps down when the endpoint
  says so, at most twice, and each concession reaches the operation log.
- A `baseUrl` means no key is needed. Ollama and LM Studio have no authentication, and they
  are most of the reason anybody asks for this.

- [x] **[BLOCKER]** Credential handling. The SDK reads `ANTHROPIC_API_KEY`, or an
      `ant auth login` profile under `~/.config/anthropic/`. For a desktop app, decide:
      reuse an existing profile, or store a key via Windows DPAPI. **Do not** put it in
      `settings.json` — that file is plaintext in `%LOCALAPPDATA%`.
      → All three, in that order: a key typed into Chapter wins, then the environment
      variable, then a login profile. `ApiKeyStore` encrypts into `credentials.dat`, so keys
      are tied to the Windows account rather than to the disk — one entry per provider, since
      a Claude key and an OpenAI key are two different secrets and switching between them
      should not mean retyping either.
      The order only matters when two exist at once, and the UI names the one it used —
      an inherited environment variable belonging to a different account is exactly the
      kind of thing that should be visible rather than inferred. The key is asked for
      inline in the commit box, because there is no settings screen and it must never be
      the thing `settings.json` is opened for.
- [x] **[BLOCKER][HARD]** Diff budgeting. `katclub` has a single staged file at **+14,057
      lines**; a naive "send the diff" blows the context and the bill. Needs:
      - `client.Messages.CountTokens(...)` to measure before sending — **never** estimate
        with a GPT tokenizer, the counts are wrong for Claude
      - a selection strategy: `--stat` first, then full hunks for small files, then
        truncated hunks for large ones, generated/lock files dropped entirely
      - explicit "diff was truncated" signal in the prompt so the model doesn't claim
        completeness it can't have
      → `DiffDigest`. All three, and the selection strategy is **water-filling** rather
      than first-come: every file gets an equal share, the ones that fit release their
      surplus, and the ones that do not split what is left. That is the difference between
      eight small files arriving whole beside a truncated giant, and the giant arriving
      whole while the eight are dropped — the second reads as working and produces a
      message about one file in a nine-file commit. Truncation happens on hunk boundaries
      only; half a hunk is not a diff. The file list is sent before the patches, so a cut
      anywhere downstream loses hunks rather than losing the shape of the change.
- [x] Model: `claude-opus-5` (1M context, $5/$25 per MTok). Expose the choice — some
      people will want `claude-haiku-4-5` for a commit message. Make it a setting; don't
      pick cheap on their behalf.
      → `settings.json`, under `ai`. A string rather than an enum: the model list moves
      faster than this app ships.
- [x] `output_config.effort` — `low` is genuinely right for a short scoped task like this.
      Worth a setting alongside model.
      → Both, and an unrecognised value falls back to `low` rather than failing — that
      file is hand-edited.
- [x] `max_tokens` deliberately small (~1024). A commit message is short by definition;
      this is one of the few legitimate reasons to go below the usual default.
      → 1024, and proportionally more for a multi-option request, where three bodies do
      not fit in one message's worth of room.
- [x] Streaming (`client.Messages.CreateStreaming`) into the message box, so it feels
      instant rather than a 3-second freeze.
      → And it is what forced the shape of the whole feature. The bridge gives up on a
      call after 60s, so `generateCommitMessage` returns an id at once and the text
      arrives on the event channel — the progress protocol the cross-cutting section
      below asks for, in its first use.
- [x] **Prompt caching** on the stable prefix — system prompt plus repo conventions.
      Minimum cacheable prefix on Opus 5 is 512 tokens, so a decent convention block
      qualifies. Cache reads are ~0.1× input price; regenerate becomes nearly free.
      Keep the diff *after* the cache breakpoint — it changes every call.
      → Two system blocks, the breakpoint on the second. The diff is in the user message,
      after it. A prefix under the minimum is simply not cached, so there is nothing to
      detect or work around.
- [x] **Structured output** (`output_config.format` with a JSON schema) for
      type / scope / subject / body, rather than parsing prose. Makes conventional-commit
      enforcement mechanical.
      → And where a repository sets `requireConventionalCommit`, *its own* type list
      becomes the schema's enum, so the API cannot return one this repo does not use.
      Streaming and structured output are not in tension: the JSON is scanned for the
      subject and body as it arrives, purely so the box fills, and the finished text is
      parsed properly and replaces it. That scanner rereads the whole buffer every time —
      escapes and multi-byte characters straddle network frames, and a parser that cannot
      see a frame boundary cannot get one wrong.
- [x] Learn the repo's style — feed the last ~20 subject lines from `git log` so generated
      messages match existing conventions instead of imposing new ones.
      → `CommitMessageReader.RecentSubjectsAsync`, which Phase 1 wrote for this. Where the
      repository has not opted into conventional commits, the model is told to look at
      those subjects and *not* introduce a prefix if they carry none.
- [x] Regenerate, and "give me 3 options"
      → Regenerate is the same button, relabelled once there is a message to replace.
      Options come back in one reply rather than three streams — three messages appearing
      a character at a time in three boxes is not something anybody wants to watch.
- [x] Handle `stop_reason == "refusal"` and network failure — fall back to a manual
      message box, never block the commit
      → Every failure path ends with the message box exactly as usable as it was before
      the button was pressed. Each API failure gets a sentence saying what to do rather
      than what went wrong internally.
- [x] Cost visibility. Show tokens/cost per generation somewhere; without it nobody can
      tell whether this feature is cheap or quietly expensive.
      → In the commit box after each generation, and in the operation log permanently.
      Cached input is called out separately, since it is the reason regenerating is nearly
      free and that is invisible if the tokens are summed. A model missing from the price
      table shows tokens and no dollars: `settings.json` is hand-edited, and an invented
      price presented confidently is worse than an honest omission.
- [x] Offline: the whole feature must degrade to "type it yourself" with no error spam
      → With no credential the button offers to take a key rather than reporting an error;
      with the feature switched off it does not render at all. A failed generation is one
      toast, and the token count falls back to a local estimate rather than surfacing a
      network error from the measuring step and naming the wrong cause.

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
      *(Phase 2 built the first one and it is the shape to copy: the call returns an id
      immediately and the work reports on the event channel, with a `cancel` method taking
      the same id. Message generation was the first thing here that could legitimately
      outlast a git command; push and clone are the next.)*
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
3. ~~**Phase 2**~~ — done. AI commit messages, through Claude or anything speaking the
   OpenAI-compatible dialect. The open decision it started with — where the API key lives —
   is answered in `ApiKeyStore`, and the streaming it needed is the long-running-operation
   protocol the rest of the roadmap was going to need anyway.
4. **Phase 3** — **Next**: branches and stash, needed before checkout is safe.
5. **Phase 7** — worktree management. Cheap, and this app should obviously own it.
6. **Phase 5** — push/pull. Blocked on credentials, so start that spike early.
7. **Phase 4 + 6** — history and conflicts. Both large; conflicts especially.
8. **Phase 8** — the differentiators, once the fundamentals are solid.

Phase 8 is tempting to do first because it's the interesting part. It won't survive
contact with users until Phase 0 exists.
