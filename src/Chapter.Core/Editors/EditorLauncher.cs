using System.Diagnostics;
using Chapter.Core.Contracts;
using Chapter.Core.Git;
using Microsoft.Win32;

namespace Chapter.Core.Editors;

/// <summary>
/// Finds installed editors and opens a file at an exact line in one.
///
/// Detection is deliberate rather than convenient: hardcoding the usual install folders
/// fails immediately in practice — the Rider on this machine lives under I:\Tool, not
/// Program Files. So the registry's uninstall entries are the primary source, with the
/// conventional locations only as a fallback.
/// </summary>
public sealed class EditorLauncher(AppSettings settings)
{
    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private List<EditorInfo>? _cache;

    /// <summary>Editors found on this machine, preferred one first.</summary>
    public IReadOnlyList<EditorInfo> Detect()
    {
        if (_cache is not null) return _cache;

        var found = new List<EditorInfo>();

        AddIfPresent(found, "rider", "JetBrains Rider", FindRider());
        AddIfPresent(found, "vscode", "Visual Studio Code", FindVsCode());

        // An explicit path in settings always wins, and can introduce editors we do not
        // know how to detect.
        foreach (var (id, path) in settings.EditorPaths)
        {
            if (!File.Exists(path)) continue;
            found.RemoveAll(e => e.Id == id);
            found.Insert(0, new EditorInfo { Id = id, Name = DisplayNameFor(id), Path = path });
        }

        if (settings.PreferredEditor.Length > 0)
        {
            var index = found.FindIndex(e => e.Id == settings.PreferredEditor);
            if (index > 0)
            {
                var preferred = found[index];
                found.RemoveAt(index);
                found.Insert(0, preferred);
            }
        }

        _cache = found;
        return found;
    }

    /// <summary>Clears the detection cache, for when settings change.</summary>
    public void Invalidate() => _cache = null;

    /// <summary>
    /// Opens a file at a position in the given editor, or the preferred one when the id
    /// is empty. Returns false when no editor could be found or launched.
    /// </summary>
    public bool Open(string worktreePath, string repoRelativePath, int line, int column, string editorId = "")
    {
        var editors = Detect();
        if (editors.Count == 0) return false;

        var editor = editorId.Length > 0
            ? editors.FirstOrDefault(e => e.Id == editorId) ?? editors[0]
            : editors[0];

        var absolutePath = RepoPaths.Resolve(worktreePath, repoRelativePath);
        var arguments = BuildArguments(editor.Id, worktreePath, absolutePath, line, column);

        var psi = new ProcessStartInfo(editor.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoPaths.ToPlatform(worktreePath),
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(psi);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Both editors reuse an already-open window when handed these arguments, which is the
    /// whole point — the alternative is the pile of windows this app exists to avoid.
    /// </summary>
    internal static string[] BuildArguments(
        string editorId, string worktreePath, string absolutePath, int line, int column) => editorId switch
    {
        // Rider needs the containing folder so it attaches the file to the right solution
        // rather than opening it as a lone text file with no context.
        "rider" => [RepoPaths.ToPlatform(worktreePath), "--line", line.ToString(), "--column", column.ToString(), absolutePath],

        // -r reuses the current window, -g jumps to file:line:column.
        "vscode" => ["-r", "-g", $"{absolutePath}:{line}:{column}"],

        _ => [absolutePath],
    };

    private static void AddIfPresent(List<EditorInfo> found, string id, string name, string? path)
    {
        if (path is not null && File.Exists(path))
            found.Add(new EditorInfo { Id = id, Name = name, Path = path });
    }

    private static string DisplayNameFor(string id) => id switch
    {
        "rider" => "JetBrains Rider",
        "vscode" => "Visual Studio Code",
        _ => id,
    };

    // -----------------------------------------------------------------------
    // Detection
    // -----------------------------------------------------------------------

    private static string? FindRider()
    {
        // Registry first: Rider installs wherever the user pointed it.
        var installLocation = FindInstallLocation(name => name.StartsWith("JetBrains Rider", StringComparison.OrdinalIgnoreCase));
        if (installLocation is not null)
        {
            var exe = Path.Combine(installLocation, "bin", "rider64.exe");
            if (File.Exists(exe)) return exe;
        }

        // JetBrains Toolbox keeps versioned installs under LocalAppData; take the newest.
        var toolbox = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JetBrains", "Toolbox", "apps");

        if (Directory.Exists(toolbox))
        {
            var candidate = Directory
                .EnumerateFiles(toolbox, "rider64.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (candidate is not null) return candidate;
        }

        return FirstExisting(
            @"C:\Program Files\JetBrains\JetBrains Rider\bin\rider64.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Rider\bin\rider64.exe"));
    }

    private static string? FindVsCode()
    {
        var installLocation = FindInstallLocation(name =>
            name.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase));

        if (installLocation is not null)
        {
            var exe = Path.Combine(installLocation, "Code.exe");
            if (File.Exists(exe)) return exe;
        }

        return FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Microsoft VS Code\Code.exe"),
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            @"C:\Program Files (x86)\Microsoft VS Code\Code.exe");
    }

    /// <summary>Scans the uninstall registry for a product whose display name matches.</summary>
    private static string? FindInstallLocation(Func<string, bool> matches)
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var keyPath in UninstallKeys)
            {
                try
                {
                    using var key = root.OpenSubKey(keyPath);
                    if (key is null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey?.GetValue("DisplayName") is not string displayName) continue;
                        if (!matches(displayName)) continue;

                        if (subKey.GetValue("InstallLocation") is string location && location.Length > 0)
                            return location.Trim('"');
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // A hive we cannot read is not a reason to give up on the others.
                }
            }
        }

        return null;
    }

    private static string? FirstExisting(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists);
}
