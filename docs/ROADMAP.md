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

- [ ] **[BLOCKER]** Mutating git command path. `GitCli` already runs commands, but every
      caller today treats a non-zero exit as "no data". Writes need the opposite: exit
      code, stderr, and *which* mutation failed, surfaced to the user.
- [ ] **[BLOCKER][TRAP]** `GIT_OPTIONAL_LOCKS=0` is set on every invocation
      (`GitCli.cs:61`). That's correct for reads — it stops the app taking `index.lock`
      while browsing — and wrong for writes. Split into read and write invocation paths.
- [ ] **[BLOCKER][TRAP]** `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`
      (`GitCli.cs:60-62`) mean any command needing credentials fails silently rather than
      prompting. Fine for `status`; fatal for `push`. See Phase 6.
- [ ] **[BLOCKER]** `index.lock` contention. An agent running `git add` while you commit
      produces `Unable to create index.lock: File exists`. Detect it, retry with backoff,
      and tell the user *which process* holds it rather than surfacing raw git stderr.
- [ ] **[BLOCKER]** Repository operation state. Detect and display in-progress
      merge / rebase / cherry-pick / revert / bisect (`MERGE_HEAD`, `REBASE_HEAD`,
      `CHERRY_PICK_HEAD`, `REVERT_HEAD`, `BISECT_LOG` in the git dir). Half the write
      operations are illegal while one is active, and the UI must say so instead of
      letting git refuse.
- [ ] **[BLOCKER]** Self-write invalidation. The app now mutates the worktree it watches.
      `WorktreeWatcher` will fire on the app's own writes, `WorkspaceService`'s change
      cache will be invalidated by them, and the index will re-parse files the app just
      wrote. Suppress or tag self-originated writes.
- [ ] **[BLOCKER]** Confirmation model for destructive actions — discard, reset, force
      push, branch delete, worktree remove. One consistent affordance, not per-feature
      dialogs.
- [ ] **[HARD]** Undo, backed by reflog. Almost every git mutation is recoverable
      (`ORIG_HEAD`, reflog, stash); a UI that surfaces "undo that" converts the whole app
      from scary to safe. Do this early — retrofitting undo is much harder.
- [ ] Editable Monaco. Currently `readOnly: true, domReadOnly: true` (`editor.ts:105-106`),
      which is load-bearing for V1 and has to become conditional, not deleted — the diff
      view should stay read-only even once conflict editing exists.
- [ ] Save path with encoding preservation. `FileContent.FromBytes` detects UTF-8/UTF-16
      BOMs on read; writes must round-trip the same encoding and line endings, or the app
      silently reformats files.
- [ ] Operation log — what the app did, when, with what git command. The first time it
      does something unexpected, this is the only way to find out what happened.

## Phase 1 — Staging and committing

- [ ] Stage / unstage whole file
- [ ] **[HARD]** Stage / unstage **hunk** — the feature that makes a git GUI worth using.
      Monaco's diff editor exposes hunk boundaries; staging them means generating a
      partial patch and feeding it to `git apply --cached`.
- [ ] **[HARD]** Stage / unstage **line range** — same mechanism, finer granularity
- [ ] Discard changes at file / hunk / line level *(destructive — Phase 0 confirmation)*
- [ ] A real staged-vs-unstaged view. The current scope switch (All / Uncommitted /
      Committed / Last) has no notion of the index; committing needs one.
- [ ] Commit: message editor, amend, `--signoff`, GPG/SSH signing, co-author trailers
- [ ] Commit message conventions — subject length, blank second line, conventional-commit
      type/scope validation, configurable per repo
- [ ] Guards: nothing staged, detached HEAD, in-progress operation, unresolved conflicts

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

- [ ] Detect conflicted state and list conflicted paths (`git status --porcelain=v2`
      unmerged entries, `u` records — currently skipped by `ParseWorkingState`)
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

- [ ] **Test strategy for mutations.** The current suite is safe because nothing writes.
      Write tests need disposable fixture repos — `RegressionTests` already has the
      `NewRepoAsync` / `Delete` helpers to build on. Never test mutations against the
      real validation repos.
- [ ] **Dry-run / preview** for anything destructive
- [ ] **Long-running operations** — the bridge has a 60s call timeout (`bridge.ts`); clone,
      fetch, and push will exceed it. Needs a progress protocol, not a longer timeout.
- [ ] **Multi-instance safety** — two Chapter windows on the same repo, or Chapter plus
      Rider, both writing
- [ ] **Keyboard-first** — the whole point of the app; every new action needs a binding
- [ ] **`.gitattributes`** — still missing, and now that the app writes files, line-ending
      normalization stops being cosmetic

---

## Suggested order

1. **Phase 0** — unavoidable, and the riskiest thing to skip
2. **Phase 1 + 2** — staging, committing, AI messages. Smallest slice that makes the app
   a git client rather than a viewer, and it's the thing asked for.
3. **Phase 3** — branches and stash, needed before checkout is safe
4. **Phase 7** — worktree management. Cheap, and this app should obviously own it.
5. **Phase 5** — push/pull. Blocked on credentials, so start that spike early.
6. **Phase 4 + 6** — history and conflicts. Both large; conflicts especially.
7. **Phase 8** — the differentiators, once the fundamentals are solid.

Phase 8 is tempting to do first because it's the interesting part. It won't survive
contact with users until Phase 0 exists.
