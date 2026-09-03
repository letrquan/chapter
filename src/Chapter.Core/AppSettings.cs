using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chapter.Core;

/// <summary>
/// User settings, persisted to LocalAppData. Deliberately small — anything that can be
/// re-derived from the repository at startup is not stored.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Repository roots to reopen at startup, most recent first.</summary>
    public List<string> RecentRepos { get; set; } = [];

    /// <summary>"dark", "light" or "system".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Preferred external editor id: "rider", "vscode", or empty to auto-detect.</summary>
    public string PreferredEditor { get; set; } = "";

    /// <summary>Explicit editor executable paths, keyed by editor id, overriding detection.</summary>
    public Dictionary<string, string> EditorPaths { get; set; } = [];

    /// <summary>Last active worktree per repository, so a restart lands where you left off.</summary>
    public Dictionary<string, string> LastWorktree { get; set; } = [];

    /// <summary>Commit message rules applied where a repository has no rules of its own.</summary>
    public Git.CommitMessagePolicy DefaultCommitPolicy { get; set; } = new();

    /// <summary>
    /// How commit messages are generated. Carries no credential — this file is plaintext,
    /// and the key lives encrypted in <see cref="Ai.ApiKeyStore"/>.
    /// </summary>
    public Ai.AiSettings Ai { get; set; } = new();

    /// <summary>
    /// Commit message rules per repository, keyed by its path.
    ///
    /// Conventional commits are house style in some projects and noise in others, so this
    /// has to be per repository rather than one global switch — the roadmap asks for exactly
    /// that. A worktree inherits its repository's entry, since a linked worktree is the same
    /// project by another path.
    /// </summary>
    public Dictionary<string, Git.CommitMessagePolicy> CommitPolicies { get; set; } = [];

    /// <summary>
    /// The last content snapshot the user explicitly reviewed for each worktree. This is
    /// deliberately metadata rather than a Git ref: reviewing must never write to a repo.
    /// </summary>
    public Dictionary<string, ReviewWatermark> ReviewWatermarks { get; set; } = [];

    /// <summary>
    /// The policy governing a worktree: its own entry, otherwise the entry of whichever
    /// configured repository contains it, otherwise the default.
    ///
    /// Longest match wins, so a rule set on a specific worktree beats the one on the
    /// repository it belongs to rather than being shadowed by it.
    /// </summary>
    public Git.CommitMessagePolicy CommitPolicyFor(string worktreePath)
    {
        if (CommitPolicies.Count == 0 || string.IsNullOrEmpty(worktreePath)) return DefaultCommitPolicy;

        if (CommitPolicies.TryGetValue(worktreePath, out var exact)) return exact;

        Git.CommitMessagePolicy? best = null;
        var bestLength = 0;

        foreach (var (path, policy) in CommitPolicies)
        {
            if (path.Length <= bestLength) continue;
            if (!worktreePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)) continue;

            // Guards against "C:\work\app" matching "C:\work\app-legacy": the character
            // after the prefix has to be a separator, or the paths are merely similar.
            if (worktreePath.Length > path.Length
                && worktreePath[path.Length] is not ('\\' or '/')) continue;

            best = policy;
            bestLength = path.Length;
        }

        return best ?? DefaultCommitPolicy;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chapter");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    /// <summary>The destination used by <see cref="Save"/>. Overridable by tests only.</summary>
    [JsonIgnore]
    internal string StoragePath { get; set; } = FilePath;

    public static AppSettings Load() => Load(FilePath);

    internal static AppSettings Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new AppSettings { StoragePath = filePath };

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), Json)
                           ?? new AppSettings();
            settings.StoragePath = filePath;

            // Parseable JSON with an explicit null member — what a truncated write or a
            // hand edit produces — deserialises past the property initialisers without
            // throwing, so the catch below never sees it and the null surfaces later as a
            // crash in the constructor, before any window exists to report it.
            settings.RecentRepos ??= [];
            settings.EditorPaths ??= [];
            settings.LastWorktree ??= [];
            settings.Theme ??= "system";
            settings.PreferredEditor ??= "";
            settings.CommitPolicies ??= [];
            settings.ReviewWatermarks ??= [];
            settings.DefaultCommitPolicy ??= new Git.CommitMessagePolicy();
            settings.Ai ??= new Ai.AiSettings();
            settings.Ai.Model ??= "claude-opus-5";
            settings.Ai.Effort ??= "low";
            settings.Ai.Provider ??= "anthropic";
            settings.Ai.BaseUrl ??= "";

            return settings;
        }
        catch
        {
            // A corrupt settings file must never stop the app starting; defaults are fine.
            return new AppSettings { StoragePath = filePath };
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(StoragePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // Losing a settings write is not worth interrupting the user over.
        }
    }

    public void RecordRepo(string repoPath)
    {
        RecentRepos.RemoveAll(r => string.Equals(r, repoPath, StringComparison.OrdinalIgnoreCase));
        RecentRepos.Insert(0, repoPath);
        if (RecentRepos.Count > 20) RecentRepos.RemoveRange(20, RecentRepos.Count - 20);
    }
}
