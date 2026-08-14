using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Chapter.Core.Indexing;

/// <summary>
/// C# indexing built on Roslyn syntax trees alone — no MSBuild, no compilation, no NuGet
/// restore.
///
/// The trade is deliberate. A full semantic model gives exact resolution but needs a
/// working build and tens of seconds per worktree, which is precisely what makes switching
/// worktrees slow in a real IDE. Parsing gives near-instant answers that never fail
/// because a project will not restore; the cost is that resolution is by name, so
/// overloads and same-named types across namespaces come back as several candidates.
/// Monaco already renders that as a chooser.
/// </summary>
public sealed class CSharpIndexer : ILanguageIndexer
{
    public string Id => "csharp";

    private static readonly CSharpParseOptions ParseOptions = new(
        languageVersion: LanguageVersion.Preview,
        // Comments are needed for nothing here, and skipping trivia parsing is faster.
        documentationMode: DocumentationMode.None);

    public bool CanIndex(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);

    public FileIndex IndexFile(string repoRelativePath, string text)
    {
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(text), ParseOptions);
        var root = tree.GetRoot();

        var walker = new DeclarationWalker(repoRelativePath, tree.GetText());
        walker.Visit(root);

        // Collected in the same pass over an already-parsed tree, so the inverted index
        // costs one extra traversal rather than a second parse.
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken)) identifiers.Add(token.ValueText);
        }

        return new FileIndex { Declarations = walker.Declarations, Identifiers = identifiers };
    }

    public string? IdentifierAt(string text, int line, int column)
    {
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(text), ParseOptions);
        var sourceText = tree.GetText();

        if (line < 1 || line > sourceText.Lines.Count) return null;

        var textLine = sourceText.Lines[line - 1];
        var position = textLine.Start + Math.Max(0, column - 1);
        if (position >= sourceText.Length) position = sourceText.Length - 1;
        if (position < 0) return null;

        var token = tree.GetRoot().FindToken(position, findInsideTrivia: false);

        // Clicking whitespace after a token yields that token; require the position to
        // actually fall inside it, or a click in blank space would navigate somewhere.
        if (!token.Span.Contains(position) && !token.Span.IntersectsWith(position)) return null;

        return token.IsKind(SyntaxKind.IdentifierToken) ? token.ValueText : null;
    }

    public IReadOnlyList<SymbolReference> FindOccurrences(string repoRelativePath, string text, string identifier)
    {
        // Cheap reject before parsing: most files in a repository never mention the name.
        if (!text.Contains(identifier, StringComparison.Ordinal)) return [];

        var tree = CSharpSyntaxTree.ParseText(SourceText.From(text), ParseOptions);
        var sourceText = tree.GetText();
        var results = new List<SymbolReference>();

        foreach (var token in tree.GetRoot().DescendantTokens())
        {
            // Tokenising is what separates a real use from the same word inside a comment
            // or a string literal, which a plain text search cannot distinguish.
            if (!token.IsKind(SyntaxKind.IdentifierToken)) continue;
            if (!string.Equals(token.ValueText, identifier, StringComparison.Ordinal)) continue;

            var start = sourceText.Lines.GetLinePosition(token.SpanStart);
            var end = sourceText.Lines.GetLinePosition(token.Span.End);

            results.Add(new SymbolReference
            {
                FilePath = repoRelativePath,
                Line = start.Line + 1,
                Column = start.Character + 1,
                EndColumn = end.Character + 1,
                Preview = sourceText.Lines[start.Line].ToString().Trim(),
                IsDeclaration = IsDeclarationName(token),
            });
        }

        return results;
    }

    /// <summary>Whether a token is the name of the construct it belongs to.</summary>
    private static bool IsDeclarationName(SyntaxToken token) =>
        token.Parent is BaseTypeDeclarationSyntax type && type.Identifier == token ||
        token.Parent is MethodDeclarationSyntax method && method.Identifier == token ||
        token.Parent is PropertyDeclarationSyntax property && property.Identifier == token ||
        token.Parent is VariableDeclaratorSyntax variable && variable.Identifier == token ||
        token.Parent is DelegateDeclarationSyntax @delegate && @delegate.Identifier == token ||
        token.Parent is EnumMemberDeclarationSyntax member && member.Identifier == token;

    /// <summary>
    /// Walks a syntax tree collecting declarations, tracking the enclosing namespace and
    /// type so each one carries a qualified container name for disambiguation.
    /// </summary>
    private sealed class DeclarationWalker(string filePath, SourceText text) : CSharpSyntaxWalker
    {
        private readonly List<SymbolDeclaration> _declarations = [];
        private readonly Stack<string> _containers = new();

        public IReadOnlyList<SymbolDeclaration> Declarations => _declarations;

        private string? CurrentContainer =>
            _containers.Count == 0 ? null : string.Join('.', _containers.Reverse());

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            _containers.Push(node.Name.ToString());
            base.VisitNamespaceDeclaration(node);
            _containers.Pop();
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            // File-scoped namespaces never pop: everything after them is inside.
            _containers.Push(node.Name.ToString());
            base.VisitFileScopedNamespaceDeclaration(node);
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) =>
            VisitType(node, SymbolKind.Class, () => base.VisitClassDeclaration(node));

        public override void VisitStructDeclaration(StructDeclarationSyntax node) =>
            VisitType(node, SymbolKind.Struct, () => base.VisitStructDeclaration(node));

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) =>
            VisitType(node, SymbolKind.Interface, () => base.VisitInterfaceDeclaration(node));

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) =>
            VisitType(node, SymbolKind.Record, () => base.VisitRecordDeclaration(node));

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node) =>
            VisitType(node, SymbolKind.Enum, () => base.VisitEnumDeclaration(node));

        private void VisitType(BaseTypeDeclarationSyntax node, SymbolKind kind, Action visitChildren)
        {
            Add(node.Identifier, kind);
            _containers.Push(node.Identifier.ValueText);
            visitChildren();
            _containers.Pop();
        }

        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            Add(node.Identifier, SymbolKind.Delegate);
            base.VisitDelegateDeclaration(node);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            Add(node.Identifier, SymbolKind.Method);
            // Not descending: local functions and parameters are noise in a symbol index.
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            Add(node.Identifier, SymbolKind.Constructor);

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
            Add(node.Identifier, SymbolKind.Property);

        public override void VisitEventDeclaration(EventDeclarationSyntax node) =>
            Add(node.Identifier, SymbolKind.Event);

        public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) =>
            Add(node.Identifier, SymbolKind.EnumMember);

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            // One field declaration can declare several names: `int a, b, c;`
            foreach (var variable in node.Declaration.Variables)
                Add(variable.Identifier, SymbolKind.Field);
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            foreach (var variable in node.Declaration.Variables)
                Add(variable.Identifier, SymbolKind.Event);
        }

        private void Add(SyntaxToken identifier, SymbolKind kind)
        {
            if (identifier.IsMissing || identifier.ValueText.Length == 0) return;

            var start = text.Lines.GetLinePosition(identifier.SpanStart);
            var end = text.Lines.GetLinePosition(identifier.Span.End);

            _declarations.Add(new SymbolDeclaration
            {
                Name = identifier.ValueText,
                Kind = kind,
                FilePath = filePath,
                Line = start.Line + 1,
                Column = start.Character + 1,
                EndLine = end.Line + 1,
                EndColumn = end.Character + 1,
                ContainerName = CurrentContainer,
                Preview = text.Lines[start.Line].ToString().Trim(),
            });
        }
    }
}
