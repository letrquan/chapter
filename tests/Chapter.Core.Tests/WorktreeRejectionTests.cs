using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Rejecting an agent worktree is tested against disposable linked repositories. The source
/// branch is reset to its default-branch merge base, and every path named by the permanent
/// confirmation is removed before committed history moves.
/// </summary>
public sealed class WorktreeRejectionTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private async Task<(string Main, string Agent)> NewRepoAsync()
    {
        var main = Path.Combine(Path.GetTempPath(), "chapter-reject-" + Guid.NewGuid().ToString("N")[..8]);
        var agent = main + "-agent";
        _created.Add(agent);
        _created.Add(main);
        Directory.CreateDirectory(main);

        await RunAsync(main, "init", "-b", "main");
        await RunAsync(main, "config", "user.email", "test@example.com");
        await RunAsync(main, "config", "user.name", "Test");
        await RunAsync(main, "config", "commit.gpgsign", "false");
        await RunAsync(main, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(main, "base.txt"), "base\n");
        await RunAsync(main, "add", "-A");
        await RunAsync(main, "commit", "-m", "initial");
        await RunAsync(main, "worktree", "add", agent, "-b", "agent");

        return (main, agent);
    }

    private static async Task RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture setup failed: {result.CommandLine}\n{result.StandardError}");
    }

    private async Task<WorkspaceService> WorkspaceAsync(string root)
    {
        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        return workspace;
    }

    [Fact]
    public async Task Preview_and_reject_reset_the_branch_and_remove_confirmed_untracked_and_ignored_files()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(agent, "committed.txt"), "agent commit\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "agent work");

        await File.WriteAllTextAsync(Path.Combine(agent, "ordinary.txt"), "scratch\n");
        await File.WriteAllTextAsync(Path.Combine(agent, ".gitignore"), "ignored.txt\n");
        await RunAsync(agent, "add", ".gitignore");
        await RunAsync(agent, "commit", "-m", "ignore generated output");
        await File.WriteAllTextAsync(Path.Combine(agent, "ignored.txt"), "discard me\n");

        var preview = await workspace.Rejections.PreviewAsync(main, agent);

        Assert.True(preview.Success, preview.Message);
        Assert.Equal("agent", preview.SourceBranch);
        Assert.Equal("main", preview.BaseBranch);
        Assert.NotEqual(preview.SourceHead, preview.BaseHead);
        Assert.Equal(2, preview.CommitCount);
        Assert.Contains(preview.Paths, path => path.Path == "committed.txt");
        Assert.Contains(preview.Paths, path => path.Path == "ordinary.txt" && path.Kind == ChangeKind.Untracked);
        Assert.Contains("ignored.txt", preview.IgnoredPaths);
        Assert.NotEmpty(preview.SnapshotFingerprint);

        var result = await workspace.Rejections.RejectAsync(
            main,
            agent,
            preview.SourceHead,
            preview.BaseHead,
            preview.SnapshotFingerprint);

        Assert.True(result.Success, result.Message);
        Assert.Equal(preview.BaseHead, (await Git.TryRunAsync(agent, default, "rev-parse", "HEAD")).Trimmed);
        Assert.False(File.Exists(Path.Combine(agent, "committed.txt")));
        Assert.False(File.Exists(Path.Combine(agent, "ordinary.txt")));
        Assert.False(File.Exists(Path.Combine(agent, "ignored.txt")));
        Assert.False(File.Exists(Path.Combine(main, "committed.txt")));
        Assert.Null(workspace.Undo.Peek(main));
        Assert.True(workspace.Undo.Peek(agent) is not null);
        Assert.True(workspace.Undo.Peek(agent)!.IsDestructive);

        var undo = await workspace.Undo.UndoAsync(agent);
        Assert.True(undo.Success, undo.Message);
        Assert.Equal(preview.SourceHead, (await Git.TryRunAsync(agent, default, "rev-parse", "HEAD")).Trimmed);
        Assert.True(File.Exists(Path.Combine(agent, "committed.txt")));
        Assert.False(File.Exists(Path.Combine(agent, "ordinary.txt")));
        Assert.False(File.Exists(Path.Combine(agent, "ignored.txt")));
    }

    [Fact]
    public async Task Reject_removes_ignored_only_work_that_was_named_in_the_preview()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(main, ".gitignore"), "generated/\n");
        await RunAsync(main, "add", ".gitignore");
        await RunAsync(main, "commit", "-m", "ignore generated output");
        await RunAsync(agent, "reset", "--hard", "main");
        Directory.CreateDirectory(Path.Combine(agent, "generated"));
        await File.WriteAllTextAsync(Path.Combine(agent, "generated", "secret.txt"), "discard me\n");

        var preview = await workspace.Rejections.PreviewAsync(main, agent);
        Assert.True(preview.Success, preview.Message);
        Assert.Equal(0, preview.CommitCount);
        Assert.Empty(preview.Paths);
        Assert.Contains("generated/secret.txt", preview.IgnoredPaths);

        var result = await workspace.Rejections.RejectAsync(
            main, agent, preview.SourceHead, preview.BaseHead, preview.SnapshotFingerprint);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(Path.Combine(agent, "generated", "secret.txt")));
        Assert.Null(workspace.Undo.Peek(agent));
    }

    [Fact]
    public async Task Reject_refuses_when_ignored_content_changes_after_preview()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);
        await File.WriteAllTextAsync(Path.Combine(agent, ".gitignore"), "secret.txt\n");
        await RunAsync(agent, "add", ".gitignore");
        await RunAsync(agent, "commit", "-m", "ignore secret");
        await File.WriteAllTextAsync(Path.Combine(agent, "secret.txt"), "before\n");

        var preview = await workspace.Rejections.PreviewAsync(main, agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "secret.txt"), "after\n");

        var result = await workspace.Rejections.RejectAsync(
            main, agent, preview.SourceHead, preview.BaseHead, preview.SnapshotFingerprint);

        Assert.False(result.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, result.Cleanup.Failure);
        Assert.Equal("after\n", await File.ReadAllTextAsync(Path.Combine(agent, "secret.txt")));
        Assert.Equal(preview.SourceHead, (await Git.TryRunAsync(agent, default, "rev-parse", "HEAD")).Trimmed);
    }

    [Fact]
    public async Task Reject_refuses_reset_when_a_new_file_appears_after_scoped_cleanup()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);
        await File.WriteAllTextAsync(Path.Combine(agent, "scratch.txt"), "discard me\n");

        var preview = await workspace.Rejections.PreviewAsync(main, agent);
        var previous = workspace.Writer.Mutated;
        workspace.Writer.Mutated = path =>
        {
            previous?.Invoke(path);
            if (string.Equals(path, agent, StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(Path.Combine(agent, "arrived-late.txt"), "keep me\n");
        };

        try
        {
            var result = await workspace.Rejections.RejectAsync(
                main, agent, preview.SourceHead, preview.BaseHead, preview.SnapshotFingerprint);

            Assert.False(result.Success);
            Assert.True(result.Cleanup.Success, result.Cleanup.Message);
            Assert.Equal(GitFailure.WouldLoseChanges, result.Reset.Failure);
            Assert.False(File.Exists(Path.Combine(agent, "scratch.txt")));
            Assert.Equal("keep me\n", await File.ReadAllTextAsync(Path.Combine(agent, "arrived-late.txt")));
            Assert.Equal(preview.SourceHead, (await Git.TryRunAsync(agent, default, "rev-parse", "HEAD")).Trimmed);
            Assert.Null(workspace.Undo.Peek(agent));
        }
        finally
        {
            workspace.Writer.Mutated = previous;
        }
    }

    [Fact]
    public async Task Preview_refuses_main_locked_and_detached_worktrees()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        var mainPreview = await workspace.Rejections.PreviewAsync(main, main);
        Assert.False(mainPreview.Success);
        Assert.Contains("main worktree", mainPreview.Detail, StringComparison.OrdinalIgnoreCase);

        await RunAsync(main, "worktree", "lock", "--reason", "agent owns it", agent);
        var lockedPreview = await workspace.Rejections.PreviewAsync(main, agent);
        Assert.False(lockedPreview.Success);
        Assert.Contains("locked", lockedPreview.Detail, StringComparison.OrdinalIgnoreCase);
        await RunAsync(main, "worktree", "unlock", agent);

        await RunAsync(agent, "checkout", "--detach");
        var detachedPreview = await workspace.Rejections.PreviewAsync(main, agent);
        Assert.False(detachedPreview.Success);
        Assert.Contains("detached", detachedPreview.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reject_refuses_when_an_untracked_file_changes_after_preview()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);
        await File.WriteAllTextAsync(Path.Combine(agent, "scratch.txt"), "before\n");

        var preview = await workspace.Rejections.PreviewAsync(main, agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "scratch.txt"), "after\n");

        var result = await workspace.Rejections.RejectAsync(
            main,
            agent,
            preview.SourceHead,
            preview.BaseHead,
            preview.SnapshotFingerprint);

        Assert.False(result.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, result.Cleanup.Failure);
        Assert.Equal("after\n", await File.ReadAllTextAsync(Path.Combine(agent, "scratch.txt")));
        Assert.Equal(preview.SourceHead, (await Git.TryRunAsync(agent, default, "rev-parse", "HEAD")).Trimmed);
    }

    [Fact]
    public async Task Reject_refuses_a_source_without_a_common_default_branch_history()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        await RunAsync(agent, "checkout", "--orphan", "orphan");
        await RunAsync(agent, "rm", "-rf", ".");
        await File.WriteAllTextAsync(Path.Combine(agent, "orphan.txt"), "orphan\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "unrelated");

        var preview = await workspace.Rejections.PreviewAsync(main, agent);

        Assert.False(preview.Success);
        Assert.Equal(GitFailure.NotFound, preview.Failure);
        Assert.Contains("common history", preview.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(agent, "orphan.txt")));
    }

    [Fact]
    public async Task Bridge_preview_and_reject_round_trip_the_snapshot_fields()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        await File.WriteAllTextAsync(Path.Combine(agent, "agent.txt"), "work\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "agent work");

        var previewRequest = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "previewRejectWorktree",
            @params = new { worktreePath = main, target = agent },
        }, BridgeJson.Options);
        var previewResponse = JsonDocument.Parse(await dispatcher.HandleAsync(previewRequest)).RootElement;
        Assert.True(previewResponse.GetProperty("ok").GetBoolean());
        var preview = previewResponse.GetProperty("result");

        var rejectRequest = JsonSerializer.Serialize(new
        {
            id = 2,
            method = "rejectWorktree",
            @params = new
            {
                worktreePath = main,
                target = agent,
                expectedSourceHead = preview.GetProperty("sourceHead").GetString(),
                expectedBaseHead = preview.GetProperty("baseHead").GetString(),
                expectedSnapshotFingerprint = preview.GetProperty("snapshotFingerprint").GetString(),
            },
        }, BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(rejectRequest)).RootElement;

        Assert.True(response.GetProperty("ok").GetBoolean());
        var payload = response.GetProperty("result");
        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.True(payload.GetProperty("cleanup").GetProperty("ok").GetBoolean());
        Assert.True(payload.GetProperty("reset").GetProperty("ok").GetBoolean());
        Assert.Equal("main", payload.GetProperty("baseBranch").GetString());
        Assert.False(File.Exists(Path.Combine(agent, "agent.txt")));
    }

    public void Dispose()
    {
        foreach (var root in Enumerable.Reverse(_created))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }
}
