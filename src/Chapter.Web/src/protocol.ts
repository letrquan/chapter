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

export interface FileContentPayload {
  path: string
  text: string
  language: string
  isBinary: boolean
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
}

export type ApiMethod = keyof Api

/** Events the backend pushes without being asked. */
export interface Events {
  filesChanged: { worktreePath: string }
  worktreesChanged: { repoPath: string }
  indexStatus: IndexStatus
}
