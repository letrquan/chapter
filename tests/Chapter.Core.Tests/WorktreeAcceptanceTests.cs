using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Accepting an agent worktree is tested against disposable linked repositories. The source
/// must be clean, integration happens in main, and cleanup is a separate optional result.
/// </summary>
public sealed class WorktreeAcceptanceTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private async Task<(string Main, string Agent)> NewRepoAsync()
    {
        var main = Path.Combine(Path.GetTempPath(), "chapter-accept-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static async Task<WorkspaceService> WorkspaceAsync(string root)
    {
        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        return workspace;
    }

    [Fact]
    public async Task Merge_accepts_a_clean_agent_branch_into_main_and_records_undo()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(agent, "agent.txt"), "accepted\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "agent work");

        var result = await workspace.Acceptances.AcceptAsync(
            main, agent, WorktreeAcceptStrategy.Merge, noFastForward: true);

        Assert.True(result.Success, result.Message);
        Assert.Equal("agent", result.SourceBranch);
        Assert.Equal(main, result.TargetWorktreePath, ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(main, "agent.txt")));
        Assert.Contains("Merge", (await Git.TryRunAsync(main, default, "log", "-1", "--format=%s")).Trimmed,
            StringComparison.OrdinalIgnoreCase);

        var parents = (await Git.TryRunAsync(main, default, "rev-list", "--parents", "-1", "HEAD")).Trimmed.Split(' ',
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, parents.Length);
        Assert.StartsWith("accept", workspace.Undo.Peek(main)!.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cherry_pick_accepts_each_linear_agent_commit_when_main_diverged()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(agent, "one.txt"), "one\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "agent one");
        await File.WriteAllTextAsync(Path.Combine(agent, "two.txt"), "two\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "agent two");

        await File.WriteAllTextAsync(Path.Combine(main, "main.txt"), "main\n");
        await RunAsync(main, "add", "-A");
        await RunAsync(main, "commit", "-m", "main work");

        var result = await workspace.Acceptances.AcceptAsync(
            main, agent, WorktreeAcceptStrategy.CherryPick);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(main, "one.txt")));
        Assert.True(File.Exists(Path.Combine(main, "two.txt")));
        Assert.Equal("agent two", (await Git.TryRunAsync(main, default, "log", "-1", "--format=%s")).Trimmed);
        Assert.StartsWith("accept", workspace.Undo.Peek(main)!.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dirty_agent_worktree_is_refused_without_touching_main()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);
        await File.WriteAllTextAsync(Path.Combine(agent, "uncommitted.txt"), "not committed\n");

        var result = await workspace.Acceptances.AcceptAsync(main, agent);

        Assert.False(result.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, result.Integration.Failure);
        Assert.False(File.Exists(Path.Combine(main, "uncommitted.txt")));
        Assert.Null(workspace.Undo.Peek(main));
    }

    [Fact]
    public async Task A_stale_source_or_target_head_is_refused_before_integration()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(agent, "agent.txt"), "accepted\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "agent work");

        var sourceHead = (await Git.TryRunAsync(agent, default, "rev-parse", "HEAD")).Trimmed;
        var targetHead = (await Git.TryRunAsync(main, default, "rev-parse", "HEAD")).Trimmed;

        var staleSource = await workspace.Acceptances.AcceptAsync(
            main, agent, expectedSourceHead: new string('0', sourceHead.Length));
        Assert.False(staleSource.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, staleSource.Integration.Failure);
        Assert.False(File.Exists(Path.Combine(main, "agent.txt")));

        var staleTarget = await workspace.Acceptances.AcceptAsync(
            main, agent, expectedTargetHead: new string('0', targetHead.Length));
        Assert.False(staleTarget.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, staleTarget.Integration.Failure);
        Assert.False(File.Exists(Path.Combine(main, "agent.txt")));
    }

    [Fact]
    public async Task A_merge_conflict_is_left_for_the_existing_conflict_surface()
    {
        var (main, agent) = await NewRepoAsync();

        await File.WriteAllTextAsync(Path.Combine(agent, "base.txt"), "agent\n");
        await RunAsync(agent, "commit", "-am", "agent edit");
        await File.WriteAllTextAsync(Path.Combine(main, "base.txt"), "main\n");
        await RunAsync(main, "commit", "-am", "main edit");

        var workspace = await WorkspaceAsync(main);
        var result = await workspace.Acceptances.AcceptAsync(main, agent);

        Assert.False(result.Success);
        Assert.Equal(GitFailure.Conflict, result.Integration.Failure);
        Assert.Equal(RepositoryOperation.Merge,
            (await workspace.GetRepositoryStateAsync(main)).Operation);
        Assert.True(File.Exists(Path.Combine(main, ".git", "MERGE_HEAD")));
    }

    [Fact]
    public async Task Clean_accept_can_remove_the_source_and_reports_cleanup_separately()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(agent, "agent.txt"), "accepted\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "agent work");

        var result = await workspace.Acceptances.AcceptAsync(
            main, agent, WorktreeAcceptStrategy.Merge, removeWorktree: true);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Removed, result.Message);
        Assert.False(Directory.Exists(agent));
        Assert.NotNull(result.Removal);
        Assert.True(result.Removal!.Success, result.Removal.Message);
        Assert.Single(await workspace.Worktrees.ListAsync(main));
    }

    [Fact]
    public async Task Optional_cleanup_refuses_ignored_source_content()
    {
        var (main, agent) = await NewRepoAsync();

        // The ignore rule is part of the agent branch, while the generated file remains
        // outside Git. Acceptance can integrate the branch, but cleanup must not hide that
        // uncommitted directory content.
        await File.WriteAllTextAsync(Path.Combine(agent, ".gitignore"), "agent-output.txt\n");
        await RunAsync(agent, "add", ".gitignore");
        await RunAsync(agent, "commit", "-m", "ignore generated output");
        await File.WriteAllTextAsync(Path.Combine(agent, "agent-output.txt"), "not committed\n");

        var workspace = await WorkspaceAsync(main);
        var result = await workspace.Acceptances.AcceptAsync(
            main, agent, WorktreeAcceptStrategy.Merge, removeWorktree: true);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Removal);
        Assert.False(result.Removal!.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, result.Removal.Failure);
        Assert.True(Directory.Exists(agent));
        Assert.True(File.Exists(Path.Combine(main, ".gitignore")));
    }

    [Fact]
    public async Task Cleanup_refuses_when_the_source_gains_a_commit_after_acceptance()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(agent, "accepted.txt"), "accepted\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "accepted work");

        var accepted = await workspace.Acceptances.AcceptAsync(main, agent);

        await File.WriteAllTextAsync(Path.Combine(agent, "later.txt"), "later\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "later work");

        var removal = await workspace.Acceptances.RemoveAcceptedWorktreeAsync(accepted);

        Assert.False(removal.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, removal.Failure);
        Assert.Contains("gained new work", removal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(agent));
    }

    [Fact]
    public async Task Cleanup_refuses_when_the_source_is_edited_after_acceptance()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);

        await File.WriteAllTextAsync(Path.Combine(agent, "accepted.txt"), "accepted\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "accepted work");

        var accepted = await workspace.Acceptances.AcceptAsync(main, agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "accepted.txt"), "edited after acceptance\n");

        var removal = await workspace.Acceptances.RemoveAcceptedWorktreeAsync(accepted);

        Assert.False(removal.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, removal.Failure);
        Assert.Contains("uncommitted changes", removal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(agent));
    }

    [Fact]
    public async Task A_multi_commit_cherry_pick_conflict_can_be_aborted_back_to_the_original_tip()
    {
        var (main, agent) = await NewRepoAsync();

        // The first source commit is independent and applies cleanly. The second edits the
        // same line main will edit, so one cherry-pick invocation enters the sequencer and
        // stops without leaving that first commit applied permanently.
        await File.WriteAllTextAsync(Path.Combine(agent, "first.txt"), "first\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "first agent change");
        await File.WriteAllTextAsync(Path.Combine(agent, "base.txt"), "agent\n");
        await RunAsync(agent, "commit", "-am", "second conflicting change");

        await File.WriteAllTextAsync(Path.Combine(main, "base.txt"), "main\n");
        await RunAsync(main, "commit", "-am", "main conflicting change");

        var originalHead = (await Git.TryRunAsync(main, default, "rev-parse", "HEAD")).Trimmed;
        var workspace = await WorkspaceAsync(main);
        var result = await workspace.Acceptances.AcceptAsync(main, agent, WorktreeAcceptStrategy.CherryPick);

        Assert.False(result.Success);
        Assert.Equal(GitFailure.Conflict, result.Integration.Failure);
        Assert.True((await workspace.GetRepositoryStateAsync(main)).Operation == RepositoryOperation.CherryPick);

        var aborted = await workspace.Conflicts.AbortAsync(main);

        Assert.True(aborted.Success, aborted.Message);
        var afterAbort = (await Git.TryRunAsync(main, default, "rev-parse", "HEAD")).Trimmed;
        Assert.Equal(originalHead, afterAbort);
        Assert.False(File.Exists(Path.Combine(main, "first.txt")));
    }

    [Fact]
    public async Task Bridge_returns_the_integration_and_removal_payloads()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        await File.WriteAllTextAsync(Path.Combine(agent, "agent.txt"), "accepted\n");
        await RunAsync(agent, "add", "-A");
        await RunAsync(agent, "commit", "-m", "bridge accept");

        var request = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "acceptWorktree",
            @params = new
            {
                worktreePath = main,
                target = agent,
                strategy = "merge",
                removeAfter = true,
            },
        }, BridgeJson.Options);

        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;
        Assert.True(response.GetProperty("ok").GetBoolean());
        var payload = response.GetProperty("result");
        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("merge", payload.GetProperty("strategy").GetString());
        Assert.True(payload.GetProperty("integration").GetProperty("ok").GetBoolean());
        Assert.True(payload.GetProperty("removal").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Bridge_rejects_an_unknown_acceptance_strategy_without_mutating_main()
    {
        var (main, agent) = await NewRepoAsync();
        var workspace = await WorkspaceAsync(main);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        var before = (await Git.TryRunAsync(main, default, "rev-parse", "HEAD")).Trimmed;
        var request = JsonSerializer.Serialize(new
        {
            id = 2,
            method = "acceptWorktree",
            @params = new
            {
                worktreePath = main,
                target = agent,
                strategy = "cherry-pik",
            },
        }, BridgeJson.Options);

        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Contains("merge or cherry-pick", response.GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, (await Git.TryRunAsync(main, default, "rev-parse", "HEAD")).Trimmed);
        Assert.False(File.Exists(Path.Combine(main, "agent.txt")));
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
                // A leftover temp fixture is not worth failing a test over.
            }
        }

        GC.SuppressFinalize(this);
    }
}
