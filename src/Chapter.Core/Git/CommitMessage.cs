using System.Text;
using System.Text.RegularExpressions;

namespace Chapter.Core.Git;

/// <summary>
/// How strictly a repository's commit messages are checked, and against what.
///
/// Per repository rather than global: conventional commits are house style in some projects
/// and noise in others, and a single setting would make the app wrong for half of them.
/// Every value here is off or permissive by default — the app arrives with an opinion about
/// the two rules git itself has (a short subject, a blank second line) and none about the
/// rest.
/// </summary>
public sealed class CommitMessagePolicy
{
    /// <summary>Soft limit on the subject line. Warned about, never enforced.</summary>
    public int SubjectLimit { get; set; } = 72;

    /// <summary>Where the subject stops being comfortable to read in a narrow log.</summary>
    public int SubjectIdeal { get; set; } = 50;

    /// <summary>Whether the second line must be blank, as git's own tooling assumes.</summary>
    public bool RequireBlankSecondLine { get; set; } = true;

    /// <summary>Whether the subject must parse as <c>type(scope): description</c>.</summary>
    public bool RequireConventionalCommit { get; set; }

    /// <summary>
    /// Types accepted when conventional commits are required. Empty means any lower-case
    /// word is a type, which is what most repositories that use the format actually enforce.
    /// </summary>
    public List<string> Types { get; set; } =
        ["feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore", "revert"];
}

/// <summary>How seriously to take a message problem.</summary>
public enum MessageSeverity
{
    /// <summary>Worth mentioning; committing is still entirely reasonable.</summary>
    Warning,

    /// <summary>Breaks a rule the repository has opted into. Still not a hard block.</summary>
    Error,
}

public sealed record MessageProblem(MessageSeverity Severity, string Message);

/// <summary>
/// The parts of a commit message, and what is wrong with it.
///
/// Nothing here refuses a commit. Message rules are conventions, and an app that blocks on
/// its own reading of one is an app that stops you committing during an incident because the
/// subject is 74 characters. The UI shows these; the user decides.
/// </summary>
public sealed record CommitMessageReview
{
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public IReadOnlyList<MessageProblem> Problems { get; init; } = [];

    /// <summary>Conventional-commit type, when the subject parsed as one.</summary>
    public string? Type { get; init; }

    public string? Scope { get; init; }
    public bool IsBreaking { get; init; }

    public bool IsEmpty => Subject.Length == 0;
    public bool HasErrors => Problems.Any(p => p.Severity is MessageSeverity.Error);
}

public static partial class CommitMessageReader
{
    /// <summary>
    /// Matches <c>type(scope)!: description</c>. Anchored at both ends so a subject that
    /// merely contains a colon — "Fix: the parser" is not conventional, and neither is
    /// "update README: add badges" — does not come back as a parsed type.
    /// </summary>
    [GeneratedRegex(@"^(?<type>[a-z][a-z0-9]*)(?:\((?<scope>[^()]+)\))?(?<breaking>!)?: (?<rest>.+)$")]
    private static partial Regex ConventionalSubject { get; }

    public static CommitMessageReview Review(string message, CommitMessagePolicy? policy = null)
    {
        policy ??= new CommitMessagePolicy();

        // Normalised before anything is measured. A message arriving from a textarea carries
        // CRLF, and counting those as message content makes every subject two characters
        // longer than git will record it.
        var normalised = message.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalised.Split('\n');

        var subject = lines.Length > 0 ? lines[0].Trim() : "";
        var body = lines.Length > 1 ? string.Join('\n', lines[1..]).Trim('\n') : "";

        var problems = new List<MessageProblem>();

        var match = ConventionalSubject.Match(subject);
        var type = match.Success ? match.Groups["type"].Value : null;
        var scope = match.Success && match.Groups["scope"].Success ? match.Groups["scope"].Value : null;
        var breaking = match.Success && match.Groups["breaking"].Success;

        if (subject.Length == 0)
        {
            problems.Add(new MessageProblem(MessageSeverity.Error, "The message needs a subject line."));
        }
        else
        {
            if (subject.Length > policy.SubjectLimit)
            {
                problems.Add(new MessageProblem(MessageSeverity.Warning,
                    $"Subject is {subject.Length} characters; {policy.SubjectLimit} is the limit here."));
            }
            else if (subject.Length > policy.SubjectIdeal)
            {
                problems.Add(new MessageProblem(MessageSeverity.Warning,
                    $"Subject is {subject.Length} characters — over {policy.SubjectIdeal}, it wraps in a narrow log."));
            }

            // Git strips a trailing period from nothing, so this is purely house style —
            // hence a warning, and only for the subject.
            if (subject.EndsWith('.'))
                problems.Add(new MessageProblem(MessageSeverity.Warning, "Subject ends with a full stop."));

            if (policy.RequireConventionalCommit)
            {
                if (!match.Success)
                {
                    problems.Add(new MessageProblem(MessageSeverity.Error,
                        "Subject is not a conventional commit — expected \"type(scope): description\"."));
                }
                else if (policy.Types.Count > 0 && !policy.Types.Contains(type!, StringComparer.Ordinal))
                {
                    problems.Add(new MessageProblem(MessageSeverity.Error,
                        $"\"{type}\" is not one of this repository's types ({string.Join(", ", policy.Types)})."));
                }
            }
        }

        // Only worth saying when there is a body at all: a one-line message has no second
        // line to be blank, and reporting one would flag most good commits in most repos.
        if (policy.RequireBlankSecondLine && lines.Length > 1 && lines[1].Trim().Length > 0)
        {
            problems.Add(new MessageProblem(MessageSeverity.Error,
                "The second line must be blank — git treats everything after it as the body."));
        }

        return new CommitMessageReview
        {
            Subject = subject,
            Body = body,
            Problems = problems,
            Type = type,
            Scope = scope,
            IsBreaking = breaking,
        };
    }

    /// <summary>
    /// The last few subject lines, for showing the user what this repository's messages
    /// look like — and, in Phase 2, for telling a model to match them rather than invent a
    /// new convention.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RecentSubjectsAsync(
        GitCli git, string worktreePath, int count = 20, CancellationToken ct = default)
    {
        var result = await git
            .TryRunAsync(worktreePath, ct, "log", $"--max-count={Math.Max(1, count)}", "--format=%s")
            .ConfigureAwait(false);

        if (!result.Success) return [];

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToArray();
    }
}

/// <summary>A person to credit on a commit, rendered as a git trailer.</summary>
public sealed record CoAuthor(string Name, string Email)
{
    /// <summary>
    /// Git matches this trailer by its exact token, and hosting providers key attribution
    /// off the same string, so the capitalisation is not stylistic.
    /// </summary>
    public string ToTrailer() => $"Co-authored-by: {Name} <{Email}>";

    /// <summary>
    /// Reads "Name &lt;email&gt;", the form git itself prints and the one people paste.
    /// Returns null rather than guessing when there is no address — a trailer without one
    /// is silently ignored by every tool that reads them, which looks like the app dropping
    /// the co-author.
    /// </summary>
    public static CoAuthor? Parse(string text)
    {
        var open = text.LastIndexOf('<');
        var close = text.LastIndexOf('>');
        if (open < 0 || close <= open) return null;

        var name = text[..open].Trim();
        var email = text[(open + 1)..close].Trim();

        return name.Length == 0 || email.Length == 0 ? null : new CoAuthor(name, email);
    }
}

/// <summary>Everything a commit needs beyond the staged content.</summary>
public sealed record CommitRequest
{
    public string Message { get; init; } = "";

    /// <summary>Replaces the tip rather than adding to it.</summary>
    public bool Amend { get; init; }

    /// <summary>Adds a <c>Signed-off-by</c> trailer.</summary>
    public bool SignOff { get; init; }

    /// <summary>
    /// Null leaves signing to the repository's own <c>commit.gpgsign</c>, which is the right
    /// default: a user who has configured signing means it, and an app that quietly passes
    /// <c>--no-gpg-sign</c> produces unsigned commits on a branch that requires them.
    /// </summary>
    public bool? Sign { get; init; }

    public IReadOnlyList<CoAuthor> CoAuthors { get; init; } = [];

    /// <summary>
    /// Reuses the previous message. Only meaningful with <see cref="Amend"/>, where it is
    /// how "amend to include this file" works without retyping anything.
    /// </summary>
    public bool ReuseMessage { get; init; }

    /// <summary>Builds the argument list, in the order git documents them.</summary>
    public string[] ToArguments()
    {
        var args = new List<string> { "commit" };

        if (Amend) args.Add("--amend");

        if (ReuseMessage && Amend)
        {
            // --no-edit keeps the existing message without opening an editor, which this
            // process has deliberately made unavailable.
            args.Add("--no-edit");
        }
        else
        {
            // A single -m argument, newlines and all. Passed through ArgumentList it never
            // touches a shell, so there is nothing to quote and nothing to escape; -F would
            // need a temp file, and stdin is closed on every invocation.
            args.Add("-m");
            args.Add(Message);

            // Git's default cleanup for -m is already `whitespace`, but a repository can
            // change it with commit.cleanup — and `scissors` or `strip` would silently eat
            // any line of the user's message starting with '#'.
            args.Add("--cleanup=whitespace");
        }

        if (SignOff) args.Add("--signoff");

        foreach (var coAuthor in CoAuthors)
        {
            args.Add("--trailer");
            args.Add(coAuthor.ToTrailer());
        }

        if (Sign is true) args.Add("--gpg-sign");
        else if (Sign is false) args.Add("--no-gpg-sign");

        return [.. args];
    }

    /// <summary>The subject as it will be recorded, for the undo label and the log.</summary>
    public string Subject
    {
        get
        {
            var first = Message.Replace("\r\n", "\n").Split('\n')[0].Trim();
            return first.Length > 0 ? first : "(no subject)";
        }
    }
}

/// <summary>
/// Makes commits.
///
/// Small on purpose: the interesting work is deciding whether a commit is legal
/// (<see cref="IndexState.Readiness"/>) and recording how to take it back
/// (<see cref="UndoService"/>). This class is what sits between them.
/// </summary>
public sealed class CommitService(GitCli git, GitWriter writer, UndoService undo)
{
    public async Task<GitMutation> CommitAsync(
        string worktreePath, CommitRequest request, CancellationToken ct = default)
    {
        // Captured before the commit, because afterwards HEAD is the new commit and where it
        // used to point is only recoverable from the reflog. For an amend this is the commit
        // being replaced, which is exactly what undo has to restore.
        var previousHead = await undo.CaptureHeadAsync(worktreePath, ct).ConfigureAwait(false);

        var operation = request.Amend ? "amend" : "commit";

        // WorkingTree, not StartsOperation: committing is how a resolved merge concludes, so
        // the in-progress guard must not block it. Git still refuses a commit with unmerged
        // paths itself, and its refusal there is more precise than a pre-check.
        var mutation = await writer
            .RunAsync(worktreePath, operation, WriteKind.WorkingTree, ct, request.ToArguments())
            .ConfigureAwait(false);

        if (!mutation.Success) return mutation;

        var subject = request.ReuseMessage && request.Amend
            ? await ReadHeadSubjectAsync(worktreePath, ct).ConfigureAwait(false)
            : request.Subject;

        if (request.Amend)
        {
            // Recorded by hand rather than through RecordCommitAsync: the inverse of an
            // amend is not "remove a commit" but "put the replaced one back", and the label
            // has to say so or undo reads as though it would delete the user's work.
            var newHead = await undo.CaptureHeadAsync(worktreePath, ct).ConfigureAwait(false);

            undo.Record(new UndoPoint
            {
                Id = Guid.NewGuid().ToString("N"),
                Label = $"amend \"{Shorten(subject)}\"",
                WorktreePath = worktreePath,
                Timestamp = DateTimeOffset.Now,
                // --soft, so the amended content stays staged and nothing on disk moves.
                // The replaced commit is still in the reflog either way.
                InverseCommand = previousHead is null
                    ? ["update-ref", "-d", "HEAD"]
                    : ["reset", "--soft", previousHead],
                HeadSha = previousHead,
                ExpectedHeadSha = newHead,
                IsDestructive = false,
                Warning = "The amended commit stays in the reflog, so nothing is lost either way.",
            });
        }
        else
        {
            await undo.RecordCommitAsync(worktreePath, previousHead, subject, ct).ConfigureAwait(false);
        }

        return mutation;
    }

    /// <summary>The message currently on HEAD, for prefilling an amend.</summary>
    public async Task<string> ReadHeadMessageAsync(string worktreePath, CancellationToken ct = default)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "log", "-1", "--format=%B").ConfigureAwait(false);

        // %B keeps the trailing newline git stores; the editor should not open with a blank
        // line already selected at the end.
        return result.Success ? result.StandardOutput.TrimEnd('\n', '\r') : "";
    }

    private async Task<string> ReadHeadSubjectAsync(string worktreePath, CancellationToken ct)
    {
        var result = await git.TryRunAsync(worktreePath, ct, "log", "-1", "--format=%s").ConfigureAwait(false);
        return result.Success && result.Trimmed.Length > 0 ? result.Trimmed : "(no subject)";
    }

    /// <summary>
    /// The identity git will record, so the commit box can show who is about to be blamed
    /// for this. A repository with no <c>user.email</c> is a real and common state, and
    /// finding out at commit time is worse than being told up front.
    /// </summary>
    public async Task<(string? Name, string? Email)> ReadIdentityAsync(
        string worktreePath, CancellationToken ct = default)
    {
        var nameTask = git.TryRunAsync(worktreePath, ct, "config", "--get", "user.name");
        var emailTask = git.TryRunAsync(worktreePath, ct, "config", "--get", "user.email");

        await Task.WhenAll(nameTask, emailTask).ConfigureAwait(false);

        var name = await nameTask.ConfigureAwait(false);
        var email = await emailTask.ConfigureAwait(false);

        return (
            name.Success && name.Trimmed.Length > 0 ? name.Trimmed : null,
            email.Success && email.Trimmed.Length > 0 ? email.Trimmed : null);
    }

    private static string Shorten(string subject)
    {
        var line = subject.Split('\n')[0].Trim();
        return line.Length <= 50 ? line : line[..47] + "…";
    }
}
