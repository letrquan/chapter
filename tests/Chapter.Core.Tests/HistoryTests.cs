using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>Phase 4 history queries, parsing, paging and the bridge seam.</summary>
public class HistoryTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private static string Row(params string[] fields) =>
        string.Join(HistoryService.FieldSeparator, fields) + HistoryService.RecordSeparator + "\n";

    [Fact]
    public void Parses_every_history_field_and_keeps_a_multiline_body()
    {
        var output = Row(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb cccccccccccccccccccccccccccccccccccccccc",
            "Ada Lovelace",
            "ada@example.com",
            "2026-08-28T09:10:11+07:00",
            "Grace Hopper",
            "grace@example.com",
            "2026-08-28T10:11:12+07:00",
            "Merge the parser",
            "HEAD -> main, tag: v1.0",
            "First paragraph.\n\nSecond paragraph.");

        var commit = Assert.Single(HistoryService.Parse(output));

        Assert.Equal("aaaaaaa", commit.ShortSha);
        Assert.Equal(2, commit.Parents.Count);
        Assert.True(commit.IsMerge);
        Assert.Equal("Ada Lovelace", commit.AuthorName);
        Assert.Equal("ada@example.com", commit.AuthorEmail);
        Assert.Equal(TimeSpan.FromHours(7), commit.AuthoredAt?.Offset);
        Assert.Equal("Grace Hopper", commit.CommitterName);
        Assert.Equal("Merge the parser", commit.Subject);
        Assert.Equal("HEAD -> main, tag: v1.0", commit.Decorations);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", commit.Body);
    }

    [Fact]
    public void Skips_truncated_rows_without_losing_the_valid_rows_around_them()
    {
        var valid = Row(
            "aaaaaaaa", "", "A", "a@example.com", "", "A", "a@example.com", "",
            "subject", "", "");

        var commits = HistoryService.Parse(valid + "too\0short\0\0\n" + valid);

        Assert.Equal(2, commits.Count);
    }

    [Fact]
    public void Parses_blame_porcelain_groups_and_uncommitted_lines()
    {
        var first = new string('a', 40);
        var second = new string('b', 40);
        var output = $"{first} 1 1 2\n" +
                     "author Ada Lovelace\n" +
                     "author-mail <ada@example.com>\n" +
                     "author-time 1798535411\n" +
                     "author-tz +0700\n" +
                     "summary first change\n" +
                     "filename src/file.txt\n" +
                     "\tone\n" +
                     "\ttwo\n" +
                     $"^{second} 3 3 1\n" +
                     "author Grace Hopper\n" +
                     "author-mail <grace@example.com>\n" +
                     "summary old line\n" +
                     "\tthree\n" +
                     $"{new string('0', 40)} 4 4 1\n" +
                     "author Not Committed Yet\n" +
                     "author-mail <not.committed.yet>\n" +
                     "summary uncommitted changes\n" +
                     "\tfour\n";

        var lines = HistoryService.ParseBlame(output);

        Assert.Equal(4, lines.Count);
        Assert.Equal([1, 2, 3, 4], lines.Select(line => line.LineNumber));
        Assert.Equal(first, lines[0].Sha);
        Assert.Equal("Ada Lovelace", lines[1].AuthorName);
        Assert.Equal("ada@example.com", lines[0].AuthorEmail);
        Assert.Equal(TimeSpan.FromHours(7), lines[0].AuthoredAt?.Offset);
        Assert.Equal("three", lines[2].Text);
        Assert.True(lines[2].IsBoundary);
        Assert.True(lines[3].IsUncommitted);
        Assert.Equal("uncommitted changes", lines[3].Subject);
    }

    [Fact]
    public async Task Lists_file_history_through_a_rename_and_keeps_paging_anchor()
    {
        var root = await NewRepoAsync(commits: 2);
        await RunAsync(root, "mv", "A.txt", "renamed.txt");
        await RunAsync(root, "commit", "-m", "rename file");
        await File.AppendAllTextAsync(Path.Combine(root, "renamed.txt"), "version 4\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "edit renamed file");

        var history = new HistoryService(Git);
        var first = await history.ListFileAsync(root, "renamed.txt", limit: 2);
        var second = await history.ListFileAsync(root, "renamed.txt", offset: 2, limit: 2, anchor: first.Anchor);

        Assert.Equal(["edit renamed file", "rename file"], first.Commits.Select(c => c.Subject));
        Assert.Equal(["commit 2", "commit 1"], second.Commits.Select(c => c.Subject));
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(first.Anchor, second.Anchor);
    }

    [Fact]
    public async Task Treats_file_history_paths_with_glob_characters_as_literal_names()
    {
        var root = await NewRepoAsync(commits: 1);
        await File.WriteAllTextAsync(Path.Combine(root, "special[a].txt"), "literal\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "literal file");

        await File.WriteAllTextAsync(Path.Combine(root, "speciala.txt"), "sibling\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "sibling file");

        var page = await new HistoryService(Git).ListFileAsync(root, "special[a].txt");

        Assert.Equal(["literal file"], page.Commits.Select(c => c.Subject));
    }

    [Fact]
    public async Task Reads_blame_for_the_working_tree_and_marks_new_lines_uncommitted()
    {
        var root = await NewRepoAsync(commits: 1);
        await File.AppendAllTextAsync(Path.Combine(root, "A.txt"), "working line\n");

        var result = await new HistoryService(Git).BlameAsync(root, "A.txt");

        Assert.Equal("A.txt", result.Path);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("version 1", result.Lines[0].Text);
        Assert.False(result.Lines[0].IsUncommitted);
        Assert.Equal("working line", result.Lines[1].Text);
        Assert.True(result.Lines[1].IsUncommitted);
    }

    [Fact]
    public async Task Reads_blame_for_a_new_untracked_file_as_uncommitted_lines()
    {
        var root = await NewRepoAsync(commits: 1);
        await File.WriteAllTextAsync(Path.Combine(root, "new.txt"), "first\nsecond\n");

        var result = await new HistoryService(Git).BlameAsync(root, "new.txt");

        Assert.Equal(2, result.Lines.Count);
        Assert.All(result.Lines, line => Assert.True(line.IsUncommitted));
        Assert.Equal(["first", "second"], result.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Rejects_rooted_drive_and_git_directory_paths()
    {
        Assert.Throws<ArgumentException>(() => HistoryService.ValidateRelativePath("C:/outside.txt"));
        Assert.Throws<ArgumentException>(() => HistoryService.ValidateRelativePath(".git/config"));
        Assert.Throws<ArgumentException>(() => HistoryService.ValidateRelativePath("src/../secret.txt"));
    }

    [Fact]
    public async Task Rejects_an_anchor_that_is_not_reachable_from_the_worktree_head()
    {
        var root = await NewRepoAsync(commits: 1);
        var history = new HistoryService(Git);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            history.ListAsync(root, offset: 1, limit: 1,
                anchor: new string('f', 40)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            history.ListFileAsync(root, "A.txt", offset: 1, limit: 1,
                anchor: new string('f', 40)));
    }

    [Fact]
    public async Task Pages_newest_first_without_repeating_the_boundary_commit()
    {
        var root = await NewRepoAsync(commits: 5);
        var history = new HistoryService(Git);

        var first = await history.ListAsync(root, offset: 0, limit: 2);
        var second = await history.ListAsync(root, offset: 2, limit: 2);
        var last = await history.ListAsync(root, offset: 4, limit: 2);

        Assert.Equal(["commit 5", "commit 4"], first.Commits.Select(c => c.Subject));
        Assert.Equal(["commit 3", "commit 2"], second.Commits.Select(c => c.Subject));
        Assert.Equal("commit 1", Assert.Single(last.Commits).Subject);
        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(last.HasMore);
        Assert.Empty(first.Commits.Select(c => c.Sha).Intersect(second.Commits.Select(c => c.Sha)));
    }

    [Fact]
    public async Task An_anchor_keeps_later_pages_stable_when_a_new_commit_arrives()
    {
        var root = await NewRepoAsync(commits: 4);
        var history = new HistoryService(Git);

        var first = await history.ListAsync(root, offset: 0, limit: 2);

        // A new tip must not shift the second page underneath an already-open history view.
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "version 5\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "commit 5");

        var second = await history.ListAsync(
            root, offset: 2, limit: 2, anchor: first.Anchor);

        Assert.Equal(["commit 2", "commit 1"], second.Commits.Select(c => c.Subject));
        Assert.Equal(first.Anchor, second.Anchor);
    }

    [Fact]
    public async Task An_unborn_repository_has_an_empty_history_page()
    {
        var root = await NewRepoAsync(commits: 0);

        var page = await new HistoryService(Git).ListAsync(root);

        Assert.Empty(page.Commits);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task The_bridge_sends_the_shape_the_history_overlay_reads()
    {
        var root = await NewRepoAsync(commits: 3, body: "Why this changed.\n\nAnd another paragraph.");
        var workspace = new WorkspaceService(Git);
        await workspace.GetWorktreesAsync(root);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        var page = await CallAsync(
            dispatcher, "getHistory", new { worktreePath = root, offset = 0, limit = 2 });

        Assert.Equal(root, page.GetProperty("worktreePath").GetString(), ignoreCase: true);
        Assert.Equal(0, page.GetProperty("offset").GetInt32());
        Assert.Equal(2, page.GetProperty("limit").GetInt32());
        Assert.True(page.GetProperty("hasMore").GetBoolean());

        var commit = page.GetProperty("commits").EnumerateArray().First();
        Assert.Equal("commit 3", commit.GetProperty("subject").GetString());
        Assert.Equal(7, commit.GetProperty("shortSha").GetString()!.Length);
        Assert.Equal("Test", commit.GetProperty("authorName").GetString());
        Assert.Equal("test@example.com", commit.GetProperty("authorEmail").GetString());
        Assert.NotNull(commit.GetProperty("committedAt").GetString());
        Assert.True(commit.GetProperty("parents").GetArrayLength() > 0);
        Assert.Contains("another paragraph", commit.GetProperty("body").GetString());
    }

    [Fact]
    public async Task The_bridge_refuses_history_for_a_worktree_this_window_never_opened()
    {
        var root = await NewRepoAsync(commits: 1);
        var dispatcher = new BridgeDispatcher(new WorkspaceService(Git), new AppSettings());

        var request = JsonSerializer.Serialize(
            new { id = 1, method = "getHistory", @params = new { worktreePath = root } },
            BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Contains("not open", response.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reads_commit_detail_against_its_first_parent_and_file_sides()
    {
        var root = await NewRepoAsync(commits: 2);
        var history = new HistoryService(Git);
        var page = await history.ListAsync(root, limit: 2);
        var commit = page.Commits[0];

        var detail = await history.GetDetailAsync(root, commit.Sha);
        var file = Assert.Single(detail.Files);
        Assert.Equal(commit.Sha, detail.Commit.Sha);
        Assert.Single(detail.Commit.Parents);
        Assert.Equal(detail.Commit.Parents[0], detail.ParentSha);
        Assert.Equal("A.txt", file.Path);
        Assert.Equal("version 1\n", (await history.GetFileDiffAsync(root, commit.Sha, file.Path)).BaseContent.Text);
        Assert.Equal("version 2\n", (await history.GetFileDiffAsync(root, commit.Sha, file.Path)).CommitContent.Text);
    }

    [Fact]
    public async Task Reads_a_root_commit_against_the_empty_tree()
    {
        var root = await NewRepoAsync(commits: 1);
        var history = new HistoryService(Git);
        var commit = Assert.Single((await history.ListAsync(root)).Commits);

        var detail = await history.GetDetailAsync(root, commit.Sha);
        var file = Assert.Single(detail.Files);
        Assert.Empty(commit.Parents);
        Assert.Equal("A.txt", file.Path);
        Assert.Empty((await history.GetFileDiffAsync(root, commit.Sha, file.Path)).BaseContent.Text);
        Assert.Equal("version 1\n", (await history.GetFileDiffAsync(root, commit.Sha, file.Path)).CommitContent.Text);
    }

    [Fact]
    public async Task The_bridge_exposes_commit_detail_and_historical_file_diff()
    {
        var root = await NewRepoAsync(commits: 2);
        var workspace = new WorkspaceService(Git);
        await workspace.GetWorktreesAsync(root);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());
        var commit = Assert.Single((await workspace.History.ListAsync(root, limit: 1)).Commits);

        var detail = await CallAsync(dispatcher, "getCommitDetail", new
        {
            worktreePath = root,
            sha = commit.Sha,
        });
        Assert.Equal(commit.Sha, detail.GetProperty("commit").GetProperty("sha").GetString());
        Assert.Equal(1, detail.GetProperty("files").GetArrayLength());

        var diff = await CallAsync(dispatcher, "getCommitFileDiff", new
        {
            worktreePath = root,
            sha = commit.Sha,
            path = "A.txt",
        });
        Assert.Equal("version 1\n", diff.GetProperty("baseText").GetString());
        Assert.Equal("version 2\n", diff.GetProperty("commitText").GetString());
        Assert.Equal("A.txt", diff.GetProperty("path").GetString());
    }

    [Fact]
    public async Task The_bridge_exposes_file_history_and_blame()
    {
        var root = await NewRepoAsync(commits: 2);
        var workspace = new WorkspaceService(Git);
        await workspace.GetWorktreesAsync(root);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        var history = await CallAsync(dispatcher, "getFileHistory", new
        {
            worktreePath = root,
            path = "A.txt",
            limit = 1,
        });
        Assert.Equal("A.txt", history.GetProperty("path").GetString());
        Assert.Single(history.GetProperty("commits").EnumerateArray());
        Assert.True(history.GetProperty("hasMore").GetBoolean());

        var blame = await CallAsync(dispatcher, "getBlame", new { worktreePath = root, path = "A.txt" });
        Assert.Equal("A.txt", blame.GetProperty("path").GetString());
        Assert.Single(blame.GetProperty("lines").EnumerateArray());
        Assert.False(blame.GetProperty("lines").EnumerateArray().First().GetProperty("isUncommitted").GetBoolean());
    }

    [Fact]
    public async Task Searches_messages_authors_paths_and_changed_content()
    {
        var root = await NewRepoAsync(commits: 1);

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "version 1\nneedle\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "Introduce the parser", "-m", "The body contains a rare phrase.");

        await File.WriteAllTextAsync(Path.Combine(root, "src-[literal].txt"), "path marker\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "-c", "user.name=Alice [Parser]", "-c", "user.email=alice@example.com",
            "commit", "-m", "A path-only change");

        var history = new HistoryService(Git);

        var message = await history.SearchAsync(root, HistorySearchKind.Message, "RARE PHRASE");
        Assert.Equal(["Introduce the parser"], message.Commits.Select(c => c.Subject));

        var author = await history.SearchAsync(root, HistorySearchKind.Author, "alice [parser]");
        Assert.Equal(["A path-only change"], author.Commits.Select(c => c.Subject));

        var path = await history.SearchAsync(root, HistorySearchKind.Path, "[literal]");
        Assert.Equal(["A path-only change"], path.Commits.Select(c => c.Subject));

        var directory = await history.SearchAsync(root, HistorySearchKind.Path, "src");
        Assert.Equal(["A path-only change"], directory.Commits.Select(c => c.Subject));

        var content = await history.SearchAsync(root, HistorySearchKind.Content, "needle");
        Assert.Equal(["Introduce the parser"], content.Commits.Select(c => c.Subject));
    }

    [Fact]
    public async Task Search_paging_keeps_its_head_anchor_when_a_new_matching_commit_arrives()
    {
        var root = await NewRepoAsync(commits: 1);
        for (var i = 1; i <= 3; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), $"version {i + 1}\n");
            await RunAsync(root, "add", "-A");
            await RunAsync(root, "commit", "-m", $"searchable {i}");
        }

        var history = new HistoryService(Git);
        var first = await history.SearchAsync(root, HistorySearchKind.Message, "searchable", limit: 1);

        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "version 5\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "searchable newest");

        var second = await history.SearchAsync(
            root, HistorySearchKind.Message, "searchable", offset: 1, limit: 1, anchor: first.Anchor);

        Assert.Equal("searchable 2", Assert.Single(second.Commits).Subject);
        Assert.Equal(first.Anchor, second.Anchor);
        Assert.True(first.HasMore);
    }

    [Fact]
    public void Rejects_empty_or_multiline_history_search_queries()
    {
        var history = new HistoryService(Git);

        Assert.Throws<ArgumentException>(() => history.SearchAsync(
            "unused", HistorySearchKind.Message, "   ").GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() => history.SearchAsync(
            "unused", HistorySearchKind.Message, "one\ntwo").GetAwaiter().GetResult());
    }

    [Fact]
    public async Task The_bridge_exposes_search_history_with_string_enum_kind()
    {
        var root = await NewRepoAsync(commits: 2);
        var workspace = new WorkspaceService(Git);
        await workspace.GetWorktreesAsync(root);
        var dispatcher = new BridgeDispatcher(workspace, new AppSettings());

        var page = await CallAsync(dispatcher, "searchHistory", new
        {
            worktreePath = root,
            kind = "message",
            query = "commit 2",
            limit = 5,
        });

        Assert.Equal("commit 2", page.GetProperty("commits").EnumerateArray().Single()
            .GetProperty("subject").GetString());
    }

    private async Task<string> NewRepoAsync(int commits, string body = "")
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-history-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.email", "test@example.com");
        await RunAsync(root, "config", "user.name", "Test");
        await RunAsync(root, "config", "commit.gpgsign", "false");
        await RunAsync(root, "config", "core.autocrlf", "false");

        for (var i = 1; i <= commits; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), $"version {i}\n");
            await RunAsync(root, "add", "-A");

            if (body.Length > 0)
                await RunAsync(root, "commit", "-m", $"commit {i}", "-m", body);
            else
                await RunAsync(root, "commit", "-m", $"commit {i}");
        }

        return root;
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
}
