using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// The staging and commit protocol over real JSON — the same path the front-end takes.
///
/// The services underneath are covered by <see cref="StagingTests"/>; what this adds is the
/// seam where a rename in Messages.cs becomes a missing field in protocol.ts rather than a
/// compile error. Every field the UI reads is asserted by name here, because that is the
/// only place the drift can be caught.
/// </summary>
public class CommitBridgeTests : IDisposable
{
    private static readonly GitCli Git = new();

    private readonly List<string> _created = [];

    private async Task<(BridgeDispatcher Dispatcher, string Root)> NewAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-bridge-" + Guid.NewGuid().ToString("N")[..8]);
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

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "one\ntwo\nthree\n");
        await Git.ExecuteAsync(root, GitIntent.Write, default, "add", "-A");
        await Git.ExecuteAsync(root, GitIntent.Write, default, "commit", "-m", "initial");

        var workspace = new WorkspaceService(Git);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        // Opening the repository is what admits its worktrees to the set the app may write
        // to — every mutation below is refused without it.
        await workspace.GetWorktreesAsync(root);

        return (dispatcher, root);
    }

    private static async Task<JsonElement> CallAsync(
        BridgeDispatcher dispatcher, string method, object? parameters = null)
    {
        var request = JsonSerializer.Serialize(new { id = 1, method, @params = parameters }, BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;

        Assert.True(
            response.GetProperty("ok").GetBoolean(),
            $"{method} failed: {(response.TryGetProperty("error", out var e) ? e.GetString() : "no error text")}");

        return response.GetProperty("result");
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
                // A leftover temp directory is not worth failing a test over.
            }
        }

        GC.SuppressFinalize(this);
    }

    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_commit_view_carries_every_field_the_panel_renders()
    {
        var (dispatcher, root) = await NewAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "one\nCHANGED\nthree\n");

        var view = await CallAsync(dispatcher, "getCommitView", new { worktreePath = root });

        Assert.Equal(0, view.GetProperty("staged").GetArrayLength());
        Assert.Equal(1, view.GetProperty("unstaged").GetArrayLength());
        Assert.Equal("main", view.GetProperty("branch").GetString());
        Assert.False(view.GetProperty("isUnborn").GetBoolean());
        Assert.False(view.GetProperty("canCommit").GetBoolean());
        Assert.Equal("Nothing is staged.", view.GetProperty("blockedReason").GetString());

        // The identity is read up front so the box can show who will be blamed.
        Assert.Equal("Test", view.GetProperty("authorName").GetString());
        Assert.Equal("test@example.com", view.GetProperty("authorEmail").GetString());

        // Prefetched so toggling amend does not put a round-trip inside a keystroke.
        Assert.Contains("initial", view.GetProperty("headMessage").GetString()!);

        // The repository state travels with it, so the panel need not ask twice.
        Assert.Equal("none", view.GetProperty("repository").GetProperty("operation").GetString());

        var file = view.GetProperty("unstaged")[0];
        Assert.Equal("A.txt", file.GetProperty("path").GetString());
        Assert.True(file.GetProperty("isUnstaged").GetBoolean());
        Assert.False(file.GetProperty("isStaged").GetBoolean());
        Assert.Equal("modified", file.GetProperty("unstagedKind").GetString());
    }

    [Fact]
    public async Task Staging_and_committing_round_trip_through_the_bridge()
    {
        var (dispatcher, root) = await NewAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "one\nCHANGED\nthree\n");

        var staged = await CallAsync(dispatcher, "stage", new { worktreePath = root, paths = new[] { "A.txt" } });
        Assert.True(staged.GetProperty("ok").GetBoolean());

        var ready = await CallAsync(dispatcher, "getCommitView", new { worktreePath = root });
        Assert.True(ready.GetProperty("canCommit").GetBoolean());
        Assert.Equal(1, ready.GetProperty("staged").GetArrayLength());

        var committed = await CallAsync(dispatcher, "commit", new
        {
            worktreePath = root,
            message = "bridge subject\n\nbody",
            signOff = true,
            coAuthors = new[] { "Ada Lovelace <ada@example.com>" },
        });

        Assert.True(committed.GetProperty("ok").GetBoolean());

        var body = (await Git.TryRunAsync(root, default, "log", "-1", "--format=%B")).StandardOutput;
        Assert.Contains("bridge subject", body);
        Assert.Contains("Signed-off-by:", body);
        Assert.Contains("Co-authored-by: Ada Lovelace <ada@example.com>", body);

        // Undo is offered immediately, labelled with what it would reverse.
        var undo = await CallAsync(dispatcher, "getUndo", new { worktreePath = root });
        Assert.Contains("bridge subject", undo.GetProperty("label").GetString()!);
    }

    [Fact]
    public async Task A_malformed_co_author_is_dropped_rather_than_written_as_a_broken_trailer()
    {
        var (dispatcher, root) = await NewAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "one\nCHANGED\nthree\n");
        await CallAsync(dispatcher, "stage", new { worktreePath = root, paths = new[] { "A.txt" } });

        await CallAsync(dispatcher, "commit", new
        {
            worktreePath = root,
            message = "no co-author",
            coAuthors = new[] { "Nobody With No Address" },
        });

        var body = (await Git.TryRunAsync(root, default, "log", "-1", "--format=%B")).StandardOutput;
        Assert.DoesNotContain("Co-authored-by", body);
    }

    [Fact]
    public async Task A_failed_mutation_comes_back_as_a_result_rather_than_an_error()
    {
        // The window must be able to render the failure. An exception here would surface as
        // a bare toast with no classification and no command line to report.
        var (dispatcher, root) = await NewAsync();

        var result = await CallAsync(dispatcher, "commit", new { worktreePath = root, message = "nothing staged" });

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("nothingToDo", result.GetProperty("failure").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task A_mutation_against_an_unopened_worktree_is_refused()
    {
        var (_, root) = await NewAsync();

        // A fresh dispatcher that has never listed this worktree. The bridge takes the path
        // as a parameter, so without the check any directory on the machine is a target.
        var stranger = new BridgeDispatcher(new WorkspaceService(Git), new AppSettings());

        var request = JsonSerializer.Serialize(
            new { id = 1, method = "stage", @params = new { worktreePath = root, paths = new[] { "A.txt" } } },
            BridgeJson.Options);

        var response = JsonDocument.Parse(await stranger.HandleAsync(request)).RootElement;

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Contains("not open", response.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task The_file_patch_carries_the_hunks_and_a_fingerprint()
    {
        var (dispatcher, root) = await NewAsync();

        await File.WriteAllTextAsync(
            Path.Combine(root, "H.txt"),
            string.Concat(Enumerable.Range(1, 20).Select(n => $"{n}\n")));

        await Git.ExecuteAsync(root, GitIntent.Write, default, "add", "-A");
        await Git.ExecuteAsync(root, GitIntent.Write, default, "commit", "-m", "twenty");

        await File.WriteAllTextAsync(
            Path.Combine(root, "H.txt"),
            string.Concat(Enumerable.Range(1, 20).Select(n => n switch
            {
                2 => "TOP\n",
                18 => "BOTTOM\n",
                _ => $"{n}\n",
            })));

        var patch = await CallAsync(dispatcher, "getFilePatch", new
        {
            worktreePath = root,
            path = "H.txt",
            side = "unstaged",
        });

        Assert.False(patch.GetProperty("isBinary").GetBoolean());
        Assert.False(string.IsNullOrEmpty(patch.GetProperty("fingerprint").GetString()));

        var hunks = patch.GetProperty("hunks");
        Assert.Equal(2, hunks.GetArrayLength());

        // The bodies travel with the hunks so the front-end can map a Monaco selection onto
        // patch positions. Without them both sides would be counting different things.
        var first = hunks[0];
        Assert.Equal(0, first.GetProperty("index").GetInt32());
        Assert.True(first.GetProperty("lines").GetArrayLength() > 0);
        Assert.Equal(1, first.GetProperty("addedLines").GetInt32());
        Assert.Equal(1, first.GetProperty("removedLines").GetInt32());

        // And the selection made against them applies.
        var applied = await CallAsync(dispatcher, "applyPatch", new
        {
            worktreePath = root,
            path = "H.txt",
            side = "unstaged",
            hunks = new[] { 0 },
            fingerprint = patch.GetProperty("fingerprint").GetString(),
        });

        Assert.True(applied.GetProperty("ok").GetBoolean(), applied.GetProperty("message").GetString());

        var staged = (await Git.TryRunAsync(root, default, "show", ":H.txt")).StandardOutput;
        Assert.Contains("TOP", staged);
        Assert.DoesNotContain("BOTTOM", staged);
    }

    [Fact]
    public async Task Reviewing_a_message_returns_problems_and_the_house_style()
    {
        var (dispatcher, root) = await NewAsync();

        var review = await CallAsync(dispatcher, "reviewMessage", new
        {
            worktreePath = root,
            message = "subject\nbody on the second line",
        });

        Assert.Equal("subject", review.GetProperty("subject").GetString());
        Assert.True(review.GetProperty("hasErrors").GetBoolean());

        var problems = review.GetProperty("problems");
        Assert.True(problems.GetArrayLength() > 0);
        Assert.Equal("error", problems[0].GetProperty("severity").GetString());

        // The repository's own subjects, for showing what its messages look like — and for
        // Phase 2 to feed a model rather than letting it invent a new convention.
        Assert.Contains("initial", review.GetProperty("recentSubjects").EnumerateArray()
            .Select(s => s.GetString()));
    }
}
