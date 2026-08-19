using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// The branch, stash and tag protocol over real JSON — the same path the front-end takes.
///
/// The services underneath are covered by <see cref="RefTests"/>; what this adds is the seam
/// where a rename in Messages.cs becomes a missing field in protocol.ts rather than a compile
/// error. Every field the UI reads is asserted by name, because that is the only place the
/// drift can be caught.
/// </summary>
public class RefBridgeTests : IDisposable
{
    private static readonly GitCli Git = new();

    private readonly List<string> _created = [];

    private async Task<(BridgeDispatcher Dispatcher, string Root, string Linked)> NewAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-refbridge-" + Guid.NewGuid().ToString("N")[..8]);
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
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");

        // A second worktree, because "this branch is open elsewhere" is the state this app
        // is actually used in and the one the payload has to describe.
        var linked = root + "-linked";
        _created.Add(linked);
        await RunAsync(root, "branch", "feature");
        await RunAsync(root, "worktree", "add", linked, "feature");

        var workspace = new WorkspaceService(Git);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        await workspace.GetWorktreesAsync(root);

        return (dispatcher, root, linked);
    }

    private static async Task RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"fixture setup failed: {result.CommandLine}\n{result.StandardError}");
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
        foreach (var root in Enumerable.Reverse(_created)) Delete(root);
        GC.SuppressFinalize(this);
    }

    private static void Delete(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetRefs_sends_every_field_the_panel_renders()
    {
        var (dispatcher, root, linked) = await NewAsync();

        await CallAsync(dispatcher, "createTag", new { worktreePath = root, name = "v1.0", message = "first" });
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "edited\n");
        await CallAsync(dispatcher, "stashPush", new { worktreePath = root, message = "wip" });

        var refs = await CallAsync(dispatcher, "getRefs", new { worktreePath = root });

        Assert.Equal(root, refs.GetProperty("worktreePath").GetString());
        Assert.Equal("main", refs.GetProperty("current").GetString());
        Assert.True(refs.GetProperty("canSwitch").GetBoolean());

        var branch = refs.GetProperty("branches").EnumerateArray()
            .Single(b => b.GetProperty("name").GetString() == "feature");

        // Named individually rather than by count: a renamed field would otherwise still
        // deserialise into an object of the right shape and simply arrive undefined.
        Assert.False(branch.GetProperty("isCurrent").GetBoolean());
        Assert.False(branch.GetProperty("isRemote").GetBoolean());
        Assert.True(branch.GetProperty("isCheckedOutElsewhere").GetBoolean());
        Assert.Equal(linked, branch.GetProperty("checkedOutIn").GetString(), ignoreCase: true);
        Assert.Equal(7, branch.GetProperty("shortSha").GetString()!.Length);
        Assert.Equal("initial", branch.GetProperty("subject").GetString());
        Assert.NotNull(branch.GetProperty("committedAt").GetString());

        var stash = Assert.Single(refs.GetProperty("stashes").EnumerateArray().ToArray());
        Assert.Equal(0, stash.GetProperty("index").GetInt32());
        Assert.Equal("stash@{0}", stash.GetProperty("selector").GetString());
        Assert.Equal("wip", stash.GetProperty("message").GetString());
        Assert.Equal("main", stash.GetProperty("branch").GetString());
        Assert.Equal(40, stash.GetProperty("sha").GetString()!.Length);

        var tag = Assert.Single(refs.GetProperty("tags").EnumerateArray().ToArray());
        Assert.Equal("v1.0", tag.GetProperty("name").GetString());
        Assert.True(tag.GetProperty("isAnnotated").GetBoolean());
        Assert.Equal("first", tag.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task An_upstream_that_is_absent_is_omitted_rather_than_sent_as_an_empty_string()
    {
        // The backend serialises with WhenWritingNull, so an untracked branch has no
        // `upstream` key at all. protocol.ts documents that `| null` means
        // `| null | undefined` for exactly this reason; a `=== null` check here would wave
        // the value through.
        var (dispatcher, root, _) = await NewAsync();

        var refs = await CallAsync(dispatcher, "getRefs", new { worktreePath = root });
        var branch = refs.GetProperty("branches").EnumerateArray()
            .Single(b => b.GetProperty("name").GetString() == "main");

        Assert.False(branch.TryGetProperty("upstream", out _));
        Assert.False(branch.TryGetProperty("ahead", out _));
    }

    [Fact]
    public async Task Switching_to_a_branch_held_elsewhere_reports_the_failure_kind_the_ui_branches_on()
    {
        var (dispatcher, root, _) = await NewAsync();

        var result = await CallAsync(
            dispatcher, "switchBranch", new { worktreePath = root, branch = "feature" });

        Assert.False(result.GetProperty("ok").GetBoolean());

        // The camelCase spelling the front-end's GitFailure union has to match exactly.
        Assert.Equal("checkedOutElsewhere", result.GetProperty("failure").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task The_checkout_strategy_crosses_the_wire_as_the_name_the_front_end_sends()
    {
        var (dispatcher, root, _) = await NewAsync();

        await RunAsync(root, "switch", "-c", "other");
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "other content\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "diverge");
        await RunAsync(root, "switch", "main");

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "local edit\n");

        // Deserialising an unknown enum name would throw and surface as a failed call, so
        // this passing is the assertion that "stashAndSwitch" is the right spelling.
        var result = await CallAsync(
            dispatcher, "switchBranch",
            new { worktreePath = root, branch = "other", strategy = "stashAndSwitch" });

        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_stash_action_naming_a_stale_sha_is_refused_over_the_wire()
    {
        var (dispatcher, root, _) = await NewAsync();

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "edited\n");
        await CallAsync(dispatcher, "stashPush", new { worktreePath = root, message = "wip" });

        var result = await CallAsync(
            dispatcher, "stashDrop",
            new
            {
                worktreePath = root,
                index = 0,
                sha = "0000000000000000000000000000000000000000",
            });

        Assert.False(result.GetProperty("ok").GetBoolean());

        // And nothing was dropped.
        var refs = await CallAsync(dispatcher, "getRefs", new { worktreePath = root });
        Assert.Single(refs.GetProperty("stashes").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task Every_ref_method_refuses_a_worktree_this_window_never_opened()
    {
        // The bridge takes a worktree path as a parameter, so without this check any
        // directory on the machine is a valid target — including one holding another
        // user's repository.
        var (dispatcher, _, _) = await NewAsync();

        var stranger = Path.Combine(Path.GetTempPath(), "chapter-not-open-" + Guid.NewGuid().ToString("N")[..8]);

        var methods = new (string Method, object Params)[]
        {
            ("getRefs", new { worktreePath = stranger }),
            ("switchBranch", new { worktreePath = stranger, branch = "main" }),
            ("createBranch", new { worktreePath = stranger, name = "x" }),
            ("renameBranch", new { worktreePath = stranger, from = "main", to = "x" }),
            ("deleteBranch", new { worktreePath = stranger, name = "x" }),
            ("setUpstream", new { worktreePath = stranger, branch = "main", upstream = "origin/main" }),
            ("stashPush", new { worktreePath = stranger, message = "x" }),
            ("stashApply", new { worktreePath = stranger, index = 0, sha = "abc" }),
            ("stashPop", new { worktreePath = stranger, index = 0, sha = "abc" }),
            ("stashDrop", new { worktreePath = stranger, index = 0, sha = "abc" }),
            ("createTag", new { worktreePath = stranger, name = "x" }),
            ("deleteTag", new { worktreePath = stranger, name = "x" }),
        };

        foreach (var (method, parameters) in methods)
        {
            var request = JsonSerializer.Serialize(
                new { id = 1, method, @params = parameters }, BridgeJson.Options);

            var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;

            Assert.False(
                response.GetProperty("ok").GetBoolean(),
                $"{method} accepted a worktree this window never opened");
        }
    }

    [Fact]
    public async Task A_branch_mutation_tells_the_front_end_to_reload_the_worktree_list()
    {
        // A worktree is labelled with the branch it is on, and nothing else in the app moves
        // that label. Without this event a switch leaves every rail row naming the branch it
        // used to be on.
        var (dispatcher, root, _) = await NewAsync();

        var repos = new List<string>();
        dispatcher.EventRaised += e =>
        {
            if (e.Event != "worktreesChanged" || e.Payload is null) return;

            var payload = JsonDocument.Parse(BridgeJson.Serialize(e.Payload)).RootElement;
            repos.Add(payload.GetProperty("repoPath").GetString()!);
        };

        await CallAsync(dispatcher, "createBranch", new { worktreePath = root, name = "fresh" });

        Assert.Contains(root, repos);
    }

    [Fact]
    public async Task Creating_and_deleting_a_tag_round_trips_through_the_bridge()
    {
        var (dispatcher, root, _) = await NewAsync();

        await CallAsync(dispatcher, "createTag", new { worktreePath = root, name = "nightly" });

        var afterCreate = await CallAsync(dispatcher, "getRefs", new { worktreePath = root });
        var tag = Assert.Single(afterCreate.GetProperty("tags").EnumerateArray().ToArray());

        Assert.Equal("nightly", tag.GetProperty("name").GetString());
        Assert.False(tag.GetProperty("isAnnotated").GetBoolean());

        await CallAsync(dispatcher, "deleteTag", new { worktreePath = root, name = "nightly" });

        var afterDelete = await CallAsync(dispatcher, "getRefs", new { worktreePath = root });
        Assert.Empty(afterDelete.GetProperty("tags").EnumerateArray().ToArray());
    }

    // -----------------------------------------------------------------------
    // Worktrees
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetRefs_carries_every_worktree_field_the_panel_renders()
    {
        var (dispatcher, root, linked) = await NewAsync();

        await CallAsync(
            dispatcher, "lockWorktree",
            new { worktreePath = root, target = linked, reason = "an agent is running" });

        var refs = await CallAsync(dispatcher, "getRefs", new { worktreePath = root });
        var worktrees = refs.GetProperty("worktrees").EnumerateArray().ToArray();

        Assert.Equal(2, worktrees.Length);

        var main = worktrees.Single(w => w.GetProperty("isMain").GetBoolean());
        Assert.Equal(root, main.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal("main", main.GetProperty("displayName").GetString());
        Assert.Equal(7, main.GetProperty("shortHead").GetString()!.Length);
        Assert.True(main.GetProperty("isUsable").GetBoolean());

        var other = worktrees.Single(w => !w.GetProperty("isMain").GetBoolean());
        Assert.Equal("feature", other.GetProperty("branch").GetString());
        Assert.True(other.GetProperty("isLocked").GetBoolean());
        Assert.Equal("an agent is running", other.GetProperty("lockReason").GetString());
        Assert.False(other.GetProperty("isPrunable").GetBoolean());
    }

    [Fact]
    public async Task Adding_and_removing_a_worktree_round_trips_through_the_bridge()
    {
        var (dispatcher, root, _) = await NewAsync();

        var path = root + "-added";
        _created.Add(path);

        var added = await CallAsync(
            dispatcher, "addWorktree",
            new { worktreePath = root, path, branch = "added", createBranch = true });

        Assert.True(added.GetProperty("ok").GetBoolean(), added.GetProperty("message").GetString());
        Assert.True(Directory.Exists(path));

        var removed = await CallAsync(
            dispatcher, "removeWorktree", new { worktreePath = root, target = path });

        Assert.True(removed.GetProperty("ok").GetBoolean(), removed.GetProperty("message").GetString());
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task Removing_the_worktree_the_request_names_as_its_own_is_not_refused_as_unknown()
    {
        // The membership check has to happen before the app lets go of the worktree's
        // watcher and index, or removing the one the panel was opened on is refused with
        // "that worktree is not open in this window" — the app turning down its own request.
        var (dispatcher, root, linked) = await NewAsync();

        var result = await CallAsync(
            dispatcher, "removeWorktree", new { worktreePath = linked, target = linked });

        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetProperty("message").GetString());
        Assert.False(Directory.Exists(linked));
    }

    [Fact]
    public async Task A_worktree_with_uncommitted_work_comes_back_with_the_failure_the_ui_branches_on()
    {
        var (dispatcher, root, linked) = await NewAsync();

        await File.WriteAllTextAsync(Path.Combine(linked, "wip.txt"), "an agent was here\n");

        var refused = await CallAsync(
            dispatcher, "removeWorktree", new { worktreePath = root, target = linked });

        Assert.False(refused.GetProperty("ok").GetBoolean());

        // The camelCase spelling the front-end tests for before asking the permanent-loss
        // question. Anything else and the dialog never appears.
        Assert.Equal("wouldLoseChanges", refused.GetProperty("failure").GetString());

        var forced = await CallAsync(
            dispatcher, "removeWorktree", new { worktreePath = root, target = linked, force = true });

        Assert.True(forced.GetProperty("ok").GetBoolean(), forced.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_refused_removal_leaves_the_worktree_open_in_this_window()
    {
        // The whole two-step flow, from the worktree being removed. The app lets go of the
        // target's watcher and index before git runs, and a refusal has to put that back: the
        // panel re-reads the moment the removal is turned down, and `getRefs` is gated on the
        // same membership. Without the restore it reports "that worktree is not open in this
        // window" — the app disowning the worktree the user is standing in — and the forced
        // removal it is about to offer fails the same way.
        var (dispatcher, _, linked) = await NewAsync();

        await File.WriteAllTextAsync(Path.Combine(linked, "wip.txt"), "an agent was here\n");

        var refused = await CallAsync(
            dispatcher, "removeWorktree", new { worktreePath = linked, target = linked });

        Assert.False(refused.GetProperty("ok").GetBoolean());

        // Each of these throws if the worktree was left disowned. CallAsync asserts on `ok`.
        await CallAsync(dispatcher, "getRefs", new { worktreePath = linked });

        var forced = await CallAsync(
            dispatcher, "removeWorktree", new { worktreePath = linked, target = linked, force = true });

        Assert.True(forced.GetProperty("ok").GetBoolean(), forced.GetProperty("message").GetString());
        Assert.False(Directory.Exists(linked));
    }

    [Fact]
    public async Task The_prune_preview_crosses_the_wire_as_the_shape_the_dialog_lists()
    {
        var (dispatcher, root, linked) = await NewAsync();

        Directory.Delete(linked, recursive: true);

        var preview = await CallAsync(dispatcher, "previewPrune", new { worktreePath = root });
        var entry = Assert.Single(preview.GetProperty("entries").EnumerateArray().ToArray());

        Assert.Equal(Path.GetFileName(linked), entry.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("reason").GetString()));

        var pruned = await CallAsync(dispatcher, "pruneWorktrees", new { worktreePath = root });
        Assert.True(pruned.GetProperty("ok").GetBoolean(), pruned.GetProperty("message").GetString());

        var refs = await CallAsync(dispatcher, "getRefs", new { worktreePath = root });
        Assert.Single(refs.GetProperty("worktrees").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task A_suggested_path_comes_back_as_a_plain_string()
    {
        var (dispatcher, root, _) = await NewAsync();

        var suggestion = await CallAsync(
            dispatcher, "suggestWorktreePath", new { worktreePath = root, name = "review" });

        // A string rather than an object: the front-end drops it straight into the prompt.
        Assert.Equal(JsonValueKind.String, suggestion.ValueKind);
        Assert.EndsWith("review", suggestion.GetString());
    }
}
