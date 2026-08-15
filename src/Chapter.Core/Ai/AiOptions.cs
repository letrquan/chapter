namespace Chapter.Core.Ai;

/// <summary>
/// How commit messages get generated, when the user asks for one.
///
/// Every default here is a choice the roadmap argues for rather than a shrug. The model is
/// the good one, not the cheap one — picking cheap on somebody's behalf is deciding how much
/// their commit messages are worth — and the effort is <c>low</c>, because writing one short
/// message from a diff that is already in front of the model is exactly the shape of task
/// that gains nothing from thinking longer.
///
/// No key-shaped field lives here on purpose: this class is serialised into
/// <c>settings.json</c>, which is plaintext. The key lives in <see cref="ApiKeyStore"/>.
/// </summary>
public sealed class AiSettings
{
    /// <summary>Turns the feature off entirely, credential or no credential.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Model id, passed to the API verbatim. A string rather than an enum because the list
    /// moves faster than this app ships, and a user who wants a model released last week
    /// should not need a new build.
    /// </summary>
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>"low", "medium", "high", "xhigh" or "max". Anything else falls back to low.</summary>
    public string Effort { get; set; } = "low";

    /// <summary>
    /// Ceiling on the generated message. A commit message is short by definition, which
    /// makes this one of the few places where going well below the usual default is right
    /// rather than merely frugal.
    /// </summary>
    public int MaxTokens { get; set; } = 1024;

    /// <summary>How many alternatives "give me options" asks for.</summary>
    public int OptionCount { get; set; } = 3;

    /// <summary>
    /// Ceiling on the whole request, measured with the API's own counter rather than guessed
    /// at, and enforced by cutting the diff until it fits.
    ///
    /// Exists because a single staged file can be +14,000 lines, and "send the diff" then
    /// blows both the context and the bill. Generous enough that an ordinary commit is never
    /// truncated, small enough that a lockfile refresh does not cost more than the work it
    /// describes.
    /// </summary>
    public int InputTokenBudget { get; set; } = 24_000;
}

/// <summary>
/// What a model costs, per million tokens.
///
/// Cache rates are derived rather than listed because they are fixed multiples of the input
/// price — writing them out per model would be four numbers to get wrong instead of two.
/// </summary>
public sealed record ModelPrice(decimal InputPerMTok, decimal OutputPerMTok)
{
    /// <summary>Writing to the cache costs a quarter more than ordinary input.</summary>
    public decimal CacheWritePerMTok => InputPerMTok * 1.25m;

    /// <summary>Reading from it costs a tenth — the reason regenerating is nearly free.</summary>
    public decimal CacheReadPerMTok => InputPerMTok * 0.1m;
}

/// <summary>What one generation used, and what it cost.</summary>
public sealed record GenerationCost
{
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }

    /// <summary>
    /// Null when the model is not in the price table — which is a real state, since
    /// <c>settings.json</c> is hand-edited and the model list moves. Tokens are still
    /// reported; only the dollars are withheld, because an invented price is worse than
    /// no price.
    /// </summary>
    public decimal? Usd { get; init; }

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    public static GenerationCost For(
        string model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens)
    {
        var price = ModelPrices.For(model);

        return new GenerationCost
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            Usd = price is null
                ? null
                : (inputTokens * price.InputPerMTok
                   + outputTokens * price.OutputPerMTok
                   + cacheReadTokens * price.CacheReadPerMTok
                   + cacheWriteTokens * price.CacheWritePerMTok) / 1_000_000m,
        };
    }
}

/// <summary>
/// List prices, so the app can answer "is this feature cheap or quietly expensive".
///
/// Kept small and matched by prefix, so a dated snapshot of a model — <c>claude-haiku-4-5-20251001</c>
/// — prices as the model it is. An unrecognised id returns null rather than a guess: the
/// numbers here are a convenience, and a wrong one presented confidently would be worse than
/// the honest omission.
/// </summary>
public static class ModelPrices
{
    private static readonly (string Prefix, ModelPrice Price)[] Table =
    [
        ("claude-opus-5", new ModelPrice(5m, 25m)),
        ("claude-sonnet-5", new ModelPrice(3m, 15m)),
        ("claude-opus-4-5", new ModelPrice(5m, 25m)),
        ("claude-sonnet-4-5", new ModelPrice(3m, 15m)),
        ("claude-haiku-4-5", new ModelPrice(1m, 5m)),
    ];

    /// <summary>Longest matching prefix wins, so a more specific entry is never shadowed.</summary>
    public static ModelPrice? For(string model)
    {
        ModelPrice? best = null;
        var bestLength = 0;

        foreach (var (prefix, price) in Table)
        {
            if (prefix.Length <= bestLength) continue;
            if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            best = price;
            bestLength = prefix.Length;
        }

        return best;
    }
}
