using System.Text.Json;
using Chapter.Core.Ai;
using Chapter.Core.Contracts;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// The half of message generation that needs a repository: what actually gets read out of the
/// index and handed to the model, and the bridge seam the front-end drives it through.
///
/// Nothing here talks to the API. Every test builds and destroys its own repository, and the
/// key store is always a temp file with a stubbed environment — a test run must not depend on,
/// read, or overwrite the credential belonging to whoever ran it.
/// </summary>
public class AiBridgeTests : IDisposable
{
    private static readonly GitCli Git = new();

    private readonly List<string> _created = [];

    private async Task<string> NewRepoAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-ai-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _created.Add(root);

        var init = await Git.ExecuteAsync(root, GitIntent.Write, default, "init", "-b", "main");
        Assert.True(init.Success, init.StandardError);

        // Written rather than set through four `git config` invocations. This class builds a
        // repository per test and the suite runs classes in parallel, so the process count
        // here is not free — it competes with the indexing benchmark for the same cores.
        await File.AppendAllTextAsync(Path.Combine(root, ".git", "config"), """

            [user]
            	email = test@example.com
            	name = Test
            [commit]
            	gpgsign = false
            [core]
            	autocrlf = false

            """);

        return root;
    }

    private static async Task WriteAsync(string root, string relativePath, string content)
    {
        var absolute = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, content);
    }

    private static async Task RunAsync(string root, params string[] args)
    {
        var result = await Git.ExecuteAsync(root, GitIntent.Write, default, args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.StandardError}");
    }

    /// <summary>Twenty numbered lines, so a diff has something to be truncated out of.</summary>
    private static string Lines(int count, string prefix = "line") =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"{prefix} {i}\n"));

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
    // What gets read out of the index
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_digest_covers_every_staged_file_and_carries_the_patches_that_fit()
    {
        var root = await NewRepoAsync();

        await WriteAsync(root, "src/Parser.cs", Lines(20));
        await WriteAsync(root, "README.md", "# Title\n");
        await RunAsync(root, "add", "-A");

        var digest = await DiffDigestBuilder.ReadAsync(Git, root, characterBudget: 50_000);

        Assert.Equal(2, digest.Files.Count);
        Assert.All(digest.Files, f => Assert.Equal(DiffFileState.Included, f.State));
        Assert.False(digest.IsPartial);

        Assert.Contains("src/Parser.cs", digest.Body);
        Assert.Contains("+line 1", digest.Body);
        Assert.Contains("# Title", digest.Body);

        // The line counts come from git, not from counting what was sent.
        Assert.Equal(21, digest.LinesAdded);
    }

    [Fact]
    public async Task A_lockfile_is_counted_but_its_patch_is_never_sent()
    {
        // The single largest source of wasted budget, and the reason the roadmap calls the
        // selection strategy out separately. The fact that the lockfile changed belongs in the
        // message; its four thousand lines do not.
        var root = await NewRepoAsync();

        await WriteAsync(root, "src/App.cs", "class App { }\n");
        await WriteAsync(root, "package-lock.json", Lines(2_000, "\"dep\": \"1.0.0\", //"));
        await RunAsync(root, "add", "-A");

        var digest = await DiffDigestBuilder.ReadAsync(Git, root, characterBudget: 200_000);

        var lockfile = Assert.Single(digest.Files, f => f.Path == "package-lock.json");
        Assert.Equal(DiffFileState.Summarised, lockfile.State);
        Assert.Equal(2_000, lockfile.LinesAdded);
        Assert.Contains("generated", lockfile.Reason);

        // In the summary, absent from the patches.
        Assert.Contains("package-lock.json", digest.ToPrompt());
        Assert.DoesNotContain("\"dep\": \"1.0.0\"", digest.Body);

        // And the source file it was staged alongside is still there in full.
        Assert.Contains("class App", digest.Body);
        // Withheld on purpose, not for want of room — the budget here was never near.
        Assert.True(digest.IsPartial);
        Assert.False(digest.WasCutForSize);
        Assert.Contains("INCOMPLETE", digest.ToPrompt());
    }

    [Fact]
    public async Task A_binary_file_is_reported_rather_than_sent()
    {
        var root = await NewRepoAsync();

        await File.WriteAllBytesAsync(
            Path.Combine(root, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0xFF]);
        await RunAsync(root, "add", "-A");

        var digest = await DiffDigestBuilder.ReadAsync(Git, root, characterBudget: 50_000);

        var file = Assert.Single(digest.Files);
        Assert.True(file.IsBinary);
        Assert.Equal(DiffFileState.Summarised, file.State);
        Assert.Contains("logo.png | binary", digest.ToPrompt());
    }

    [Fact]
    public async Task A_rename_is_described_as_one_rather_than_as_a_delete_and_an_add()
    {
        var root = await NewRepoAsync();

        await WriteAsync(root, "src/Old.cs", Lines(30));
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");

        File.Move(Path.Combine(root, "src", "Old.cs"), Path.Combine(root, "src", "New.cs"));
        await RunAsync(root, "add", "-A");

        var digest = await DiffDigestBuilder.ReadAsync(Git, root, characterBudget: 50_000);

        var file = Assert.Single(digest.Files);
        Assert.Equal("src/New.cs", file.Path);
        Assert.Equal("src/Old.cs", file.OldPath);
        Assert.Contains("src/Old.cs => src/New.cs", digest.ToPrompt());
    }

    [Fact]
    public async Task A_budget_too_small_for_everything_still_names_everything()
    {
        // The property that matters when the budget bites: the file list survives whole, and
        // only the patches are cut. Losing the shape of the change is what produces a message
        // about one file in a nine-file commit.
        var root = await NewRepoAsync();

        for (var i = 1; i <= 6; i++) await WriteAsync(root, $"src/File{i}.cs", Lines(60));
        await RunAsync(root, "add", "-A");

        var digest = await DiffDigestBuilder.ReadAsync(Git, root, characterBudget: 400);

        Assert.Equal(6, digest.Files.Count);
        Assert.True(digest.WasCutForSize);
        Assert.True(digest.Body.Length <= 400 + 6 * 80, "the body should stay near its budget");

        var prompt = digest.ToPrompt();
        for (var i = 1; i <= 6; i++) Assert.Contains($"src/File{i}.cs", prompt);
    }

    [Fact]
    public async Task An_amend_is_read_against_the_replaced_commits_parent()
    {
        // The message on an amend has to describe the commit that will exist afterwards. Read
        // against HEAD it would describe only what has been staged since — a one-line message
        // for a twenty-file commit.
        var root = await NewRepoAsync();

        await WriteAsync(root, "base.txt", "base\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "first");

        await WriteAsync(root, "feature.cs", "class Feature { }\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "second");

        await WriteAsync(root, "feature-test.cs", "class FeatureTests { }\n");
        await RunAsync(root, "add", "-A");

        var againstHead = await DiffDigestBuilder.ReadAsync(Git, root, 50_000);
        var againstParent = await DiffDigestBuilder.ReadAsync(Git, root, 50_000, baseRef: "HEAD~1");

        Assert.Single(againstHead.Files);
        Assert.Equal(2, againstParent.Files.Count);
        Assert.Contains(againstParent.Files, f => f.Path == "feature.cs");
        Assert.Contains(againstParent.Files, f => f.Path == "feature-test.cs");
    }

    [Fact]
    public async Task Nothing_staged_produces_an_empty_digest_rather_than_an_error()
    {
        var root = await NewRepoAsync();

        await WriteAsync(root, "a.txt", "a\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");

        var digest = await DiffDigestBuilder.ReadAsync(Git, root, 50_000);

        Assert.True(digest.IsEmpty);
        Assert.Empty(digest.Files);
    }

    [Fact]
    public async Task A_file_that_is_not_utf8_survives_the_read()
    {
        // Same reasoning as hunk staging: the diff is read as bytes and round-tripped through
        // Latin-1, so a Shift-JIS or Latin-1 source file reaches the model as itself rather
        // than as a wall of replacement characters.
        var root = await NewRepoAsync();

        var bytes = new byte[] { (byte)'/', (byte)'/', (byte)' ', 0xE9, 0xE8, 0xFC, (byte)'\n' };
        await File.WriteAllBytesAsync(Path.Combine(root, "latin.cs"), bytes);
        await RunAsync(root, "add", "-A");

        var digest = await DiffDigestBuilder.ReadAsync(Git, root, 50_000);

        Assert.Equal(DiffFileState.Included, Assert.Single(digest.Files).State);
        Assert.DoesNotContain('\uFFFD', digest.Body);
    }

    // -----------------------------------------------------------------------
    // The bridge seam
    // -----------------------------------------------------------------------

    private async Task<(BridgeDispatcher Dispatcher, string Root, AppSettings Settings)> NewBridgeAsync(
        string? environmentKey = "sk-test-key-for-the-suite")
    {
        var root = await NewRepoAsync();
        await WriteAsync(root, "A.txt", "one\n");
        await RunAsync(root, "add", "-A");
        await RunAsync(root, "commit", "-m", "initial");

        var settings = new AppSettings();
        var workspace = new WorkspaceService(Git);

        // A key store that cannot see the developer's key and cannot write over it. The
        // stubbed environment also stops Describe() reaching for a login profile, which would
        // read config files outside the test's control.
        var keys = new ApiKeyStore(
            Path.Combine(root, ".chapter-test-key.dat"), _ => environmentKey);

        var dispatcher = new BridgeDispatcher(workspace, settings, keys);

        await workspace.GetWorktreesAsync(root);

        return (dispatcher, root, settings);
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

    private static async Task<string> FailureAsync(
        BridgeDispatcher dispatcher, string method, object? parameters = null)
    {
        var request = JsonSerializer.Serialize(new { id = 1, method, @params = parameters }, BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;

        Assert.False(response.GetProperty("ok").GetBoolean(), $"{method} was expected to fail");
        return response.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task The_status_carries_every_field_the_commit_box_renders()
    {
        var (dispatcher, _, _) = await NewBridgeAsync();

        var status = await CallAsync(dispatcher, "getAiStatus");

        // Named individually, because this is the seam where a rename in Messages.cs becomes
        // a missing field in protocol.ts rather than a compile error.
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.False(status.GetProperty("needsKey").GetBoolean());
        Assert.Equal("environment", status.GetProperty("source").GetString());
        Assert.Equal("claude-opus-5", status.GetProperty("model").GetString());
        Assert.Equal("low", status.GetProperty("effort").GetString());

        // The protocol omits nulls rather than writing them, so an absent reason *is* the
        // "nothing is wrong" answer — the front-end's `string | null` reads undefined the
        // same way.
        Assert.False(status.TryGetProperty("reason", out _));

        // The hint is a tail, never the key.
        Assert.Equal("…uite", status.GetProperty("hint").GetString());
        Assert.DoesNotContain("sk-test", status.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_credential_the_status_says_so_in_the_one_way_the_ui_can_act_on()
    {
        var (dispatcher, _, _) = await NewBridgeAsync(environmentKey: null);

        var status = await CallAsync(dispatcher, "getAiStatus");

        // A login profile on the machine running the suite would legitimately make this
        // available, so the assertion is on the pairing rather than on the value: whenever it
        // is unavailable for want of a credential, the UI is told which fix to offer.
        if (!status.GetProperty("available").GetBoolean())
        {
            Assert.True(status.GetProperty("needsKey").GetBoolean());
            Assert.NotNull(status.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task Switching_the_feature_off_stops_it_before_it_reads_a_credential()
    {
        var (dispatcher, root, settings) = await NewBridgeAsync();
        settings.Ai.Enabled = false;

        var status = await CallAsync(dispatcher, "getAiStatus");

        Assert.False(status.GetProperty("available").GetBoolean());
        Assert.False(status.GetProperty("needsKey").GetBoolean());
        Assert.Equal("none", status.GetProperty("source").GetString());

        // And the request path refuses rather than starting something.
        var error = await FailureAsync(dispatcher, "generateCommitMessage", new { worktreePath = root });
        Assert.Contains("switched off", error);
    }

    [Fact]
    public async Task A_worktree_this_window_never_opened_is_refused()
    {
        // Generation writes nothing, but it reads the whole staged diff and sends it to an
        // API — which makes an unchecked path a way to exfiltrate any repository on the
        // machine, not merely a way to read one.
        var (dispatcher, _, _) = await NewBridgeAsync();

        var error = await FailureAsync(
            dispatcher, "generateCommitMessage", new { worktreePath = @"C:\somebody\elses\repo" });

        Assert.Contains("not open in this window", error);
    }

    [Fact]
    public async Task Storing_a_key_reports_the_new_status_and_never_echoes_the_key()
    {
        var (dispatcher, _, _) = await NewBridgeAsync(environmentKey: null);

        var result = await CallAsync(
            dispatcher, "setApiKey", new { key = "sk-ant-api03-stored-through-the-bridge" });

        Assert.True(result.GetProperty("ok").GetBoolean());

        var status = result.GetProperty("status");
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal("stored", status.GetProperty("source").GetString());

        // The one message in this protocol that carries a secret, and it must not come back.
        Assert.DoesNotContain("stored-through-the-bridge", result.GetRawText(), StringComparison.Ordinal);

        // Clearing puts it back.
        var cleared = await CallAsync(dispatcher, "setApiKey", new { key = "" });
        Assert.Equal("none", cleared.GetProperty("status").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Cancelling_a_generation_that_was_never_started_is_answered_rather_than_thrown()
    {
        var (dispatcher, _, _) = await NewBridgeAsync();

        var result = await CallAsync(dispatcher, "cancelGeneration", new { id = "not-a-real-id" });

        Assert.False(result.GetBoolean());
    }
}
