using System.Text.Json;

namespace Chapter.Core.Ai.Providers;

/// <summary>
/// How hard the model should work, in this app's terms rather than any vendor's.
///
/// Five levels because that is what the setting has always accepted; each provider maps them
/// onto whatever it actually has, and one that has nothing of the kind ignores them.
/// </summary>
public enum ModelEffort
{
    Low,
    Medium,
    High,
    Xhigh,
    Max,
}

/// <summary>
/// One request for a commit message, in terms no provider owns.
///
/// The two halves of the system prompt stay separate rather than being joined here, and that
/// is the whole point of the seam rather than an accident of it. <see cref="Instructions"/> is
/// identical for every repository and every call; <see cref="Conventions"/> changes only when
/// a repository's rules or its recent history change; <see cref="UserMessage"/> is different
/// every time. A provider with prompt caching puts its breakpoint between the second and the
/// third and makes regeneration nearly free. One without simply concatenates. Joining them
/// here would throw away the fact that makes the first possible.
/// </summary>
public sealed record ModelRequest
{
    public required string Model { get; init; }

    /// <summary>How to write a commit message. The same text for every repository.</summary>
    public required string Instructions { get; init; }

    /// <summary>This repository's rules and its recent subjects. Stable for the session.</summary>
    public required string Conventions { get; init; }

    /// <summary>The ask and the diff. Different every call, and never inside a cached prefix.</summary>
    public required string UserMessage { get; init; }

    /// <summary>The JSON shape the reply must take.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Schema { get; init; }

    /// <summary>A name for the schema. Some providers require one; Anthropic ignores it.</summary>
    public string SchemaName { get; init; } = "commit_message";

    /// <summary>
    /// Ceiling on the reply. A provider whose reasoning is not separately billed may raise
    /// this — an unused allowance costs nothing, and a reply cut in half costs the feature.
    /// </summary>
    public required int MaxTokens { get; init; }

    public ModelEffort Effort { get; init; } = ModelEffort.Low;

    /// <summary>
    /// Whether the user asked for deliberation. False means the provider should suppress
    /// reasoning where it can — the diff is already in front of the model and the answer is
    /// one sentence.
    /// </summary>
    public bool Deliberate { get; init; }
}

/// <summary>Whatever came back, in the terms the generator acts on.</summary>
public sealed record ModelOutcome
{
    /// <summary>The reply's text, which is expected to be the JSON the schema asked for.</summary>
    public string Text { get; init; } = "";

    /// <summary>The model declined. Not a failure of this app, and not retried.</summary>
    public bool Refused { get; init; }

    /// <summary>The reply hit the token ceiling, so the JSON is very likely incomplete.</summary>
    public bool Truncated { get; init; }

    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }

    /// <summary>
    /// Anything the provider had to give up on to make the call work — a field the endpoint
    /// did not recognise, structured output it could not do. Recorded in the operation log
    /// rather than hidden, because a message written without a schema is a different thing
    /// from one written with it.
    /// </summary>
    public IReadOnlyList<string> Concessions { get; init; } = [];
}

/// <summary>
/// Somewhere a commit message can be written.
///
/// Two implementations: the Anthropic API through its own SDK, and the OpenAI-compatible
/// <c>chat/completions</c> dialect, which is what Azure, Ollama, LM Studio, vLLM, OpenRouter,
/// Groq and most of the rest speak. The second is deliberately spelled by hand rather than
/// through a vendor SDK — the target is the dialect, not any one implementation of it.
/// </summary>
public interface IMessageProvider : IDisposable
{
    /// <summary>Stable id, matching the <c>provider</c> setting: "anthropic" or "openai".</summary>
    string Id { get; }

    /// <summary>
    /// How many input tokens the request will cost.
    ///
    /// Never throws. A provider with a counting endpoint uses it; one without estimates, and
    /// so does one that is offline — being unable to measure is about to fail the call
    /// anyway, and reporting a network error from the measuring step names the wrong cause.
    /// </summary>
    Task<int> CountTokensAsync(ModelRequest request, CancellationToken ct = default);

    /// <summary>
    /// Asks for the message, reporting the reply as it arrives.
    /// </summary>
    /// <param name="onProgress">
    /// Called with the text accumulated so far, not with each fragment — a snapshot is
    /// idempotent, and the caller is extracting fields out of half-finished JSON rather than
    /// concatenating. Null asks for no streaming at all, which is what several alternatives
    /// at once wants: three messages arriving a character at a time in three boxes is not
    /// something anybody watches.
    /// </param>
    Task<ModelOutcome> CompleteAsync(
        ModelRequest request, Action<string>? onProgress, CancellationToken ct = default);
}
