using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Anthropic;
using Anthropic.Core;
using Anthropic.Credentials;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;

namespace Chapter.Core.Ai;

/// <summary>Whether a message can be generated here, and what with.</summary>
public sealed record AiAvailability
{
    public required bool Available { get; init; }

    /// <summary>Why not, in one sentence. Null when it is.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Distinguishes "no key" from every other reason, because it is the only one the user
    /// can fix from inside the app and the UI offers a different affordance for it.
    /// </summary>
    public bool NeedsKey { get; init; }

    public required string Source { get; init; }
    public string? Hint { get; init; }
    public required string Model { get; init; }
    public required string Effort { get; init; }
}

/// <summary>What has been generated so far, as text rather than as half-arrived JSON.</summary>
public sealed record GenerationProgress(string Id, string WorktreePath, string Message);

/// <summary>The end of one generation, however it ended.</summary>
public sealed record GenerationResult
{
    public required string Id { get; init; }
    public required string WorktreePath { get; init; }
    public required bool Ok { get; init; }

    /// <summary>One sentence for the user. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Best first. One entry for an ordinary generation, several when asked.</summary>
    public IReadOnlyList<GeneratedMessage> Options { get; init; } = [];

    public GenerationCost? Cost { get; init; }

    /// <summary>Whether the model saw the whole change. Shown, not hidden.</summary>
    public bool DiffTruncated { get; init; }

    /// <summary>Anything true and worth saying that is not a failure.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Writes commit messages with Claude.
///
/// Everything here is arranged around one fact: this is the first thing the app does that
/// leaves the machine. So it says what it sent (the operation log records every call), it
/// says what it cost, it says when it could not see the whole diff, and it never becomes the
/// reason a commit cannot happen — every failure path ends with the message box exactly as
/// usable as it was before the button was pressed.
///
/// Generation is started rather than awaited. The bridge is request/response with a sixty
/// second ceiling, and a model call is the first thing in this app that can legitimately take
/// longer than a git command — so the call returns an id at once and the text arrives as
/// events, which is also the progress protocol the roadmap's cross-cutting section asks for.
/// </summary>
public sealed class CommitMessageGenerator
{
    private readonly GitCli _git;
    private readonly AppSettings _settings;
    private readonly ApiKeyStore _keys;
    private readonly OperationLog _log;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    /// <summary>
    /// Resolved once and remembered. Reading it touches config files and can exchange a
    /// token over the network, and this question is asked every time the commit box paints.
    /// </summary>
    private CredentialResult? _profile;

    private bool _profileResolved;

    public CommitMessageGenerator(GitCli git, AppSettings settings, ApiKeyStore keys, OperationLog log)
    {
        _git = git;
        _settings = settings;
        _keys = keys;
        _log = log;
    }

    /// <summary>Fired as text arrives, so the message box fills rather than waits.</summary>
    public event Action<GenerationProgress>? Progress;

    /// <summary>Fired exactly once per generation, success or failure.</summary>
    public event Action<GenerationResult>? Finished;

    /// <summary>
    /// Whether the feature can run, without doing anything that costs money.
    ///
    /// Answers "no key" as a distinct state rather than a generic failure: it is the only
    /// reason the user can act on, and the commit box turns it into an inline prompt instead
    /// of an error.
    /// </summary>
    public AiAvailability Describe()
    {
        var ai = _settings.Ai;

        // Answered before anything is read. A feature that is switched off has no business
        // opening credential files or exchanging a token to find out how switched off it is.
        if (!ai.Enabled)
        {
            return new AiAvailability
            {
                Available = false,
                Reason = "Message generation is switched off in settings.json.",
                Source = "none",
                Model = ai.Model,
                Effort = ai.Effort,
            };
        }

        // Cheap sources first. Resolving a login profile is the expensive one and is only
        // worth doing when neither of the others answered.
        var state = _keys.Read();

        if (!state.HasKey && ResolveProfile() is not null)
            state = new ApiKeyState(ApiKeySource.Profile, null);

        var source = state.Source switch
        {
            ApiKeySource.Stored => "stored",
            ApiKeySource.Environment => "environment",
            ApiKeySource.Profile => "profile",
            _ => "none",
        };

        return new AiAvailability
        {
            Available = state.HasKey,
            NeedsKey = !state.HasKey,
            Reason = state.HasKey
                ? null
                : "Chapter has no Claude API key. Add one to write commit messages here.",
            Source = source,
            Hint = state.Hint,
            Model = ai.Model,
            Effort = ai.Effort,
        };
    }

    /// <summary>
    /// Starts a generation and returns its id immediately.
    ///
    /// The work runs detached, reporting through <see cref="Progress"/> and
    /// <see cref="Finished"/>. Nothing it does can throw into the caller: the whole body is
    /// wrapped, and every exit raises <see cref="Finished"/> exactly once, because a UI left
    /// waiting for an event that never comes is worse than one shown an error.
    /// </summary>
    /// <param name="amend">
    /// Generate for the commit an amend would produce. The diff then runs from HEAD's parent
    /// rather than HEAD, because the message has to describe the whole replacement commit,
    /// not just the files added since the one being replaced.
    /// </param>
    public string Begin(string worktreePath, bool amend = false, int count = 1)
    {
        var id = Guid.NewGuid().ToString("N");
        var cancellation = new CancellationTokenSource();
        _running[id] = cancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await RunAsync(id, worktreePath, amend, count, cancellation.Token)
                    .ConfigureAwait(false);
                Raise(result);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Guarded, because an HTTP timeout also arrives as a TaskCanceledException.
                // Ungated, a request that hung for ninety seconds would be reported as
                // "Generation cancelled." — and the user would reasonably believe they had
                // pressed something. Anything not asked for falls through to Describe below,
                // which has a sentence for exactly this.
                Raise(new GenerationResult
                {
                    Id = id,
                    WorktreePath = worktreePath,
                    Ok = false,
                    Error = "Generation cancelled.",
                });
            }
            catch (Exception ex)
            {
                Raise(Failure(id, worktreePath, Describe(ex)));
            }
            finally
            {
                if (_running.TryRemove(id, out var registered)) registered.Dispose();
            }
        });

        return id;
    }

    /// <summary>Stops a generation. Returns false when it had already finished.</summary>
    public bool Cancel(string id)
    {
        if (!_running.TryGetValue(id, out var cancellation)) return false;

        cancellation.Cancel();
        return true;
    }

    /// <summary>Stops everything running for a worktree the user has navigated away from.</summary>
    public void CancelAll()
    {
        foreach (var cancellation in _running.Values) cancellation.Cancel();
    }

    private void Raise(GenerationResult result)
    {
        try
        {
            Finished?.Invoke(result);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Generation subscriber failed: {ex.Message}");
        }
    }

    private async Task<GenerationResult> RunAsync(
        string id, string worktreePath, bool amend, int count, CancellationToken ct)
    {
        var ai = _settings.Ai;
        var stopwatch = Stopwatch.StartNew();

        var client = CreateClient();
        if (client is null) return Failure(id, worktreePath, Describe().Reason ?? "No credential is configured.");

        using var owned = client;

        // An amend describes the commit that will exist afterwards, which is the index against
        // the replaced commit's parent. Reading against HEAD would describe only what has been
        // staged since, and produce a message about a two-line fix for a fifty-file commit.
        var baseRef = amend ? await AmendBaseAsync(worktreePath, ct).ConfigureAwait(false) : null;

        var policy = _settings.CommitPolicyFor(worktreePath);

        var recent = await CommitMessageReader
            .RecentSubjectsAsync(_git, worktreePath, 20, ct)
            .ConfigureAwait(false);

        // Four characters to the token is a deliberate over-estimate for source code, which
        // tokenises worse than prose. It only sets the first attempt; the count that decides
        // anything comes from the API below.
        var digest = await DiffDigestBuilder
            .ReadAsync(_git, worktreePath, ai.InputTokenBudget * 4, baseRef, ct: ct)
            .ConfigureAwait(false);

        if (digest.IsEmpty)
        {
            return Failure(id, worktreePath, amend
                ? "That commit has no changes to describe."
                : "Nothing is staged, so there is nothing to describe.");
        }

        var system = BuildSystem(policy, recent);
        var parameters = BuildRequest(ai, system, digest, policy, count);

        // Measured with the API's own counter, never estimated. A tokeniser borrowed from
        // another model family is wrong for this one by enough to matter, and the point of
        // the budget is to be right about the request that is actually about to be sent.
        var counted = await CountAsync(owned, parameters, system, ct).ConfigureAwait(false);

        if (counted > ai.InputTokenBudget)
        {
            // One retry, scaled by how far over it went. Bisecting would cost several more
            // round trips to save tokens that are already inside the budget's own slack.
            var scaled = (int)(ai.InputTokenBudget * 4L * ai.InputTokenBudget / Math.Max(1, counted));

            digest = await DiffDigestBuilder
                .ReadAsync(_git, worktreePath, scaled, baseRef, ct: ct)
                .ConfigureAwait(false);

            parameters = BuildRequest(ai, system, digest, policy, count);
        }

        var outcome = count <= 1
            ? await StreamAsync(owned, id, worktreePath, parameters, ct).ConfigureAwait(false)
            : await OnceAsync(owned, parameters, ct).ConfigureAwait(false);

        stopwatch.Stop();

        var cost = GenerationCost.For(
            ai.Model, outcome.InputTokens, outcome.OutputTokens,
            outcome.CacheReadTokens, outcome.CacheWriteTokens);

        LogGeneration(worktreePath, ai.Model, outcome, cost, stopwatch.ElapsedMilliseconds, digest);

        if (outcome.Refused)
        {
            return new GenerationResult
            {
                Id = id,
                WorktreePath = worktreePath,
                Ok = false,
                Error = "Claude declined to write a message for this change. Write one yourself.",
                Cost = cost,
            };
        }

        var options = GeneratedMessage.ReadAll(outcome.Text);

        if (options.Count == 0)
        {
            return new GenerationResult
            {
                Id = id,
                WorktreePath = worktreePath,
                Ok = false,
                Error = outcome.Truncated
                    ? "The reply was cut off before a message was finished. Try again, or raise maxTokens."
                    : "Claude replied with something that was not a commit message.",
                Cost = cost,
            };
        }

        return new GenerationResult
        {
            Id = id,
            WorktreePath = worktreePath,
            Ok = true,
            Options = options,
            // The narrower question of the two the digest answers. A commit that touches a
            // lockfile is not "too large to send whole", and saying so would train people to
            // ignore the one case where it is true.
            DiffTruncated = digest.WasCutForSize,
            Cost = cost,
            Note = digest.WasCutForSize
                ? "The change was too large to send whole — some files were summarised rather than shown."
                : null,
        };
    }

    // -----------------------------------------------------------------------
    // The request
    // -----------------------------------------------------------------------

    /// <summary>
    /// The system prompt, in two blocks.
    ///
    /// The split is what makes caching work. The first block is the same for every repository
    /// and every call; the second changes only when the repository's conventions or its recent
    /// history change. Marking the end of the second as the cache breakpoint means a
    /// regenerate — the button people press most — re-reads both at a tenth of the input price
    /// instead of paying for them again, and the diff, which is different every time, stays
    /// after the breakpoint where it belongs.
    ///
    /// A prefix shorter than the model's minimum is simply not cached; the API does not
    /// complain, and there is nothing to detect or work around.
    /// </summary>
    private static List<TextBlockParam> BuildSystem(
        CommitMessagePolicy policy, IReadOnlyList<string> recentSubjects)
    {
        const string instructions = """
            You write git commit messages. You are shown the staged diff of one change and you
            describe it, in the voice the repository already uses.

            How to write the subject:
            - Imperative mood, as if completing "this commit will …": "add", never "added" or
              "adds".
            - Say what the change does and, where it is not obvious, what it is for. Never
              restate the diff — "update Parser.cs" tells a reader nothing they could not see.
            - No trailing full stop. No issue numbers unless the repository's own subjects
              carry them.

            How to write the body:
            - Only when there is something to say that the subject cannot hold: why this
              approach, what it replaces, what a reader would otherwise be surprised by.
            - Wrapped at 72 columns, blank line between paragraphs.
            - An empty string is the right answer for a small, self-evident change. Padding a
              trivial commit with three paragraphs is worse than saying nothing.

            What not to do:
            - Do not describe files you were not shown the patch for. Where the diff is marked
              incomplete, describe the change at the level the file list supports and no
              further.
            - Do not invent a motive. If the diff does not say why, the subject says what.
            - Do not mention Claude, this tool, or that the message was generated.
            """;

        var conventions = new StringBuilder();
        conventions.Append("This repository's conventions.\n\n");

        conventions.Append("Subject length: aim for ").Append(policy.SubjectIdeal)
            .Append(" characters, never exceed ").Append(policy.SubjectLimit).Append(".\n");

        if (policy.RequireConventionalCommit)
        {
            conventions.Append("Conventional commits are required here. Choose a type from: ")
                .Append(string.Join(", ", policy.Types)).Append(".\n");
        }
        else
        {
            conventions.Append(
                "Conventional commits are not enforced here. Look at the subjects below: if "
                + "they carry a type prefix, use one and match their types; if they do not, "
                + "leave the type empty rather than introducing a convention this repository "
                + "has not adopted.\n");
        }

        if (recentSubjects.Count > 0)
        {
            conventions.Append("\nThe last ").Append(recentSubjects.Count)
                .Append(" subjects written here, newest first:\n");

            foreach (var subject in recentSubjects) conventions.Append("  ").Append(subject).Append('\n');

            conventions.Append(
                "\nMatch their register, their level of detail, and their capitalisation. They "
                + "are the house style; your message should be indistinguishable from them.\n");
        }

        return
        [
            new TextBlockParam(instructions),
            new TextBlockParam(conventions.ToString())
            {
                // The breakpoint. Everything above it is stable for the session; the diff,
                // which follows in the user message, is not and must stay outside.
                CacheControl = new CacheControlEphemeral(),
            },
        ];
    }

    private static MessageCreateParams BuildRequest(
        AiSettings ai,
        List<TextBlockParam> system,
        DiffDigest digest,
        CommitMessagePolicy policy,
        int count)
    {
        var ask = count <= 1
            ? "Write the commit message for this change."
            : $"Write {count} commit messages for this change — genuinely different framings, "
              + "not rewordings of one another. Best first.";

        var effort = ParseEffort(ai.Effort);

        // Thinking is off at the effort levels this app is built around, and that is what
        // makes the small ceiling below safe. Left unset, a current model thinks adaptively,
        // and thinking counts against `max_tokens` — so 1024 would be spent reasoning about a
        // one-sentence answer and the JSON would arrive cut in half, reported to the user as
        // "the reply was cut off". The comment on Effort has always said thinking buys
        // nothing here; this is the line that makes it true.
        //
        // Above `high` the request is left alone instead: somebody who set `max` effort for a
        // commit message asked for deliberation, and the right response is to give the reply
        // room for it rather than to overrule them.
        var deliberates = effort is Effort.Xhigh or Effort.Max;

        var perMessage = deliberates ? Math.Max(ai.MaxTokens, 4096) : ai.MaxTokens;

        return new MessageCreateParams
        {
            Model = ai.Model,
            // Deliberately small. A commit message is short by definition, and this is one of
            // the few places where a low ceiling is a statement about the task rather than
            // about the budget. Several alternatives need proportionally more room.
            MaxTokens = count <= 1 ? perMessage : perMessage * Math.Min(count, 4),
            Thinking = deliberates ? null : new ThinkingConfigParam(new ThinkingConfigDisabled()),
            System = new MessageCreateParamsSystem(system),
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = $"{ask}\n\n{digest.ToPrompt()}",
                },
            ],
            OutputConfig = new OutputConfig
            {
                // Low is genuinely right here rather than merely cheap: the diff is already in
                // front of the model and the answer is one sentence. Extended thinking buys
                // nothing and delays a button the user is watching — see Thinking above, which
                // is where that is actually enforced.
                Effort = effort,

                // Structured, so conventional-commit conformance is a property of the response
                // rather than something to check for afterwards with a regular expression.
                Format = new JsonOutputFormat { Schema = GeneratedMessage.Schema(policy, count) },
            },
        };
    }

    internal static Effort ParseEffort(string value) =>
        Enum.TryParse<Effort>(value, ignoreCase: true, out var effort) ? effort : Effort.Low;

    // -----------------------------------------------------------------------
    // Talking to the API
    // -----------------------------------------------------------------------

    /// <summary>Whatever came back, in the terms the caller acts on.</summary>
    private sealed record Outcome
    {
        public string Text { get; init; } = "";
        public bool Refused { get; init; }
        public bool Truncated { get; init; }
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public long CacheReadTokens { get; init; }
        public long CacheWriteTokens { get; init; }
    }

    /// <summary>
    /// Streams one message, lifting the subject and body out of the JSON as it arrives.
    ///
    /// The extraction is cosmetic — its only job is that the box fills instead of freezing —
    /// and the complete text is parsed properly once the stream ends. A structured response
    /// arrives either as text deltas or as input-JSON deltas depending on how the model
    /// serves it; both carry fragments of the same JSON, so both go into the same buffer and
    /// the difference stops mattering.
    /// </summary>
    private async Task<Outcome> StreamAsync(
        AnthropicClient client, string id, string worktreePath, MessageCreateParams parameters,
        CancellationToken ct)
    {
        var buffer = new StringBuilder();

        long inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheWrite = 0;
        var refused = false;
        var truncated = false;

        var lastSent = "";
        var lastSentAt = 0L;
        var clock = Stopwatch.StartNew();

        await foreach (var evt in client.Messages.CreateStreaming(parameters, ct).ConfigureAwait(false))
        {
            if (evt.TryPickStart(out var start))
            {
                var usage = start.Message.Usage;
                inputTokens = usage.InputTokens;
                cacheRead = usage.CacheReadInputTokens ?? 0;
                cacheWrite = usage.CacheCreationInputTokens ?? 0;
                continue;
            }

            if (evt.TryPickDelta(out var messageDelta))
            {
                outputTokens = messageDelta.Usage.OutputTokens;

                var stop = messageDelta.Delta.StopReason?.Value();
                refused = stop is StopReason.Refusal;
                truncated = stop is StopReason.MaxTokens;
                continue;
            }

            if (!evt.TryPickContentBlockDelta(out var blockDelta)) continue;

            if (blockDelta.Delta.TryPickText(out var text)) buffer.Append(text.Text);
            else if (blockDelta.Delta.TryPickInputJson(out var json)) buffer.Append(json.PartialJson);
            else continue;

            // Throttled, because a token-by-token event stream would repaint the message box
            // several hundred times for one short message. Fifty milliseconds still reads as
            // continuous typing.
            if (clock.ElapsedMilliseconds - lastSentAt < 50) continue;
            lastSentAt = clock.ElapsedMilliseconds;

            var partial = Partial(buffer.ToString());
            if (partial == lastSent) continue;

            lastSent = partial;
            Report(new GenerationProgress(id, worktreePath, partial));
        }

        return new Outcome
        {
            Text = buffer.ToString(),
            Refused = refused,
            Truncated = truncated,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
        };
    }

    /// <summary>The message as far as it has arrived, assembled from incomplete JSON.</summary>
    private static string Partial(string json)
    {
        var subject = PartialJson.ReadString(json, "subject");
        if (subject is null) return "";

        var type = PartialJson.ReadString(json, "type");
        var scope = PartialJson.ReadString(json, "scope");
        var body = PartialJson.ReadString(json, "body");

        return new GeneratedMessage
        {
            Type = type,
            Scope = scope,
            Subject = subject,
            Body = body ?? "",
        }.Message;
    }

    private static async Task<Outcome> OnceAsync(
        AnthropicClient client, MessageCreateParams parameters, CancellationToken ct)
    {
        var message = await client.Messages.Create(parameters, ct).ConfigureAwait(false);

        var text = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var t)) text.Append(t.Text);
        }

        var stop = message.StopReason?.Value();

        return new Outcome
        {
            Text = text.ToString(),
            Refused = stop is StopReason.Refusal,
            Truncated = stop is StopReason.MaxTokens,
            InputTokens = message.Usage.InputTokens,
            OutputTokens = message.Usage.OutputTokens,
            CacheReadTokens = message.Usage.CacheReadInputTokens ?? 0,
            CacheWriteTokens = message.Usage.CacheCreationInputTokens ?? 0,
        };
    }

    /// <summary>
    /// Measures the request before sending it.
    ///
    /// Falls back to a character estimate when the count cannot be taken — being offline is
    /// about to fail the generation anyway, and a network error raised from the measuring step
    /// would report the wrong cause.
    /// </summary>
    private static async Task<int> CountAsync(
        AnthropicClient client, MessageCreateParams parameters, List<TextBlockParam> system,
        CancellationToken ct)
    {
        try
        {
            var count = await client.Messages.CountTokens(new MessageCountTokensParams
            {
                Model = parameters.Model,
                Messages = parameters.Messages,
                System = new MessageCountTokensParamsSystem(system),
            }, ct).ConfigureAwait(false);

            return (int)Math.Min(int.MaxValue, count.InputTokens);
        }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException or TaskCanceledException)
        {
            var characters = parameters.Messages.Sum(m =>
                m.Content.Value is string text ? text.Length : 0);

            return characters / 4;
        }
    }

    // -----------------------------------------------------------------------
    // Credentials and plumbing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a client for whichever credential is configured, or null when none is.
    ///
    /// The timeout is the app's, not the SDK's default: a commit message that has not arrived
    /// in ninety seconds is not going to, and the user is watching a button.
    /// </summary>
    private AnthropicClient? CreateClient()
    {
        var options = new ClientOptions
        {
            Timeout = TimeSpan.FromSeconds(90),
            MaxRetries = 2,
        };

        var key = _keys.ReadKey();
        if (key is not null) return new AnthropicClient(options with { ApiKey = key });

        var profile = ResolveProfile();
        if (profile is null) return null;

        options = options with { Credentials = profile.Credentials };

        // Both are optional on a resolved profile, and assigning null would overwrite the
        // SDK's own defaults with nothing rather than leaving them alone.
        if (profile.ExtraHeaders is not null) options = options with { ExtraHeaders = profile.ExtraHeaders };
        if (profile.BaseUrl is not null) options = options with { BaseUrl = profile.BaseUrl };

        return new AnthropicClient(options);
    }

    /// <summary>
    /// An <c>ant auth login</c> profile, if there is one.
    ///
    /// The SDK resolves this from its own config files and returns null when
    /// <c>ANTHROPIC_API_KEY</c> should be used instead — so this is only ever reached with
    /// both other sources already exhausted. Resolved once: it reads files and can exchange a
    /// token over the network, neither of which belongs on a UI repaint.
    /// </summary>
    private CredentialResult? ResolveProfile()
    {
        if (_profileResolved) return _profile;
        _profileResolved = true;

        try
        {
            _profile = AnthropicCredentials.Resolve(profile: null, baseUrl: null, httpClient: null);
        }
        catch (Exception ex)
        {
            // No profile, an unreadable config, or no network to exchange a token on. None of
            // those is worth surfacing: the answer to all three is "there is no credential".
            Debug.WriteLine($"Credential profile resolution failed: {ex.Message}");
            _profile = null;
        }

        return _profile;
    }

    /// <summary>
    /// What an amend would be compared against.
    ///
    /// <c>HEAD~1</c>, except for the very first commit on a branch, which has no parent —
    /// there the empty tree stands in, which is how git itself describes a root commit.
    /// </summary>
    private async Task<string> AmendBaseAsync(string worktreePath, CancellationToken ct)
    {
        var parent = await _git
            .TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD^{commit}")
            .ConfigureAwait(false);

        if (!parent.Success) return EmptyTree;

        var grandparent = await _git
            .TryRunAsync(worktreePath, ct, "rev-parse", "--verify", "--quiet", "HEAD~1^{commit}")
            .ConfigureAwait(false);

        return grandparent.Success && grandparent.Trimmed.Length > 0 ? grandparent.Trimmed : EmptyTree;
    }

    /// <summary>Git's hash of the empty tree — the base of every root commit.</summary>
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private void Report(GenerationProgress progress)
    {
        try
        {
            Progress?.Invoke(progress);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Generation progress subscriber failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Records the call in the operation log.
    ///
    /// The log exists to answer "what did this app just do to my repository", and sending the
    /// staged diff to an API is squarely that — arguably more so than a git command, since it
    /// is the only thing the app does that leaves the machine. The command line names the
    /// model and the endpoint; the detail carries what it cost.
    /// </summary>
    private void LogGeneration(
        string worktreePath, string model, Outcome outcome, GenerationCost cost, long elapsedMs,
        DiffDigest digest)
    {
        var sent = digest.Files.Count(f => f.State is not DiffFileState.Summarised);

        var detail = new StringBuilder();
        detail.Append(cost.InputTokens).Append(" in / ").Append(cost.OutputTokens).Append(" out");

        if (cost.CacheReadTokens > 0) detail.Append(", ").Append(cost.CacheReadTokens).Append(" cached");
        if (cost.Usd is { } usd) detail.Append(", $").Append(usd.ToString("0.0000"));

        detail.Append("; sent ").Append(sent).Append(" of ").Append(digest.Files.Count)
            .Append(digest.Files.Count == 1 ? " file's patch" : " files' patches");

        if (outcome.Refused) detail.Append("; declined");

        _log.Append(new OperationLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Operation = "generate message",
            WorktreePath = worktreePath,
            CommandLine = $"anthropic messages.create {model}",
            ExitCode = outcome.Refused ? 1 : 0,
            ElapsedMs = elapsedMs,
            Detail = detail.ToString(),
            Failure = outcome.Refused ? "Refusal" : null,
        });
    }

    private static GenerationResult Failure(string id, string worktreePath, string error) => new()
    {
        Id = id,
        WorktreePath = worktreePath,
        Ok = false,
        Error = error,
    };

    /// <summary>
    /// Turns an exception into one sentence a user can act on.
    ///
    /// Every branch ends the same way in practice — the message box is still there and still
    /// works — so these say what to do rather than what went wrong internally.
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException =>
            "Claude rejected the API key. Check the key Chapter is using.",

        AnthropicForbiddenException =>
            "That key is not permitted to use this model.",

        AnthropicRateLimitException =>
            "Rate limited. Wait a moment and generate again.",

        AnthropicNotFoundException =>
            "That model does not exist. Check the model name in settings.json.",

        AnthropicBadRequestException or AnthropicUnprocessableEntityException =>
            "The API refused the request. The diff may be too large even after trimming.",

        Anthropic5xxException =>
            "The API is having trouble. Try again shortly.",

        AnthropicIOException or HttpRequestException =>
            "Could not reach the API. Write the message yourself, or try again when you are back online.",

        TaskCanceledException or TimeoutException =>
            "The request took too long and was given up on.",

        AnthropicException =>
            $"The API call failed: {ex.Message}",

        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
