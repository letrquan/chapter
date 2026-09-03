using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Review marks are metadata, but their snapshot is a real view of a worktree. These tests
/// keep the two halves honest: a mark survives a restart, ordinary work invalidates it, and
/// ignored build output does not make a worktree perpetually look new.
/// </summary>
public sealed class ReviewWatermarkTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private async Task<string> NewRepoAsync(bool commit = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-watermark-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.email", "test@example.com");
        await RunAsync(root, "config", "user.name", "Test");
        await RunAsync(root, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "base\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored/\n");
        if (commit)
        {
            await RunAsync(root, "add", "-A");
            await RunAsync(root, "commit", "-m", "initial");
        }

        return root;
    }

    private static async Task RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture command failed: {result.CommandLine}\n{result.StandardError}");
    }

    private AppSettings Settings()
    {
        var path = Path.Combine(Path.GetTempPath(), "chapter-watermark-settings-" + Guid.NewGuid().ToString("N") + ".json");
        _created.Add(path);
        return new AppSettings { StoragePath = path };
    }

    public void Dispose()
    {
        foreach (var path in Enumerable.Reverse(_created))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public async Task Mark_persists_and_a_new_service_reads_the_same_snapshot()
    {
        var root = await NewRepoAsync();
        var settings = Settings();
        var service = new ReviewWatermarkService(Git, settings);

        var before = await service.GetAsync(root);
        Assert.True(before.Success);
        Assert.Null(before.Watermark);
        Assert.True(before.HasUnreviewedChanges);

        var marked = await service.MarkAsync(root);
        Assert.True(marked.Success);
        Assert.False(marked.HasUnreviewedChanges);
        Assert.True(File.Exists(settings.StoragePath));

        var reloaded = AppSettings.Load(settings.StoragePath);
        var afterRestart = await new ReviewWatermarkService(Git, reloaded).GetAsync(root);
        Assert.True(afterRestart.Success);
        Assert.False(afterRestart.HasUnreviewedChanges);
        Assert.Equal(marked.Fingerprint, afterRestart.Fingerprint);
        Assert.Equal(marked.Watermark!.ReviewedAt, afterRestart.Watermark!.ReviewedAt);
    }

    [Fact]
    public async Task Tracked_and_ordinary_untracked_changes_make_the_mark_new_again()
    {
        var root = await NewRepoAsync();
        var service = new ReviewWatermarkService(Git, Settings());
        await service.MarkAsync(root);

        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "edited\n");
        var tracked = await service.GetAsync(root);
        Assert.True(tracked.HasUnreviewedChanges);

        await service.MarkAsync(root);
        await File.WriteAllTextAsync(Path.Combine(root, "ordinary.txt"), "new\n");
        var untracked = await service.GetAsync(root);
        Assert.True(untracked.HasUnreviewedChanges);
    }

    [Fact]
    public async Task Ignored_files_and_atomic_write_temps_do_not_invalidate_the_mark()
    {
        var root = await NewRepoAsync();
        var service = new ReviewWatermarkService(Git, Settings());
        await service.MarkAsync(root);

        Directory.CreateDirectory(Path.Combine(root, "ignored"));
        await File.WriteAllTextAsync(Path.Combine(root, "ignored", "build.bin"), "generated\n");
        await File.WriteAllTextAsync(
            Path.Combine(root, "tracked.txt" + WorkingTreeWriter.TempSuffix), "half-written\n");

        var current = await service.GetAsync(root);
        Assert.True(current.Success);
        Assert.False(current.HasUnreviewedChanges);
    }

    [Fact]
    public async Task An_unborn_repository_has_a_usable_empty_head_snapshot()
    {
        var root = await NewRepoAsync(commit: false);
        var service = new ReviewWatermarkService(Git, Settings());

        var first = await service.GetAsync(root);
        Assert.True(first.Success, first.Detail);
        Assert.Equal(string.Empty, first.Head);
        Assert.True(first.HasUnreviewedChanges);

        var marked = await service.MarkAsync(root);
        Assert.True(marked.Success, marked.Detail);
        Assert.False(marked.HasUnreviewedChanges);
    }

    [Fact]
    public async Task Mark_refuses_when_the_worktree_changed_after_the_review_snapshot()
    {
        var root = await NewRepoAsync();
        var service = new ReviewWatermarkService(Git, Settings());
        var shown = await service.GetAsync(root);

        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "arrived later\n");
        var refused = await service.MarkAsync(root, shown.Fingerprint);

        Assert.False(refused.Success);
        Assert.True(refused.HasUnreviewedChanges);
        Assert.Contains("changed", refused.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(shown.Fingerprint, refused.Fingerprint);
        Assert.Null(refused.Watermark);
    }

    [Fact]
    public async Task Bridge_round_trips_watermark_fields()
    {
        var root = await NewRepoAsync();
        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        var dispatcher = new BridgeDispatcher(workspace, Settings());

        var readRequest = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "getReviewWatermark",
            @params = new { worktreePath = root },
        }, BridgeJson.Options);
        var readResponse = JsonDocument.Parse(await dispatcher.HandleAsync(readRequest)).RootElement;
        var expected = readResponse.GetProperty("result").GetProperty("fingerprint").GetString();

        var request = JsonSerializer.Serialize(new
        {
            id = 2,
            method = "markReviewWatermark",
            @params = new { worktreePath = root, expectedFingerprint = expected },
        }, BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;

        Assert.True(response.GetProperty("ok").GetBoolean());
        var result = response.GetProperty("result");
        foreach (var field in new[] { "worktreePath", "head", "fingerprint", "watermark", "hasUnreviewedChanges", "success" })
            Assert.True(result.TryGetProperty(field, out _), $"missing '{field}'");
        Assert.False(result.GetProperty("hasUnreviewedChanges").GetBoolean());
        Assert.Equal(root, result.GetProperty("worktreePath").GetString(), ignoreCase: true);
    }
}
