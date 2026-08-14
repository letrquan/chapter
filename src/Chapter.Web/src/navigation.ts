import { monaco, ensureModel, hasModel, modelUri, resolveModelOrigin } from './editor'
import { call } from './bridge'
import type { SymbolLocation } from './protocol'

/**
 * Wires the C# index into Monaco's own navigation surfaces.
 *
 * Registering providers rather than building bespoke UI means F12, Shift+F12, the peek
 * widget and the multi-candidate chooser all come for free and behave the way they do in
 * any editor. It is also the extension point for a second language: another
 * registerDefinitionProvider backed by a different indexer, and nothing else changes.
 */

/** How the app opens a file when Monaco asks to navigate somewhere. */
export type NavigateHandler = (worktreePath: string, path: string, line: number, column: number) => void

let navigate: NavigateHandler = () => {}

export function setNavigateHandler(handler: NavigateHandler): void {
  navigate = handler
}

/**
 * Resolves the model a navigation request came from, and refuses the base pane.
 *
 * Positions in the left pane are line numbers in the *old* revision, but the index is
 * built from the working tree. Feeding one to the other resolves against unrelated code
 * and jumps somewhere wrong — worse than declining, since nothing signals the mismatch.
 * Returning null makes Monaco report "no definition found" instead.
 */
function originOf(model: monaco.editor.ITextModel): { worktreePath: string; path: string } | null {
  const origin = resolveModelOrigin(model.uri)
  if (!origin) return null
  if (origin.side === 'base') return null

  return { worktreePath: origin.worktreePath, path: origin.path }
}

/**
 * Creates models for files a result points at.
 *
 * Peek renders from models, so without this a definition in a file you have not opened
 * shows an empty preview pane.
 */
async function materialise(worktreePath: string, paths: Iterable<string>): Promise<void> {
  const missing = [...new Set(paths)].filter((path) => !hasModel(worktreePath, path))

  await Promise.all(
    missing.map(async (path) => {
      try {
        // Always the working tree: the index is built from it, so navigation line numbers
        // only line up against that text regardless of which scope the file list shows.
        const file = await call('getFileContent', { worktreePath, path, scope: 'branch' })
        if (!file.isBinary) ensureModel(worktreePath, path, file.text, file.language)
      } catch {
        // A file we cannot read simply gets no preview; the jump still works.
      }
    }),
  )
}

function toMonacoLocation(worktreePath: string, location: SymbolLocation): monaco.languages.Location {
  return {
    uri: modelUri(worktreePath, location.path),
    range: {
      startLineNumber: location.line,
      startColumn: location.column,
      endLineNumber: location.endLine || location.line,
      endColumn: location.endColumn || location.column,
    },
  }
}

export function registerCSharpNavigation(): void {
  monaco.languages.registerDefinitionProvider('csharp', {
    provideDefinition: async (model, position) => {
      const origin = originOf(model)
      if (!origin?.path) return []

      const locations = await call('goToDefinition', {
        worktreePath: origin.worktreePath,
        path: origin.path,
        line: position.lineNumber,
        column: position.column,
      })

      await materialise(origin.worktreePath, locations.map((l) => l.path))
      return locations.map((location) => toMonacoLocation(origin.worktreePath, location))
    },
  })

  monaco.languages.registerReferenceProvider('csharp', {
    provideReferences: async (model, position) => {
      const origin = originOf(model)
      if (!origin?.path) return []

      const locations = await call('findReferences', {
        worktreePath: origin.worktreePath,
        path: origin.path,
        line: position.lineNumber,
        column: position.column,
      })

      await materialise(origin.worktreePath, locations.map((l) => l.path))
      return locations.map((location) => toMonacoLocation(origin.worktreePath, location))
    },
  })

  monaco.languages.registerDocumentSymbolProvider('csharp', {
    displayName: 'Chapter',
    provideDocumentSymbols: async (model) => {
      const origin = originOf(model)
      if (!origin?.path) return []

      const symbols = await call('documentSymbols', {
        worktreePath: origin.worktreePath,
        path: origin.path,
      })

      return symbols.map((symbol) => ({
        name: symbol.name,
        detail: symbol.containerName ?? '',
        kind: monacoSymbolKind(symbol.kind),
        tags: [],
        range: {
          startLineNumber: symbol.line,
          startColumn: 1,
          endLineNumber: symbol.endLine || symbol.line,
          endColumn: symbol.endColumn || 1,
        },
        selectionRange: {
          startLineNumber: symbol.line,
          startColumn: symbol.column,
          endLineNumber: symbol.line,
          endColumn: symbol.endColumn || symbol.column,
        },
      }))
    },
  })

  // Monaco navigates by asking the host to open a model. Without this, following a
  // definition into another file silently does nothing, because the standalone editor has
  // no concept of our tabs.
  monaco.editor.registerEditorOpener({
    openCodeEditor(_source, resource, selectionOrPosition) {
      const origin = resolveModelOrigin(resource)
      if (!origin) return false

      const line =
        selectionOrPosition && 'startLineNumber' in selectionOrPosition
          ? selectionOrPosition.startLineNumber
          : (selectionOrPosition?.lineNumber ?? 1)

      const column =
        selectionOrPosition && 'startColumn' in selectionOrPosition
          ? selectionOrPosition.startColumn
          : (selectionOrPosition?.column ?? 1)

      navigate(origin.worktreePath, origin.path, line, column)
      return true
    },
  })
}

/** Maps our symbol kinds onto Monaco's, which drives the icon shown in pickers. */
export function monacoSymbolKind(kind: string): monaco.languages.SymbolKind {
  const kinds = monaco.languages.SymbolKind
  switch (kind) {
    case 'class':
      return kinds.Class
    case 'struct':
      return kinds.Struct
    case 'interface':
      return kinds.Interface
    case 'record':
      return kinds.Class
    case 'enum':
      return kinds.Enum
    case 'enummember':
      return kinds.EnumMember
    case 'delegate':
      return kinds.Function
    case 'method':
      return kinds.Method
    case 'constructor':
      return kinds.Constructor
    case 'property':
      return kinds.Property
    case 'field':
      return kinds.Field
    case 'event':
      return kinds.Event
    case 'namespace':
      return kinds.Namespace
    default:
      return kinds.Variable
  }
}
