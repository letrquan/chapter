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
  fileName: string
  basePath: string
  hasBaseSide: boolean
  hasWorkingSide: boolean
}

/** Which slice of a worktree's work to show. */
export type DiffScope = 'branch' | 'committed' | 'uncommitted' | 'lastCommit'

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
  getDiff: { params: { worktreePath: string; path: string; scope: DiffScope }; result: DiffPayload }
  getFileContent: {
    params: { worktreePath: string; path: string; scope: DiffScope }
    result: FileContentPayload
  }
  getAsset: {
    params: { worktreePath: string; path: string; scope: DiffScope }
    result: AssetPayload
  }
  getSettings: { params: void; result: AppSettings }
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
}
