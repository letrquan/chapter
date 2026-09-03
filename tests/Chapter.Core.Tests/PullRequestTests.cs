using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

public sealed class PullRequestTests
{
    [Fact]
    public void Parses_gh_json_without_leaking_unexpected_shapes()
    {
        var values = PullRequestService.ParseList("""
            [
              {
                "number": 17,
                "url": "https://github.com/acme/project/pull/17",
                "title": "Ship it",
                "body": "details",
                "state": "OPEN",
                "isDraft": true,
                "author": { "login": "ada" },
                "headRefName": "feature/ship",
                "baseRefName": "main",
                "headRepository": { "nameWithOwner": "ada/project" },
                "createdAt": "2026-08-31T10:00:00Z",
                "updatedAt": "2026-09-01T10:00:00Z"
              },
              { "number": 0, "title": "not a PR" },
              { "title": "missing number" }
            ]
            """);

        var value = Assert.Single(values);
        Assert.Equal(17, value.Number);
        Assert.Equal("ada", value.Author);
        Assert.Equal("feature/ship", value.HeadRefName);
        Assert.True(value.IsDraft);
        Assert.Equal("ada/project", value.HeadRepository);
        Assert.Equal(31, value.CreatedAt!.Value.Day);
    }

    [Fact]
    public void Selector_validation_rejects_shell_and_path_injection()
    {
        Assert.Null(GetValidation("17"));
        Assert.Null(GetValidation("https://github.com/acme/project/pull/17"));
        Assert.NotNull(GetValidation("17 & whoami"));
        Assert.NotNull(GetValidation("https://evil.example/pull/17"));
        Assert.NotNull(GetValidation("-1"));
    }

    [Fact]
    public async Task Refusals_describe_the_configured_gh_executable()
    {
        var service = new PullRequestService(new GitCli(),
            new GitWriter(new GitCli(), new OperationLog()), "custom-gh");

        var result = await service.ViewAsync(".", "not a selector");

        Assert.False(result.Success);
        Assert.Contains("custom-gh", result.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_configured_gh_is_classified_as_not_found()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-pr-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var git = new GitCli();
            Assert.True((await git.ExecuteAsync(root, GitIntent.Write, default, "init", "-b", "main")).Success);
            var service = new PullRequestService(git, new GitWriter(git, new OperationLog()),
                "definitely-not-a-gh-executable");

            var result = await service.ViewAsync(root);

            Assert.False(result.Success);
            Assert.Equal(GitFailure.NotFound, result.Failure);
            Assert.Contains("definitely-not-a-gh-executable", result.StandardError,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData("gh: not logged in", GitFailure.AuthenticationRequired)]
    [InlineData("no pull request found for branch", GitFailure.NothingToDo)]
    [InlineData("pull request not found", GitFailure.NotFound)]
    [InlineData("something went wrong", GitFailure.Unknown)]
    public void Maps_common_gh_failures(string stderr, GitFailure expected) =>
        Assert.Equal(expected, PullRequestService.ClassifyGhFailure(stderr));

    [Fact]
    public async Task Missing_gh_is_reported_through_the_bridge_without_crashing_the_window()
    {
        var root = Path.Combine(Path.GetTempPath(), "chapter-pr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var git = new GitCli();
            Assert.True((await git.ExecuteAsync(root, GitIntent.Write, default, "init", "-b", "main")).Success);

            var workspace = new WorkspaceService(git, new OperationLog(), "definitely-not-a-gh-executable");
            await workspace.GetWorktreesAsync(root);
            var dispatcher = new BridgeDispatcher(workspace, new AppSettings());
            var request = JsonSerializer.Serialize(new
            {
                id = 1,
                method = "getPullRequests",
                @params = new { worktreePath = root },
            }, BridgeJson.Options);

            var response = JsonDocument.Parse(await dispatcher.HandleAsync(request)).RootElement;
            Assert.True(response.GetProperty("ok").GetBoolean());
            var result = response.GetProperty("result");
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.Empty(result.GetProperty("pullRequests").EnumerateArray());
            dispatcher.Dispose();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? GetValidation(string selector)
    {
        // The public operation intentionally returns a structured refusal rather than
        // exposing its parser; this small helper keeps the assertion independent of gh.
        return PullRequestService.ValidateSelector(selector, required: true);
    }
}
