using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chapter.Core.Ai.Providers;

/// <summary>A call the endpoint refused, carrying the sentence to show the user.</summary>
public sealed class ProviderException(string message, HttpStatusCode status, string? body = null)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The endpoint's own words, for the operation log.</summary>
    public string? Body { get; } = body;
}

/// <summary>
/// Anything that speaks OpenAI's <c>chat/completions</c>.
///
/// Which is most things: OpenAI itself, Azure OpenAI, Ollama, LM Studio, vLLM, llama.cpp,
/// OpenRouter, Together, Groq, DeepSeek, Mistral. Written by hand against the wire rather
/// than through a vendor SDK, because the target is the *dialect* and an SDK tracks one
/// implementation of it — including, increasingly, that implementation's newer and
/// incompatible endpoints.
///
/// The dialect is not one thing, though, and two fields have no universally safe choice:
///
/// <list type="bullet">
/// <item><c>max_completion_tokens</c> is required by OpenAI's reasoning models and unknown to
/// older compatible servers, which only understand <c>max_tokens</c>;</item>
/// <item><c>response_format: json_schema</c> is supported by OpenAI, vLLM and recent Ollama,
/// and rejected outright by plenty of else.</item>
/// </list>
///
/// So the request goes out fully featured and steps down when told to: a rejection naming a
/// field drops that field and retries, at most twice, and every concession is reported back
/// so it reaches the operation log. Guessing capabilities up front would need a table of
/// every server anybody might run; being told by the server is both shorter and correct. It
/// is also the insurance policy for this file being written against a moving target — a
/// wrong assumption costs one logged retry rather than a feature that does not work.
/// </summary>
public sealed class OpenAiProvider : IMessageProvider
{
    /// <summary>Where OpenAI itself lives, for when no base URL was configured.</summary>
    private const string DefaultBaseUrl = "https://api.openai.com/v1";

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _apiKey;

    public string Id => "openai";

    private OpenAiProvider(HttpClient http, string endpoint, string? apiKey)
    {
        _http = http;
        _endpoint = endpoint;
        _apiKey = apiKey;
    }

    /// <summary>
    /// Builds a client, or null when there is neither a key nor a local endpoint.
    ///
    /// A key is not required when a base URL was given. Ollama and LM Studio are the reason
    /// people ask for an OpenAI-compatible client at all and neither has authentication, so
    /// insisting on a credential would refuse precisely the users this exists for. Nobody
    /// sets a base URL to reach api.openai.com, which makes it a sound signal.
    /// </summary>
    public static OpenAiProvider? TryCreate(string? apiKey, string? baseUrl, HttpMessageHandler? handler = null)
    {
        if (apiKey is null && baseUrl is null) return null;

        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);

        // Generous, because the read is streamed and the ceiling is on the whole exchange. A
        // commit message that has not arrived in ninety seconds is not going to.
        http.Timeout = TimeSpan.FromSeconds(90);

        return new OpenAiProvider(http, Endpoint(baseUrl ?? DefaultBaseUrl), apiKey);
    }

    /// <summary>
    /// Turns whatever the user wrote into a completions URL.
    ///
    /// People paste all of <c>http://localhost:11434</c>, <c>.../v1</c> and the full
    /// <c>.../v1/chat/completions</c>, and all three mean the same thing. Guessing wrong here
    /// produces a 404 that reads like a missing model, so it is worth being liberal.
    /// </summary>
    internal static string Endpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');

        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return trimmed;
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return trimmed + "/chat/completions";

        return trimmed + "/v1/chat/completions";
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Estimated rather than measured. The dialect has no counting endpoint, and asking a
    /// tokeniser from another model family would be worse than an honest over-estimate — the
    /// counts are simply wrong across families, which is the whole reason the Anthropic
    /// provider uses a real one.
    /// </summary>
    public Task<int> CountTokensAsync(ModelRequest request, CancellationToken ct = default) =>
        Task.FromResult(TokenEstimate.For(request));

    public async Task<ModelOutcome> CompleteAsync(
        ModelRequest request, Action<string>? onProgress, CancellationToken ct = default)
    {
        // The three things that might not be understood, each droppable independently.
        var schema = true;
        var modernTokenField = true;
        var usageInStream = true;

        var concessions = new List<string>();

        // At most one downgrade per droppable feature, then the failure is real and is
        // reported. An unbounded ladder would turn a genuinely broken endpoint into a silent
        // series of retries, each one billed.
        for (var attempt = 0; ; attempt++)
        {
            var body = BuildBody(request, onProgress is not null, schema, modernTokenField, usageInStream);

            try
            {
                return await SendAsync(body, onProgress, concessions, ct).ConfigureAwait(false);
            }
            catch (ProviderException ex) when (attempt < 3 && ex.Status == HttpStatusCode.BadRequest)
            {
                var offending = (ex.Body ?? "") + " " + ex.Message;

                if (modernTokenField && offending.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
                {
                    modernTokenField = false;
                    concessions.Add("retried with max_tokens");
                    continue;
                }

                if (schema && MentionsSchema(offending))
                {
                    schema = false;
                    concessions.Add("retried without a response schema");
                    continue;
                }

                // The cheapest thing to lose: without it the cost line reads zero, which is a
                // worse outcome than a hard error only if you would rather have no message.
                if (usageInStream && offending.Contains("stream_options", StringComparison.OrdinalIgnoreCase))
                {
                    usageInStream = false;
                    concessions.Add("retried without usage reporting");
                    continue;
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Whether a rejection is about structured output.
    ///
    /// Matched on several spellings because the servers disagree: OpenAI names the parameter,
    /// Ollama talks about the format, and others simply say it is unsupported.
    /// </summary>
    private static bool MentionsSchema(string message) =>
        message.Contains("response_format", StringComparison.OrdinalIgnoreCase)
        || message.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
        || message.Contains("structured output", StringComparison.OrdinalIgnoreCase);

    // -----------------------------------------------------------------------
    // The request
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the request body.
    ///
    /// The two system halves are joined here rather than kept apart: this dialect has one
    /// system message and no cache breakpoint to place. Where caching exists on these
    /// endpoints it is automatic and prefix-based, so keeping the stable text first — which
    /// the seam already guarantees — is all that can usefully be done for it.
    /// </summary>
    internal static JsonObject BuildBody(
        ModelRequest request, bool stream, bool schema, bool modernTokenField, bool usageInStream = true)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.Instructions + "\n\n" + request.Conventions,
            },
            new JsonObject
            {
                ["role"] = "user",
                // The shape is asked for in the prompt as well as in the schema. When the
                // schema has to be dropped for an endpoint that will not take one, this
                // sentence is the only thing left holding the reply to a parseable form.
                ["content"] = request.UserMessage + "\n\n" + ShapeInstruction(request.Schema),
            },
        };

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = stream,
        };

        // Reasoning on these endpoints cannot be switched off the way Anthropic's can, and it
        // is charged against the same ceiling as the reply — so the ceiling is raised rather
        // than the reasoning suppressed. An allowance that goes unused costs nothing; a reply
        // cut in half costs the feature.
        var maxTokens = Math.Max(request.MaxTokens, 4096);

        if (modernTokenField) body["max_completion_tokens"] = maxTokens;
        else body["max_tokens"] = maxTokens;

        if (schema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.SchemaName,
                    ["strict"] = true,
                    ["schema"] = Strict(request.Schema),
                },
            };
        }

        // Asked for explicitly, because without it a streamed response carries no usage at
        // all and the cost line would read as zero for every generation. Older builds of some
        // servers reject the field outright, which is what the ladder's third rung is for.
        if (stream && usageInStream) body["stream_options"] = new JsonObject { ["include_usage"] = true };

        return body;
    }

    /// <summary>
    /// Restates the schema as a sentence, for the endpoints that will not take one.
    ///
    /// Deliberately terse: it is a fallback, and where the schema *is* accepted this is
    /// redundant text the model has already been told in a stronger form.
    /// </summary>
    private static string ShapeInstruction(IReadOnlyDictionary<string, JsonElement> schema)
    {
        var fields = new List<string>();

        if (schema.TryGetValue("properties", out var properties)
            && properties.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject()) fields.Add(property.Name);
        }

        return fields.Count == 0
            ? "Reply with JSON and nothing else."
            : $"Reply with JSON and nothing else — no prose, no markdown fence — with these "
              + $"fields: {string.Join(", ", fields)}.";
    }

    /// <summary>
    /// Rewrites the schema for OpenAI's strict mode.
    ///
    /// Strict mode is worth having — it is what makes the reply parseable without hoping —
    /// and it costs two mechanical changes: every property has to appear in <c>required</c>,
    /// and a property that is genuinely optional expresses that as a union with null rather
    /// than by being absent from the list. So the fields this app treats as optional arrive
    /// as explicit nulls instead of missing keys, which the reader already tolerates: it
    /// takes a value only when it is a string.
    ///
    /// Applied recursively, because the multi-option schema nests one object inside an array
    /// inside another.
    /// </summary>
    internal static JsonNode Strict(IReadOnlyDictionary<string, JsonElement> schema) =>
        Strict(JsonSerializer.SerializeToNode(schema)!);

    private static JsonNode Strict(JsonNode node)
    {
        if (node is JsonArray array)
        {
            var rewritten = new JsonArray();
            foreach (var item in array) rewritten.Add(item is null ? null : Strict(item));
            return rewritten;
        }

        if (node is not JsonObject obj) return node.DeepClone();

        var result = new JsonObject();
        foreach (var (name, value) in obj)
            result[name] = value is null ? null : Strict(value);

        // Read defensively, and not out of habit: this app's own schema has a *property*
        // called "type" — the conventional-commit one — so inside a `properties` map,
        // `result["type"]` is an object describing that field rather than a node's own type
        // keyword. Asking it for a string throws.
        if (result["type"] is not JsonValue keyword
            || !keyword.TryGetValue<string>(out var kind)
            || kind != "object")
        {
            return result;
        }

        result["additionalProperties"] = false;

        if (result["properties"] is not JsonObject properties) return result;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (result["required"] is JsonArray existing)
        {
            foreach (var entry in existing)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out var name)) required.Add(name);
            }
        }

        var all = new JsonArray();

        foreach (var (name, value) in properties)
        {
            all.Add(name);

            // Everything must be required, so optionality moves into the type as a union with
            // null. A property that was already required is left exactly as it was.
            if (required.Contains(name) || value is not JsonObject property) continue;

            if (property["type"] is JsonValue type && type.TryGetValue<string>(out var single))
                property["type"] = new JsonArray(single, "null");

            // And null has to be allowed by the enum as well, because the two are checked
            // independently: a property whose type says string-or-null and whose enum lists
            // only strings cannot be null after all.
            //
            // This is not a corner. A repository that has *not* opted into conventional
            // commits still carries the default type list, so without this the model would be
            // forced to prefix every subject on exactly the repositories the instructions tell
            // it to leave alone.
            if (property["enum"] is JsonArray values && !values.Any(v => v is null)) values.Add(null);
        }

        result["required"] = all;
        return result;
    }

    // -----------------------------------------------------------------------
    // The wire
    // -----------------------------------------------------------------------

    private async Task<ModelOutcome> SendAsync(
        JsonObject body, Action<string>? onProgress, IReadOnlyList<string> concessions, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // Omitted entirely when there is none, rather than sent empty. A local server that
        // ignores the header is fine either way; one that validates what it is given is not.
        if (_apiKey is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        // ResponseHeadersRead, or the whole reply is buffered before a single token reaches
        // the caller — which turns streaming into a ninety-second wait followed by the entire
        // message at once, and makes the timeout apply to the download rather than the
        // connection.
        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ProviderException(Explain(response.StatusCode, text), response.StatusCode, text);
        }

        return onProgress is null
            ? await ReadOnceAsync(response, concessions, ct).ConfigureAwait(false)
            : await ReadStreamAsync(response, onProgress, concessions, ct).ConfigureAwait(false);
    }

    private static async Task<ModelOutcome> ReadOnceAsync(
        HttpResponseMessage response, IReadOnlyList<string> concessions, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        var content = new StringBuilder();
        var refused = false;
        var truncated = false;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind is JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var reply))
                {
                    if (Text(reply, "content") is { } body) content.Append(body);

                    // Structured outputs report a decline in their own field rather than as a
                    // finish reason, and it is the only place the refusal text appears.
                    if (Text(reply, "refusal") is { Length: > 0 }) refused = true;
                }

                switch (Text(choice, "finish_reason"))
                {
                    case "length": truncated = true; break;
                    case "content_filter": refused = true; break;
                }
            }
        }

        var (input, output, cached) = ReadUsage(root);

        return new ModelOutcome
        {
            Text = content.ToString(),
            Refused = refused,
            Truncated = truncated,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cached,
            Concessions = concessions,
        };
    }

    private static async Task<ModelOutcome> ReadStreamAsync(
        HttpResponseMessage response, Action<string> onProgress, IReadOnlyList<string> concessions,
        CancellationToken ct)
    {
        var buffer = new StringBuilder();
        long input = 0, output = 0, cached = 0;
        var refused = false;
        var truncated = false;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct).ConfigureAwait(false))
        {
            var data = item.Data;

            // The sentinel that ends every stream in this dialect, and not JSON. Parsing it
            // would throw on the last event of every successful call.
            if (data.Length == 0 || data == "[DONE]") continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                // A keep-alive comment or a server improvising. Skipping a chunk costs a few
                // characters of a preview; throwing costs the whole message.
                continue;
            }

            using (document)
            {
                var root = document.RootElement;

                var (chunkInput, chunkOutput, chunkCached) = ReadUsage(root);
                if (chunkInput > 0) input = chunkInput;
                if (chunkOutput > 0) output = chunkOutput;
                if (chunkCached > 0) cached = chunkCached;

                // With include_usage the final chunk carries usage and an *empty* choices
                // array. Indexing into it is the classic way to crash on the last event of an
                // otherwise perfect stream.
                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind is not JsonValueKind.Array) continue;

                var changed = false;

                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (Text(delta, "content") is { Length: > 0 } fragment)
                        {
                            buffer.Append(fragment);
                            changed = true;
                        }

                        if (Text(delta, "refusal") is { Length: > 0 }) refused = true;
                    }

                    switch (Text(choice, "finish_reason"))
                    {
                        case "length": truncated = true; break;
                        case "content_filter": refused = true; break;
                    }
                }

                if (changed) onProgress(buffer.ToString());
            }
        }

        return new ModelOutcome
        {
            Text = buffer.ToString(),
            Refused = refused,
            Truncated = truncated,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cached,
            Concessions = concessions,
        };
    }

    /// <summary>
    /// Reads a usage block. Absent usage is zeros rather than an error — plenty of local
    /// servers do not report any, and a missing cost line is not worth failing a generation
    /// that otherwise worked.
    /// </summary>
    private static (long Input, long Output, long Cached) ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind is not JsonValueKind.Object)
            return (0, 0, 0);

        var input = Number(usage, "prompt_tokens");
        var output = Number(usage, "completion_tokens");

        long cached = 0;
        if (usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind is JsonValueKind.Object)
        {
            cached = Number(details, "cached_tokens");
        }

        // Reported as part of the prompt, so counting it in both places would price the
        // cached half twice — once at full rate and once at the cached one.
        return (Math.Max(0, input - cached), output, cached);
    }

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// One sentence about a rejection, preferring the endpoint's own words.
    ///
    /// These endpoints are run by everybody, and a local server's message about a model that
    /// has not been pulled is far more useful than anything this app could infer from a
    /// status code.
    /// </summary>
    private static string Explain(HttpStatusCode status, string body)
    {
        var detail = ErrorMessage(body);

        var prefix = status switch
        {
            HttpStatusCode.Unauthorized => "The endpoint rejected the API key.",
            HttpStatusCode.Forbidden => "That key is not permitted to use this model.",
            HttpStatusCode.NotFound => "The endpoint or model was not found — check baseUrl and model.",
            HttpStatusCode.TooManyRequests => "Rate limited. Wait a moment and generate again.",
            HttpStatusCode.BadRequest => "The endpoint refused the request.",
            >= HttpStatusCode.InternalServerError => "The endpoint is having trouble. Try again shortly.",
            _ => $"The endpoint returned {(int)status}.",
        };

        return detail is null ? prefix : $"{prefix} {detail}";
    }

    /// <summary>Digs the message out of the several error shapes these servers return.</summary>
    private static string? ErrorMessage(string body)
    {
        if (body.Length == 0) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind is JsonValueKind.String) return error.GetString();
                if (Text(error, "message") is { Length: > 0 } message) return message;
            }

            if (Text(root, "message") is { Length: > 0 } plain) return plain;
        }
        catch (JsonException)
        {
            // Not JSON at all — an HTML error page from a proxy, most likely. A wall of markup
            // in a toast helps nobody.
            return null;
        }

        return null;
    }
}
