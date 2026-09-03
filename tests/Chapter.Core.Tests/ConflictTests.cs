using System.Text.Json;
using System.Text;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>Conflict stages, marker regions and operation-specific resolution commands.</summary>
public sealed class ConflictTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private async Task<string> NewRepoAsync(bool withSpaces = false)
    {
        var root = Path.Combine(
            Path.GetTempPath(), (withSpaces ? "chapter conflict test-" : "chapter-conflict-test-") +
                Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        foreach (var args in new[]
                 {
                     new[] { "init", "-b", "main" },
                     ["config", "user.email", "test@example.com"],
                     ["config", "user.name", "Test"],
                     ["config", "commit.gpgsign", "false"],
                     ["config", "core.autocrlf", "false"],
                 })
        {
            await RunAsync(root, args);
        }

        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "base\n");
        await RunAsync(root, "add", "value.txt");
        await RunAsync(root, "commit", "-m", "base");
        return root;
    }

    private static async Task RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture setup failed: {result.CommandLine}\n{result.StandardError}");
    }

    private static async Task<WorkspaceService> WorkspaceAsync(string root)
    {
        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        return workspace;
    }

    private static async Task CreateMergeConflictAsync(string root)
    {
        await RunAsync(root, "switch", "-c", "side");
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "theirs\n");
        await RunAsync(root, "commit", "-am", "theirs");
        await RunAsync(root, "switch", "main");
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "ours\n");
        await RunAsync(root, "commit", "-am", "ours");

        var merge = await Git.ExecuteAsync(root, GitIntent.Write, default, "merge", "side");
        Assert.False(merge.Success, "fixture merge should stop on a conflict");
    }

    [Fact]
    public async Task State_contains_all_three_stages_and_marker_regions()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        await CreateMergeConflictAsync(root);

        var state = await workspace.Conflicts.GetStateAsync(root);

        Assert.Equal(RepositoryOperation.Merge, state.Operation);
        var file = Assert.Single(state.Files);
        Assert.Equal("value.txt", file.Path);
        Assert.Equal("base\n", file.BaseText);
        Assert.Equal("ours\n", file.OursText);
        Assert.Equal("theirs\n", file.TheirsText);
        Assert.Contains("<<<<<<<", file.WorkingText);
        var region = Assert.Single(file.Regions);
        Assert.Equal("ours", region.OursText);
        Assert.Equal("theirs", region.TheirsText);
        Assert.Equal(1, region.StartLine);
        Assert.True(state.HasConflicts);
        Assert.False(state.CanContinue);
        Assert.False(state.CanSkip);
        Assert.True(state.CanAbort);
    }

    [Theory]
    [InlineData(ConflictResolutionAction.Ours, "ours\n")]
    [InlineData(ConflictResolutionAction.Theirs, "theirs\n")]
    [InlineData(ConflictResolutionAction.Both, "ours\ntheirs\n")]
    public async Task Choosing_a_side_writes_marker_free_content(
        ConflictResolutionAction action, string expected)
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        await CreateMergeConflictAsync(root);

        var mutation = await workspace.Conflicts.ResolveFileAsync(root, "value.txt", action);

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal(expected, await File.ReadAllTextAsync(Path.Combine(root, "value.txt")));
        Assert.Contains("value.txt", (await Git.TryRunAsync(root, default, "ls-files", "-u")).StandardOutput);
    }

    [Fact]
    public async Task A_stale_conflict_fingerprint_refuses_to_overwrite_a_newer_file()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        await CreateMergeConflictAsync(root);
        var file = Assert.Single((await workspace.Conflicts.GetStateAsync(root)).Files);

        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "changed elsewhere\n");
        var mutation = await workspace.Conflicts.ResolveFileAsync(
            root, "value.txt", ConflictResolutionAction.Ours,
            expectedFingerprint: file.Fingerprint);

        Assert.False(mutation.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, mutation.Failure);
        Assert.Equal("changed elsewhere\n", await File.ReadAllTextAsync(Path.Combine(root, "value.txt")));
    }

    [Fact]
    public async Task A_single_marker_region_can_be_resolved_without_replacing_the_other_text()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        await CreateMergeConflictAsync(root);
        var file = Assert.Single((await workspace.Conflicts.GetStateAsync(root)).Files);

        var mutation = await workspace.Conflicts.ResolveFileAsync(
            root, "value.txt", ConflictResolutionAction.Theirs,
            region: 0, expectedFingerprint: file.Fingerprint);

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal("theirs\n", await File.ReadAllTextAsync(Path.Combine(root, "value.txt")));
        Assert.Empty((await workspace.Conflicts.GetFileAsync(root, "value.txt"))!.Regions);
    }

    [Fact]
    public async Task Binary_side_resolution_writes_the_exact_stage_bytes_and_logs_it()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        var path = Path.Combine(root, "image.bin");

        await File.WriteAllBytesAsync(path, [0, 1, 2]);
        await RunAsync(root, "add", "image.bin");
        await RunAsync(root, "commit", "-m", "binary base");
        await RunAsync(root, "switch", "-c", "binary-side");
        await File.WriteAllBytesAsync(path, [0, 3, 4]);
        await RunAsync(root, "commit", "-am", "binary theirs");
        await RunAsync(root, "switch", "main");
        await File.WriteAllBytesAsync(path, [0, 5, 6]);
        await RunAsync(root, "commit", "-am", "binary ours");
        Assert.False((await Git.ExecuteAsync(root, GitIntent.Write, default, "merge", "binary-side")).Success);

        var file = Assert.Single((await workspace.Conflicts.GetStateAsync(root)).Files);
        Assert.True(file.IsBinary);
        var mutation = await workspace.Conflicts.ResolveFileAsync(
            root, "image.bin", ConflictResolutionAction.Theirs,
            expectedFingerprint: file.Fingerprint);

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal([0, 3, 4], await File.ReadAllBytesAsync(path));
        Assert.Contains(workspace.Log.Recent(), entry =>
            entry.Operation == "resolve conflict image.bin" && entry.Success);
    }

    [Fact]
    public async Task Choosing_a_deleted_side_removes_the_working_file_and_can_finish_the_merge()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "delete-side");
        await RunAsync(root, "rm", "value.txt");
        await RunAsync(root, "commit", "-m", "delete value");
        await RunAsync(root, "switch", "main");
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "main changed\n");
        await RunAsync(root, "commit", "-am", "modify value");
        Assert.False((await Git.ExecuteAsync(root, GitIntent.Write, default, "merge", "delete-side")).Success);

        var file = Assert.Single((await workspace.Conflicts.GetStateAsync(root)).Files);
        Assert.True(file.HasOurs);
        Assert.False(file.HasTheirs);
        var mutation = await workspace.Conflicts.ResolveFileAsync(
            root, "value.txt", ConflictResolutionAction.Theirs,
            expectedFingerprint: file.Fingerprint);

        Assert.True(mutation.Success, mutation.Message);
        Assert.False(File.Exists(Path.Combine(root, "value.txt")));
        Assert.True((await workspace.Conflicts.MarkResolvedAsync(root, "value.txt")).Success);
        Assert.True((await workspace.Conflicts.ContinueAsync(root)).Success);
        Assert.False(File.Exists(Path.Combine(root, "value.txt")));
    }

    [Fact]
    public async Task Text_side_resolution_preserves_crlf_working_tree_format()
    {
        var root = await NewRepoAsync();
        await RunAsync(root, "config", "core.autocrlf", "true");
        var workspace = await WorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "crlf-side");
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "theirs\r\n");
        await RunAsync(root, "commit", "-am", "crlf theirs");
        await RunAsync(root, "switch", "main");
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "ours\r\n");
        await RunAsync(root, "commit", "-am", "crlf ours");
        Assert.False((await Git.ExecuteAsync(root, GitIntent.Write, default, "merge", "crlf-side")).Success);

        var file = Assert.Single((await workspace.Conflicts.GetStateAsync(root)).Files);
        var mutation = await workspace.Conflicts.ResolveFileAsync(
            root, "value.txt", ConflictResolutionAction.Ours,
            expectedFingerprint: file.Fingerprint);

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal(Encoding.UTF8.GetBytes("ours\r\n"),
            await File.ReadAllBytesAsync(Path.Combine(root, "value.txt")));
    }

    [Fact]
    public async Task Mark_resolved_then_continue_concludes_a_merge()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        await CreateMergeConflictAsync(root);

        await workspace.Conflicts.ResolveFileAsync(root, "value.txt", ConflictResolutionAction.Ours);
        var marked = await workspace.Conflicts.MarkResolvedAsync(root, "value.txt");
        Assert.True(marked.Success, marked.Message);

        var continued = await workspace.Conflicts.ContinueAsync(root);

        Assert.True(continued.Success, continued.Message);
        Assert.Equal(RepositoryOperation.None, (await workspace.GetRepositoryStateAsync(root)).Operation);
        Assert.Equal("ours\n", await File.ReadAllTextAsync(Path.Combine(root, "value.txt")));
        Assert.Equal(4, (await Git.TryRunAsync(root, default, "rev-list", "--count", "HEAD")).Trimmed is var count
            ? int.Parse(count)
            : -1);
    }

    [Fact]
    public async Task Merge_abort_restores_the_pre_merge_tip()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        await CreateMergeConflictAsync(root);
        var before = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;

        var aborted = await workspace.Conflicts.AbortAsync(root);

        Assert.True(aborted.Success, aborted.Message);
        Assert.Equal(before, (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed);
        Assert.Equal("ours\n", await File.ReadAllTextAsync(Path.Combine(root, "value.txt")));
    }

    [Fact]
    public async Task Cherry_pick_conflict_can_be_resolved_and_continued_through_shared_service()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "source");
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "source\n");
        await RunAsync(root, "commit", "-am", "source edit");
        var source = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;
        await RunAsync(root, "revert", "--no-edit", source);
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "main\n");
        await RunAsync(root, "commit", "-am", "main edit");

        var started = await workspace.HistoryMutations.CherryPickAsync(root, source);
        Assert.True(started.Failure == GitFailure.Conflict, started.Message);

        var state = await workspace.Conflicts.GetStateAsync(root);
        Assert.Equal(RepositoryOperation.CherryPick, state.Operation);
        await workspace.Conflicts.ResolveFileAsync(root, "value.txt", ConflictResolutionAction.Theirs);
        Assert.True((await workspace.Conflicts.MarkResolvedAsync(root, "value.txt")).Success);
        var continued = await workspace.Conflicts.ContinueAsync(root);

        Assert.True(continued.Success, continued.Message);
        Assert.Equal("source\n", await File.ReadAllTextAsync(Path.Combine(root, "value.txt")));
    }

    [Fact]
    public async Task Stash_pop_conflict_is_described_without_a_fake_abort()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);

        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "stash\n");
        await RunAsync(root, "stash", "push", "-m", "saved");
        var stash = Assert.Single(await workspace.Stashes.ListAsync(root));
        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "main\n");
        await RunAsync(root, "commit", "-am", "main edit");

        var popped = await workspace.Stashes.PopAsync(root, stash.Index, stash.Sha);
        Assert.Equal(GitFailure.Conflict, popped.Failure);

        var state = await workspace.Conflicts.GetStateAsync(root);
        Assert.True(state.IsStashRestore);
        Assert.Equal(RepositoryOperation.None, state.Operation);
        Assert.False(state.CanAbort);

        await workspace.Conflicts.ResolveFileAsync(root, "value.txt", ConflictResolutionAction.Ours);
        Assert.True((await workspace.Conflicts.MarkResolvedAsync(root, "value.txt")).Success);
        var staged = await workspace.Conflicts.GetStateAsync(root);
        Assert.True(staged.IsPaused);
        Assert.False(staged.HasConflicts);
        Assert.True(staged.CanContinue);
        var done = await workspace.Conflicts.ContinueAsync(root);
        Assert.True(done.Success, done.Message);
        Assert.NotEmpty(await workspace.Stashes.ListAsync(root));
    }

    [Fact]
    public async Task Conflict_controls_round_trip_over_the_bridge()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        await CreateMergeConflictAsync(root);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        static async Task<JsonElement> CallAsync(
            BridgeDispatcher dispatcher, string method, object parameters)
        {
            var request = JsonSerializer.Serialize(
                new { id = 1, method, @params = parameters }, BridgeJson.Options);
            var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;
            Assert.True(response.GetProperty("ok").GetBoolean(), method);
            return response.GetProperty("result");
        }

        var state = await CallAsync(dispatcher, "getConflictState", new { worktreePath = root });
        Assert.Equal("merge", state.GetProperty("operation").GetString());
        Assert.True(state.GetProperty("hasConflicts").GetBoolean());

        var file = await CallAsync(dispatcher, "getConflictFile", new { worktreePath = root, path = "value.txt" });
        Assert.Equal("ours\n", file.GetProperty("oursText").GetString());
        Assert.True(file.GetProperty("canRoundTrip").GetBoolean());
        Assert.True(file.GetProperty("regions").GetArrayLength() > 0);

        var resolved = await CallAsync(dispatcher, "resolveConflict", new
        {
            worktreePath = root,
            path = "value.txt",
            action = "theirs",
            fingerprint = file.GetProperty("fingerprint").GetString(),
        });
        Assert.True(resolved.GetProperty("ok").GetBoolean());
        var marked = await CallAsync(dispatcher, "markResolved", new { worktreePath = root, path = "value.txt" });
        Assert.True(marked.GetProperty("ok").GetBoolean());
        var continued = await CallAsync(dispatcher, "continueOperation", new { worktreePath = root });
        Assert.True(continued.GetProperty("ok").GetBoolean(), continued.GetProperty("message").GetString());
        dispatcher.Dispose();
    }

    public void Dispose()
    {
        foreach (var root in _created)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // A leftover temp repository is not worth failing a test over.
            }
        }

        GC.SuppressFinalize(this);
    }
}
