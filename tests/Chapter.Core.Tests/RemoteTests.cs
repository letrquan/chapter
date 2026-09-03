using System.Diagnostics;
using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Remote operations against disposable repositories. The remote is a local bare repository,
/// so the tests exercise git's real fetch/push protocol without depending on credentials or a
/// network service that could mutate somebody else's history.
/// </summary>
public sealed class RemoteTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private async Task<string> NewRepoAsync(string prefix = "chapter-remote")
    {
        var root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}"[..25]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.email", "test@example.com");
        await RunAsync(root, "config", "user.name", "Test User");
        await RunAsync(root, "config", "commit.gpgsign", "false");
        await RunAsync(root, "config", "core.autocrlf", "false");

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "one\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");
        return root;
    }

    private async Task<string> NewBareAsync(string source)
    {
        var bare = Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileName(source)}-bare");
        _created.Add(bare);
        await RunAsync(source, "init", "--bare", bare);
        return bare;
    }

    private static async Task<GitResult> RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture command failed: {result.CommandLine}\n{result.StandardError}");
        return result;
    }

    private static async Task<WorkspaceService> OpenAsync(string root)
    {
        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        return workspace;
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
                // A failed cleanup is less useful than a failed git assertion.
            }
        }
    }

    [Fact]
    public void Parses_fetch_and_push_urls_in_git_remote_output()
    {
        var remotes = RemoteService.Parse(
            "origin\thttps://user:token@example.com/a repo (fetch)\n" +
            "origin\tgit@example.com:a/repo (push)\n" +
            "upstream\tC:/mirror (fetch)\n");

        Assert.Equal(2, remotes.Count);
        Assert.Equal("origin", remotes[0].Name);
        Assert.Equal("https://user:token@example.com/a repo", remotes[0].FetchUrl);
        Assert.Equal("git@example.com:a/repo", remotes[0].PushUrl);
        Assert.Equal("C:/mirror", remotes[1].FetchUrl);
        Assert.Equal("C:/mirror", remotes[1].PushUrl);
    }

    [Fact]
    public async Task Adds_renames_prunes_and_removes_a_remote()
    {
        var root = await NewRepoAsync();
        var workspace = await OpenAsync(root);

        var added = await workspace.Remotes.AddAsync(root, "origin", root);
        Assert.True(added.Success, added.Message);
        Assert.Equal("origin", Assert.Single(await workspace.Remotes.ListAsync(root)).Name);

        var renamed = await workspace.Remotes.RenameAsync(root, "origin", "upstream");
        Assert.True(renamed.Success, renamed.Message);
        Assert.Equal("upstream", Assert.Single(await workspace.Remotes.ListAsync(root)).Name);

        var pruned = await workspace.Remotes.PruneAsync(root, "upstream");
        Assert.True(pruned.Success, pruned.Message);

        var removed = await workspace.Remotes.RemoveAsync(root, "upstream");
        Assert.True(removed.Success, removed.Message);
        Assert.Empty(await workspace.Remotes.ListAsync(root));
    }

    [Fact]
    public async Task Embedded_url_credentials_never_reach_the_payload_or_operation_log()
    {
        var root = await NewRepoAsync();
        var log = new OperationLog();
        var workspace = new WorkspaceService(Git, log);
        await workspace.GetWorktreesAsync(root);

        const string url = "https://user:secret-token@example.com/repo.git";
        var added = await workspace.Remotes.AddAsync(root, "origin", url);
        Assert.True(added.Success, added.Message);
        Assert.DoesNotContain("secret-token", added.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", Assert.Single(log.Recent()).CommandLine, StringComparison.Ordinal);

        var remote = Assert.Single(await workspace.Remotes.ListAsync(root));
        Assert.Equal("https://***@example.com/repo.git", remote.FetchUrl);
        Assert.Equal("https://***@example.com/repo.git", remote.PushUrl);
    }

    [Fact]
    public async Task Pushes_and_fetches_against_a_local_bare_repository()
    {
        var root = await NewRepoAsync();
        var bare = await NewBareAsync(root);
        var workspace = await OpenAsync(root);

        Assert.True((await workspace.Remotes.AddAsync(root, "origin", bare)).Success);
        var pushed = await workspace.Remotes.PushAsync(root, "origin", "main", setUpstream: true);
        Assert.True(pushed.Success, pushed.Message);

        var remoteHead = (await Git.RunAsync(bare, default, "rev-parse", "refs/heads/main")).Trim();
        var localHead = (await Git.RunAsync(root, default, "rev-parse", "HEAD")).Trim();
        Assert.Equal(localHead, remoteHead);

        // The remote branch is now a real tracking ref. Fetching it again is a successful,
        // observable network operation even though there is nothing new to transfer.
        var fetched = await workspace.Remotes.FetchAsync(root, "origin");
        Assert.True(fetched.Success, fetched.Message);
        var tracking = await Git.RunAsync(root, default, "rev-parse", "refs/remotes/origin/main");
        Assert.Equal(localHead, tracking.Trim());
    }

    [Fact]
    public async Task Worktree_listing_carries_tracking_counts_after_a_fetch()
    {
        var root = await NewRepoAsync();
        var bare = await NewBareAsync(root);
        var workspace = await OpenAsync(root);
        Assert.True((await workspace.Remotes.AddAsync(root, "origin", bare)).Success);
        Assert.True((await workspace.Remotes.PushAsync(root, "origin", "main", setUpstream: true)).Success);

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "local\n");
        await RunAsync(root, "commit", "-am", "local ahead");
        var worktree = (await workspace.Worktrees.ListAsync(root)).Single();
        Assert.Equal("origin/main", worktree.Upstream);
        Assert.Equal(1, worktree.Ahead);
        Assert.Equal(0, worktree.Behind);
        Assert.False(worktree.IsUpstreamGone);
    }

    [Theory]
    [InlineData(PullStrategy.Merge, "--no-rebase")]
    [InlineData(PullStrategy.Rebase, "--rebase")]
    [InlineData(PullStrategy.FastForwardOnly, "--ff-only")]
    public async Task Pull_strategy_is_spelled_explicitly(PullStrategy strategy, string expected)
    {
        var root = await NewRepoAsync();
        var bare = await NewBareAsync(root);
        var workspace = await OpenAsync(root);
        Assert.True((await workspace.Remotes.AddAsync(root, "origin", bare)).Success);
        Assert.True((await workspace.Remotes.PushAsync(root, "origin", "main", setUpstream: true)).Success);

        var pulled = await workspace.Remotes.PullAsync(root, strategy, "origin", "main");
        Assert.Contains(expected, pulled.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Force_push_uses_force_with_lease_and_never_plain_force()
    {
        var root = await NewRepoAsync();
        var bare = await NewBareAsync(root);
        var workspace = await OpenAsync(root);
        Assert.True((await workspace.Remotes.AddAsync(root, "origin", bare)).Success);
        Assert.True((await workspace.Remotes.PushAsync(root, "origin", "main", setUpstream: true)).Success);

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "rewritten\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "--amend", "--no-edit");

        var pushed = await workspace.Remotes.PushAsync(root, "origin", "main", forceWithLease: true);
        Assert.True(pushed.Success, pushed.Message);
        Assert.Contains("--force-with-lease", pushed.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("--force ", pushed.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detached_fetch_returns_an_id_and_emits_completion()
    {
        var root = await NewRepoAsync();
        var bare = await NewBareAsync(root);
        var workspace = await OpenAsync(root);
        Assert.True((await workspace.Remotes.AddAsync(root, "origin", bare)).Success);
        Assert.True((await workspace.Remotes.PushAsync(root, "origin", "main", setUpstream: true)).Success);

        var finished = new TaskCompletionSource<RemoteProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.Remotes.Finished += progress => finished.TrySetResult(progress);

        var stopwatch = Stopwatch.StartNew();
        var started = workspace.Remotes.StartFetch(root, "origin");
        stopwatch.Stop();

        Assert.False(string.IsNullOrWhiteSpace(started.Id));
        Assert.Equal("fetch", started.Operation);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));

        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(started.Id, result.Id);
        Assert.Equal("completed", result.State);
        Assert.NotNull(result.Mutation);
    }

    [Fact]
    public async Task Bridge_round_trips_remote_listing_and_start_payload()
    {
        var root = await NewRepoAsync();
        var bare = await NewBareAsync(root);
        var workspace = new WorkspaceService(Git, new OperationLog());
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());
        await workspace.GetWorktreesAsync(root);
        dispatcher.StartWatching();

        await CallAsync(dispatcher, "addRemote", new { worktreePath = root, name = "origin", url = bare });
        var remotes = await CallAsync(dispatcher, "getRemotes", new { worktreePath = root });
        Assert.Equal("origin", remotes.EnumerateArray().Single().GetProperty("name").GetString());

        var finished = new TaskCompletionSource<RemoteProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.EventRaised += evt =>
        {
            if (evt.Event != "remoteFinished" || evt.Payload is null) return;
            var payload = JsonDocument.Parse(BridgeJson.Serialize(evt.Payload)).RootElement;
            if (payload.GetProperty("worktreePath").GetString()!.Equals(root, StringComparison.OrdinalIgnoreCase))
                finished.TrySetResult(new RemoteProgress
                {
                    Id = payload.GetProperty("id").GetString()!,
                    WorktreePath = root,
                    Operation = payload.GetProperty("operation").GetString()!,
                    State = payload.GetProperty("state").GetString()!,
                });
        };

        var started = await CallAsync(dispatcher, "fetch", new { worktreePath = root, remote = "origin" });
        Assert.False(string.IsNullOrWhiteSpace(started.GetProperty("id").GetString()));
        Assert.Equal("fetch", started.GetProperty("operation").GetString());
        var ended = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(started.GetProperty("id").GetString(), ended.Id);
        Assert.Equal("completed", ended.State);
        dispatcher.Dispose();
    }

    // -----------------------------------------------------------------------
    // Previews
    // -----------------------------------------------------------------------

    [Fact]
    public void Reads_the_refs_remote_prune_would_delete()
    {
        var refs = RemoteService.ParsePrunePreview(
            "Pruning origin\n" +
            "URL: https://example.com/repo.git\n" +
            " * [would prune] origin/stale-one\n" +
            " * [would prune] origin/stale-two\n");

        Assert.Equal(["origin/stale-one", "origin/stale-two"], refs);
    }

    [Fact]
    public void Ignores_prune_output_that_names_nothing()
    {
        Assert.Empty(RemoteService.ParsePrunePreview("Pruning origin\nURL: https://example.com/repo.git\n"));
        Assert.Empty(RemoteService.ParsePrunePreview(""));
    }

    /// <summary>
    /// The flag column is the whole of what separates the three outcomes, and each one carries
    /// a differently shaped range: two dots for a fast-forward, three for a rewrite, none at
    /// all for a refusal.
    /// </summary>
    [Fact]
    public void Reads_the_flag_and_range_from_push_porcelain()
    {
        var updates = RemoteService.ParsePushPreview(
            "To ../origin.git\n" +
            "+\trefs/heads/main:refs/heads/main\te056461...76bae02 (forced update)\n" +
            " \trefs/heads/topic:refs/heads/topic\te056461..4eee221\n" +
            "!\trefs/heads/old:refs/heads/old\t[rejected] (stale info)\n" +
            "Done\n");

        Assert.Equal(3, updates.Count);

        Assert.True(updates[0].IsForced);
        Assert.False(updates[0].IsRejected);
        Assert.Equal("refs/heads/main", updates[0].To);
        Assert.Equal("e056461", updates[0].OldSha);
        Assert.Equal("76bae02", updates[0].NewSha);

        Assert.False(updates[1].IsForced);
        Assert.Equal("e056461", updates[1].OldSha);
        Assert.Equal("4eee221", updates[1].NewSha);

        Assert.True(updates[2].IsRejected);
        Assert.Equal("", updates[2].OldSha);
        Assert.Contains("rejected", updates[2].Summary);
    }

    /// <summary>
    /// Git's flag column separates deleted from refused, and confusing the two told the user
    /// the server had rejected a delete it had just confirmed. Taken from real
    /// <c>push --porcelain --dry-run origin :refs/heads/x</c> output, which exits 0.
    /// </summary>
    [Fact]
    public void Reads_a_deleted_ref_as_a_success_rather_than_a_refusal()
    {
        var update = Assert.Single(RemoteService.ParsePushPreview(
            "To ../origin.git\n" +
            "-\t:refs/heads/doomed\t[deleted]\n" +
            "Done\n"));

        Assert.True(update.IsDeleted);
        Assert.False(update.IsRejected);
        Assert.False(update.IsForced);
        Assert.Equal("refs/heads/doomed", update.To);
    }

    /// <summary>
    /// Unlike fetch, pull and push, the remote is positional here, so an empty one would be
    /// passed to git as an empty argument and answered with
    /// "'' does not appear to be a git repository".
    /// </summary>
    [Fact]
    public async Task Refuses_to_push_a_tag_without_naming_a_remote()
    {
        var root = await NewRepoAsync();
        var workspace = await OpenAsync(root);

        var refused = await workspace.Remotes.PushTagAsync(root, "", "v1.0");

        Assert.False(refused.Success);
        Assert.Contains("needs a name", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, refused.Attempts);
    }

    [Fact]
    public void Ignores_push_porcelain_lines_that_are_not_ref_updates()
    {
        Assert.Empty(RemoteService.ParsePushPreview("To ../origin.git\nDone\n"));
        Assert.Empty(RemoteService.ParsePushPreview("fatal: could not read from remote repository\n"));
    }

    /// <summary>
    /// The value of the preview is the commit list, and it can only come from the remote: the
    /// dry run reports the tip the server holds right now, which is the thing a force push
    /// replaces.
    /// </summary>
    [Fact]
    public async Task Previews_the_commits_a_force_push_would_remove_from_the_remote()
    {
        var root = await NewRepoAsync();
        var bare = await NewBareAsync(root);
        var workspace = await OpenAsync(root);

        Assert.True((await workspace.Remotes.AddAsync(root, "origin", bare)).Success);
        await RunAsync(root, "push", "-u", "origin", "main");

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "two\n");
        await RunAsync(root, "commit", "-am", "on the server only");
        await RunAsync(root, "push", "origin", "main");

        // Rewrite the local branch so the server's tip is no longer an ancestor.
        await RunAsync(root, "reset", "--hard", "HEAD~1");
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "three\n");
        await RunAsync(root, "commit", "-am", "replaces it");

        var preview = await workspace.Remotes.PreviewPushAsync(root, "origin", "main", forceWithLease: true);

        Assert.True(preview.Ok, preview.Message);
        var update = Assert.Single(preview.Updates);
        Assert.True(update.IsForced);
        Assert.False(update.DroppedUnknown);
        Assert.Contains("on the server only", Assert.Single(update.Dropped));

        // And nothing was actually pushed: the remote still has the commit it was about to lose.
        var remoteLog = await Git.TryRunAsync(bare, default, "log", "--format=%s", "-n", "1", "main");
        Assert.Equal("on the server only", remoteLog.Trimmed);
    }

    [Fact]
    public async Task Reports_a_fast_forward_push_as_nothing_dropped()
    {
        var root = await NewRepoAsync();
        var bare = await NewBareAsync(root);
        var workspace = await OpenAsync(root);

        Assert.True((await workspace.Remotes.AddAsync(root, "origin", bare)).Success);
        await RunAsync(root, "push", "-u", "origin", "main");

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "two\n");
        await RunAsync(root, "commit", "-am", "ahead");

        var preview = await workspace.Remotes.PreviewPushAsync(root, "origin", "main", forceWithLease: true);

        Assert.True(preview.Ok, preview.Message);
        var update = Assert.Single(preview.Updates);
        Assert.False(update.IsForced);
        Assert.Empty(update.Dropped);
    }

    [Fact]
    public async Task Previews_the_tracking_refs_a_remote_prune_would_delete()
    {
        var root = await NewRepoAsync();
        var bare = await NewBareAsync(root);
        var workspace = await OpenAsync(root);

        Assert.True((await workspace.Remotes.AddAsync(root, "origin", bare)).Success);
        await RunAsync(root, "push", "origin", "HEAD:refs/heads/gone");
        await RunAsync(root, "fetch", "origin");

        // Deleting the ref in the bare repository leaves the tracking ref stale, which is the
        // state prune exists for. Pushing a delete would also drop the tracking ref locally.
        await RunAsync(bare, "update-ref", "-d", "refs/heads/gone");

        var preview = await workspace.Remotes.PreviewPruneAsync(root, "origin");

        Assert.True(preview.Ok, preview.Message);
        Assert.Equal("origin/gone", Assert.Single(preview.Refs));

        // A preview writes nothing: the stale ref is still there until prune itself runs.
        var refs = await Git.TryRunAsync(root, default, "for-each-ref", "--format=%(refname)", "refs/remotes");
        Assert.Contains("refs/remotes/origin/gone", refs.StandardOutput);
    }

    private static async Task<JsonElement> CallAsync(BridgeDispatcher dispatcher, string method, object parameters)
    {
        var request = JsonSerializer.Serialize(new { id = 1, method, @params = parameters }, BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;
        Assert.True(response.GetProperty("ok").GetBoolean(), response.TryGetProperty("error", out var error)
            ? error.GetString()
            : $"{method} failed");
        return response.GetProperty("result");
    }
}
