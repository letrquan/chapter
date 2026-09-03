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
      prompting. Fine for `status`; fatal for `push`.
      → The seam exists: `GitCli.AllowCredentialPrompts` governs `GCM_INTERACTIVE` for
      network intent alone. It is enabled by default now; terminal prompts remain disabled,
      so Git Credential Manager can open its own UI without a headless git process blocking.
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

**Complete**, including pushing a tag, which is implemented with the remote operations in
Phase 5. The local ref work and the network path remain separate: creating a tag never
contacts a server, while pushing one uses the credential-aware network intent.

Two facts about git shaped this more than the commands did, and both were settled by
running git rather than from memory:

- **The stash is repository-wide.** `refs/stash` lives in the *common* git directory, so one
  stash list serves every worktree — an entry made in one appears in all of them, and
  `stash@{0}` renumbers whenever any of them stashes. Every other client can treat a
  positional selector as an identity; this app cannot.
- **`for-each-ref` and the log formatters are different format languages.** The separator is
  `%1f` in one and `%x1f` in the other, and each spelling is emitted *literally* by the
  other — silently, with every field landing in column one.

- [x] Branch list per repo (local + remote), with ahead/behind
      → One `for-each-ref` per call, not one process per branch. `%(worktreepath)` comes back
      in the same invocation, which is the field that makes the list worth showing in *this*
      app: it says which worktree already has a branch open, so the row can offer to go there
      instead of attempting a switch git will refuse. Ahead/behind is `%(upstream:track)`,
      and it is as old as the last fetch — stated in the tooltip rather than implied.
- [x] Create / rename / delete branch *(delete is destructive)*
      → Delete is destructive and **recoverable**, which the confirmation is allowed to say
      because the tip is resolved before the delete and undo recreates the branch at exactly
      that commit. Git's own `Deleted branch x (was 33a982a)` is abbreviated, and an
      abbreviation that is unambiguous today stops being so as the repository grows.
      Renaming a branch another worktree has checked out **succeeds** and moves that worktree
      with it — the opposite of delete, and worth knowing rather than assuming.
- [x] **[HARD]** Checkout with a dirty tree — the decision point most GUIs get wrong.
      Offer: stash-and-switch, carry changes, or abort. Never silently discard.
      → **Attempted, then offered — never pre-checked.** Git carries uncommitted changes
      across whenever no file differs between the two branches, which is the common case and
      is exactly the "carry" option; refusing first on "the tree is dirty" would block the
      switch git was going to allow. Only git's refusal turns into the question.
      The stash path is the one with a window where the user's work exists in one place only,
      so every failure inside it has to end somewhere findable: the switch failing pops the
      stash back, and *the pop failing too* — an agent writing to the same files in that
      second, which is this app's ordinary case — is reported with the stash named. A
      conflicted restore is reported as a successful switch whose work is still in the stash,
      because saying "could not switch" would send the user looking on the wrong branch.
- [x] Set upstream / track
- [x] Stash: create (with message, optionally including untracked), list, apply, pop, drop
      → Every mutation carries the sha the UI displayed and is refused when the entry at that
      index is no longer the same object. `apply` is then named by sha outright; `pop` and
      `drop` cannot be — both remove an entry, which is inherently positional, and git
      rejects a raw commit for either. Dropping is undoable via `stash store`, which is why
      its confirmation says so rather than claiming permanence it does not have.
- [x] Tags: list, create annotated/lightweight, delete
      → A message is what makes a tag annotated, so asking for one *is* the choice between
      the two kinds — git's rule (`-m` implies `-a`), not an invention. Delete restores the
      ref's own object rather than the commit behind it: recreating an annotated tag from its
      commit would silently produce a lightweight one and lose the message.
- [x] Tags: push → implemented in Phase 5 alongside the credential-aware network path.

## Phase 4 — History

**Complete.** The log, graph, commit detail, file history, blame, history mutations and
interactive rebase surface are complete:
every worktree has a newest-first, paginated log with merge parents, author/committer
metadata, decorations and the full message, and an open file can follow its history or show
line attribution. Cherry-pick and revert validate the selected full commit id against the
current worktree, support an explicit merge parent, preserve conflicts for later resolution,
and record an undo point only when `HEAD` actually moves. The rebase planner validates the
captured tip, edits the todo order/action/message, and leaves a paused operation available
to the shared conflict banner.

- [x] Commit log per worktree, paginated. `git log --format` with a stable field separator.
- [x] **[HARD]** Graph rendering for branch topology — lane-aware SVG edges keep merge
      parents visible across paginated pages.
- [x] Commit detail view — selecting a commit loads its parent comparison and opens a
      historical file diff in the existing Monaco view; merge commits can choose a parent.
- [x] File history and blame. File history follows renames from the editor toolbar and keeps
      pagination anchored to the displayed `HEAD`; Code mode can show Monaco gutter
      attribution for saved working-tree files, with new lines marked uncommitted. Dirty
      buffers deliberately disable blame until they are saved, because Git can only
      attribute bytes on disk.
- [x] Search history: message, author, path, content (`git log -S`). The history overlay
      keeps one search field with explicit modes: messages and authors are literal,
      case-insensitive matches; paths are escaped literal substrings; content uses Git's
      exact pickaxe semantics. Every result page stays anchored to the displayed `HEAD`.
- [x] Cherry-pick and revert. The history detail actions and contextual `C` / `R` shortcuts
      run through the guarded writer with `--no-edit`; merge parents translate from the
      zero-based UI index to Git's one-based `-m`. A source commit must be a full object id
      reachable from the displayed worktree's `HEAD`. Clean operations are undoable; a
      conflict is classified and left in Git's cherry-pick/revert state for Phase 6's
      resolve / continue / abort controls.
- [x] **[HARD]** Interactive rebase. The history planner edits order and pick/reword/edit/
      squash/fixup/drop actions, supplies replacement messages, and starts a guarded Git
      sequence. The persistent operation banner handles continue, skip and abort, including
      a rebase resumed after an application restart.

## Phase 5 — Remotes

The credential story is the hard part, not the commands.

- **Complete.** Remote commands now run through a credential-aware network intent and the
  bridge's detached progress protocol. The operation id is returned before git starts, live
  stderr progress is forwarded as events, and cancellation kills the git process tree. Pull
  requests arrived last and are the one thing here that is not git: they are `gh`, kept at
  arm's length behind the same failure classification as everything else.

- [x] **[BLOCKER][TRAP]** Undo the no-prompt environment from Phase 0 for network
      operations, and integrate with Git Credential Manager. `GIT_TERMINAL_PROMPT=0` still
      prevents a headless terminal prompt, while an unset `GCM_INTERACTIVE` lets Git
      Credential Manager open its own sign-in UI. The switch remains configurable for hosts
      that must be completely non-interactive, and embedded URL credentials are redacted from
      commands, progress, messages and the operation log.
- [x] Fetch / pull / push with progress. These are detached from the bridge request, so the
      60s call timeout no longer cuts off a transfer.
- [x] Pull strategy: merge vs rebase vs fast-forward-only. Merge passes `--no-rebase --no-edit`
      so a merge commit never waits for an editor that the desktop shell does not provide.
- [x] `--force-with-lease` — never plain `--force`
- [x] Ahead/behind indicators in the worktree rail
      *(The counts are joined onto each worktree from `%(upstream:track)` and refreshed after
      a fetch, pull or push. They remain a local view of the last fetched tracking refs.)*
- [x] **Push a tag.** The tag action uses the same detached, credential-aware push path and
      sends only the selected `refs/tags/<name>` ref.
- [x] Remote management: add, rename, remove, prune
      *(The refs overlay lists fetch/push URLs, redacts embedded credentials, and exposes the
      four local configuration actions.)*
- [x] PR integration via `gh` CLI (create, view, checkout) — optional, high value given the
      agent workflow
      → A fifth refs section, and the one place the app shells out to something other than
      git. `gh` owns authentication, host selection and enterprise GitHub; Chapter supplies
      non-interactive arguments (`GH_PROMPT_DISABLED`, `GH_PAGER=cat`, `GH_FORCE_TTY=0`) and
      parses only the bounded `--json` field list it asked for. Reads go straight to the CLI;
      **checkout goes through `GitWriter`**, because it moves the local worktree and has to
      take the same repository lease and operation guard as any other mutation. It records no
      undo point: what it changes is which branch is checked out, and the branch it came from
      is still a branch.
      → `gh` not being installed is an ordinary outcome rather than a crash: for the actions
      that mutate, its stderr is classified into the same `GitFailure` set as git's, so "not
      logged in" reads as `AuthenticationRequired` and a missing executable as `NotFound`. A
      failed *list* is softer — it carries `gh`'s own sentence into the section rather than a
      classified kind, because there the only thing the app can usefully do is repeat what
      the CLI said instead of showing an empty list. Creation re-reads the PR afterwards, because some
      `gh` versions print a success sentence rather than a URL and the panel needs the object.
      Displayed URLs are pattern-checked before they reach an `href`.

## Phase 6 — Conflict resolution

**Complete.** The conflict surface is shared by merge, rebase, cherry-pick, revert, mailbox
apply and stash restore, while preserving each operation's different continue/skip/abort
semantics.

- [x] Detect conflicted state and list conflicted paths (`git status --porcelain=v2`
      unmerged entries, `u` records — currently skipped by `ParseWorkingState`)
      → Done in Phase 0, because the write guard needs it: `ParseWorkingState` now reads
      `u` records, `ChangedFile.IsConflicted` carries it, and `RepositoryState` lists the
      paths. The rest of this phase now reads the same unmerged index directly, so a partial
      status probe cannot hide a stage.
- [x] **[HARD]** Three-way merge view. A custom four-pane Monaco surface shows Base, Ours,
      Theirs and an editable Result; it uses responsive 2x2 desktop layout and stacks panes
      at a narrow window width rather than letting absolute Monaco hosts collapse.
- [x] Per-conflict actions: take ours, take theirs, take both, edit manually. Side choices
      preserve the working file's encoding/newline format where possible; binary sides are
      copied byte-for-byte, and modify/delete conflicts expose an explicit delete choice.
- [x] Conflict markers as first-class regions rather than raw `<<<<<<<` text. Regions are
      parsed with optional diff3 base content, highlighted in the result, and carry local
      Ours/Theirs/Both controls; literal marker text outside a parsed region remains content.
- [x] Mark resolved (`git add`), then continue / skip / abort the merge or rebase. The shared
      banner keeps a paused operation visible after files are staged and refuses continuation
      while any unmerged index stage remains.
- [x] Conflict resolution during rebase, cherry-pick, revert, and stash-apply — each has
      operation-specific continue/skip/abort behavior. Stash apply/pop conflicts retain the
      stash and offer an explicit Continue, with no unsafe fake Abort.
- [x] `git rerere` support, if you hit the same conflicts repeatedly across worktrees. The
      bridge can enable rerere, apply recorded resolutions, inspect status and forget paths.

## Phase 7 — Worktree management

The natural home-turf feature — the app is already worktree-shaped.

**Complete.** It lives as a fourth section of the refs overlay rather than in the rail. The
rail *is* the worktree list, which is the argument for putting it there and does not survive
contact with the widths involved: it is 160px of single-button rows, with nowhere to put four
actions per worktree and no honest way to ask "where should this one go?". The overlay
already had the row grid, the filter, the inline prompt, the confirmation and the keyboard.

Two facts decided more than the commands did, and both came from running git:

- **Every mutation runs in the repository's main worktree**, never in the one being acted on.
  `git worktree remove` will happily delete the directory the command is running in — it
  succeeded in the experiment — leaving git standing in a deleted CWD, which is undefined on
  POSIX and impossible on Windows, where an in-use directory cannot be deleted at all. Git
  refuses to remove or move the main worktree, so running from there means the host is never
  the target.
- **`worktree prune --verbose` reports on stderr**, not stdout. Reading stdout gives a
  confident, permanently empty preview: a dialog saying "nothing to prune" beside a button
  that then prunes four worktrees.

- [x] Create worktree from an existing branch or a new one
      → One question, not two: the panel already holds the branch list, so it knows whether
      the name is a checkout or a creation, and asking the user to classify something the app
      can see is a question with a right answer. The destination is checked before git runs —
      the one place in this codebase that pre-empts git deliberately, because
      `worktree add -b` creates the branch *first* and only then finds the path occupied, so
      the retry fails with a second error about something the user never did.
- [x] Remove worktree *(destructive)*
      → Attempted without `--force`, so git decides whether tracked work is at risk, and its
      refusal raises a second question about somebody's work in progress. Both say
      **permanent**, and the first one said "undoable" until review caught it: the argument
      was that git refuses whenever anything uncommitted exists, and git's check is `status`,
      which does not report ignored files. A worktree whose only untracked content is a
      `.env` and a `node_modules` is clean to that check and is deleted in silence — and
      nothing here reverses a deleted directory anyway, which is why removal records no undo
      point. A **locked**
      worktree is asked about first and separately: git wants `--force --force` there and the
      app passes one, because a lock is somebody's explicit instruction and the way past it
      is to unlock, not to override them with a flag on something else.
      → Removing the worktree you are standing in is an ordinary case rather than an edge
      one, and it is the case the plumbing is shaped around: the watcher and symbol index are
      released *before* git runs (a directory anything holds open is undeletable on Windows),
      the membership check happens before that release (or the app refuses its own request),
      and the repository is resolved before the mutation (afterwards there is no directory
      left to ask git about, and the rail would go on showing what was just deleted).
- [x] **Prune stale worktrees** — `heat` has a prunable one right now, and today the app
      can only display it, not fix it
      → With a dry run first, which is the cross-cutting "preview anything destructive" item
      in its first use. Prune is the one action here with no row to name: it acts on records
      whose directories are already gone. It also earned `confirm.ts` a third recoverability
      answer — neither "undoable" nor "permanent" is true of it, and putting the app's loudest
      warning on its least consequential action is how a warning starts being read past.
- [x] Lock / unlock, with reason
- [x] Move worktree
      → Where it went is deliberately not passed back to the window. Git resolves the typed
      destination — against the main worktree, into the platform's separators — so the string
      the panel holds frequently is not the path that now exists. The window re-reads the
      list and takes the one that was not there before, scoped to that repository: every
      worktree of every *other* repository is also "new" by that test, and the unscoped
      version dropped the user into an unrelated project.
- [x] Sensible default paths — support both layouts already handled (`.worktrees/` nested
      and scattered siblings)
      → Follows whatever the repository already does, and picks the sibling layout where
      there is no precedent: a worktree nested inside the main one shows up in that
      worktree's own `git status` as an untracked directory, which in this app means the
      repository being reviewed grows a phantom change that is really another agent's entire
      checkout.

## Phase 8 — Agent-workflow differentiators

Nothing else on this list is unique to this app. These are.

- [x] **Cross-worktree compare** — two agents solved the same task; choose any other usable
      worktree from the refs panel and compare the two live, Git-visible snapshots without
      changing the active worktree. Tracked and non-ignored untracked files are included,
      ignored files are excluded, exact renames retain both paths, and binary files remain
      visible with byte metadata. Selecting a row opens a read-only Monaco diff; the ordinary
      worktree tabs and editor state are restored when the comparison closes.
- [x] **Accept this agent's work** — one action: merge or cherry-pick a worktree's branch
      into main, then optionally remove the worktree.
      → Requires a clean source (tracked, staged and ordinary untracked changes are refused).
      Merge preserves the agent boundary with `--no-ff`; cherry-pick applies linear commits
      atomically and leaves conflicts for the existing resolver. A successful integration is
      one undo point. Optional removal rechecks the source branch, tip and status — including
      ignored content — and leaves the directory in place when anything changed after review.
- [x] **Reject and reset** — discard a worktree's work and reset it to base
      → A preview names commits, tracked changes, ordinary untracked files and ignored files.
      Cleanup is restricted to those literal paths, then the branch is reset to its merge base
      only if the source head, base head and content fingerprint are unchanged. The committed
      tip gets a destructive undo point; discarded bytes do not.
- [x] Batch review — walk every worktree's changes in sequence with a keystroke
      → `[` / `]` move to the previous/next usable worktree with changes, across collapsed
      repository groups too. The gesture marks the worktree being left only when its exact
      displayed snapshot is still current; an agent edit during the scan remains visibly new.
- [x] Link a worktree to the agent session that produced it, if the session log is on disk
      → The refs panel scans the local Claude Code, Book and Codex stores for bounded metadata
      prefixes, matching exact worktree paths first and Claude's encoded project folder as a
      fallback. Branch and timestamp data rank otherwise-valid records, while a recorded cwd
      for another worktree is never rescued by a common branch name. Transcript text stays on
      disk; the bridge returns metadata only, and opening a log re-resolves its provider/id,
      rejects reparse-point paths outside the known `.jsonl` roots, then hands the validated
      file to the host shell. `L` (in the Worktrees section) and the external-link row action
      open the newest match. Missing stores are an ordinary empty result, and malformed or
      oversized records are ignored without blocking refs refresh.
- [x] "What changed since I last looked" — per-worktree review watermark
      → A SHA-256 snapshot of HEAD, staged/unstaged diffs, conflicts and ordinary untracked
      bytes is persisted outside Git. Ignored build output is excluded, and marking carries
      the fingerprint it was read against so unseen agent edits cannot be blessed.

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
      Phase 7 added `WorktreeTests`, which creates and destroys directories rather than refs
      and is the first suite where a wrong path deletes somebody's work — every fixture path
      is registered for cleanup before it exists, and `RealRepoTests` stopped constructing a
      `WorktreeService` by hand so that reading a worktree list and writing to one come from
      the same place the app builds them.
      Phase 3 added a **two-worktree** fixture, because half of what it had to get right is
      only observable with one: which worktree holds a branch, that the stash is shared, that
      a rename follows into the other worktree. Its fixtures also pin `core.autocrlf=false`
      rather than inheriting it — the Windows installer's default is `true`, which rewrites
      every line ending on checkout and fails content assertions for reasons unrelated to
      what is under test.
- [x] **Dry-run / preview** for anything destructive
      *(The first was pruning worktrees, which shows `worktree prune --dry-run` in the
      confirmation before the button does anything. It was the case where a preview is not a
      nicety — the action names nothing on screen, because what it removes is the record of
      directories that have already gone — and it turned out to be the shape for the rest.)*
      → Four more, each answering a question the dialog could not previously answer:
      **removing a worktree** lists its uncommitted, untracked and *ignored* content;
      **force-pushing** asks the server what it would replace; **pruning a remote** shows the
      tracking refs that have gone from it; **deleting a branch** names the commits that would
      be left with nothing pointing at them.
      → The one that justifies the whole item is the worktree removal, and it is not the
      obvious one. Git already refuses to remove a worktree with ignored content — that guard
      was written in Phase 7 — so the dangerous case was never "git lets it through". It was
      that both dialogs described a *path* while the thing at risk was a `.env`, and a user
      who has read two warnings about a directory has not been told about the file.
      → **A preview is asked, not done.** All four run outside `GitWriter` and so leave no
      trace in the operation log, which is the record of what the app did. Two of them
      nevertheless contact the server — `push --dry-run` and `remote prune --dry-run` are
      network commands with the transfer left out — so they run under `GitIntent.Network`
      rather than the read path. On the read path they would inherit `GCM_INTERACTIVE=never`
      and fail authentication against every private remote, which is to say: fail in exactly
      the case where somebody wanted to check first.
      → Force-push earns its preview by asking the *remote*, not the tracking refs. The lease
      in `--force-with-lease` is evaluated against the server's current tip, which is a fact
      only the server has; a preview computed locally would be confident and stale in precisely
      the situation the lease exists to catch. The dry run reports the old tip, and the commits
      between it and the new one are what the remote would stop having.
      → A preview that fails is not a veto. Every one of them degrades to the dialog's own
      words plus a line saying why it could not look — an unreachable server is a reason to
      think harder about a push, not a reason the app should refuse to ask the question.
      → Deleting a branch is where the preview and git disagree on purpose. `git branch -d`
      refuses when a branch is not merged into HEAD or its upstream; the preview counts commits
      that *no remaining ref* would reach, and a branch can fail the first test while scoring
      zero on the second. Git still decides — nothing here pre-empts it or passes `-D` on the
      preview's evidence. What changed is that the second dialog stopped claiming "commits that
      are on no other branch" when the preview can see that they are.
      → Not everything destructive gets one, and the exceptions are deliberate: discarding a
      hunk or a file is previewed by the diff already on screen, and dropping a stash or
      deleting a tag restores exactly from the undo point, so a list of contents would be
      answering a question nobody has.
- [x] **Long-running operations** — the bridge has a 60s call timeout (`bridge.ts`), and
      fetch, push and clone all outlive it. Each returns an id immediately and reports on the
      event channel, with a `cancel` method taking the same id.
      *(Phase 2 built the first one and it was the shape to copy: message generation was the
      first thing here that could legitimately outlast a git command; the remote operations
      and clone are the rest.)*
      → Clone is the odd one out and the reason the protocol earned a second implementation
      rather than a parameter. Every other detached operation has a worktree to guard, a
      writer to invalidate and a rail entry to refresh; a clone has **no repository yet**, so
      the destination is validated before git starts — it must not exist, its parent must,
      and neither string may hold control characters or a leading dash that git would read as
      an option. The registration that follows is what makes it feel like an app action
      rather than a command: the finished clone is added to the workspace without disturbing
      the tab you were on, and a failure to register is reported without unwinding a transfer
      that actually succeeded.
      → Progress parsing moved out of the remote path into `ProgressLineParser` when clone
      needed the same thing. Git separates transfer status with carriage returns as well as
      newlines and does not terminate the last one at all, so the parser breaks on either,
      drops repeated identical lines, bounds an over-long one rather than growing a buffer,
      and flushes what is left when the process closes its streams — otherwise the message a
      user most wants, the final one, is the single line that never arrives.
- [x] **Multi-instance safety** — two Chapter windows on the same repo, or Chapter plus
      Rider, both writing
      *(One case was already handled: a hunk selection carries a fingerprint of the diff it
      was made against, and the backend refuses when the file changed in between. Without it
      the user approves hunk 2 of one diff and the app stages hunk 2 of another — which, in a
      worktree an agent is actively writing to, is not a hypothetical.)*
      → The general case is `RepositoryWriteLock`: a short repository-wide lease held around
      a whole mutation, from the guard re-read to the classification of git's result. Git's
      own `index.lock` protects one low-level write, which is not the failure here — two
      windows can each read the same branch and stash list, then act on a snapshot the other
      has already invalidated, and both writes individually succeed. The guard is re-read
      *after* the lease is taken, because checking before it means checking a state that was
      allowed to change while queuing.
      → It is a file-region lock, not a lock file, and it lives outside git's namespace. The
      operating system releases a byte-range lock when the process dies, so a killed Chapter
      cannot leave a stale marker that the next one has to be taught to break — the failure
      mode that makes hand-rolled lock files worse than what they replace. Linked worktrees
      resolve to their common git directory first, so every worktree of one repository
      converges on the same lease. Waiting is bounded at two seconds and then refused as
      `Locked` — "another Chapter instance is writing this repository — try again" — because
      a mutation that silently blocks is indistinguishable from one that hung.
      → **This coordinates Chapter with Chapter, not Chapter with Rider.** Nothing here
      constrains another program; against an external writer the app still relies on git's
      own locking and on the fingerprint checks that refuse a stale plan.
- [x] **Keyboard-first** — the whole point of the app; every new action needs a binding
      → Audited rather than assumed, and the audit was worse than expected. The refs panel
      had **twenty-five row actions and twelve footer buttons with no key path at all**:
      `Tab` is spent cycling sections, so nothing there was reachable except by mouse.
      → Worse, the three that were documented did not work either. `A`, `R` and `L` are
      guarded by "not while typing", the filter is an `<input>`, and the filter takes focus
      when the panel opens and again on every section change — so the panel's own hint bar,
      the help overlay and the README all named shortcuts that fired only after a click had
      moved focus somewhere else.
      → The fix is roving focus: `→` from the end of the filter steps into the selected row's
      actions and then the footer, `←` walks back and off the front returns to the filter with
      the caret at the end. Chosen over a mnemonic per action because there are thirty-seven of
      them and no letters left, and over "letters act when the filter is empty" because
      filtering for `agent-1` is what the filter is for. It also gives `A`/`R`/`L` the thing
      they were missing: a way to leave the filter.
      → Two smaller things fell out of it. `Enter` on a focused button ran the *row's* primary
      action rather than the button — already true for a button reached by clicking, and it
      would have made the new navigation actively wrong. And every render rebuilds the list, so
      the focused control is destroyed several times a second while a fetch reports progress;
      the render now hands focus back to the same action *by name*, not by position, because a
      worktree row swaps Lock for Unlock and the footer gains and loses Prune.
      → A third came out of driving the built app rather than reading it, which is the only
      way it would have: **answering or cancelling an inline prompt left the whole panel
      keyboard-dead.** `finish()` rebuilds the footer, which destroys the input focus was in,
      and focus lands on `<body>` — outside the element the panel's handler is bound to, so
      every key after that went nowhere. It looked exactly like a filter that had stopped
      accepting text.
      → So did a fourth, and it is the largest: **five documented shortcuts died whenever the
      caret was in the editor.** Monaco binds `Ctrl` `H` to Replace, `Ctrl` `G` to Go to Line
      and `Ctrl` `Shift` `O` to Go to Symbol, and it stops those events before the window
      listener runs — so History, Write the commit message and Clone were all reachable only
      from a toolbar. Registered on the editors themselves, which is the pattern `Ctrl` `S`
      and `Ctrl` `D` already use for the same reason, and checked against Monaco's own
      keybinding source rather than from memory: `Ctrl` `O`, `Ctrl` `Shift` `E`, `Ctrl` `\`
      and `Ctrl` `Shift` `H` are unbound there and needed nothing. `Ctrl` `F` is deliberately
      left alone — that one is Monaco's Find, which the app has no replacement for.
      → The other two are `Ctrl` `B` and `Ctrl` `Shift` `B`, and they are why this note says
      *driven* rather than *read*. Reading finds nothing: Monaco's source binds neither, and
      an earlier commit recorded both as checked from inside the editor. Running the built app
      says otherwise — the refs panel does not open with the caret in a pane and opens at once
      from the file list. Registering them on the editors alongside the other three makes them
      work there, which is the whole requirement; the dispatch that was eating them is
      Monaco's, not a default in its keymap.
      → Elsewhere: `Ctrl` `O` adds a repository, `Ctrl` `Shift` `E` opens the file in Rider or
      VS Code — a keystroke the README had promised since V1 and the app had never had — and
      `Ctrl` `\` switches inline and side-by-side. In the history timeline, `↓` on the last row
      loads the next page instead of wrapping to the newest commit, which was the last thing in
      that overlay reachable only by clicking it.
      → Standing rule rather than a finished job: this stays on the list as the thing every
      new action has to satisfy, and the roving strip is what makes that cheap — an action
      added to a refs row is reachable the moment it is rendered.
- [x] **`.gitattributes`** — now that the app writes files, line-ending normalization stops
      being cosmetic
      → `* text=auto eol=lf`, with the source, config and documentation extensions named
      explicitly and binary assets marked so nothing tries to normalize a PNG or a DLL. It
      **pins what was already true rather than changing it**: `git ls-files --eol` reports
      every tracked text file as `i/lf` in the index, so there is no renormalizing commit to
      make and no diff to review. What it prevents is the next write. The Windows installer
      defaults `core.autocrlf=true` — this checkout has it — which is why most working files
      here are `w/crlf`, and an app that saves an edited file, stages a hunk or resolves a
      conflict is one misconfigured machine away from committing a file whose every line
      moved. The test fixtures already pin `core.autocrlf=false` for the same reason; this is
      that lesson applied to the repository itself.

---

## Suggested order

1. ~~**Phase 0**~~ — done. Unavoidable, and the riskiest thing to skip.
2. ~~**Phase 1**~~ — done. Staging, committing. The smallest slice that makes the app a git
   client rather than a viewer.
3. ~~**Phase 2**~~ — done. AI commit messages, through Claude or anything speaking the
   OpenAI-compatible dialect. The open decision it started with — where the API key lives —
   is answered in `ApiKeyStore`, and the streaming it needed is the long-running-operation
   protocol the rest of the roadmap was going to need anyway.
4. ~~**Phase 3**~~ — done. Branches, stash and tags. Checkout with a dirty tree turned out
   to need less invention than expected and more care than expected: git already does the
   right thing most of the time, and the work was in not getting in its way, then in making
   sure the one path where the user's work exists only in a stash can never end silently.
5. ~~**Phase 7**~~ — done. Worktree management. Cheap as predicted, and the cost was not in
   the git commands but in everything the app holds open: a worktree is a directory with a
   file watcher, a symbol index, editor models and the active selection all keyed to its
   path, and removing one means letting go of all of them in the right order.
6. ~~**Phase 5**~~ — done. Remotes, credential-aware sync and progress. The network path is
   detached from the bridge, and tag pushing now lives beside the other remote actions.
7. ~~**Phase 4 + 6**~~ — done. History, interactive rebase and conflict resolution now share
   the persistent operation surface; binary side choices and stash restores retain their
   explicit safety limits.
8. ~~**Phase 8**~~ — done. The differentiators, built last and on top of fundamentals that
   were already solid, which is why each one is a composition of existing parts rather than
   a new subsystem: comparison reuses the diff view, acceptance reuses the writer and the
   undo log, rejection reuses the confirmation's preview.

Phase 8 was tempting to do first because it's the interesting part. It would not have
survived contact with users until Phase 0 existed.

**Every item on this roadmap is now done.** The two cross-cutting ones are ticked as of the
work described above, but they are standing rules rather than closed boxes: a new destructive
action needs a preview unless it is one of the named exceptions, and a new action of any kind
needs a keyboard path. Both are cheap to keep now — the refs panel's roving focus reaches an
action the moment it is rendered, and the preview shape is the same four lines each time.
