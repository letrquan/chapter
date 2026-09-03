using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Cross-worktree comparison uses real linked worktrees because the important cases are
/// repository-wide: ignored files must be excluded, while an untracked file in either
/// checkout must still be visible.
/// </summary>
public sealed class WorktreeComparisonTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private async Task<string> NewRepoAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-compare-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.email", "test@example.com");
        await RunAsync(root, "config", "user.name", "Test");
        await RunAsync(root, "config", "commit.gpgsign", "false");
        await RunAsync(root, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "shared.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored/\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");
        return root;
    }

    private string Sibling(string root, string name)
    {
        var path = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-" + name);
        _created.Add(path);
        return path;
    }

    private static async Task RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture command failed: {result.CommandLine}\n{result.StandardError}");
    }

    public void Dispose()
    {
        foreach (var path in Enumerable.Reverse(_created))
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public async Task Comparison_includes_live_changes_on_both_sides_but_not_ignored_files()
    {
        var root = await NewRepoAsync();
        var other = Sibling(root, "other");
        await RunAsync(root, "worktree", "add", other, "-b", "other");

        await File.WriteAllTextAsync(Path.Combine(root, "left.txt"), "left\n");
        await File.WriteAllTextAsync(Path.Combine(other, "right.txt"), "right\n");
        Directory.CreateDirectory(Path.Combine(root, "ignored"));
        Directory.CreateDirectory(Path.Combine(other, "ignored"));
        await File.WriteAllTextAsync(Path.Combine(root, "ignored", "secret.txt"), "secret\n");
        await File.WriteAllTextAsync(Path.Combine(other, "ignored", "secret.txt"), "different\n");

        var worktrees = await new WorktreeService(Git, new GitWriter(Git, new OperationLog())).ListAsync(root);
        var left = worktrees.Single(w => w.IsMain);
        var right = worktrees.Single(w => !w.IsMain);
        var comparison = await new WorktreeComparisonService(Git).CompareAsync(left, right);

        Assert.Contains(comparison.Files, file => file.Path == "left.txt" && file.Kind == ChangeKind.Deleted);
        Assert.Contains(comparison.Files, file => file.Path == "right.txt" && file.Kind == ChangeKind.Added);
        Assert.DoesNotContain(comparison.Files, file => file.Path.Contains("ignored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exact_rename_is_reported_with_both_paths_and_content_can_be_loaded()
    {
        var root = await NewRepoAsync();
        var other = Sibling(root, "other");
        await RunAsync(root, "worktree", "add", other, "-b", "other");

        File.Move(Path.Combine(other, "shared.txt"), Path.Combine(other, "renamed.txt"));

        var worktrees = await new WorktreeService(Git, new GitWriter(Git, new OperationLog())).ListAsync(root);
        var left = worktrees.Single(w => w.IsMain);
        var right = worktrees.Single(w => !w.IsMain);
        var service = new WorktreeComparisonService(Git);
        var comparison = await service.CompareAsync(left, right);

        var rename = Assert.Single(comparison.Files, file => file.Kind == ChangeKind.Renamed);
        Assert.Equal("shared.txt", rename.OldPath);
        Assert.Equal("shared.txt", rename.LeftPath);
        Assert.Equal("renamed.txt", rename.RightPath);

        var content = await service.GetFileAsync(left, right, rename.LeftPath, rename.RightPath);
        Assert.Equal("base\n", content.LeftText);
        Assert.Equal("base\n", content.RightText);
        Assert.Equal("renamed.txt", content.Path);
    }

    [Fact]
    public async Task Bridge_comparison_round_trips_and_rejects_a_worktree_from_another_repository()
    {
        var root = await NewRepoAsync();
        var other = Sibling(root, "other");
        await RunAsync(root, "worktree", "add", other, "-b", "other");
        await File.WriteAllTextAsync(Path.Combine(other, "shared.txt"), "changed on the right\n");
        var foreign = await NewRepoAsync();

        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());
        var worktrees = await workspace.Worktrees.ListAsync(root);
        var left = worktrees.Single(w => w.IsMain).Path;
        var right = worktrees.Single(w => !w.IsMain).Path;

        var result = await CallAsync(dispatcher, "getWorktreeComparison", new
        {
            leftWorktreePath = left,
            rightWorktreePath = right,
        });
        Assert.True(result.TryGetProperty("left", out _));
        Assert.True(result.TryGetProperty("right", out _));
        var file = Assert.Single(result.GetProperty("files").EnumerateArray());
        Assert.Equal("shared.txt", file.GetProperty("path").GetString());
        Assert.Equal("shared.txt", file.GetProperty("leftPath").GetString());
        Assert.Equal("shared.txt", file.GetProperty("rightPath").GetString());
        Assert.Equal("modified", file.GetProperty("kind").GetString());
        Assert.True(file.GetProperty("leftExists").GetBoolean());
        Assert.True(file.GetProperty("rightExists").GetBoolean());
        Assert.True(result.GetProperty("totalAdded").GetInt32() > 0);
        Assert.True(result.GetProperty("totalRemoved").GetInt32() > 0);

        var content = await CallAsync(dispatcher, "getWorktreeComparisonFile", new
        {
            leftWorktreePath = left,
            rightWorktreePath = right,
            leftPath = "shared.txt",
            rightPath = "shared.txt",
        });
        Assert.Equal("base\n", content.GetProperty("leftText").GetString());
        Assert.Equal("changed on the right\n", content.GetProperty("rightText").GetString());
        Assert.Equal("plaintext", content.GetProperty("language").GetString());
        Assert.False(content.GetProperty("isBinary").GetBoolean());

        var responseJson = await dispatcher.HandleAsync(JsonSerializer.Serialize(new
        {
            id = 2,
            method = "getWorktreeComparison",
            @params = new { leftWorktreePath = left, rightWorktreePath = foreign },
        }, BridgeJson.Options));
        var response = JsonDocument.Parse(responseJson).RootElement;
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Contains("not open", response.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Binary_changes_keep_byte_metadata_and_return_no_text()
    {
        var root = await NewRepoAsync();
        var other = Sibling(root, "other");
        await RunAsync(root, "worktree", "add", other, "-b", "other");
        await File.WriteAllBytesAsync(Path.Combine(root, "asset.bin"), [0, 1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(other, "asset.bin"), [0, 9, 8, 7, 6]);

        var worktrees = await new WorktreeService(Git, new GitWriter(Git, new OperationLog())).ListAsync(root);
        var left = worktrees.Single(w => w.IsMain);
        var right = worktrees.Single(w => !w.IsMain);
        var service = new WorktreeComparisonService(Git);
        var comparison = await service.CompareAsync(left, right);

        var file = Assert.Single(comparison.Files, entry => entry.Path == "asset.bin");
        Assert.True(file.IsBinary);
        Assert.Equal(4, file.LeftBytes);
        Assert.Equal(5, file.RightBytes);

        var content = await service.GetFileAsync(left, right, file.LeftPath, file.RightPath);
        Assert.True(content.IsBinary);
        Assert.Empty(content.LeftText);
        Assert.Empty(content.RightText);
    }

    [Fact]
    public void Line_counts_cover_insertions_deletions_and_replacements()
    {
        Assert.Equal((1, 0), WorktreeComparisonService.CountLineChanges("a\n", "a\nb\n"));
        Assert.Equal((0, 1), WorktreeComparisonService.CountLineChanges("a\nb\n", "a\n"));
        Assert.Equal((1, 1), WorktreeComparisonService.CountLineChanges("a\n", "b\n"));
        Assert.Equal((0, 0), WorktreeComparisonService.CountLineChanges("a\r\nb\r\n", "a\nb\n"));
    }

    private static async Task<JsonElement> CallAsync(BridgeDispatcher dispatcher, string method, object parameters)
    {
        var request = JsonSerializer.Serialize(new { id = 1, method, @params = parameters }, BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;
        Assert.True(
            response.GetProperty("ok").GetBoolean(),
            response.TryGetProperty("error", out var error) ? error.GetString() : "bridge call failed");
        return response.GetProperty("result");
    }
}
