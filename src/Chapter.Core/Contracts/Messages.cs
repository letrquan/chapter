using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chapter.Core.Contracts;

/// <summary>
/// The contract between the C# backend and the Monaco front-end.
///
/// Everything crosses the WebView2 boundary as JSON via PostWebMessageAsJson — a plain
/// request/response protocol with a separate push channel for events. The TypeScript
/// mirror of these shapes lives in <c>Chapter.Web/src/protocol.ts</c>; the two must be
/// changed together.
/// </summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>A call from the front-end. <see cref="Id"/> is echoed back on the response.</summary>
public sealed record BridgeRequest
{
    public int Id { get; init; }
    public string Method { get; init; } = "";
    public JsonElement Params { get; init; }

    /// <summary>Deserialises the parameter payload, or returns a default when absent.</summary>
    public T ParamsAs<T>() where T : new() =>
        Params.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new T()
            : Params.Deserialize<T>(BridgeJson.Options) ?? new T();
}

public sealed record BridgeResponse
{
    public required int Id { get; init; }
    public required bool Ok { get; init; }
    public object? Result { get; init; }
    public string? Error { get; init; }

    public static BridgeResponse Success(int id, object? result) => new() { Id = id, Ok = true, Result = result };
    public static BridgeResponse Failure(int id, string error) => new() { Id = id, Ok = false, Error = error };
}

/// <summary>An unsolicited push from the backend — file changes, index progress, and so on.</summary>
public sealed record BridgeEvent
{
    public required string Event { get; init; }
    public object? Payload { get; init; }
}

// ---------------------------------------------------------------------------
// Request payloads
// ---------------------------------------------------------------------------

public sealed record RepoRequest
{
    public string RepoPath { get; init; } = "";
}

public sealed record WorktreeRequest
{
    public string WorktreePath { get; init; } = "";

    /// <summary>Which slice of the work to show. Defaults to everything on the branch.</summary>
    public Git.DiffScope Scope { get; init; } = Git.DiffScope.Branch;
}

public sealed record FileRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>Must match the scope the file list was built with, or the two sides disagree.</summary>
    public Git.DiffScope Scope { get; init; } = Git.DiffScope.Branch;
}

public sealed record OpenInEditorRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
    public int Line { get; init; } = 1;
    public int Column { get; init; } = 1;

    /// <summary>Editor id, e.g. "rider" or "vscode". Empty means the configured default.</summary>
    public string Editor { get; init; } = "";
}

public sealed record NavigationRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
    public int Line { get; init; }
    public int Column { get; init; }
}

public sealed record SearchRequest
{
    public string WorktreePath { get; init; } = "";
    public string Query { get; init; } = "";
    public int Limit { get; init; } = 50;
}

// ---------------------------------------------------------------------------
// Response payloads
// ---------------------------------------------------------------------------

/// <summary>The two sides of a file's diff, ready to hand straight to Monaco.</summary>
public sealed record DiffPayload
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required string BaseText { get; init; }
    public required string WorkingText { get; init; }

    /// <summary>Monaco language id inferred from the file extension.</summary>
    public required string Language { get; init; }

    public bool IsBinary { get; init; }
    public string Kind { get; init; } = "modified";
}

public sealed record FileContentPayload
{
    public required string Path { get; init; }
    public required string Text { get; init; }
    public required string Language { get; init; }
    public bool IsBinary { get; init; }
}

/// <summary>A place in the code — the unit of every navigation result.</summary>
public sealed record SymbolLocation
{
    public required string Path { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }

    /// <summary>Display name, e.g. <c>AgentTurnRunner.RunAsync</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>Symbol kind as a Monaco-friendly string: class, method, property…</summary>
    public string Kind { get; init; } = "";

    public string? ContainerName { get; init; }

    /// <summary>Source line text, for preview rows in search and reference lists.</summary>
    public string? Preview { get; init; }
}

public sealed record EditorInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
}

public sealed record IndexStatusPayload
{
    public required string WorktreePath { get; init; }
    public required string State { get; init; }
    public int FilesIndexed { get; init; }
    public int SymbolCount { get; init; }
    public long ElapsedMs { get; init; }
}
