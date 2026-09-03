using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Detached clone coverage uses local repositories so it exercises Git's real transfer path
/// without depending on a network service or credentials.
/// </summary>
public sealed class CloneTests : IDisposable
{
    private static readonly GitCli Git = new();
    private readonly List<string> _created = [];

    [Theory]
    [InlineData("", "C:\\new-repo", "a repository URL or path is required")]
    [InlineData("-bad", "C:\\new-repo", "cannot begin with a dash")]
    [InlineData("source", "", "a destination folder is required")]
    public void Validates_clone_inputs(string source, string destination, string expected)
    {
        var error = CloneService.Validate(source, destination);
        Assert.NotNull(error);
        Assert.Contains(expected, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clones_a_local_repository_and_reports_completion()
    {
        var source = await NewRepoAsync("chapter-clone-source");
        var destination = Path.Combine(Path.GetDirectoryName(source)!,
            "chapter-clone-destination-" + Guid.NewGuid().ToString("N"));
        _created.Add(destination);

        var service = new CloneService(Git, new OperationLog());
        var finished = new TaskCompletionSource<CloneProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.Finished += progress => finished.TrySetResult(progress);

        var started = service.Start(source, destination);
        Assert.False(string.IsNullOrWhiteSpace(started.Id));
        Assert.Equal(Path.GetFullPath(destination), started.Destination);

        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(started.Id, result.Id);
        Assert.Equal("completed", result.State);
        Assert.NotNull(result.Mutation);
        Assert.True(result.Mutation!.Success, result.Mutation.Message);
        Assert.True(File.Exists(Path.Combine(destination, "README.txt")));
        var clonedText = await File.ReadAllTextAsync(Path.Combine(destination, "README.txt"));
        Assert.Equal("source", clonedText.Trim());
    }

    /// <summary>
    /// Asserted on the command rather than on a populated submodule directory, because git
    /// refuses <c>file</c>-transport submodules by default and the app does not override
    /// that — so a local fixture would fail for a reason unrelated to the flag. The flag is
    /// what was wrong: the option is checked by default and only its negation was ever
    /// passed, so "Include submodules" cloned none.
    /// </summary>
    [Theory]
    [InlineData(true, "--recurse-submodules")]
    [InlineData(false, "--no-recurse-submodules")]
    public async Task States_the_submodule_choice_to_git_in_both_directions(bool recursive, string expected)
    {
        var source = await NewRepoAsync("chapter-clone-sub-source");
        var destination = Path.Combine(Path.GetDirectoryName(source)!,
            "chapter-clone-sub-destination-" + Guid.NewGuid().ToString("N"));
        _created.Add(destination);

        var service = new CloneService(Git, new OperationLog());
        var finished = new TaskCompletionSource<CloneProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.Finished += progress => finished.TrySetResult(progress);

        service.Start(source, destination, bare: false, recursive: recursive);

        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Mutation!.Success, result.Mutation.Message);
        Assert.Contains(expected, result.Mutation.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bridge_registers_a_successful_clone_and_emits_events()
    {
        var source = await NewRepoAsync("chapter-clone-bridge-source");
        var destination = Path.Combine(Path.GetDirectoryName(source)!,
            "chapter-clone-bridge-destination-" + Guid.NewGuid().ToString("N"));
        _created.Add(destination);

        var settingsPath = Path.Combine(Path.GetTempPath(),
            "chapter-clone-settings-" + Guid.NewGuid().ToString("N") + ".json");
        _created.Add(settingsPath);
        var settings = new AppSettings { StoragePath = settingsPath };
        var workspace = new WorkspaceService(Git, new OperationLog());
        var dispatcher = new BridgeDispatcher(workspace, settings);
        var finished = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.EventRaised += evt =>
        {
            if (evt.Payload is null) return;
            var payload = JsonDocument.Parse(BridgeJson.Serialize(evt.Payload)).RootElement.Clone();
            if (evt.Event == "cloneFinished") finished.TrySetResult(payload);
            if (evt.Event == "reposChanged") changed.TrySetResult(payload);
        };
        dispatcher.StartWatching();

        var request = JsonSerializer.Serialize(new
        {
            id = 7,
            method = "startClone",
            @params = new { source, destination, recursive = true, bare = false },
        }, BridgeJson.Options);
        var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;
        Assert.True(response.GetProperty("ok").GetBoolean());
        var id = response.GetProperty("result").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));

        var ended = await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(id, ended.GetProperty("id").GetString());
        Assert.Equal("completed", ended.GetProperty("state").GetString());
        Assert.Equal(Path.GetFullPath(destination), ended.GetProperty("repositoryPath").GetString());

        var registered = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Path.GetFullPath(destination), registered.GetProperty("repoPath").GetString());
        Assert.Contains(Path.GetFullPath(destination), settings.RecentRepos,
            StringComparer.OrdinalIgnoreCase);

        dispatcher.Dispose();
    }

    [Fact]
    public async Task Cancel_returns_false_for_an_unknown_clone()
    {
        var service = new CloneService(Git, new OperationLog());
        Assert.False(service.Cancel("does-not-exist"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Progress_parser_handles_chunk_boundaries_carriage_returns_and_final_lines()
    {
        var lines = new List<(GitOutputStream Stream, string Text)>();
        var parser = new ProgressLineParser((stream, text) => lines.Add((stream, text)));

        parser.Push(new GitOutputChunk(GitOutputStream.StandardError, "remote: 1"));
        parser.Push(new GitOutputChunk(GitOutputStream.StandardError, "0%\rremote: 20"));
        parser.Push(new GitOutputChunk(GitOutputStream.StandardError, "%\nfinal status"));
        parser.Flush();

        Assert.Equal(
            [
                (GitOutputStream.StandardError, "remote: 10%"),
                (GitOutputStream.StandardError, "remote: 20%"),
                (GitOutputStream.StandardError, "final status"),
            ],
            lines);
    }

    [Fact]
    public void Progress_parser_bounds_unterminated_output_and_deduplicates_lines()
    {
        var lines = new List<string>();
        var parser = new ProgressLineParser((_, text) => lines.Add(text));
        var longLine = new string('x', 400);

        parser.Push(new GitOutputChunk(GitOutputStream.StandardError, longLine));
        parser.Flush();

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.True(line.Length <= 160));
        Assert.Equal(400, lines.Sum(line => line.Length));

        // Identical continuation pieces are still real output; only complete repeated lines
        // are de-duplicated.
        parser.Push(new GitOutputChunk(GitOutputStream.StandardError, longLine));
        parser.Flush();
        Assert.Equal(800, lines.Sum(line => line.Length));
    }

    private async Task<string> NewRepoAsync(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _created.Add(root);

        await RunAsync(root, "init", "-b", "main");
        await RunAsync(root, "config", "user.name", "Clone Test");
        await RunAsync(root, "config", "user.email", "clone@example.com");
        await RunAsync(root, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "README.txt"), "source\n");
        await RunAsync(root, "add", "--", ":(literal)README.txt");
        await RunAsync(root, "commit", "-m", "initial");
        return root;
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
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    continue;
                }

                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // A leftover temporary fixture is less useful to report than the assertion.
            }
        }
    }
}
