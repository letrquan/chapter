using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Cherry-pick and revert against disposable repositories. These operations deliberately
/// leave conflicts in place, so a fixture is never shared with another test.
/// </summary>
public sealed class HistoryMutationTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private async Task<string> NewRepoAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-history-mutation-" + Guid.NewGuid().ToString("N")[..8]);
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
            var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
            Assert.True(result.Success, result.StandardError);
        }

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "one\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");
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

    [Fact]
    public async Task Cherry_pick_creates_a_commit_and_records_an_undo_point()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "source");
        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "picked\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "add picked file");
        var source = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;
        // Keep the source in the current ancestry while removing its tree effect. This is
        // the history overlay's safety contract: actions only accept commits it displayed.
        await RunAsync(root, "revert", "--no-edit", source);

        var mutation = await workspace.HistoryMutations.CherryPickAsync(root, source);

        Assert.True(mutation.Success, mutation.Message);
        Assert.Contains("cherry-pick", mutation.Operation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("add picked file", (await Git.TryRunAsync(root, default, "log", "-1", "--format=%s")).Trimmed);
        Assert.True(File.Exists(Path.Combine(root, "B.txt")));

        var undo = workspace.Undo.Peek(root);
        Assert.NotNull(undo);
        Assert.StartsWith("cherry-pick", undo!.Label, StringComparison.Ordinal);

        var undone = await workspace.Undo.UndoAsync(root);
        Assert.True(undone.Success, undone.Message);
        // reset --soft preserves the cherry-picked content in the index and working tree;
        // undo rewinds the ref without throwing that content away.
        Assert.True(File.Exists(Path.Combine(root, "B.txt")));
        Assert.Contains("B.txt", (await Git.TryRunAsync(root, default, "status", "--short")).StandardOutput);
    }

    [Fact]
    public async Task Revert_creates_an_undoable_inverse_commit()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "changed\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "change file");
        var target = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;

        var mutation = await workspace.HistoryMutations.RevertAsync(root, target);

        Assert.True(mutation.Success, mutation.Message);
        Assert.Equal("one\n", await File.ReadAllTextAsync(Path.Combine(root, "A.txt")));
        Assert.Contains("Revert \"change file\"", (await Git.TryRunAsync(
            root, default, "log", "-1", "--format=%s")).StandardOutput);
        Assert.StartsWith("revert", workspace.Undo.Peek(root)!.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_merge_parent_is_translated_to_gits_one_based_mainline()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);

        // Build a merge commit whose tree effect is later reverted, so cherry-picking the
        // merge can create a real commit while using its original parents.
        await RunAsync(root, "switch", "-c", "side");
        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "side\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "side change");
        await RunAsync(root, "switch", "main");
        await File.WriteAllTextAsync(Path.Combine(root, "C.txt"), "main\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "main change");
        await RunAsync(root, "merge", "--no-ff", "--no-edit", "side");
        var merge = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;
        await RunAsync(root, "revert", "--no-edit", "-m", "2", merge);

        var mutation = await workspace.HistoryMutations.CherryPickAsync(root, merge, parentIndex: 1);

        Assert.True(mutation.Success, mutation.Message);
        var logged = workspace.Log.Recent(10).First(entry =>
            entry.Operation.StartsWith("cherry-pick", StringComparison.Ordinal));
        Assert.Contains("cherry-pick --no-edit -m 2", logged.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_merge_parent_is_refused_before_git_runs()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        var commit = Assert.Single((await workspace.History.ListAsync(root)).Commits);

        var mutation = await workspace.HistoryMutations.CherryPickAsync(root, commit.Sha, parentIndex: 1);

        Assert.False(mutation.Success);
        Assert.Equal(GitFailure.NotFound, mutation.Failure);
        Assert.Contains("parent", mutation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(workspace.Log.Recent(1));
    }

    [Fact]
    public async Task A_conflict_is_returned_and_the_cherry_pick_state_is_left_for_resolution()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);

        await RunAsync(root, "switch", "-c", "source");
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "source\n");
        await RunAsync(root, "commit", "-am", "source edit");
        var source = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;
        await RunAsync(root, "revert", "--no-edit", source);
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "main\n");
        await RunAsync(root, "commit", "-am", "main edit");

        var mutation = await workspace.HistoryMutations.CherryPickAsync(root, source);

        Assert.False(mutation.Success);
        Assert.Equal(GitFailure.Conflict, mutation.Failure);
        Assert.Equal(RepositoryOperation.CherryPick,
            (await workspace.GetRepositoryStateAsync(root)).Operation);

        // The service must not auto-abort. The user can now resolve and continue/abort in
        // the later conflict-resolution phase.
        Assert.True(File.Exists(Path.Combine(root, ".git", "CHERRY_PICK_HEAD")));
    }

    [Fact]
    public async Task The_bridge_exposes_both_history_mutations_as_payloads()
    {
        var root = await NewRepoAsync();
        var workspace = await WorkspaceAsync(root);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        await RunAsync(root, "switch", "-c", "source");
        await File.WriteAllTextAsync(Path.Combine(root, "B.txt"), "picked\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "bridge pick");
        var source = (await Git.TryRunAsync(root, default, "rev-parse", "HEAD")).Trimmed;
        await RunAsync(root, "revert", "--no-edit", source);

        var request = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "cherryPick",
            @params = new { worktreePath = root, sha = source },
        }, BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;

        Assert.True(response.GetProperty("ok").GetBoolean());
        var result = response.GetProperty("result");
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("none", result.GetProperty("failure").GetString());
        Assert.StartsWith("cherry-pick ", result.GetProperty("operation").GetString(), StringComparison.Ordinal);
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
                // A leftover temp fixture is not worth failing a test over.
            }
        }

        GC.SuppressFinalize(this);
    }
}
