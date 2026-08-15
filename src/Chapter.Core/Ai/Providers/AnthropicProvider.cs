using System.Text;
using Anthropic;
using Anthropic.Core;
using Anthropic.Credentials;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Chapter.Core.Ai.Providers;

/// <summary>
/// The Anthropic API, through its own SDK.
///
/// The one provider that can do everything this app asks for natively: a token counter that
/// is right rather than estimated, an explicit cache breakpoint, and thinking that can be
/// turned off — which is what makes the deliberately small token ceiling safe.
/// </summary>
public sealed class AnthropicProvider : IMessageProvider
{
    private readonly AnthropicClient _client;

    public string Id => "anthropic";

    private AnthropicProvider(AnthropicClient client) => _client = client;

    /// <summary>
    /// Builds a client for whichever credential is configured, or null when none is.
    ///
    /// The timeout is the app's, not the SDK's default: a commit message that has not arrived
    /// in ninety seconds is not going to, and the user is watching a button.
    /// </summary>
    public static AnthropicProvider? TryCreate(string? apiKey, CredentialResult? profile)
    {
        var options = new ClientOptions
        {
            Timeout = TimeSpan.FromSeconds(90),
            MaxRetries = 2,
        };

        if (apiKey is not null) return new AnthropicProvider(new AnthropicClient(options with { ApiKey = apiKey }));

        if (profile is null) return null;

        options = options with { Credentials = profile.Credentials };

        // Both are optional on a resolved profile, and assigning null would overwrite the
        // SDK's own defaults with nothing rather than leaving them alone.
        if (profile.ExtraHeaders is not null) options = options with { ExtraHeaders = profile.ExtraHeaders };
        if (profile.BaseUrl is not null) options = options with { BaseUrl = profile.BaseUrl };

        return new AnthropicProvider(new AnthropicClient(options));
    }

    public void Dispose() => _client.Dispose();

    public async Task<int> CountTokensAsync(ModelRequest request, CancellationToken ct = default)
    {
        try
        {
            var count = await _client.Messages.CountTokens(new MessageCountTokensParams
            {
                Model = request.Model,
                Messages = [new() { Role = Role.User, Content = request.UserMessage }],
                System = new MessageCountTokensParamsSystem(SystemBlocks(request)),
            }, ct).ConfigureAwait(false);

            return (int)Math.Min(int.MaxValue, count.InputTokens);
        }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException or TaskCanceledException)
        {
            return TokenEstimate.For(request);
        }
    }

    public async Task<ModelOutcome> CompleteAsync(
        ModelRequest request, Action<string>? onProgress, CancellationToken ct = default)
    {
        var parameters = Build(request);

        return onProgress is null
            ? await OnceAsync(parameters, ct).ConfigureAwait(false)
            : await StreamAsync(parameters, onProgress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The system prompt, in two blocks with the cache breakpoint between the second and the
    /// user message.
    ///
    /// The split is what makes caching work. The first block is the same for every repository
    /// and every call; the second changes only when the repository's conventions or its recent
    /// history change. Marking the end of the second means a regenerate — the button people
    /// press most — re-reads both at a tenth of the input price instead of paying for them
    /// again, and the diff, which is different every time, stays after the breakpoint.
    ///
    /// A prefix shorter than the model's minimum is simply not cached; the API does not
    /// complain, and there is nothing to detect or work around.
    /// </summary>
    private static List<TextBlockParam> SystemBlocks(ModelRequest request) =>
    [
        new(request.Instructions),
        new(request.Conventions) { CacheControl = new CacheControlEphemeral() },
    ];

    private static MessageCreateParams Build(ModelRequest request)
    {
        // Thinking is off unless deliberation was asked for, and that is what makes the small
        // ceiling safe. Left unset, a current model thinks adaptively and thinking counts
        // against max_tokens — so 1024 would be spent reasoning about a one-sentence answer
        // and the JSON would arrive cut in half.
        //
        // When it is asked for, the request is left alone and given room instead: somebody who
        // set `max` effort for a commit message wanted deliberation, and the right response is
        // to make space for it rather than to overrule them.
        return new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.Deliberate ? Math.Max(request.MaxTokens, 4096) : request.MaxTokens,
            Thinking = request.Deliberate ? null : new ThinkingConfigParam(new ThinkingConfigDisabled()),
            System = new MessageCreateParamsSystem(SystemBlocks(request)),
            Messages = [new() { Role = Role.User, Content = request.UserMessage }],
            OutputConfig = new OutputConfig
            {
                Effort = Map(request.Effort),

                // Structured, so conventional-commit conformance is a property of the response
                // rather than something checked afterwards with a regular expression.
                Format = new JsonOutputFormat { Schema = request.Schema },
            },
        };
    }

    internal static Effort Map(ModelEffort effort) => effort switch
    {
        ModelEffort.Medium => Effort.Medium,
        ModelEffort.High => Effort.High,
        ModelEffort.Xhigh => Effort.Xhigh,
        ModelEffort.Max => Effort.Max,
        _ => Effort.Low,
    };

    /// <summary>
    /// Streams one message.
    ///
    /// A structured response arrives either as text deltas or as input-JSON deltas depending
    /// on how the model serves it; both carry fragments of the same JSON, so both go into the
    /// same buffer and the difference stops mattering.
    /// </summary>
    private async Task<ModelOutcome> StreamAsync(
        MessageCreateParams parameters, Action<string> onProgress, CancellationToken ct)
    {
        var buffer = new StringBuilder();

        long inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheWrite = 0;
        var refused = false;
        var truncated = false;

        await foreach (var evt in _client.Messages.CreateStreaming(parameters, ct).ConfigureAwait(false))
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

            onProgress(buffer.ToString());
        }

        return new ModelOutcome
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

    private async Task<ModelOutcome> OnceAsync(MessageCreateParams parameters, CancellationToken ct)
    {
        var message = await _client.Messages.Create(parameters, ct).ConfigureAwait(false);

        var text = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var t)) text.Append(t.Text);
        }

        var stop = message.StopReason?.Value();

        return new ModelOutcome
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
}

/// <summary>
/// A local guess at a request's size, for providers that cannot be asked and for the moments
/// when the one that can is unreachable.
///
/// Four characters to the token is a deliberate over-estimate for source code, which
/// tokenises worse than prose. Being wrong high means the budget trims a little more than it
/// had to; being wrong low means the request is refused by the API for length, which is the
/// expensive direction.
/// </summary>
internal static class TokenEstimate
{
    public static int For(ModelRequest request) =>
        (request.Instructions.Length + request.Conventions.Length + request.UserMessage.Length) / 4;
}
