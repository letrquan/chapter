namespace Chapter.Core.Indexing;

/// <summary>
/// The seam that keeps this from being a C#-only tool.
///
/// C# is the first implementation because Roslyn is a .NET library and parses without a
/// build. A second language is another implementation of this interface — backed by a
/// language server or a tree-sitter grammar — and nothing above it has to change.
/// </summary>
public interface ILanguageIndexer
{
    /// <summary>Identifier used in diagnostics and settings, e.g. "csharp".</summary>
    string Id { get; }

    /// <summary>Whether this indexer handles the file, judged by extension.</summary>
    bool CanIndex(string path);

    /// <summary>
    /// Parses a file once and returns everything the index needs from it. The caller
    /// supplies the text so the indexer never touches the filesystem, which keeps it
    /// trivially testable.
    /// </summary>
    FileIndex IndexFile(string repoRelativePath, string text);

    /// <summary>
    /// Finds the identifier at a position, so a click can be resolved to a name.
    /// Returns null when the position is in whitespace, a comment or a string.
    /// </summary>
    string? IdentifierAt(string text, int line, int column);

    /// <summary>
    /// Positions where <paramref name="identifier"/> appears as real code — excluding
    /// comments and string literals, which a plain text search would wrongly include.
    /// </summary>
    IReadOnlyList<SymbolReference> FindOccurrences(string repoRelativePath, string text, string identifier);
}

/// <summary>
/// Everything one parse of a file yields.
///
/// <see cref="Identifiers"/> is what makes find-usages fast: it becomes an inverted index
/// from name to file, so answering a query means re-reading only the handful of files that
/// actually mention the name instead of every file in the worktree.
/// </summary>
public sealed record FileIndex
{
    public required IReadOnlyList<SymbolDeclaration> Declarations { get; init; }
    public required IReadOnlySet<string> Identifiers { get; init; }

    public static readonly FileIndex Empty = new()
    {
        Declarations = [],
        Identifiers = new HashSet<string>(),
    };
}

public enum SymbolKind
{
    Namespace,
    Class,
    Struct,
    Interface,
    Record,
    Enum,
    EnumMember,
    Delegate,
    Method,
    Constructor,
    Property,
    Field,
    Event,
}

/// <summary>A declaration found in a file, with the span of its name token.</summary>
public sealed record SymbolDeclaration
{
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }

    /// <summary>Repo-relative, forward-slashed.</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based, matching what editors display and what Monaco expects.</summary>
    public required int Line { get; init; }

    public required int Column { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }

    /// <summary>Enclosing namespace and type, e.g. <c>Heat.Agent.Core.AgentTurnRunner</c>.</summary>
    public string? ContainerName { get; init; }

    /// <summary>The declaration's source line, trimmed — shown in pickers and peek lists.</summary>
    public string? Preview { get; init; }

    /// <summary>Fully qualified display name for disambiguating candidates.</summary>
    public string FullName => ContainerName is { Length: > 0 } ? $"{ContainerName}.{Name}" : Name;
}

/// <summary>One occurrence of an identifier in a file.</summary>
public sealed record SymbolReference
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required int EndColumn { get; init; }
    public string? Preview { get; init; }

    /// <summary>True when this occurrence is the declaration itself rather than a use.</summary>
    public bool IsDeclaration { get; init; }
}
