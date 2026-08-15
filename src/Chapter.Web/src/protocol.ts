/**
 * TypeScript mirror of Chapter.Core/Contracts/Messages.cs.
 *
 * These two files are one contract expressed twice; change them together. Names are
 * camelCase here because the backend serialises with a camelCase naming policy.
 */

export interface RepoInfo {
  path: string
  name: string
}

export interface Worktree {
  path: string
  head: string
  branch: string | null
  isBare: boolean
  isDetached: boolean
  isMain: boolean
  isPrunable: boolean
  prunableReason: string | null
  isLocked: boolean
  lockReason: string | null
  displayName: string
  isUsable: boolean
}

export type ChangeKind =
  | 'added'
  | 'modified'
  | 'deleted'
  | 'renamed'
  | 'copied'
  | 'typeChanged'
  | 'untracked'

export interface ChangedFile {
  path: string
  oldPath: string | null
  kind: ChangeKind
  similarity: number | null
  linesAdded: number
  linesRemoved: number
  isBinary: boolean
  /** Staged, unstaged or untracked — i.e. not yet committed. */
  isUncommitted: boolean
  /** Has an unresolved merge conflict; nothing may be committed while any file does. */
  isConflicted: boolean
  /**
   * How the index differs from HEAD, or null when nothing about this file is staged.
   * Independent of `unstagedKind`: an edited-staged-edited file carries both.
   */
  stagedKind: ChangeKind | null
  /** How the working tree differs from the index, or null when nothing is unstaged. */
  unstagedKind: ChangeKind | null
  isStaged: boolean
  isUnstaged: boolean
  fileName: string
  basePath: string
  hasBaseSide: boolean
  hasWorkingSide: boolean
}

/** Which slice of a worktree's work to show. */
export type DiffScope = 'branch' | 'committed' | 'uncommitted' | 'lastCommit'

/**
 * Which half of an uncommitted change to show. `combined` defers to the scope and is what
 * every review view asks for; the commit view names a side, because staging acts on one
 * comparison specifically.
 */
export type DiffSide = 'combined' | 'staged' | 'unstaged'

/** What a discard throws away. */
export type DiscardTarget = 'unstaged' | 'everything'

export interface DiffBase {
  sha: string
  description: string
  branchName: string | null
  scope: DiffScope
  toRef: string | null
  includeUntracked: boolean
}

export interface WorktreeChanges {
  worktree: Worktree
  base: DiffBase
  files: ChangedFile[]
  totalAdded: number
  totalRemoved: number
}

export interface DiffPayload {
  path: string
  oldPath: string | null
  baseText: string
  workingText: string
  language: string
  isBinary: boolean
  kind: string
}

/** How a file's bytes encode its characters. Preserved across a save. */
export type FileEncoding = 'utf8' | 'utf8Bom' | 'utf16Le' | 'utf16Be'

/** `mixed` means the file must be written back with its newlines untouched. */
export type LineEnding = 'lf' | 'crLf' | 'mixed'

export interface FileContentPayload {
  path: string
  text: string
  language: string
  isBinary: boolean
  encoding: FileEncoding
  lineEnding: LineEnding
  /** False for content read at a commit and for binaries — the editor must not save over history. */
  isEditable: boolean
}

/** A multi-step git operation that has stopped part-way and is waiting for the user. */
export type RepositoryOperation =
  | 'none'
  | 'merge'
  | 'rebase'
  | 'rebaseInteractive'
  | 'applyMailbox'
  | 'cherryPick'
  | 'revert'
  | 'bisect'

export interface RepositoryState {
  worktreePath: string
  operation: RepositoryOperation
  branch: string | null
  isDetached: boolean
  /** Before the first commit: HEAD names a branch that has no tip yet. */
  isUnborn: boolean
  step: number | null
  totalSteps: number | null
  conflictedPaths: string[]
  hasConflicts: boolean
  isOperationInProgress: boolean
  /** True when a probe failed, so the fields above are defaults rather than observations. */
  probeFailed: boolean
  /** A phrase for the UI: "rebase in progress (3/7)". */
  description: string
}

/** Why a mutation failed, in the terms the UI acts on. */
export type GitFailure =
  | 'none'
  | 'locked'
  | 'operationInProgress'
  | 'authenticationRequired'
  | 'conflict'
  | 'wouldLoseChanges'
  | 'rejected'
  | 'nothingToDo'
  | 'notFound'
  | 'unknown'

export interface MutationPayload {
  operation: string
  ok: boolean
  /** One sentence to show the user. Never empty, even when git said nothing useful. */
  message: string
  failure: GitFailure
  commandLine: string
  exitCode: number
  /** Above one means the command was retried through lock contention. */
  attempts: number
  elapsedMs: number
}

export interface SavePayload {
  path: string
  ok: boolean
  error: string | null
  bytesWritten: number
}

/** How seriously to take a commit-message problem. Neither ever blocks a commit. */
export type MessageSeverity = 'warning' | 'error'

export interface MessageProblem {
  severity: MessageSeverity
  message: string
}

export interface MessageReviewPayload {
  subject: string
  body: string
  problems: MessageProblem[]
  type: string | null
  scope: string | null
  isBreaking: boolean
  isEmpty: boolean
  hasErrors: boolean
  /** The repository's recent subjects, so the box can show the house style. */
  recentSubjects: string[]
}

/** What a commit would take, what it would leave, and whether it may happen at all. */
export interface CommitViewPayload {
  worktreePath: string
  staged: ChangedFile[]
  unstaged: ChangedFile[]
  repository: RepositoryState
  branch: string | null
  isUnborn: boolean
  canCommit: boolean
  /** Why a commit is refused, or null when it is not. */
  blockedReason: string | null
  /** True and worth saying, but not a refusal — a detached HEAD, a merge being concluded. */
  note: string | null
  /**
   * The same three answered for an amend, which needs nothing staged. Both are sent
   * because the amend toggle is client-side; asking again on every flip would put a
   * round-trip inside a checkbox.
   */
  canAmend: boolean
  amendBlockedReason: string | null
  amendNote: string | null
  authorName: string | null
  authorEmail: string | null
  /** The message on HEAD, for prefilling an amend. */
  headMessage: string | null
}

/**
 * One hunk, as git divided it.
 *
 * Staging controls must be rendered from these boundaries, not from Monaco's change
 * regions: Monaco computes its own diff and groups it differently, so a control placed on
 * one of its regions would name a hunk the user never saw.
 */
export interface HunkPayload {
  index: number
  header: string
  oldStart: number
  oldCount: number
  newStart: number
  newCount: number
  section: string
  /** Lines with their leading markers. Positions here are what `PatchLineSelection.line` means. */
  lines: string[]
  addedLines: number
  removedLines: number
}

export interface FilePatchPayload {
  path: string
  side: DiffSide
  hunks: HunkPayload[]
  isBinary: boolean
  /** Send back with any selection made against these hunks. */
  fingerprint: string
}

/** One changed line picked out of a hunk. */
export interface PatchLineSelection {
  hunk: number
  /** Position within the hunk body, counting context and changes alike. */
  line: number
}

/* --------------------------------------------------------------------------
   Generated commit messages
   -------------------------------------------------------------------------- */

/** Where the credential in use came from. `none` means generation is unavailable. */
export type ApiKeySource = 'none' | 'stored' | 'environment' | 'profile'

/**
 * Which dialect the backend is speaking. `openai` means OpenAI-*compatible* — the
 * chat/completions shape that Azure, Ollama, LM Studio, vLLM and OpenRouter also implement.
 */
export type AiProvider = 'anthropic' | 'openai'

export interface AiAvailability {
  available: boolean
  /** Why not, in one sentence. Null when it is. */
  reason: string | null
  /** The one reason the user can fix from inside the app, so it gets its own affordance. */
  needsKey: boolean
  provider: AiProvider
  /** Where an OpenAI-compatible provider points, when it is not the default. */
  baseUrl: string | null
  /** The variable this provider reads, so the key prompt names the right one. */
  environmentVariable: string
  source: ApiKeySource
  /** The last few characters of the key, for telling two accounts apart. Never the key. */
  hint: string | null
  model: string
  effort: string
}

/** One message the model wrote, in parts rather than as prose. */
export interface GeneratedMessage {
  type: string | null
  scope: string | null
  subject: string
  body: string
  isBreaking: boolean
  isEmpty: boolean
  /** Subject, blank line, body — assembled by the backend so both sides cannot disagree. */
  message: string
}

export interface GenerationCost {
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  totalTokens: number
  /** Null for a model that is not in the price table — tokens are still reported. */
  usd: number | null
}

/** What `generateCommitMessage` returns. The text follows on the event channel. */
export interface GenerationStarted {
  id: string
  worktreePath: string
}

export interface GenerationResult {
  id: string
  worktreePath: string
  ok: boolean
  error: string | null
  /** Best first. One entry for an ordinary generation, several when asked. */
  options: GeneratedMessage[]
  cost: GenerationCost | null
  /** Whether the model saw the whole change. Shown, not hidden. */
  diffTruncated: boolean
  note: string | null
}

export interface ApiKeyPayload {
  ok: boolean
  error: string | null
  status: AiAvailability
}

export interface ReflogEntry {
  sha: string
  /** The selector git accepts to reach it, e.g. `HEAD@{2}`. */
  selector: string
  subject: string
  timestamp: string | null
  shortSha: string
}

export interface UndoPayload {
  /** Null when nothing is recorded for this worktree. */
  label: string | null
  isDestructive: boolean
  warning: string | null
  /** Outlives the undo stack and the app itself. */
  reflog: ReflogEntry[]
}

export interface OperationLogEntry {
  timestamp: string
  operation: string
  worktreePath: string
  commandLine: string
  exitCode: number
  elapsedMs: number
  attempts: number
  failure: string | null
  detail: string | null
  success: boolean
}

export interface AssetPayload {
  path: string
  /** Populated when the image could be inlined; null with a `reason` otherwise. */
  dataUri: string | null
  reason: string | null
}

export interface SymbolLocation {
  path: string
  line: number
  column: number
  endLine: number
  endColumn: number
  name: string
  kind: string
  containerName: string | null
  preview: string | null
}

export interface EditorInfo {
  id: string
  name: string
  path: string
}

export interface IndexStatus {
  worktreePath: string
  state: 'idle' | 'indexing' | 'ready' | 'failed'
  filesIndexed: number
  symbolCount: number
  elapsedMs: number
}

export interface AppSettings {
  recentRepos: string[]
  theme: 'dark' | 'light' | 'system'
  preferredEditor: string
  editorPaths: Record<string, string>
  lastWorktree: Record<string, string>
}

/** Methods the backend exposes, with their parameter and return types. */
export interface Api {
  ping: { params: void; result: string }
  listRepos: { params: void; result: RepoInfo[] }
  addRepo: { params: { repoPath: string }; result: RepoInfo | null }
  removeRepo: { params: { repoPath: string }; result: boolean }
  getWorktrees: { params: { repoPath: string }; result: Worktree[] }
  getChanges: { params: { worktreePath: string; scope: DiffScope }; result: WorktreeChanges }
  getDiff: {
    params: { worktreePath: string; path: string; scope: DiffScope; side?: DiffSide }
    result: DiffPayload
  }
  getFileContent: {
    params: { worktreePath: string; path: string; scope: DiffScope }
    result: FileContentPayload
  }
  getAsset: {
    params: { worktreePath: string; path: string; scope: DiffScope }
    result: AssetPayload
  }
  getSettings: { params: void; result: AppSettings }
  /** Persists the preference and repaints the native window caption to match. */
  setTheme: { params: { theme: 'dark' | 'light' | 'system' }; result: boolean }
  pickFolder: { params: void; result: string | null }
  openInEditor: {
    params: { worktreePath: string; path: string; line: number; column: number; editor: string }
    result: boolean
  }
  listEditors: { params: void; result: EditorInfo[] }
  ensureIndex: { params: { worktreePath: string }; result: IndexStatus }
  goToDefinition: {
    params: { worktreePath: string; path: string; line: number; column: number }
    result: SymbolLocation[]
  }
  findReferences: {
    params: { worktreePath: string; path: string; line: number; column: number }
    result: SymbolLocation[]
  }
  searchSymbols: { params: { worktreePath: string; query: string; limit: number }; result: SymbolLocation[] }
  searchFiles: { params: { worktreePath: string; query: string; limit: number }; result: string[] }
  documentSymbols: { params: { worktreePath: string; path: string }; result: SymbolLocation[] }

  getRepositoryState: { params: { worktreePath: string }; result: RepositoryState }
  saveFile: { params: { worktreePath: string; path: string; text: string }; result: SavePayload }

  getCommitView: { params: { worktreePath: string }; result: CommitViewPayload }
  stage: { params: { worktreePath: string; paths: string[] }; result: MutationPayload }
  unstage: { params: { worktreePath: string; paths: string[] }; result: MutationPayload }
  discard: {
    params: {
      worktreePath: string
      paths: string[]
      untracked: string[]
      target: DiscardTarget
    }
    result: MutationPayload
  }
  getFilePatch: {
    params: { worktreePath: string; path: string; side: DiffSide }
    result: FilePatchPayload
  }
  applyPatch: {
    params: {
      worktreePath: string
      path: string
      side: DiffSide
      hunks?: number[]
      lines?: PatchLineSelection[]
      reverse?: boolean
      applyToWorkingTree?: boolean
      fingerprint?: string
    }
    result: MutationPayload
  }
  commit: {
    params: {
      worktreePath: string
      message: string
      amend?: boolean
      signOff?: boolean
      sign?: boolean | null
      coAuthors?: string[]
    }
    result: MutationPayload
  }
  reviewMessage: {
    params: { worktreePath: string; message: string }
    result: MessageReviewPayload
  }
  getAiStatus: { params: void; result: AiAvailability }
  /** Stores the key, or forgets it when empty. The key is never returned. */
  setApiKey: { params: { key: string }; result: ApiKeyPayload }
  /**
   * Starts a generation and returns at once — a model call can outlast the bridge's 60s
   * ceiling, so the text arrives as `messageDelta` and `messageGenerated` events.
   */
  generateCommitMessage: {
    params: { worktreePath: string; amend?: boolean; count?: number }
    result: GenerationStarted
  }
  cancelGeneration: { params: { id: string }; result: boolean }
  getUndo: { params: { worktreePath: string }; result: UndoPayload }
  undo: { params: { worktreePath: string }; result: MutationPayload }
  getOperationLog: { params: { limit: number }; result: OperationLogEntry[] }
}

export type ApiMethod = keyof Api

/** Events the backend pushes without being asked. */
export interface Events {
  /**
   * `selfOriginated` marks a change the app made itself. It is information, not a licence
   * to skip the refresh — attribution is by time window, so an agent's write landing
   * alongside one of ours carries the same flag.
   */
  filesChanged: { worktreePath: string; selfOriginated?: boolean }
  worktreesChanged: { repoPath: string }
  indexStatus: IndexStatus
  /** The undo stack for a worktree gained or lost an entry; re-label the action. */
  undoChanged: { worktreePath: string }
  /** The app performed a mutation. Pushed as it happens so the log can stream. */
  operationLogged: OperationLogEntry
  /**
   * A generation in progress. `message` is the whole message so far, not an increment, so a
   * dropped or reordered event costs nothing.
   */
  messageDelta: { id: string; worktreePath: string; message: string }
  /** A generation ended, however it ended. Fired exactly once per id. */
  messageGenerated: GenerationResult
}
