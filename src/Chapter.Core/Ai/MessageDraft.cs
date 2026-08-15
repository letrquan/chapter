using System.Text;
using System.Text.Json;
using Chapter.Core.Git;

namespace Chapter.Core.Ai;

/// <summary>
/// A commit message the model wrote, in parts rather than as prose.
///
/// The parts are the point. Asking for <c>type</c>, <c>scope</c>, <c>subject</c> and
/// <c>body</c> separately — through the API's structured output rather than by parsing what
/// comes back — is what makes conventional-commit conformance mechanical instead of a
/// regular expression run over a sentence that may or may not have been written in the shape
/// it was asked for.
/// </summary>
public sealed record GeneratedMessage
{
    /// <summary>Conventional-commit type, or null where the repository does not use them.</summary>
    public string? Type { get; init; }

    public string? Scope { get; init; }
    public required string Subject { get; init; }
    public string Body { get; init; } = "";

    /// <summary>True when the model judged the change to break something.</summary>
    public bool IsBreaking { get; init; }

    public bool IsEmpty => Subject.Trim().Length == 0;

    /// <summary>
    /// The message as git will store it: subject, blank line, body.
    ///
    /// The type and scope are re-attached here rather than having been asked for inside the
    /// subject, so the two can never disagree — a model that returns
    /// <c>type: "feat"</c> alongside <c>subject: "fix: the parser"</c> yields one prefix, not
    /// two.
    ///
    /// A computed property rather than a method so it crosses the bridge with the rest of the
    /// record, the way <see cref="ChangedFile.FileName"/> does: the front-end wants the text,
    /// not the parts, and assembling it twice is how the two sides drift.
    /// </summary>
    public string Message
    {
        get
        {
            var subject = Subject.Trim();
            if (subject.Length == 0) return "";

            if (!string.IsNullOrWhiteSpace(Type))
            {
                var scope = string.IsNullOrWhiteSpace(Scope) ? "" : $"({Scope!.Trim()})";
                subject = $"{Type!.Trim()}{scope}{(IsBreaking ? "!" : "")}: {subject}";
            }

            var body = Body.Replace("\r\n", "\n").Trim('\n', ' ', '\t');

            return body.Length == 0 ? subject : $"{subject}\n\n{body}";
        }
    }

    /// <summary>
    /// Reads one message out of the model's JSON. Returns null when the payload is not an
    /// object with a subject, which is the shape a refusal or a truncated response takes.
    /// </summary>
    public static GeneratedMessage? FromElement(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Object) return null;

        var subject = Text(element, "subject");
        if (subject is null || subject.Trim().Length == 0) return null;

        return new GeneratedMessage
        {
            Type = Text(element, "type"),
            Scope = Text(element, "scope"),
            Subject = subject,
            Body = Text(element, "body") ?? "",
            IsBreaking = element.TryGetProperty("breaking", out var breaking)
                         && breaking.ValueKind is JsonValueKind.True,
        };
    }

    /// <summary>
    /// Reads every message out of a response, whether it came back as one object or as an
    /// <c>options</c> array. Both shapes are asked for by this app, and a model that answers
    /// the singular question with an array of one is not worth failing over.
    /// </summary>
    public static IReadOnlyList<GeneratedMessage> ReadAll(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind is JsonValueKind.Object
                && root.TryGetProperty("options", out var options)
                && options.ValueKind is JsonValueKind.Array)
            {
                return [.. options.EnumerateArray()
                    .Select(FromElement)
                    .Where(m => m is not null)
                    .Select(m => m!)];
            }

            if (root.ValueKind is JsonValueKind.Array)
            {
                return [.. root.EnumerateArray()
                    .Select(FromElement)
                    .Where(m => m is not null)
                    .Select(m => m!)];
            }

            var single = FromElement(root);
            return single is null ? [] : [single];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The schema the model is held to.
    ///
    /// Built rather than written out as a constant because it depends on the repository:
    /// where <see cref="CommitMessagePolicy.RequireConventionalCommit"/> is set, <c>type</c>
    /// becomes required and is restricted to that repository's own list, so the format is
    /// enforced by the API rather than checked afterwards and complained about. That is also
    /// why the SDK's own attribute-driven schema inference is not used here — it describes a
    /// fixed C# type, and this shape changes per repository.
    ///
    /// Two rules of the structured-output dialect have to be honoured by hand as a result,
    /// and both are load-bearing rather than stylistic:
    ///
    /// <list type="bullet">
    /// <item><c>additionalProperties: false</c> on <b>every</b> object, nested ones included.
    /// The API rejects the request without it, and the rejection reads as a bad request —
    /// which the UI would report as the diff being too large, sending the user off to shrink
    /// a diff that was never the problem.</item>
    /// <item>No <c>minItems</c> above 1, and no <c>maxItems</c>: array-size constraints are
    /// not part of the dialect. The count has to be asked for in prose instead, which is why
    /// it appears in the description below and in the user message.</item>
    /// </list>
    /// </summary>
    public static Dictionary<string, JsonElement> Schema(CommitMessagePolicy policy, int optionCount)
    {
        var conventional = policy.RequireConventionalCommit;

        var type = new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = conventional
                ? "Conventional-commit type."
                : "Conventional-commit type. Omit unless this repository's existing subjects use them.",
        };

        if (policy.Types.Count > 0) type["enum"] = policy.Types;

        var message = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["type"] = type,
                ["scope"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "Area of the codebase, as this repository names it. Omit if unclear.",
                },
                ["subject"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] =
                        $"Imperative summary without a type prefix and without a trailing full stop. "
                        + $"Aim for {policy.SubjectIdeal} characters; never exceed {policy.SubjectLimit}.",
                },
                ["body"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] =
                        "Why the change was made, wrapped at 72 columns. Empty string for a change "
                        + "whose subject says everything.",
                },
                ["breaking"] = new Dictionary<string, object>
                {
                    ["type"] = "boolean",
                    ["description"] = "True only if this change breaks an existing contract.",
                },
            },
            // Required covers the two fields a commit cannot do without. Type is added to it
            // only where the repository has said it means to enforce the format.
            ["required"] = conventional
                ? new[] { "type", "subject", "body" }
                : new[] { "subject", "body" },
            ["additionalProperties"] = false,
        };

        var root = optionCount <= 1
            ? message
            : new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["options"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = message,
                        // Asked for in prose because minItems above 1 and maxItems are not
                        // part of the dialect. Stating it twice — here and in the user
                        // message — is cheap, and returning two when three were asked for is
                        // a mild disappointment rather than a failure.
                        ["description"] =
                            $"Exactly {optionCount} genuinely different framings of the same change, best first.",
                    },
                },
                ["required"] = new[] { "options" },
                ["additionalProperties"] = false,
            };

        // Round-tripped through JsonElement because that is the shape the SDK's schema
        // property takes, and building it as a dictionary keeps this readable.
        return JsonSerializer
            .Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(root))!;
    }
}

/// <summary>
/// Pulls a top-level string property out of JSON that has not finished arriving.
///
/// This is what lets a structured response stream into the message box. The API streams the
/// generated JSON a fragment at a time, and a user watching a box fill with
/// <c>{"type":"feat","subject":"reb</c> is worse served than one watching nothing at all —
/// so the subject and body are lifted out as they arrive and shown as text.
///
/// Cosmetic only. When the stream ends, the complete text is parsed properly by
/// <see cref="GeneratedMessage.ReadAll"/> and that result replaces whatever was on screen, so
/// a mistake here costs a flicker rather than a wrong commit message.
///
/// Deliberately scans the whole accumulated buffer every time rather than consuming chunks
/// incrementally. Escape sequences and multi-byte characters routinely straddle the boundary
/// between two network frames, and a parser that remembers where it got to has to be right
/// about every one of them; a parser that starts from the beginning cannot see a boundary at
/// all. The buffer is a commit message, so the cost of rescanning is nothing.
/// </summary>
public static class PartialJson
{
    /// <summary>
    /// The value of a top-level string property, decoded as far as it has arrived, or null
    /// when the property has not been reached yet.
    /// </summary>
    public static string? ReadString(string json, string property)
    {
        var depth = 0;
        var index = 0;

        while (index < json.Length)
        {
            var c = json[index];

            switch (c)
            {
                case '{' or '[':
                    depth++;
                    index++;
                    continue;

                case '}' or ']':
                    depth--;
                    index++;
                    continue;

                case '"':
                    var (text, next, complete) = ReadStringToken(json, index);

                    // Only a completed string can be a key — an unterminated one is the tail
                    // of the buffer and has no colon after it yet.
                    if (complete && depth == 1 && string.Equals(text, property, StringComparison.Ordinal))
                    {
                        var colon = SkipWhitespace(json, next);
                        if (colon < json.Length && json[colon] == ':')
                        {
                            var start = SkipWhitespace(json, colon + 1);
                            if (start >= json.Length) return "";
                            if (json[start] != '"') return null;

                            return ReadStringToken(json, start).Text;
                        }
                    }

                    index = next;
                    continue;

                default:
                    index++;
                    continue;
            }
        }

        return null;
    }

    private static int SkipWhitespace(string json, int index)
    {
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        return index;
    }

    /// <summary>
    /// Decodes one JSON string starting at its opening quote.
    /// <c>Complete</c> is false when the buffer ran out before the closing quote, which is
    /// the ordinary case for the value currently being written.
    /// </summary>
    private static (string Text, int Next, bool Complete) ReadStringToken(string json, int start)
    {
        var builder = new StringBuilder();
        var index = start + 1;

        while (index < json.Length)
        {
            var c = json[index];

            if (c == '"') return (builder.ToString(), index + 1, true);

            if (c != '\\')
            {
                builder.Append(c);
                index++;
                continue;
            }

            // A backslash at the very end is half an escape; stop rather than guess what it
            // was going to be. The next fragment brings the rest and this runs again.
            if (index + 1 >= json.Length) break;

            var escape = json[index + 1];
            index += 2;

            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;

                case 'u':
                    // Four hex digits, and the same reasoning as the backslash above: an
                    // incomplete one is not an error, it is the middle of the stream.
                    if (index + 4 > json.Length) return (builder.ToString(), json.Length, false);

                    if (ushort.TryParse(
                            json.AsSpan(index, 4), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var code))
                    {
                        builder.Append((char)code);
                    }

                    index += 4;
                    break;

                default:
                    builder.Append(escape);
                    break;
            }
        }

        return (builder.ToString(), json.Length, false);
    }
}
