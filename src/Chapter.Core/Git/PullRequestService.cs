using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chapter.Core.Git;

/// <summary>A pull request as reported by GitHub CLI.</summary>
public sealed record PullRequest
{
    public required int Number { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string State { get; init; } = "";
    public bool IsDraft { get; init; }
    public string Author { get; init; } = "";
    public string HeadRefName { get; init; } = "";
    public string BaseRefName { get; init; } = "";
    public string HeadRepository { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>The result of a PR read or create operation.</summary>
public sealed record PullRequestResult
{
    public required string Operation { get; init; }
    public required string WorktreePath { get; init; }
    public bool Success { get; init; }
    public PullRequest? PullRequest { get; init; }
    public string Message { get; init; } = "";
    public string CommandLine { get; init; } = "";
    public int ExitCode { get; init; }
    public GitFailure Failure { get; init; } = GitFailure.None;
    public string StandardError { get; init; } = "";
}

/// <summary>
/// Bridges Chapter to GitHub CLI (<c>gh</c>) without embedding a GitHub API client.
///
/// The CLI owns authentication, host selection and enterprise GitHub support. Chapter only
/// supplies non-interactive arguments and parses the bounded JSON it asks for. Checkout is
/// routed through <see cref="GitWriter"/> because it changes the local worktree; reads and
/// PR creation use the same process environment but do not invent a second credential store.
/// </summary>
public sealed class PullRequestService(GitCli git, GitWriter writer, string ghPath = "gh")
{
    private const string JsonFields =
        "number,url,title,body,state,isDraft,author,headRefName,baseRefName,headRepository,createdAt,updatedAt";

    private static readonly Regex NumberPattern = new("^[1-9][0-9]{0,8}$", RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new(
        "^https://[^\\s/]+/(?:[^\\s/]+/){2,}pull/[1-9][0-9]{0,8}/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyDictionary<string, string?> NonInteractiveEnvironment =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GH_PROMPT_DISABLED"] = "1",
            ["GH_PAGER"] = "cat",
            ["GH_FORCE_TTY"] = "0",
        };

    public string GhPath { get; } = ghPath;

    /// <summary>Lists open and closed PRs for the repository containing the worktree.</summary>
    public async Task<IReadOnlyList<PullRequest>> ListAsync(
        string worktreePath, int limit = 100, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var result = await ExecuteReadAsync(
            worktreePath, "list pull requests", ct,
            "pr", "list", "--state", "all", "--limit", limit.ToString(), "--json", JsonFields)
            .ConfigureAwait(false);

        if (!result.Success)
            throw new GitException(
                result.CommandLine, result.ExitCode,
                GitCli.RedactText(result.StandardError));
        return ParseList(result.StandardOutput);
    }

    /// <summary>Loads one PR, or the PR associated with the current branch when selector is empty.</summary>
    public async Task<PullRequestResult> ViewAsync(
        string worktreePath, string selector = "", CancellationToken ct = default)
    {
        var invalid = ValidateSelector(selector, required: false);
        if (invalid is not null) return Refused(worktreePath, "view pull request", invalid);

        var args = new List<string> { "pr", "view" };
        if (!string.IsNullOrWhiteSpace(selector)) args.Add(selector.Trim());
        args.AddRange(["--json", JsonFields]);

        var result = await ExecuteReadAsync(worktreePath, "view pull request", ct, [.. args])
            .ConfigureAwait(false);
        if (!result.Success)
            return Failed(worktreePath, "view pull request", result);

        var parsed = ParseOne(result.StandardOutput);
        return parsed is null
            ? Failed(worktreePath, "view pull request", result, GitFailure.Unknown,
                "gh returned pull-request data in an unexpected format")
            : Succeeded(worktreePath, "view pull request", result, parsed);
    }

    /// <summary>
    /// Creates a PR without allowing gh to open an editor or browser. The URL emitted by
    /// <c>gh pr create</c> is followed by a structured <c>view</c>, so callers receive the
    /// same metadata shape regardless of the gh version installed.
    /// </summary>
    public async Task<PullRequestResult> CreateAsync(
        string worktreePath,
        string title,
        string body = "",
        string baseBranch = "",
        string headBranch = "",
        bool draft = false,
        CancellationToken ct = default)
    {
        var invalid = ValidateText(title, "a pull-request title");
        invalid ??= ValidateRef(baseBranch, "a base branch");
        invalid ??= ValidateRef(headBranch, "a head branch");
        if (invalid is not null) return Refused(worktreePath, "create pull request", invalid);

        var args = new List<string> { "pr", "create", "--title", title.Trim(), "--body", body ?? "" };
        if (!string.IsNullOrWhiteSpace(baseBranch)) args.AddRange(["--base", baseBranch.Trim()]);
        if (!string.IsNullOrWhiteSpace(headBranch)) args.AddRange(["--head", headBranch.Trim()]);
        if (draft) args.Add("--draft");

        // This is a local mutation too: gh may push a branch before opening the PR. Routing
        // it through the writer keeps the watcher, operation log and lock handling intact.
        GitMutation mutation;
        try
        {
            mutation = await writer.RunExternalAsync(
                worktreePath, "create pull request", WriteKind.WorkingTree, GitIntent.Network,
                GhPath, NonInteractiveEnvironment, ct, [.. args]).ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return new PullRequestResult
            {
                Operation = "create pull request",
                WorktreePath = worktreePath,
                Message = $"Could not create pull request: {ex.Message}",
                CommandLine = ex.CommandLine,
                ExitCode = ex.ExitCode,
                Failure = ClassifyGhFailure(ex.StandardError),
                StandardError = GitCli.RedactText(ex.StandardError),
            };
        }

        if (!mutation.Success)
            return new PullRequestResult
            {
                Operation = "create pull request",
                WorktreePath = worktreePath,
                Message = mutation.Message,
                CommandLine = mutation.CommandLine,
                ExitCode = mutation.ExitCode,
                Failure = ClassifyGhFailure(mutation.StandardError, mutation.Message),
                StandardError = mutation.StandardError,
            };

        var url = ExtractPullRequestUrl(mutation.StandardOutput) ??
                  ExtractPullRequestUrl(mutation.StandardError);
        if (url is null)
        {
            // Some gh versions print only a short success sentence. A follow-up view of the
            // current branch still produces the structured object when creation succeeded.
            var current = await ViewAsync(worktreePath, ct: ct).ConfigureAwait(false);
            return current.Success
                ? current with { Operation = "create pull request" }
                : Succeeded(worktreePath, "create pull request", mutation,
                    pullRequest: null, message: "Pull request created.");
        }

        var viewed = await ViewAsync(worktreePath, url, ct).ConfigureAwait(false);
        return viewed.Success
            ? viewed with { Operation = "create pull request" }
            : Succeeded(worktreePath, "create pull request", mutation,
                pullRequest: null, message: $"Pull request created: {url}");
    }

    /// <summary>Checks out a PR through gh, preserving the normal mutation safeguards.</summary>
    public Task<GitMutation> CheckoutAsync(
        string worktreePath, string selector, CancellationToken ct = default)
    {
        var invalid = ValidateSelector(selector, required: true);
        if (invalid is not null)
            return Task.FromResult(new GitMutation
            {
                Operation = "checkout pull request",
                WorktreePath = worktreePath,
                CommandLine = GitCli.DescribeCommand(GhPath, ["pr", "checkout", selector ?? ""]),
                ExitCode = -1,
                Failure = GitFailure.NotFound,
                Detail = $"Could not checkout pull request: {invalid}",
                Attempts = 0,
            });

        return CheckoutCoreAsync(worktreePath, selector.Trim(), ct);
    }

    private async Task<GitMutation> CheckoutCoreAsync(
        string worktreePath, string selector, CancellationToken ct)
    {
        try
        {
            return await writer.RunExternalAsync(
                worktreePath, "checkout pull request", WriteKind.StartsOperation, GitIntent.Network,
                GhPath, NonInteractiveEnvironment, ct, "pr", "checkout", selector)
                .ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return new GitMutation
            {
                Operation = "checkout pull request",
                WorktreePath = worktreePath,
                CommandLine = ex.CommandLine,
                ExitCode = ex.ExitCode,
                Failure = ClassifyGhFailure(ex.StandardError),
                Detail = $"Could not checkout pull request: {GitCli.RedactText(ex.StandardError).Trim()}",
                Attempts = 1,
            };
        }
    }

    // ---------------------------------------------------------------------
    // Parsing and failure mapping (internal for focused tests)
    // ---------------------------------------------------------------------

    internal static IReadOnlyList<PullRequest> ParseList(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Select(ParseElement)
                .Where(static value => value is not null)
                .Cast<PullRequest>()
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static PullRequest? ParseOne(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(ParseElement).FirstOrDefault(static p => p is not null)
                : ParseElement(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static GitFailure ClassifyGhFailure(string stderr, string stdout = "")
    {
        var text = $"{stderr}\n{stdout}";
        if (text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("gh auth login", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
            return GitFailure.AuthenticationRequired;

        if (text.Contains("no pull request", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no pull requests", StringComparison.OrdinalIgnoreCase))
            return GitFailure.NothingToDo;

        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("could not resolve", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed to start", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("is not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase))
            return GitFailure.NotFound;

        if (text.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("already have a pull request", StringComparison.OrdinalIgnoreCase))
            return GitFailure.NothingToDo;

        return GitFailure.Unknown;
    }

    private async Task<GitResult> ExecuteReadAsync(
        string worktreePath, string operation, CancellationToken ct, params string[] args)
    {
        try
        {
            return await git.ExecuteExternalAsync(
                GhPath, worktreePath, GitIntent.Network, NonInteractiveEnvironment, ct, args)
                .ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return new GitResult(ex.CommandLine, ex.ExitCode, "", ex.StandardError);
        }
    }

    private static PullRequest? ParseElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("number", out var number) ||
            !number.TryGetInt32(out var value) || value <= 0)
            return null;

        return new PullRequest
        {
            Number = value,
            Url = StringProperty(element, "url"),
            Title = StringProperty(element, "title"),
            Body = StringProperty(element, "body"),
            State = StringProperty(element, "state"),
            IsDraft = BoolProperty(element, "isDraft"),
            Author = NestedStringProperty(element, "author", "login"),
            HeadRefName = StringProperty(element, "headRefName"),
            BaseRefName = StringProperty(element, "baseRefName"),
            HeadRepository = NestedStringProperty(element, "headRepository", "nameWithOwner"),
            CreatedAt = DateProperty(element, "createdAt"),
            UpdatedAt = DateProperty(element, "updatedAt"),
        };
    }

    private static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? GitCli.RedactText(value.GetString() ?? "")
            : "";

    private static string NestedStringProperty(JsonElement element, string outer, string inner) =>
        element.TryGetProperty(outer, out var value) && value.ValueKind == JsonValueKind.Object
            ? StringProperty(value, inner)
            : "";

    private static bool BoolProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? DateProperty(JsonElement element, string name) =>
        DateTimeOffset.TryParse(StringProperty(element, name), out var value) ? value : null;

    private static string? ExtractPullRequestUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('"', '\'', ',', '.', ')', '(', '[', ']'))
            .FirstOrDefault(token => UrlPattern.IsMatch(token));
    }

    internal static string? ValidateSelector(string? value, bool required)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0) return required ? "a pull-request number or URL is required" : null;
        if (NumberPattern.IsMatch(value) || UrlPattern.IsMatch(value)) return null;
        return "use a pull-request number or an https://github.com/.../pull/<number> URL";
    }

    private static string? ValidateRef(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return ValidateText(value, description);
    }

    private static string? ValidateText(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"{description} is required";
        return value.Any(char.IsControl) ? $"{description} cannot contain control characters" : null;
    }

    private PullRequestResult Refused(string worktreePath, string operation, string reason) =>
        new()
        {
            Operation = operation,
            WorktreePath = worktreePath,
            Message = $"Could not {operation}: {reason}",
            CommandLine = GitCli.DescribeCommand(GhPath, ["pr"]),
            ExitCode = -1,
            Failure = GitFailure.NotFound,
        };

    private static PullRequestResult Failed(
        string worktreePath, string operation, GitResult result,
        GitFailure? failure = null, string? message = null) => new()
        {
            Operation = operation,
            WorktreePath = worktreePath,
            Message = message ?? FirstLine(result.StandardError) ?? $"Could not {operation}",
            CommandLine = result.CommandLine,
            ExitCode = result.ExitCode,
            Failure = failure ?? ClassifyGhFailure(result.StandardError, result.StandardOutput),
            StandardError = GitCli.RedactText(result.StandardError),
        };

    private static PullRequestResult Succeeded(
        string worktreePath, string operation, GitResult result,
        PullRequest? pullRequest, string? message = null) => new()
        {
            Operation = operation,
            WorktreePath = worktreePath,
            Success = true,
            PullRequest = pullRequest,
            Message = message ?? (pullRequest is null ? $"{operation} succeeded" : pullRequest.Url),
            CommandLine = result.CommandLine,
            ExitCode = result.ExitCode,
        };

    private static PullRequestResult Succeeded(
        string worktreePath, string operation, GitMutation mutation,
        PullRequest? pullRequest, string? message = null) => new()
        {
            Operation = operation,
            WorktreePath = worktreePath,
            Success = true,
            PullRequest = pullRequest,
            Message = message ?? (pullRequest is null ? $"{operation} succeeded" : pullRequest.Url),
            CommandLine = mutation.CommandLine,
            ExitCode = mutation.ExitCode,
        };

    private static string? FirstLine(string text) => text.Split(['\r', '\n'])
        .Select(line => GitCli.RedactText(line.Trim()))
        .FirstOrDefault(line => line.Length > 0);
}
