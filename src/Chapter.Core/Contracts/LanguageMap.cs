namespace Chapter.Core.Contracts;

/// <summary>
/// Maps file extensions to Monaco language ids.
///
/// Monaco tokenises far more than C#, and that is deliberate: the worktrees in daily use
/// today are TypeScript, so browsing and diffing has to look right for every language even
/// though semantic navigation is C#-only in this version.
/// </summary>
public static class LanguageMap
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".razor"] = "razor",
        [".cshtml"] = "razor",
        [".vb"] = "vb",
        [".fs"] = "fsharp",
        [".fsx"] = "fsharp",

        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".mts"] = "typescript",
        [".cts"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",

        [".json"] = "json",
        [".jsonc"] = "json",
        [".json5"] = "json",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".toml"] = "ini",
        [".ini"] = "ini",
        [".editorconfig"] = "ini",

        [".xml"] = "xml",
        [".csproj"] = "xml",
        [".fsproj"] = "xml",
        [".vbproj"] = "xml",
        [".props"] = "xml",
        [".targets"] = "xml",
        [".slnx"] = "xml",
        [".config"] = "xml",
        [".axaml"] = "xml",
        [".xaml"] = "xml",
        [".svg"] = "xml",

        [".html"] = "html",
        [".htm"] = "html",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",

        [".md"] = "markdown",
        [".markdown"] = "markdown",
        [".mdx"] = "markdown",

        [".py"] = "python",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".kts"] = "kotlin",
        [".swift"] = "swift",
        [".rb"] = "ruby",
        [".php"] = "php",
        [".lua"] = "lua",
        [".dart"] = "dart",
        [".scala"] = "scala",
        [".r"] = "r",

        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".cxx"] = "cpp",
        [".hpp"] = "cpp",

        [".sh"] = "shell",
        [".bash"] = "shell",
        [".zsh"] = "shell",
        [".ps1"] = "powershell",
        [".psm1"] = "powershell",
        [".psd1"] = "powershell",
        [".bat"] = "bat",
        [".cmd"] = "bat",

        [".sql"] = "sql",
        [".graphql"] = "graphql",
        [".gql"] = "graphql",
        [".proto"] = "proto",
        [".dockerfile"] = "dockerfile",
    };

    /// <summary>Files with no extension whose name alone identifies the language.</summary>
    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile",
        ["Makefile"] = "makefile",
        [".gitignore"] = "ignore",
        [".gitattributes"] = "ignore",
        [".dockerignore"] = "ignore",
    };

    public static string ForPath(string path)
    {
        var fileName = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

        if (ByFileName.TryGetValue(fileName, out var byName)) return byName;

        var dot = fileName.LastIndexOf('.');
        if (dot < 0) return "plaintext";

        return ByExtension.TryGetValue(fileName[dot..], out var byExt) ? byExt : "plaintext";
    }

    /// <summary>Whether this version can offer semantic navigation for the file.</summary>
    public static bool HasSemanticSupport(string path) =>
        ForPath(path) == "csharp";
}
