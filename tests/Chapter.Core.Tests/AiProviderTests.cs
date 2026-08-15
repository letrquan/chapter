using System.Net;
using System.Text;
using System.Text.Json;
using Chapter.Core.Ai;
using Chapter.Core.Ai.Providers;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// An HTTP handler that answers from a script and remembers what it was asked.
///
/// The OpenAI-compatible provider is spelled by hand against a wire, which makes the wire the
/// risky half — and it is entirely testable without a network. A typo'd field name is silent
/// otherwise: the request goes out, the endpoint ignores what it does not recognise, and the
/// symptom is a worse commit message rather than an error.
/// </summary>
internal sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private int _next;

    /// <summary>Every request body sent, in order, so the ladder's steps can be inspected.</summary>
    public List<string> Bodies { get; } = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_next >= responses.Length)
            throw new InvalidOperationException($"unscripted request #{_next + 1}");

        return responses[_next++];
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>A streamed reply, assembled from the chunks a real endpoint sends.</summary>
    public static HttpResponseMessage Sse(params string[] events)
    {
        var payload = string.Concat(events.Select(e => $"data: {e}\n\n"));

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream"),
        };
    }
}

/// <summary>The OpenAI-compatible wire, driven end to end without a network.</summary>
public class OpenAiProviderTests
{
    private static ModelRequest Request(int optionCount = 1) => new()
    {
        Model = "gpt-test",
        Instructions = "You write git commit messages.",
        Conventions = "This repository's conventions.",
        UserMessage = "Write the commit message for this change.",
        Schema = GeneratedMessage.Schema(new CommitMessagePolicy(), optionCount),
        MaxTokens = 1024,
        Effort = ModelEffort.Low,
    };

    private static OpenAiProvider Create(ScriptedHandler handler, string? key = "sk-test-key", string? baseUrl = null) =>
        OpenAiProvider.TryCreate(key, baseUrl ?? "http://localhost:11434", handler)!;

    // -----------------------------------------------------------------------
    // The URL
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234/v1/chat/completions")]
    [InlineData("https://api.groq.com/openai/v1/", "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("https://x.dev/v1/chat/completions", "https://x.dev/v1/chat/completions")]
    public void Whatever_form_the_base_url_was_pasted_in_reaches_the_same_endpoint(
        string baseUrl, string expected)
    {
        // People paste the host, the /v1, and the full path, and all three mean the same
        // thing. Guessing wrong produces a 404 that reads like a missing model.
        Assert.Equal(expected, OpenAiProvider.Endpoint(baseUrl));
    }

    [Fact]
    public void Neither_a_key_nor_an_endpoint_means_no_provider()
    {
        Assert.Null(OpenAiProvider.TryCreate(null, null));

        // A key alone is api.openai.com; an endpoint alone is a local server with no auth.
        Assert.NotNull(OpenAiProvider.TryCreate("sk-x", null));
        Assert.NotNull(OpenAiProvider.TryCreate(null, "http://localhost:11434"));
    }

    // -----------------------------------------------------------------------
    // The request body
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_request_carries_the_fields_this_dialect_names()
    {
        var handler = new ScriptedHandler(ScriptedHandler.Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"{\"subject\":\"add it\",\"body\":\"\"}"},
             "finish_reason":"stop"}],
             "usage":{"prompt_tokens":100,"completion_tokens":20}}
            """));

        using var provider = Create(handler);
        await provider.CompleteAsync(Request(), onProgress: null);

        var body = JsonDocument.Parse(handler.Bodies[0]).RootElement;

        // Asserted by name, because a misspelled field is accepted and ignored by most of
        // these servers — the symptom is a worse message, not an error.
        Assert.Equal("gpt-test", body.GetProperty("model").GetString());
        Assert.False(body.GetProperty("stream").GetBoolean());

        // The modern field first. The ladder falls back to max_tokens only when told to.
        Assert.True(body.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.TryGetProperty("max_tokens", out _));

        var messages = body.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());

        // The two stable halves are joined for this dialect, which has one system message —
        // but the seam kept them apart so the Anthropic provider could put a cache breakpoint
        // between them.
        var system = messages[0].GetProperty("content").GetString()!;
        Assert.Contains("You write git commit messages.", system);
        Assert.Contains("This repository's conventions.", system);

        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("commit_message", format.GetProperty("json_schema").GetProperty("name").GetString());

        Assert.Equal("Bearer sk-test-key", handler.Requests[0].Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task A_local_endpoint_with_no_key_is_sent_no_authorization_header()
    {
        // Ollama and LM Studio are the reason people ask for this, and neither has auth.
        // Sending an empty bearer token to a server that validates one is worse than sending
        // nothing at all.
        var handler = new ScriptedHandler(ScriptedHandler.Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"{\"subject\":\"x\",\"body\":\"\"}"}}]}
            """));

        using var provider = Create(handler, key: null);
        await provider.CompleteAsync(Request(), onProgress: null);

        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task The_shape_is_restated_in_the_prompt_as_well_as_the_schema()
    {
        // Insurance for the endpoints that will not take a schema: when the ladder drops it,
        // this sentence is the only thing left holding the reply to a parseable form.
        var handler = new ScriptedHandler(ScriptedHandler.Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"{\"subject\":\"x\",\"body\":\"\"}"}}]}
            """));

        using var provider = Create(handler);
        await provider.CompleteAsync(Request(), onProgress: null);

        var user = JsonDocument.Parse(handler.Bodies[0]).RootElement
            .GetProperty("messages")[1].GetProperty("content").GetString()!;

        Assert.Contains("Reply with JSON and nothing else", user);
        Assert.Contains("subject", user);
        Assert.Contains("body", user);
    }

    // -----------------------------------------------------------------------
    // Strict mode
    // -----------------------------------------------------------------------

    [Fact]
    public void Strict_mode_requires_every_property_and_makes_optionality_a_null_union()
    {
        // Strict mode is what makes the reply parseable without hoping, and it costs two
        // mechanical changes. Getting them wrong means the schema is rejected outright, which
        // sends the ladder down a step it never needed to take.
        var strict = OpenAiProvider.Strict(GeneratedMessage.Schema(new CommitMessagePolicy(), 1));
        var schema = JsonDocument.Parse(strict.ToJsonString()).RootElement;

        var required = schema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()!).ToHashSet();

        var properties = schema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).ToHashSet();

        Assert.Equal(properties, required);
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        // subject and body were already required, so their types are untouched.
        Assert.Equal(JsonValueKind.String,
            schema.GetProperty("properties").GetProperty("subject").GetProperty("type").ValueKind);

        // type and scope were optional, so they say so as a union rather than by absence.
        var optional = schema.GetProperty("properties").GetProperty("scope").GetProperty("type");
        Assert.Equal(JsonValueKind.Array, optional.ValueKind);
        Assert.Equal(["string", "null"], optional.EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Strict_mode_reaches_objects_nested_inside_an_array()
    {
        // The multi-option schema nests a message object inside an array inside an object.
        // A transform that only touched the root would be rejected for the inner one.
        var strict = OpenAiProvider.Strict(GeneratedMessage.Schema(new CommitMessagePolicy(), 3));
        var schema = JsonDocument.Parse(strict.ToJsonString()).RootElement;

        var item = schema.GetProperty("properties").GetProperty("options").GetProperty("items");

        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("scope", item.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void A_strict_schema_still_parses_back_into_a_message()
    {
        // The other half of the null-union trade: optional fields now arrive as explicit
        // nulls rather than missing keys, and the reader has to be fine with that.
        var options = GeneratedMessage.ReadAll(
            """{"type":null,"scope":null,"subject":"tidy the parser","body":"","breaking":null}""");

        var only = Assert.Single(options);
        Assert.Equal("tidy the parser", only.Message);
        Assert.Null(only.Type);
    }

    // -----------------------------------------------------------------------
    // Streaming
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_streamed_reply_arrives_in_pieces_and_ends_with_its_usage()
    {
        var handler = new ScriptedHandler(ScriptedHandler.Sse(
            """{"choices":[{"delta":{"content":"{\"subject\":\"add "}}]}""",
            """{"choices":[{"delta":{"content":"the parser\",\"body\":\"\"}"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            // With include_usage the last chunk carries usage and an *empty* choices array.
            // Indexing choices[0] here is the classic way to crash on the final event of an
            // otherwise perfect stream.
            """{"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":18,"prompt_tokens_details":{"cached_tokens":100}}}""",
            // And the sentinel, which is not JSON. Parsing it would throw at the end of every
            // successful call.
            "[DONE]"));

        var snapshots = new List<string>();

        using var provider = Create(handler);
        var outcome = await provider.CompleteAsync(Request(), snapshots.Add);

        Assert.Equal("""{"subject":"add the parser","body":""}""", outcome.Text);
        Assert.False(outcome.Refused);
        Assert.False(outcome.Truncated);

        // Snapshots, not increments — each one is the whole reply so far.
        Assert.Equal(2, snapshots.Count);
        Assert.StartsWith(snapshots[0], snapshots[1], StringComparison.Ordinal);

        // Cached input is reported inside prompt_tokens, so counting it in both places would
        // price the cached half twice — once at full rate and once at the cached one.
        Assert.Equal(20, outcome.InputTokens);
        Assert.Equal(100, outcome.CacheReadTokens);
        Assert.Equal(18, outcome.OutputTokens);

        Assert.True(JsonDocument.Parse(handler.Bodies[0]).RootElement
            .GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task A_stream_that_reports_no_usage_at_all_still_succeeds()
    {
        // Plenty of local servers report none. A missing cost line is not worth failing a
        // generation that otherwise worked.
        var handler = new ScriptedHandler(ScriptedHandler.Sse(
            """{"choices":[{"delta":{"content":"{\"subject\":\"x\",\"body\":\"\"}"}}]}""",
            "[DONE]"));

        using var provider = Create(handler);
        var outcome = await provider.CompleteAsync(Request(), _ => { });

        Assert.Equal(0, outcome.InputTokens);
        Assert.Single(GeneratedMessage.ReadAll(outcome.Text));
    }

    [Fact]
    public async Task Junk_in_the_middle_of_a_stream_costs_a_chunk_rather_than_the_message()
    {
        var handler = new ScriptedHandler(ScriptedHandler.Sse(
            """{"choices":[{"delta":{"content":"{\"subject\":\"x\","}}]}""",
            "not json at all",
            """{"choices":[{"delta":{"content":"\"body\":\"\"}"}}]}""",
            "[DONE]"));

        using var provider = Create(handler);
        var outcome = await provider.CompleteAsync(Request(), _ => { });

        Assert.Equal("""{"subject":"x","body":""}""", outcome.Text);
    }

    [Theory]
    [InlineData("length", true, false)]
    [InlineData("content_filter", false, true)]
    [InlineData("stop", false, false)]
    public async Task Finish_reasons_map_onto_the_two_outcomes_the_ui_branches_on(
        string finishReason, bool truncated, bool refused)
    {
        var handler = new ScriptedHandler(ScriptedHandler.Sse(
            $$"""{"choices":[{"delta":{"content":"{}"},"finish_reason":"{{finishReason}}"}]}""",
            "[DONE]"));

        using var provider = Create(handler);
        var outcome = await provider.CompleteAsync(Request(), _ => { });

        Assert.Equal(truncated, outcome.Truncated);
        Assert.Equal(refused, outcome.Refused);
    }

    [Fact]
    public async Task A_structured_refusal_is_read_from_its_own_field()
    {
        // Structured outputs report a decline in `refusal` rather than as a finish reason,
        // and it is the only place the refusal appears at all.
        var handler = new ScriptedHandler(ScriptedHandler.Json(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":null,"refusal":"I can't help with that."},
             "finish_reason":"stop"}]}
            """));

        using var provider = Create(handler);
        var outcome = await provider.CompleteAsync(Request(), onProgress: null);

        Assert.True(outcome.Refused);
    }

    // -----------------------------------------------------------------------
    // The downgrade ladder
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_endpoint_that_wants_the_older_token_field_is_retried_with_it()
    {
        var handler = new ScriptedHandler(
            ScriptedHandler.Json(HttpStatusCode.BadRequest, """
                {"error":{"message":"Unrecognized request argument supplied: max_completion_tokens",
                 "param":"max_completion_tokens"}}
                """),
            ScriptedHandler.Json(HttpStatusCode.OK, """
                {"choices":[{"message":{"content":"{\"subject\":\"x\",\"body\":\"\"}"}}]}
                """));

        using var provider = Create(handler);
        var outcome = await provider.CompleteAsync(Request(), onProgress: null);

        Assert.Equal(2, handler.Bodies.Count);

        var second = JsonDocument.Parse(handler.Bodies[1]).RootElement;
        Assert.True(second.TryGetProperty("max_tokens", out _));
        Assert.False(second.TryGetProperty("max_completion_tokens", out _));

        // Said out loud rather than swallowed — the operation log is where this ends up.
        Assert.Contains("retried with max_tokens", outcome.Concessions);
    }

    [Fact]
    public async Task An_endpoint_that_cannot_do_schemas_is_retried_without_one()
    {
        var handler = new ScriptedHandler(
            ScriptedHandler.Json(HttpStatusCode.BadRequest,
                """{"error":{"message":"response_format is not supported"}}"""),
            ScriptedHandler.Json(HttpStatusCode.OK, """
                {"choices":[{"message":{"content":"{\"subject\":\"x\",\"body\":\"\"}"}}]}
                """));

        using var provider = Create(handler);
        var outcome = await provider.CompleteAsync(Request(), onProgress: null);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.False(JsonDocument.Parse(handler.Bodies[1]).RootElement
            .TryGetProperty("response_format", out _));

        Assert.Contains("retried without a response schema", outcome.Concessions);

        // The message still parses, because the prompt asked for the shape too.
        Assert.Single(GeneratedMessage.ReadAll(outcome.Text));
    }

    [Fact]
    public async Task The_ladder_takes_each_step_at_most_once_and_then_gives_up()
    {
        // An unbounded ladder would turn a genuinely broken endpoint into a silent series of
        // retries, each one billed.
        var handler = new ScriptedHandler(
            ScriptedHandler.Json(HttpStatusCode.BadRequest,
                """{"error":{"message":"max_completion_tokens is not supported"}}"""),
            ScriptedHandler.Json(HttpStatusCode.BadRequest,
                """{"error":{"message":"response_format is not supported"}}"""),
            ScriptedHandler.Json(HttpStatusCode.BadRequest,
                """{"error":{"message":"and now something else entirely"}}"""));

        using var provider = Create(handler);

        var thrown = await Assert.ThrowsAsync<ProviderException>(
            () => provider.CompleteAsync(Request(), onProgress: null));

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("and now something else entirely", thrown.Message);
    }

    [Fact]
    public async Task A_failure_that_is_not_about_a_field_is_not_retried()
    {
        var handler = new ScriptedHandler(
            ScriptedHandler.Json(HttpStatusCode.Unauthorized,
                """{"error":{"message":"Incorrect API key provided"}}"""));

        using var provider = Create(handler);

        var thrown = await Assert.ThrowsAsync<ProviderException>(
            () => provider.CompleteAsync(Request(), onProgress: null));

        Assert.Single(handler.Bodies);
        Assert.Equal(HttpStatusCode.Unauthorized, thrown.Status);

        // The endpoint's own words, which beat anything this app could infer from a status.
        Assert.Contains("rejected the API key", thrown.Message);
        Assert.Contains("Incorrect API key provided", thrown.Message);
    }

    [Fact]
    public async Task An_html_error_page_from_a_proxy_does_not_become_the_toast()
    {
        var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body><h1>502 Bad Gateway</h1></body></html>"),
        });

        using var provider = Create(handler);

        var thrown = await Assert.ThrowsAsync<ProviderException>(
            () => provider.CompleteAsync(Request(), onProgress: null));

        Assert.DoesNotContain("<html>", thrown.Message);
        Assert.Contains("having trouble", thrown.Message);
    }

    // -----------------------------------------------------------------------
    // Budgeting
    // -----------------------------------------------------------------------

    [Fact]
    public async Task The_token_count_is_estimated_because_this_dialect_cannot_be_asked()
    {
        // No counting endpoint exists in the dialect, and borrowing a tokeniser from another
        // model family would be worse than an honest over-estimate — the counts are simply
        // wrong across families.
        using var provider = Create(new ScriptedHandler());

        var request = Request();
        var counted = await provider.CountTokensAsync(request);

        var characters = request.Instructions.Length + request.Conventions.Length
                         + request.UserMessage.Length;

        Assert.Equal(characters / 4, counted);
    }
}
