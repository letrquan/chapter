using System.Text.Json;

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

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chapter");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json)
                           ?? new AppSettings();

            // Parseable JSON with an explicit null member — what a truncated write or a
            // hand edit produces — deserialises past the property initialisers without
            // throwing, so the catch below never sees it and the null surfaces later as a
            // crash in the constructor, before any window exists to report it.
            settings.RecentRepos ??= [];
            settings.EditorPaths ??= [];
            settings.LastWorktree ??= [];
            settings.Theme ??= "system";
            settings.PreferredEditor ??= "";

            return settings;
        }
        catch
        {
            // A corrupt settings file must never stop the app starting; defaults are fine.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
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
