using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Anthropic.Credentials;
using Anthropic.Exceptions;
using Chapter.Core.Ai.Providers;
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

    /// <summary>"anthropic" or "openai" — which dialect is being spoken.</summary>
    public required string Provider { get; init; }

    /// <summary>Where an OpenAI-compatible provider is pointed, when it is not the default.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The environment variable this provider reads, so the key prompt names the right one.</summary>
    public required string EnvironmentVariable { get; init; }

    public required string Source { get; init; }
    public string? Hint { get; init; }
    public required string Model { get; init; }
    public required string Effort { get; init; }

    /// <summary>
    /// How many alternatives the "options" button should ask for, so it can label itself with
    /// the number it is actually going to request rather than a hardcoded one.
    /// </summary>
    public required int OptionCount { get; init; }
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
/// Writes commit messages with a model.
///
/// Everything here is arranged around one fact: this is the first thing the app does that
/// leaves the machine. So it says what it sent (the operation log records every call), it
/// says what it cost, it says when it could not see the whole diff, and it never becomes the
/// reason a commit cannot happen — every failure path ends with the message box exactly as
/// usable as it was before the button was pressed.
///
/// Which model is somebody else's business. This class assembles a
/// <see cref="ModelRequest"/> and reads a <see cref="ModelOutcome"/>; a
/// <see cref="IMessageProvider"/> knows the wire. Two exist — Anthropic's own API, and the
/// OpenAI-compatible dialect that Azure, Ollama, LM Studio, vLLM, OpenRouter and most of the
/// rest speak — and nothing above the seam is written twice for them.
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

    /// <summary>
    /// Overridable so tests can drive the OpenAI-compatible wire without a network. Null uses
    /// an ordinary handler, which is every real run.
    /// </summary>
    internal Func<HttpMessageHandler>? HttpHandlerFactory { get; set; }

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

    // -----------------------------------------------------------------------
    // Availability
    // -----------------------------------------------------------------------

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
        var provider = NormaliseProvider(ai.Provider);
        var variable = ApiKeyStore.EnvironmentVariableFor(provider);

        // Answered before anything is read. A feature that is switched off has no business
        // opening credential files or exchanging a token to find out how switched off it is.
        if (!ai.Enabled)
        {
            return new AiAvailability
            {
                Available = false,
                Reason = "Message generation is switched off in settings.json.",
                Provider = provider,
                BaseUrl = Blank(ai.BaseUrl),
                EnvironmentVariable = variable,
                Source = "none",
                Model = ai.Model,
                Effort = ai.Effort,
                OptionCount = OptionsFor(ai),
            };
        }

        var state = _keys.Read(provider);

        // A login profile is Anthropic's own, and resolving one costs a file read and possibly
        // a token exchange. Asking for it while pointed at an OpenAI-compatible endpoint would
        // report the feature available on a credential the request cannot use.
        if (!state.HasKey && provider == "anthropic" && ResolveProfile() is not null)
            state = new ApiKeyState(ApiKeySource.Profile, null);

        var source = state.Source switch
        {
            ApiKeySource.Stored => "stored",
            ApiKeySource.Environment => "environment",
            ApiKeySource.Profile => "profile",
            _ => "none",
        };

        // The case this whole provider split exists for. Ollama and LM Studio are the reason
        // people ask for an OpenAI-compatible client, and neither has authentication at all —
        // so demanding a key from somebody who pointed the app at localhost would refuse
        // exactly the users the feature was widened for. A base URL is the signal: nobody sets
        // one to reach api.openai.com.
        var local = provider == "openai" && Blank(ai.BaseUrl) is not null;

        var available = state.HasKey || local;

        return new AiAvailability
        {
            Available = available,
            NeedsKey = !available,
            Reason = available
                ? null
                : $"Chapter has no API key for {Label(provider)}. Add one to write commit messages here.",
            Provider = provider,
            BaseUrl = Blank(ai.BaseUrl),
            EnvironmentVariable = variable,
            Source = local && !state.HasKey ? "none" : source,
            Hint = state.Hint,
            Model = ai.Model,
            Effort = ai.Effort,
            OptionCount = OptionsFor(ai),
        };
    }

    /// <summary>
    /// How many alternatives to ask for, clamped to what the bridge will accept.
    ///
    /// Clamped here rather than only at the call site so the number the button labels itself
    /// with is the number that will actually be requested — a setting of 9 that silently
    /// becomes 5 is worse than one that says 5.
    /// </summary>
    internal static int OptionsFor(AiSettings ai) => Math.Clamp(ai.OptionCount, 2, 5);

    /// <summary>Unknown values fall back rather than failing — settings.json is hand-edited.</summary>
    internal static string NormaliseProvider(string? provider) =>
        string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase) ? "openai" : "anthropic";

    private static string Label(string provider) => provider == "openai" ? "this OpenAI-compatible endpoint" : "Claude";

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // -----------------------------------------------------------------------
    // Running one
    // -----------------------------------------------------------------------

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
    public bool Cancel(string id) =>
        _running.TryGetValue(id, out var cancellation) && TryCancel(cancellation);

    /// <summary>
    /// Stops every generation in flight, whichever worktree it belongs to.
    ///
    /// Called from the dispatcher's <c>Dispose</c>, so it runs during window teardown — which
    /// is exactly where an exception is least welcome and hardest to attribute.
    /// </summary>
    public void CancelAll()
    {
        foreach (var cancellation in _running.Values) TryCancel(cancellation);
    }

    /// <summary>
    /// Cancels a token source that may already have been disposed.
    ///
    /// The race is small and real: <see cref="Begin"/> removes and disposes the source in its
    /// finally block, and a cancel that read it from the dictionary a moment earlier then
    /// calls <c>Cancel</c> on a disposed object, which throws. Both callers are asking for
    /// something that has already happened, so the answer is "yes, it is stopped" rather than
    /// an exception — one of them is the front-end pressing Stop as the reply lands, and the
    /// other is a window closing.
    /// </summary>
    private static bool TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
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

        using var provider = CreateProvider();
        if (provider is null)
            return Failure(id, worktreePath, Describe().Reason ?? "No credential is configured.");

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
        // anything comes from the provider below.
        var digest = await DiffDigestBuilder
            .ReadAsync(_git, worktreePath, ai.InputTokenBudget * 4, baseRef, ct: ct)
            .ConfigureAwait(false);

        if (digest.IsEmpty)
        {
            return Failure(id, worktreePath, amend
                ? "That commit has no changes to describe."
                : "Nothing is staged, so there is nothing to describe.");
        }

        var request = BuildRequest(ai, policy, recent, digest, count);

        // Measured by whoever is about to be sent to, never estimated where it can be asked.
        // A tokeniser borrowed from another model family is wrong for this one by enough to
        // matter, and the point of the budget is to be right about the request that is
        // actually going out.
        var counted = await provider.CountTokensAsync(request, ct).ConfigureAwait(false);

        if (counted > ai.InputTokenBudget)
        {
            // One retry, scaled by how far over it went. Bisecting would cost several more
            // round trips to save tokens that are already inside the budget's own slack.
            var scaled = (int)(ai.InputTokenBudget * 4L * ai.InputTokenBudget / Math.Max(1, counted));

            digest = await DiffDigestBuilder
                .ReadAsync(_git, worktreePath, scaled, baseRef, ct: ct)
                .ConfigureAwait(false);

            request = BuildRequest(ai, policy, recent, digest, count);
        }

        // Streaming only for a single message. Three arriving a character at a time in three
        // boxes is not something anybody watches.
        var onProgress = count <= 1 ? Throttled(id, worktreePath) : null;

        var outcome = await provider.CompleteAsync(request, onProgress, ct).ConfigureAwait(false);

        stopwatch.Stop();

        var cost = GenerationCost.For(
            ai.Model, outcome.InputTokens, outcome.OutputTokens,
            outcome.CacheReadTokens, outcome.CacheWriteTokens);

        LogGeneration(worktreePath, provider, ai.Model, outcome, cost, stopwatch.ElapsedMilliseconds, digest);

        if (outcome.Refused)
        {
            return new GenerationResult
            {
                Id = id,
                WorktreePath = worktreePath,
                Ok = false,
                Error = "The model declined to write a message for this change. Write one yourself.",
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
                    : "The model replied with something that was not a commit message.",
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

    /// <summary>
    /// Wraps the progress callback so the UI is not repainted per token.
    ///
    /// A token-by-token event stream would repaint the message box several hundred times for
    /// one short message. Fifty milliseconds still reads as continuous typing, and identical
    /// snapshots are dropped as well — most fragments land inside JSON punctuation and change
    /// nothing a user can see.
    /// </summary>
    private Action<string> Throttled(string id, string worktreePath)
    {
        var clock = Stopwatch.StartNew();
        var lastSentAt = 0L;
        var lastSent = "";

        return accumulated =>
        {
            if (clock.ElapsedMilliseconds - lastSentAt < 50) return;
            lastSentAt = clock.ElapsedMilliseconds;

            var partial = Partial(accumulated);
            if (partial == lastSent) return;

            lastSent = partial;
            Report(new GenerationProgress(id, worktreePath, partial));
        };
    }

    /// <summary>The message as far as it has arrived, assembled from incomplete JSON.</summary>
    private static string Partial(string json)
    {
        var subject = PartialJson.ReadString(json, "subject");
        if (subject is null) return "";

        return new GeneratedMessage
        {
            Type = PartialJson.ReadString(json, "type"),
            Scope = PartialJson.ReadString(json, "scope"),
            Subject = subject,
            Body = PartialJson.ReadString(json, "body") ?? "",
        }.Message;
    }

    // -----------------------------------------------------------------------
    // The request
    // -----------------------------------------------------------------------

    private static ModelRequest BuildRequest(
        AiSettings ai,
        CommitMessagePolicy policy,
        IReadOnlyList<string> recentSubjects,
        DiffDigest digest,
        int count)
    {
        var ask = count <= 1
            ? "Write the commit message for this change."
            : $"Write {count} commit messages for this change — genuinely different framings, "
              + "not rewordings of one another. Best first.";

        var effort = ParseEffort(ai.Effort);

        // Deliberation is the user's to ask for. Below these two levels the provider is told
        // to suppress reasoning where it can: the diff is already in front of the model and
        // the answer is one sentence.
        var deliberate = effort is ModelEffort.Xhigh or ModelEffort.Max;

        var perMessage = deliberate ? Math.Max(ai.MaxTokens, 4096) : ai.MaxTokens;

        return new ModelRequest
        {
            Model = ai.Model,
            Instructions = Instructions,
            Conventions = BuildConventions(policy, recentSubjects),
            UserMessage = $"{ask}\n\n{digest.ToPrompt()}",
            Schema = GeneratedMessage.Schema(policy, count),
            // Deliberately small. A commit message is short by definition, and this is one of
            // the few places where a low ceiling is a statement about the task rather than
            // about the budget. Several alternatives need proportionally more room.
            MaxTokens = count <= 1 ? perMessage : perMessage * Math.Min(count, 4),
            Effort = effort,
            Deliberate = deliberate,
        };
    }

    internal static ModelEffort ParseEffort(string value) =>
        Enum.TryParse<ModelEffort>(value, ignoreCase: true, out var effort) ? effort : ModelEffort.Low;

    /// <summary>
    /// How to write a commit message. Identical for every repository and every call, which is
    /// what lets a provider with prompt caching keep it in a cached prefix.
    /// </summary>
    private const string Instructions = """
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
        - Do not mention the model, this tool, or that the message was generated.
        """;

    /// <summary>
    /// This repository's rules and its recent subjects. Stable for as long as the repository
    /// is, which is what makes regenerating nearly free where caching exists.
    /// </summary>
    private static string BuildConventions(CommitMessagePolicy policy, IReadOnlyList<string> recentSubjects)
    {
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

        return conventions.ToString();
    }

    // -----------------------------------------------------------------------
    // Credentials and plumbing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the provider the settings ask for, or null when nothing can authenticate.
    ///
    /// One per generation rather than one per app, so a key stored or a provider switched
    /// while the window is open takes effect on the next press rather than the next restart.
    /// </summary>
    private IMessageProvider? CreateProvider()
    {
        var ai = _settings.Ai;
        var provider = NormaliseProvider(ai.Provider);
        var key = _keys.ReadKey(provider);

        if (provider == "openai")
        {
            return OpenAiProvider.TryCreate(
                key, Blank(ai.BaseUrl), HttpHandlerFactory?.Invoke());
        }

        return AnthropicProvider.TryCreate(key, key is null ? ResolveProfile() : null);
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
    /// provider and the model; the detail carries what it cost, and anything the provider had
    /// to give up on to get an answer at all.
    /// </summary>
    private void LogGeneration(
        string worktreePath, IMessageProvider provider, string model, ModelOutcome outcome,
        GenerationCost cost, long elapsedMs, DiffDigest digest)
    {
        var sent = digest.Files.Count(f => f.State is not DiffFileState.Summarised);

        var detail = new StringBuilder();
        detail.Append(cost.InputTokens).Append(" in / ").Append(cost.OutputTokens).Append(" out");

        if (cost.CacheReadTokens > 0) detail.Append(", ").Append(cost.CacheReadTokens).Append(" cached");
        if (cost.Usd is { } usd) detail.Append(", $").Append(usd.ToString("0.0000"));

        detail.Append("; sent ").Append(sent).Append(" of ").Append(digest.Files.Count)
            .Append(digest.Files.Count == 1 ? " file's patch" : " files' patches");

        // Said out loud rather than swallowed. A message written without a schema, because the
        // endpoint would not take one, is a different thing from one written with it — and the
        // only place that difference is recoverable afterwards is here.
        foreach (var concession in outcome.Concessions) detail.Append("; ").Append(concession);

        if (outcome.Refused) detail.Append("; declined");

        _log.Append(new OperationLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Operation = "generate message",
            WorktreePath = worktreePath,
            CommandLine = $"{provider.Id} {model}",
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
    /// works — so these say what to do rather than what went wrong internally. The two
    /// providers raise different exception types for the same handful of situations, so both
    /// families are mapped onto the same sentences.
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

        AnthropicIOException =>
            "Could not reach the API. Write the message yourself, or try again when you are back online.",

        // The OpenAI-compatible provider's own, which carries the status and the endpoint's
        // own words — those are more useful than anything this app could infer.
        ProviderException provider => provider.Message,

        HttpRequestException =>
            "Could not reach the endpoint. Write the message yourself, or try again when you are back online.",

        TaskCanceledException or TimeoutException =>
            "The request took too long and was given up on.",

        AnthropicException =>
            $"The API call failed: {ex.Message}",

        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
