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

public sealed record SaveFileRequest
{
    public string WorktreePath { get; init; } = "";
    public string Path { get; init; } = "";
    public string Text { get; init; } = "";
}

public sealed record OperationLogRequest
{
    public int Limit { get; init; } = 100;
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

    /// <summary>The file's encoding on disk, which a save has to reproduce.</summary>
    public Git.FileEncoding Encoding { get; init; } = Git.FileEncoding.Utf8;

    public Git.LineEnding LineEnding { get; init; } = Git.LineEnding.Lf;

    /// <summary>
    /// Whether this content can be written back. False for anything read at a commit and
    /// for binary files — the editor must not offer to save over history.
    /// </summary>
    public bool IsEditable { get; init; }
}

/// <summary>The result of writing a file back to the working tree.</summary>
public sealed record SavePayload
{
    public required string Path { get; init; }
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public int BytesWritten { get; init; }
}

/// <summary>
/// The outcome of a mutation, as the UI needs it: whether it worked, one sentence about
/// why not, and enough classification to decide what to offer next.
/// </summary>
public sealed record MutationPayload
{
    public required string Operation { get; init; }
    public required bool Ok { get; init; }
    public required string Message { get; init; }
    public Git.GitFailure Failure { get; init; } = Git.GitFailure.None;
    public string CommandLine { get; init; } = "";
    public int ExitCode { get; init; }
    public int Attempts { get; init; }
    public long ElapsedMs { get; init; }

    public static MutationPayload From(Git.GitMutation mutation) => new()
    {
        Operation = mutation.Operation,
        Ok = mutation.Success,
        Message = mutation.Message,
        Failure = mutation.Failure,
        CommandLine = mutation.CommandLine,
        ExitCode = mutation.ExitCode,
        Attempts = mutation.Attempts,
        ElapsedMs = mutation.ElapsedMs,
    };
}

/// <summary>What undo would do next, so the UI can label the action rather than guess.</summary>
public sealed record UndoPayload
{
    /// <summary>Null when there is nothing recorded for this worktree.</summary>
    public string? Label { get; init; }

    public bool IsDestructive { get; init; }
    public string? Warning { get; init; }

    /// <summary>Recent HEAD movements, which outlive the undo stack and the app itself.</summary>
    public IReadOnlyList<Git.ReflogEntry> Reflog { get; init; } = [];
}

/// <summary>
/// An image referenced by a Markdown document, inlined for the preview.
///
/// The page is served from a virtual host with a strict CSP, so it cannot read files off
/// disk — the backend has to hand the bytes over. <see cref="DataUri"/> is null when the
/// asset could not be supplied, with <see cref="Reason"/> saying why so the preview can
/// render an honest placeholder rather than a broken image.
/// </summary>
public sealed record AssetPayload
{
    public required string Path { get; init; }
    public string? DataUri { get; init; }
    public string? Reason { get; init; }
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
