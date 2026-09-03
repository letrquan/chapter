using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// The repository lease is intentionally tested independently of Git's index.lock. Two
/// Chapter instances can otherwise make contradictory decisions even when each Git command
/// would succeed on its own.
/// </summary>
public sealed class RepositoryWriteLockTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    [Fact]
    public async Task Independent_instances_cannot_hold_the_same_repository_lease()
    {
        var root = await NewRepoAsync();
        var first = await RepositoryWriteLock.AcquireAsync(
            new GitCli(), root, default, TimeSpan.FromMilliseconds(500));
        Assert.NotNull(first);

        try
        {
            var second = await RepositoryWriteLock.AcquireAsync(
                new GitCli(), root, default, TimeSpan.FromMilliseconds(120));
            Assert.Null(second);
        }
        finally
        {
            first!.Dispose();
        }

        using var afterRelease = await RepositoryWriteLock.AcquireAsync(
            new GitCli(), root, default, TimeSpan.FromMilliseconds(500));
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public async Task Linked_worktrees_share_the_repository_lease()
    {
        var root = await NewRepoAsync();
        var linked = root + "-linked";
        _created.Add(linked);
        await RunAsync(root, "worktree", "add", linked, "-b", "linked");

        using var first = await RepositoryWriteLock.AcquireAsync(
            new GitCli(), root, default, TimeSpan.FromMilliseconds(500));
        Assert.NotNull(first);

        var second = await RepositoryWriteLock.AcquireAsync(
            new GitCli(), linked, default, TimeSpan.FromMilliseconds(120));
        Assert.Null(second);
    }

    [Fact]
    public async Task A_writer_blocked_by_this_process_says_so_rather_than_blaming_another_instance()
    {
        var root = await NewRepoAsync();
        var first = await RepositoryWriteLock.AcquireAsync(
            new GitCli(), root, default, TimeSpan.FromMilliseconds(500));
        Assert.NotNull(first);

        try
        {
            var log = new OperationLog();
            var workspace = new WorkspaceService(new GitCli(), log);
            await workspace.GetWorktreesAsync(root);

            var mutation = await workspace.Writer.RunAsync(
                root, "test mutation", CancellationToken.None, "status");

            Assert.False(mutation.Success);
            Assert.Equal(GitFailure.Locked, mutation.Failure);

            // The lease above is held by this process, so this is the same-window case. It
            // used to be reported as "another Chapter instance is writing this repository",
            // which is untrue whenever only one window is open — and that is the common case
            // for it, because the app's own long operations are what hold the lease.
            Assert.Contains("this window", mutation.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("another Chapter instance", mutation.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(log.Recent(), entry => entry.Failure == nameof(GitFailure.Locked));
        }
        finally
        {
            first!.Dispose();
        }
    }

    private async Task<string> NewRepoAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-lease-" + Guid.NewGuid().ToString("N"));
        _created.Add(root);
        Directory.CreateDirectory(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.name", "Lease Test");
        await RunAsync(root, "config", "user.email", "lease@example.com");
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "base\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");
        return root;
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
}
