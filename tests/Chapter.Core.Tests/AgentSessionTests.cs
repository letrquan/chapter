using System.Text.Json;
using Chapter.Core;
using Chapter.Core.AgentSessions;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Session links read only small metadata prefixes from disposable fake agent stores. The
/// fixtures deliberately contain transcript-shaped content too, so the tests assert that
/// content never becomes part of the bridge payload.
/// </summary>
public sealed class AgentSessionTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    private (string Root, AgentSessionLocations Locations) Stores()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-agent-sessions-" + Guid.NewGuid().ToString("N"));
        var claude = Path.Combine(root, "claude", "projects");
        var book = Path.Combine(root, "book", "sessions");
        var codex = Path.Combine(root, "codex", "sessions");
        Directory.CreateDirectory(claude);
        Directory.CreateDirectory(book);
        Directory.CreateDirectory(codex);
        _created.Add(root);

        return (root, new AgentSessionLocations
        {
            ClaudeProjectsPath = claude,
            BookSessionsPath = book,
            BookIndexPath = Path.Combine(book, "session-index.json"),
            CodexSessionsPath = codex,
            CodexIndexPath = Path.Combine(root, "codex", "session_index.jsonl"),
        });
    }

    private static async Task<string> RepoAsync(string root)
    {
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        await RunAsync(repo, "init", "-b", "main");
        await RunAsync(repo, "config", "user.email", "test@example.com");
        await RunAsync(repo, "config", "user.name", "Test");
        await RunAsync(repo, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "base\n");
        await RunAsync(repo, "add", "-A");
        await RunAsync(repo, "commit", "-m", "initial");
        return repo;
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
                // A leftover fixture is less useful than a failed assertion.
            }
        }
    }

    [Fact]
    public async Task Finds_claude_book_and_codex_metadata_without_transcript_content()
    {
        var (root, locations) = Stores();
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);

        var claudeFolder = Path.Combine(locations.ClaudeProjectsPath, "fixture-project");
        Directory.CreateDirectory(claudeFolder);
        var claudePath = Path.Combine(claudeFolder, "claude-1.jsonl");
        await File.WriteAllTextAsync(claudePath,
            "{\"type\":\"mode\",\"sessionId\":\"claude-1\"}\n" +
            $"{{\"type\":\"user\",\"sessionId\":\"claude-1\",\"cwd\":{JsonSerializer.Serialize(worktree)},\"gitBranch\":\"feature/one\",\"timestamp\":\"2026-08-31T10:00:00Z\"}}\n" +
            "{\"type\":\"ai-title\",\"aiTitle\":\"Claude task\"}\n" +
            "{\"type\":\"assistant\",\"message\":{\"content\":\"SECRET TRANSCRIPT\"}}\n");

        var bookId = "book-1";
        var bookPath = Path.Combine(locations.BookSessionsPath, bookId + ".jsonl");
        await File.WriteAllTextAsync(bookPath, "{\"type\":\"assistant\",\"content\":\"BOOK SECRET\"}\n");
        var bookIndex = new
        {
            sessions = new Dictionary<string, object>
            {
                [bookId] = new
                {
                    meta = new
                    {
                        id = bookId,
                        cwd = worktree,
                        name = "Book task",
                        createdAt = 1788160800000L,
                        updatedAt = 1788160860000L,
                        messageCount = 7,
                    },
                },
            },
        };
        await File.WriteAllTextAsync(
            locations.BookIndexPath, JsonSerializer.Serialize(bookIndex, BridgeJson.Options));

        var codexId = "codex-1";
        var codexPath = Path.Combine(locations.CodexSessionsPath, "rollout-2026-08-31T10-00-00-" + codexId + ".jsonl");
        var codexMeta = new
        {
            type = "session_meta",
            timestamp = "2026-08-31T10:00:00Z",
            payload = new
            {
                session_id = codexId,
                id = codexId,
                cwd = worktree,
                git = new { branch = "feature/one" },
            },
        };
        await File.WriteAllTextAsync(codexPath,
            JsonSerializer.Serialize(codexMeta, BridgeJson.Options) + "\n" +
            "{\"type\":\"response_item\",\"payload\":{\"content\":\"CODEX SECRET\"}}\n");
        await File.WriteAllTextAsync(locations.CodexIndexPath,
            JsonSerializer.Serialize(new { id = codexId, thread_name = "Codex task", updated_at = "2026-08-31T10:05:00Z" }, BridgeJson.Options) + "\n");

        var sessions = await new AgentSessionService(locations).FindAsync(worktree, "feature/one");

        Assert.Equal(3, sessions.Count);
        Assert.Contains(sessions, s => s.Provider == AgentSessionProvider.Claude
            && s.Id == "claude-1" && s.Name == "Claude task" && s.Branch == "feature/one");
        Assert.Contains(sessions, s => s.Provider == AgentSessionProvider.Book
            && s.Id == bookId && s.Name == "Book task" && s.MessageCount == 7);
        Assert.Contains(sessions, s => s.Provider == AgentSessionProvider.Codex
            && s.Id == codexId && s.Name == "Codex task" && s.Branch == "feature/one");

        var json = JsonSerializer.Serialize(sessions);
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_known_cwd_is_not_rescued_by_a_common_branch_name()
    {
        var (root, locations) = Stores();
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var path = Path.Combine(locations.CodexSessionsPath, "rollout-2026-08-31T10-00-00-nope.jsonl");
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new { id = "wrong-repo", cwd = first, git = new { branch = "main" } },
            }, BridgeJson.Options) + "\n");

        var sessions = await new AgentSessionService(locations).FindAsync(second, "main");
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task Branch_only_metadata_remains_a_fallback_for_older_logs()
    {
        var (root, locations) = Stores();
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);

        var path = Path.Combine(locations.CodexSessionsPath, "rollout-branch-only.jsonl");
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new { id = "branch-only", git = new { branch = "main" } },
            }, BridgeJson.Options) + "\n");

        var sessions = await new AgentSessionService(locations).FindAsync(worktree, "main");
        Assert.Contains(sessions, session => session.Id == "branch-only");
    }

    [Fact]
    public async Task A_non_object_book_index_does_not_hide_fallback_session_metadata()
    {
        var (root, locations) = Stores();
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);

        const string id = "book-fallback";
        await File.WriteAllTextAsync(
            Path.Combine(locations.BookSessionsPath, id + ".jsonl"),
            JsonSerializer.Serialize(new { meta = new { id, cwd = worktree, name = "Fallback task" } }) + "\n");
        await File.WriteAllTextAsync(locations.BookIndexPath, "42");

        var session = Assert.Single(await new AgentSessionService(locations).FindAsync(worktree));
        Assert.Equal(AgentSessionProvider.Book, session.Provider);
        Assert.Equal(id, session.Id);
        Assert.Equal("Fallback task", session.Name);
    }

    [Fact]
    public async Task Metadata_records_are_bounded_before_json_parsing()
    {
        var (root, locations) = Stores();
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);

        var padding = new string('x', 140 * 1024);
        var path = Path.Combine(locations.CodexSessionsPath, "oversized.jsonl");
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new { id = "oversized", cwd = worktree, padding },
            }, BridgeJson.Options) + "\n");

        // The scanner must not parse or retain an unbounded metadata record. A later complete
        // line would still be discoverable, but this one is intentionally cut at the byte cap.
        Assert.Empty(await new AgentSessionService(locations).FindAsync(worktree));
    }

    [Fact]
    public async Task Codex_recovers_the_full_uuid_from_a_rollout_name_when_metadata_has_no_id()
    {
        var (root, locations) = Stores();
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);

        var id = Guid.NewGuid().ToString("D");
        var path = Path.Combine(
            locations.CodexSessionsPath, $"rollout-2026-08-31T10-00-00-{id}.jsonl");
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new { cwd = worktree, git = new { branch = "feature/session" } },
            }, BridgeJson.Options) + "\n");

        var session = Assert.Single(
            await new AgentSessionService(locations).FindAsync(worktree, "feature/session"));
        Assert.Equal(AgentSessionProvider.Codex, session.Provider);
        Assert.Equal(id, session.Id);
    }

    [Fact]
    public async Task Safe_log_paths_stay_inside_known_jsonl_stores()
    {
        var (root, locations) = Stores();
        var service = new AgentSessionService(locations);
        var inside = Path.Combine(locations.BookSessionsPath, "ok.jsonl");
        await File.WriteAllTextAsync(inside, "{}\n");

        Assert.True(service.IsSafeLogPath(inside));
        Assert.False(service.IsSafeLogPath(Path.Combine(root, "outside.jsonl")));
        Assert.False(service.IsSafeLogPath(Path.Combine(locations.BookSessionsPath, "not-a-log.txt")));
        Assert.False(service.IsSafeLogPath(Path.Combine(locations.BookSessionsPath, "..", "outside.jsonl")));
    }

    [Fact]
    public async Task Bridge_returns_session_metadata_and_rechecks_before_opening()
    {
        var (root, locations) = Stores();
        var repo = await RepoAsync(root);
        var id = "bridge-1";
        var log = Path.Combine(locations.BookSessionsPath, id + ".jsonl");
        await File.WriteAllTextAsync(log, "{\"type\":\"assistant\"}\n");
        var bridgeIndex = new
        {
            sessions = new Dictionary<string, object>
            {
                [id] = new { meta = new { id, cwd = repo, name = "Bridge task", messageCount = 2 } },
            },
        };
        await File.WriteAllTextAsync(
            locations.BookIndexPath, JsonSerializer.Serialize(bridgeIndex, BridgeJson.Options));

        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(repo);
        var dispatcher = new BridgeDispatcher(
            workspace, new AppSettings(), agentSessions: new AgentSessionService(locations));
        var opened = "";
        dispatcher.OpenExternalPath = path => { opened = path; return true; };

        var read = await CallAsync(dispatcher, "getAgentSessions", new { worktreePath = repo });
        Assert.True(read.GetProperty("success").GetBoolean());
        Assert.Equal("bridge-1", read.GetProperty("sessions")[0].GetProperty("id").GetString());
        Assert.False(read.GetProperty("sessions")[0].TryGetProperty("content", out _));

        var open = await CallAsync(dispatcher, "openAgentSession", new
        {
            worktreePath = repo,
            provider = "book",
            sessionId = id,
            // These untrusted path-shaped fields are deliberately ignored. The backend
            // resolves provider + id again and opens only the discovered safe file.
            logPath = Path.Combine(root, "outside.jsonl"),
            path = Path.Combine(root, "outside.jsonl"),
        });
        Assert.True(open.GetProperty("success").GetBoolean());
        Assert.Equal(Path.GetFullPath(log), opened, ignoreCase: true);

        dispatcher.Dispose();
    }

    [Fact]
    public async Task GetRefs_links_sessions_for_every_worktree_in_the_repository()
    {
        var (root, locations) = Stores();
        var repo = await RepoAsync(root);
        var linked = Path.Combine(root, "linked");
        await RunAsync(repo, "branch", "feature/session");
        await RunAsync(repo, "worktree", "add", linked, "feature/session");

        var index = new
        {
            sessions = new Dictionary<string, object>
            {
                ["main-session"] = new { meta = new { id = "main-session", cwd = repo } },
                ["linked-session"] = new { meta = new { id = "linked-session", cwd = linked } },
            },
        };
        foreach (var id in new[] { "main-session", "linked-session" })
            await File.WriteAllTextAsync(Path.Combine(locations.BookSessionsPath, id + ".jsonl"), "{}\n");
        await File.WriteAllTextAsync(
            locations.BookIndexPath, JsonSerializer.Serialize(index, BridgeJson.Options));

        var workspace = new WorkspaceService(Git, new OperationLog());
        await workspace.GetWorktreesAsync(repo);
        var dispatcher = new BridgeDispatcher(
            workspace, new AppSettings(), agentSessions: new AgentSessionService(locations));

        var refs = await CallAsync(dispatcher, "getRefs", new { worktreePath = repo });
        var sessions = refs.GetProperty("agentSessions");
        Assert.Equal(
            "main-session",
            sessions.GetProperty(repo).EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Equal(
            "linked-session",
            sessions.GetProperty(linked).EnumerateArray().Single().GetProperty("id").GetString());

        dispatcher.Dispose();
    }

    private static async Task<JsonElement> CallAsync(
        BridgeDispatcher dispatcher, string method, object parameters)
    {
        var request = JsonSerializer.Serialize(
            new { id = 1, method, @params = parameters }, BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;
        Assert.True(response.GetProperty("ok").GetBoolean(),
            response.TryGetProperty("error", out var error) ? error.GetString() : method);
        return response.GetProperty("result");
    }
}
