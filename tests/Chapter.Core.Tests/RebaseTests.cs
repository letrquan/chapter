using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>Interactive rebase behavior against disposable repositories.</summary>
public sealed class RebaseTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private async Task<string> NewRepoAsync(bool withSpaces = false)
    {
        var root = Path.Combine(
            Path.GetTempPath(), (withSpaces ? "chapter rebase test-" : "chapter-rebase-test-") +
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

        await CommitFileAsync(root, "base.txt", "base\n", "base");
        return root;
    }

    private static RebaseService NewService(out UndoService undo)
    {
        var writer = new GitWriter(Git, new OperationLog());
        undo = new UndoService(Git, writer);
        return new RebaseService(Git, writer, undo);
    }

    private static async Task CommitFileAsync(
        string root, string path, string content, string subject)
    {
        await File.WriteAllTextAsync(Path.Combine(root, path), content);
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", subject);
    }

    private static async Task RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture setup failed: {result.CommandLine}\n{result.StandardError}");
    }

    private static async Task<string> RevAsync(string root, string revision = "HEAD") =>
        (await Git.TryRunAsync(root, default, "rev-parse", revision)).Trimmed;

    private static async Task<string[]> SubjectsAsync(string root, string range) =>
        (await Git.TryRunAsync(root, default, "log", "--reverse", "--format=%s", range))
        .Trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Plan_is_oldest_first_and_anchored_to_full_object_ids()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);

        Assert.Equal(["one", "two"], plan.Entries.Select(entry => entry.Subject));
        Assert.All(plan.Entries, entry => Assert.True(HistoryService.IsObjectId(entry.Sha)));
        Assert.Equal(await RevAsync(root), plan.Head);
        Assert.Equal("main", plan.Branch);
    }

    [Fact]
    public async Task Reorder_and_drop_rewrite_the_branch_and_record_an_exact_undo()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");
        await CommitFileAsync(root, "three.txt", "three\n", "three");
        var originalHead = await RevAsync(root);

        var service = NewService(out var undo);
        var plan = await service.GetPlanAsync(root, baseSha);
        var entries = new[]
        {
            plan.Entries[2] with { Action = RebaseAction.Pick },
            plan.Entries[0] with { Action = RebaseAction.Drop },
            plan.Entries[1] with { Action = RebaseAction.Pick },
        };

        var result = await service.StartAsync(root, baseSha, entries, plan.Head);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["three", "two"], await SubjectsAsync(root, $"{baseSha}..HEAD"));
        Assert.False(File.Exists(Path.Combine(root, "one.txt")));
        Assert.True(File.Exists(Path.Combine(root, "two.txt")));
        Assert.True(File.Exists(Path.Combine(root, "three.txt")));

        var point = undo.Peek(root);
        Assert.NotNull(point);
        Assert.Equal(["reset", "--keep", originalHead], point!.InverseCommand);
        Assert.False(point.IsDestructive);
    }

    [Fact]
    public async Task Reword_squash_and_fixup_use_the_supplied_messages()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");
        await CommitFileAsync(root, "three.txt", "three\n", "three");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);
        var entries = new[]
        {
            plan.Entries[0] with { Action = RebaseAction.Reword, Message = "first renamed\n\nbody" },
            plan.Entries[1] with { Action = RebaseAction.Squash, Message = "combined work\n\ncombined body" },
            plan.Entries[2] with { Action = RebaseAction.Fixup },
        };

        var result = await service.StartAsync(root, baseSha, entries, plan.Head);

        Assert.True(result.Success, result.Message);
        var resultingSubjects = await SubjectsAsync(root, $"{baseSha}..HEAD");
        Assert.Equal(["combined work"], resultingSubjects);
        var body = (await Git.TryRunAsync(root, default, "log", "-1", "--format=%B")).Trimmed;
        Assert.Equal("combined work\n\ncombined body", body.Replace("\r", ""));
    }

    [Fact]
    public async Task Edit_pauses_and_continue_finishes_the_rebase()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);
        var entries = new[]
        {
            plan.Entries[0] with { Action = RebaseAction.Edit },
            plan.Entries[1],
        };

        var started = await service.StartAsync(root, baseSha, entries, plan.Head);
        var paused = await service.GetStateAsync(root);

        Assert.True(started.Success, started.Message);
        Assert.True(paused.IsPaused);
        Assert.Equal(plan.Entries[0].Sha, paused.CurrentCommit);
        Assert.True(paused.CanContinue);

        var continued = await service.ContinueAsync(root);

        Assert.True(continued.Success, continued.Message);
        Assert.False((await service.GetStateAsync(root)).IsPaused);
        Assert.Equal(["one", "two"], await SubjectsAsync(root, $"{baseSha}..HEAD"));
    }

    [Fact]
    public async Task Edit_continue_can_replace_the_stopped_commit_message()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);
        var entries = new[]
        {
            plan.Entries[0] with { Action = RebaseAction.Edit },
            plan.Entries[1],
        };

        var started = await service.StartAsync(root, baseSha, entries, plan.Head);
        Assert.True(started.Success, started.Message);

        var continued = await service.ContinueAsync(root, "renamed while stopped");

        Assert.True(continued.Success, continued.Message);
        Assert.Equal(
            ["renamed while stopped", "two"],
            await SubjectsAsync(root, $"{baseSha}..HEAD"));
    }

    [Fact]
    public async Task An_edit_pause_can_resume_after_the_service_is_recreated()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");

        var first = NewService(out _);
        var plan = await first.GetPlanAsync(root, baseSha);
        var started = await first.StartAsync(
            root,
            baseSha,
            [plan.Entries[0] with { Action = RebaseAction.Edit }, plan.Entries[1]],
            plan.Head);
        Assert.True(started.Success, started.Message);
        Assert.True((await first.GetStateAsync(root)).IsPaused);
        first.Dispose();

        var second = NewService(out _);
        var continued = await second.ContinueAsync(root, "renamed after restart");

        Assert.True(continued.Success, continued.Message);
        Assert.False((await second.GetStateAsync(root)).IsPaused);
        Assert.Equal(
            ["renamed after restart", "two"],
            await SubjectsAsync(root, $"{baseSha}..HEAD"));
        second.Dispose();
    }

    [Fact]
    public async Task Root_rebase_supports_reordering_non_conflicting_commits()
    {
        var root = await NewRepoAsync();
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root);
        var result = await service.StartAsync(
            root,
            plan,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["base", "one", "two"], await SubjectsAsync(root, "HEAD"));

        // Run the same root plan with the two independent commits reversed. The root commit
        // stays first so the fixture's base file remains available to both replays.
        var reordered = new[] { plan.Entries[0], plan.Entries[2], plan.Entries[1] };
        var second = await service.StartAsync(root, "", reordered, await RevAsync(root));

        Assert.True(second.Success, second.Message);
        Assert.Equal(["base", "two", "one"], await SubjectsAsync(root, "HEAD"));
        service.Dispose();
    }

    [Fact]
    public async Task A_conflict_can_be_skipped_and_the_rebase_can_finish()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "value.txt", "one\n", "one");
        await CommitFileAsync(root, "value.txt", "two\n", "two");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);
        var started = await service.StartAsync(
            root,
            baseSha,
            [plan.Entries[1], plan.Entries[0] with { Action = RebaseAction.Drop }],
            plan.Head);
        Assert.False(started.Success);
        Assert.Equal(GitFailure.Conflict, started.Failure);

        var skipped = await service.SkipAsync(root);

        Assert.True(skipped.Success, skipped.Message);
        Assert.False((await service.GetStateAsync(root)).IsPaused);
        Assert.Equal(baseSha, await RevAsync(root));
        Assert.False(File.Exists(Path.Combine(root, "value.txt")));
        service.Dispose();
    }

    [Fact]
    public async Task The_last_explicit_squash_message_wins_for_one_message_prompt()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");
        await CommitFileAsync(root, "three.txt", "three\n", "three");
        await CommitFileAsync(root, "four.txt", "four\n", "four");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);
        var result = await service.StartAsync(
            root,
            baseSha,
            [
                plan.Entries[0],
                plan.Entries[1] with { Action = RebaseAction.Squash, Message = "first squash" },
                plan.Entries[2] with { Action = RebaseAction.Squash, Message = "last squash" },
                plan.Entries[3] with { Action = RebaseAction.Fixup },
            ],
            plan.Head);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["last squash"], await SubjectsAsync(root, $"{baseSha}..HEAD"));
        Assert.Equal("last squash", (await Git.TryRunAsync(root, default, "log", "-1", "--format=%B")).Trimmed);
        service.Dispose();
    }

    [Fact]
    public async Task Rebase_editor_scripts_handle_worktree_paths_with_spaces()
    {
        var root = await NewRepoAsync(withSpaces: true);
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);
        var result = await service.StartAsync(root, plan, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["one"], await SubjectsAsync(root, $"{baseSha}..HEAD"));
        service.Dispose();
    }

    [Fact]
    public async Task A_conflict_is_left_for_continue_skip_or_abort()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "value.txt", "one\n", "one");
        await CommitFileAsync(root, "value.txt", "two\n", "two");
        var originalHead = await RevAsync(root);

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);
        var entries = new[]
        {
            plan.Entries[1],
            plan.Entries[0] with { Action = RebaseAction.Drop },
        };

        var started = await service.StartAsync(root, baseSha, entries, plan.Head);
        var paused = await service.GetStateAsync(root);

        Assert.False(started.Success);
        Assert.Equal(GitFailure.Conflict, started.Failure);
        Assert.True(paused.IsPaused);
        Assert.Contains("value.txt", paused.ConflictedPaths);
        Assert.False(paused.CanContinue);
        Assert.True(paused.CanSkip);
        Assert.True(paused.CanAbort);

        var aborted = await service.AbortAsync(root);

        Assert.True(aborted.Success, aborted.Message);
        Assert.Equal(originalHead, await RevAsync(root));
        Assert.Equal("two\n", await File.ReadAllTextAsync(Path.Combine(root, "value.txt")));
    }

    [Fact]
    public async Task A_conflict_can_be_resolved_and_continued()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "value.txt", "one\n", "one");
        await CommitFileAsync(root, "value.txt", "two\n", "two");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);
        var started = await service.StartAsync(
            root,
            baseSha,
            [plan.Entries[1], plan.Entries[0] with { Action = RebaseAction.Drop }],
            plan.Head);
        Assert.False(started.Success);

        await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "resolved\n");
        await RunAsync(root, "add", "value.txt");
        var continued = await service.ContinueAsync(root);

        Assert.True(continued.Success, continued.Message);
        Assert.False((await service.GetStateAsync(root)).IsPaused);
        Assert.Equal("resolved\n", await File.ReadAllTextAsync(Path.Combine(root, "value.txt")));
    }

    [Fact]
    public async Task A_stale_head_or_dirty_tree_is_refused_before_rebase_starts()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");

        var service = NewService(out _);
        var stalePlan = await service.GetPlanAsync(root, baseSha);
        await CommitFileAsync(root, "two.txt", "two\n", "two");

        var stale = await service.StartAsync(root, baseSha, stalePlan.Entries, stalePlan.Head);
        Assert.False(stale.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, stale.Failure);
        Assert.Contains("HEAD changed", stale.Message);

        var dirtyPlan = await service.GetPlanAsync(root, baseSha);
        await File.WriteAllTextAsync(Path.Combine(root, "untracked.txt"), "dirty\n");

        var dirty = await service.StartAsync(root, baseSha, dirtyPlan.Entries, dirtyPlan.Head);
        Assert.False(dirty.Success);
        Assert.Equal(GitFailure.WouldLoseChanges, dirty.Failure);
        Assert.Contains("not clean", dirty.Message);
    }

    [Fact]
    public async Task Undo_restores_the_pre_rebase_tree_without_erasing_untracked_work()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");
        var originalHead = await RevAsync(root);

        var service = NewService(out var undo);
        var plan = await service.GetPlanAsync(root, baseSha);
        var result = await service.StartAsync(
            root, baseSha,
            [plan.Entries[1], plan.Entries[0] with { Action = RebaseAction.Drop }],
            plan.Head);
        Assert.True(result.Success, result.Message);

        await File.WriteAllTextAsync(Path.Combine(root, "after.txt"), "keep me\n");
        var undone = await undo.UndoAsync(root);

        Assert.True(undone.Success, undone.Message);
        Assert.Equal("keep me\n", await File.ReadAllTextAsync(Path.Combine(root, "after.txt")));
        Assert.Equal(originalHead, await RevAsync(root));
    }

    [Fact]
    public async Task Bridge_round_trips_plan_and_rebase_controls()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        var workspace = new Chapter.Core.WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(root);
        var dispatcher = new Chapter.Core.Contracts.BridgeDispatcher(
            workspace, new Chapter.Core.AppSettings());

        static async Task<System.Text.Json.JsonElement> CallAsync(
            Chapter.Core.Contracts.BridgeDispatcher dispatcher, string method, object parameters)
        {
            var request = System.Text.Json.JsonSerializer.Serialize(
                new { id = 1, method, @params = parameters },
                Chapter.Core.Contracts.BridgeJson.Options);
            var response = System.Text.Json.JsonDocument.Parse(
                await dispatcher.HandleAsync(request)).RootElement;
            Assert.True(response.GetProperty("ok").GetBoolean(), method);
            return response.GetProperty("result");
        }

        var plan = await CallAsync(dispatcher, "getRebasePlan",
            new { worktreePath = root, upstream = baseSha });
        Assert.Equal(baseSha, plan.GetProperty("upstream").GetString());
        var entry = plan.GetProperty("entries").EnumerateArray().Single();
        Assert.Equal("pick", entry.GetProperty("action").GetString());

        var state = await CallAsync(dispatcher, "getRebaseState", new { worktreePath = root });
        Assert.Equal("none", state.GetProperty("operation").GetString());
        dispatcher.Dispose();
    }

    [Fact]
    public async Task Dropping_every_commit_after_a_base_moves_the_branch_to_that_base()
    {
        var root = await NewRepoAsync();
        var baseSha = await RevAsync(root);
        await CommitFileAsync(root, "one.txt", "one\n", "one");
        await CommitFileAsync(root, "two.txt", "two\n", "two");

        var service = NewService(out _);
        var plan = await service.GetPlanAsync(root, baseSha);
        var result = await service.StartAsync(
            root,
            baseSha,
            plan.Entries.Select(entry => entry with { Action = RebaseAction.Drop }).ToArray(),
            plan.Head);

        Assert.True(result.Success, result.Message);
        Assert.Equal(baseSha, await RevAsync(root));
        Assert.False(File.Exists(Path.Combine(root, "one.txt")));
        Assert.False(File.Exists(Path.Combine(root, "two.txt")));
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
