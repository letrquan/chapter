using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Git;
using Chapter.Core.Indexing;

namespace Chapter.Core.Tests;

/// <summary>
/// Guards for defects found in code review. Each one reproduces the reported failure, so
/// a regression fails here rather than being noticed in the UI weeks later.
/// </summary>
public class RegressionTests
{
    private static readonly GitCli Git = new();

    /// <summary>
    /// Creates a throwaway git repository. Committing is optional so the no-commits case
    /// can be exercised.
    /// </summary>
    private static async Task<string> NewRepoAsync(bool withCommit)
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-rt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        await Git.RunAsync(root, default, "init", "-b", "main");
        await Git.RunAsync(root, default, "config", "user.email", "test@example.com");
        await Git.RunAsync(root, default, "config", "user.name", "Test");

        await File.WriteAllTextAsync(Path.Combine(root, "First.cs"), "class First { }\n");

        if (withCommit)
        {
            await Git.RunAsync(root, default, "add", "-A");
            await Git.RunAsync(root, default, "commit", "-m", "initial");
        }

        return root;
    }

    private static void Delete(string root)
    {
        try
        {
            // git marks objects read-only, which blocks a plain recursive delete.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    // -----------------------------------------------------------------------

    [Fact]
    public void File_search_rejects_non_matches_instead_of_returning_everything()
    {
        var index = new WorkspaceIndex("unused", new CSharpIndexer());

        // The filename bonus used to be added before the no-match sentinel was tested, so
        // every unrelated file scored (-1 + 20) = 19 and survived the filter — the palette
        // listed the whole worktree for any query.
        var scoreForJunk = FuzzyMatcher.Score("AgentTurnRunner.cs", "zzzq");
        Assert.True(scoreForJunk < 0, "precondition: the query must not match at all");

        Assert.Empty(index.SearchFiles("zzzq", 60));
    }

    [Fact]
    public async Task File_search_still_ranks_filename_matches_above_path_only_matches()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-rt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "runner"));
        await File.WriteAllTextAsync(Path.Combine(root, "runner", "Unrelated.cs"), "class A { }");
        await File.WriteAllTextAsync(Path.Combine(root, "Runner.cs"), "class B { }");

        try
        {
            var index = new WorkspaceIndex(root, new CSharpIndexer());
            await index.EnsureBuiltAsync();

            var results = index.SearchFiles("runner", 10);

            // Both match, but the one whose *name* matches has to win.
            Assert.Equal("Runner.cs", results[0]);
            Assert.Contains("runner/Unrelated.cs", results);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Repository_with_no_commits_lists_its_files_instead_of_erroring()
    {
        var root = await NewRepoAsync(withCommit: false);
        try
        {
            var resolver = new BaseBranchResolver(Git);

            // `git rev-parse HEAD` fails here. Passing the literal "HEAD" on to git diff
            // used to fail again, so a freshly initialised repo showed a git error toast
            // instead of the files it contains.
            foreach (var scope in Enum.GetValues<DiffScope>())
            {
                var diffBase = await resolver.ResolveBaseAsync(root, scope);
                var files = await new DiffService(Git).GetChangedFilesAsync(root, diffBase);

                Assert.Equal("no commits yet", diffBase.Description);
                Assert.Contains(files, f => f.Path == "First.cs");
            }
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Code_view_honours_the_scope_it_was_asked_for()
    {
        var root = await NewRepoAsync(withCommit: true);
        try
        {
            // Commit one version, then leave a different one on disk.
            await File.WriteAllTextAsync(Path.Combine(root, "First.cs"), "class First { int uncommitted; }\n");

            var workspace = new WorkspaceService(Git);

            var branch = await workspace.GetFileContentAsync(root, "First.cs", DiffScope.Branch);
            var committed = await workspace.GetFileContentAsync(root, "First.cs", DiffScope.Committed);

            // Ctrl+D out of a Committed view used to read the working tree regardless,
            // showing exactly the uncommitted edits that view exists to exclude.
            Assert.Contains("uncommitted", branch.Text);
            Assert.DoesNotContain("uncommitted", committed.Text);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task A_failed_index_build_can_be_retried_rather_than_latching()
    {
        var missing = Path.Combine(Path.GetTempPath(), "chapter-absent-" + Guid.NewGuid().ToString("N")[..8]);
        var index = new WorkspaceIndex(missing, new CSharpIndexer());

        await index.EnsureBuiltAsync();
        Assert.Equal(IndexState.Failed, index.State);

        // The build task used to be memoised forever, so one failure disabled F12 and
        // Ctrl+T for that worktree for the rest of the session.
        Directory.CreateDirectory(missing);
        await File.WriteAllTextAsync(Path.Combine(missing, "Later.cs"), "class Later { }");

        try
        {
            await index.EnsureBuiltAsync();

            Assert.Equal(IndexState.Ready, index.State);
            Assert.Contains(index.FindDeclarations("Later"), d => d.Kind == SymbolKind.Class);
        }
        finally
        {
            Delete(missing);
        }
    }

    [Fact]
    public async Task An_empty_or_unreadable_worktree_fails_rather_than_reporting_success()
    {
        var missing = Path.Combine(Path.GetTempPath(), "chapter-absent-" + Guid.NewGuid().ToString("N")[..8]);
        var index = new WorkspaceIndex(missing, new CSharpIndexer());

        await index.EnsureBuiltAsync();

        // Reporting Ready with zero files would latch: every later build short-circuits on
        // the Ready fast path and the worktree is never indexed.
        Assert.Equal(IndexState.Failed, index.State);
        Assert.NotNull(index.Error);
    }

    [Fact]
    public async Task Changes_arriving_during_a_build_are_applied_rather_than_dropped()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-rt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Existing.cs"), "class Existing { }");

        try
        {
            var index = new WorkspaceIndex(root, new CSharpIndexer());

            // Simulates the watcher firing while the initial build is still running: the
            // build snapshots the file list, so an edit landing mid-build used to be lost
            // permanently — no later event fires for a file that stops changing.
            var build = index.EnsureBuiltAsync();

            await File.WriteAllTextAsync(Path.Combine(root, "DuringBuild.cs"), "class DuringBuild { }");
            await index.ReindexFileAsync("DuringBuild.cs");

            await build;

            Assert.Contains(index.FindDeclarations("DuringBuild"), d => d.Kind == SymbolKind.Class);
            Assert.Contains(index.SearchFiles("DuringBuild", 10), p => p.EndsWith("DuringBuild.cs"));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Settings_with_explicit_null_members_load_without_crashing()
    {
        var path = AppSettings.FilePath;
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            Directory.CreateDirectory(AppSettings.DirectoryPath);

            // Parseable JSON, so the try/catch in Load never fires; the nulls used to
            // overwrite the property initialisers and crash in the app constructor.
            File.WriteAllText(path, """
                { "recentRepos": null, "editorPaths": null, "lastWorktree": null, "theme": null }
                """);

            var settings = AppSettings.Load();

            Assert.NotNull(settings.RecentRepos);
            Assert.NotNull(settings.EditorPaths);
            Assert.NotNull(settings.LastWorktree);

            settings.RecordRepo(@"C:\somewhere");   // used to throw NullReferenceException
            Assert.Single(settings.RecentRepos);
        }
        finally
        {
            if (backup is not null) File.WriteAllText(path, backup);
            else File.Delete(path);
        }
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\config\SAM")]   // rooted: Path.Combine drops the root
    [InlineData("../../../Windows/win.ini")]          // parent traversal
    [InlineData(@"..\..\outside.txt")]
    public void Paths_escaping_the_worktree_are_refused(string escape)
    {
        Assert.Throws<ArgumentException>(() => RepoPaths.Resolve(@"C:\worktree", escape));
    }

    [Theory]
    [InlineData("src/Foo.cs")]
    [InlineData("src/nested/deep/Bar.cs")]
    [InlineData("Foo.cs")]
    public void Paths_inside_the_worktree_still_resolve(string inside)
    {
        var resolved = RepoPaths.Resolve(@"C:\worktree", inside);
        Assert.StartsWith(@"C:\worktree\", resolved);
    }

    /// <summary>
    /// Markdown in a worktree is untrusted — an agent wrote it. An image reference is a
    /// path the document controls, so the asset endpoint is a read primitive pointed at
    /// arbitrary input and has to refuse everything outside the worktree.
    /// </summary>
    [Fact]
    public async Task Preview_assets_refuse_paths_outside_the_worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-asset-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "docs"));

        try
        {
            foreach (var escape in new[] { "../../../../Windows/win.ini", @"C:\Windows\win.ini" })
            {
                var refused = await WorkspaceService.GetAssetAsync(root, escape);
                Assert.Null(refused.DataUri);
                Assert.Equal("outside the worktree", refused.Reason);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Preview_assets_inline_images_and_explain_what_they_cannot()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-asset-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "docs"));

        try
        {
            // A one-pixel PNG is enough to prove the round-trip and the media type.
            byte[] png =
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
                0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
            ];
            await File.WriteAllBytesAsync(Path.Combine(root, "docs", "diagram.png"), png);
            await File.WriteAllTextAsync(Path.Combine(root, "docs", "notes.txt"), "not an image");

            var inlined = await WorkspaceService.GetAssetAsync(root, "docs/diagram.png");
            Assert.NotNull(inlined.DataUri);
            Assert.StartsWith("data:image/png;base64,", inlined.DataUri);

            // Every refusal carries a reason, so the preview can say why rather than
            // rendering a broken-image glyph.
            var missing = await WorkspaceService.GetAssetAsync(root, "docs/absent.png");
            Assert.Equal("not found", missing.Reason);

            var wrongType = await WorkspaceService.GetAssetAsync(root, "docs/notes.txt");
            Assert.Equal("unsupported image type", wrongType.Reason);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Unknown_scope_values_do_not_crash_the_dispatcher()
    {
        // The front-end and backend enums must stay in step; a mismatch should degrade to
        // an error response rather than taking the window down.
        var dispatcher = new Contracts.BridgeDispatcher(new WorkspaceService(Git), new AppSettings());

        var response = await dispatcher.HandleAsync(
            """{"id":1,"method":"getChanges","params":{"worktreePath":"C:\\nope","scope":"nonsense"}}""");

        var parsed = JsonDocument.Parse(response).RootElement;
        Assert.False(parsed.GetProperty("ok").GetBoolean());
    }
}
