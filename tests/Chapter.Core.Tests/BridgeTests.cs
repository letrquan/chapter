using System.Text.Json;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Exercises the dispatcher over real JSON — the same path the front-end takes.
///
/// This is the layer where a contract drift between Messages.cs and protocol.ts shows up
/// as a null field rather than a compile error, so the shapes are asserted explicitly.
/// </summary>
public class BridgeTests
{
    private const string HeatRepo = @"I:\MyProject\02-AI-ML-Projects\heat";

    private static BridgeDispatcher NewDispatcher() =>
        new(new WorkspaceService(new GitCli()), new AppSettings());

    private static async Task<JsonElement> CallAsync(BridgeDispatcher dispatcher, string method, object? parameters = null)
    {
        var request = JsonSerializer.Serialize(
            new { id = 1, method, @params = parameters },
            BridgeJson.Options);

        var responseJson = await dispatcher.HandleAsync(request);
        var response = JsonDocument.Parse(responseJson).RootElement;

        Assert.True(
            response.GetProperty("ok").GetBoolean(),
            $"{method} failed: {(response.TryGetProperty("error", out var e) ? e.GetString() : "no error text")}");

        return response.GetProperty("result");
    }

    [Fact]
    public async Task Ping_round_trips()
    {
        var result = await CallAsync(NewDispatcher(), "ping");
        Assert.Equal("pong", result.GetString());
    }

    [Fact]
    public async Task Unknown_method_fails_without_throwing()
    {
        var responseJson = await NewDispatcher().HandleAsync("""{"id":7,"method":"nope","params":null}""");
        var response = JsonDocument.Parse(responseJson).RootElement;

        // The window must survive a bad call; the front-end turns this into a toast.
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal(7, response.GetProperty("id").GetInt32());
        Assert.Contains("nope", response.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task Malformed_json_fails_without_throwing()
    {
        var responseJson = await NewDispatcher().HandleAsync("not json at all");
        var response = JsonDocument.Parse(responseJson).RootElement;

        Assert.False(response.GetProperty("ok").GetBoolean());
    }

    [SkippableFact]
    public async Task Worktrees_serialise_with_the_fields_the_ui_reads()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var result = await CallAsync(NewDispatcher(), "getWorktrees", new { repoPath = HeatRepo });
        var first = result.EnumerateArray().First();

        // camelCase, and the computed properties the rail depends on must be present.
        foreach (var field in new[] { "path", "branch", "isMain", "isPrunable", "displayName", "isUsable" })
            Assert.True(first.TryGetProperty(field, out _), $"missing '{field}' in worktree payload");
    }

    [SkippableFact]
    public async Task Changes_payload_includes_totals_and_per_file_shape()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var result = await CallAsync(NewDispatcher(), "getChanges", new { worktreePath = HeatRepo });

        Assert.True(result.TryGetProperty("base", out var baseInfo));
        Assert.Equal(40, baseInfo.GetProperty("sha").GetString()!.Length);
        Assert.True(result.TryGetProperty("totalAdded", out _));

        var files = result.GetProperty("files");
        Assert.True(files.GetArrayLength() > 0);

        var file = files.EnumerateArray().First();
        foreach (var field in new[] { "path", "kind", "linesAdded", "linesRemoved", "fileName", "hasBaseSide" })
            Assert.True(file.TryGetProperty(field, out _), $"missing '{field}' in changed-file payload");

        // Enum serialisation must be camelCase strings, since the front-end switches on them.
        Assert.False(file.GetProperty("kind").ValueKind == JsonValueKind.Number);
    }

    [SkippableFact]
    public async Task Diff_payload_carries_both_sides_and_a_monaco_language()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var dispatcher = NewDispatcher();
        var changes = await CallAsync(dispatcher, "getChanges", new { worktreePath = HeatRepo });

        var target = changes.GetProperty("files").EnumerateArray()
            .FirstOrDefault(f =>
                f.GetProperty("kind").GetString() == "modified" &&
                !f.GetProperty("isBinary").GetBoolean() &&
                f.GetProperty("path").GetString()!.EndsWith(".cs"));

        Skip.If(target.ValueKind == JsonValueKind.Undefined, "no modified C# file in the worktree");

        var diff = await CallAsync(dispatcher, "getDiff", new
        {
            worktreePath = HeatRepo,
            path = target.GetProperty("path").GetString(),
        });

        Assert.Equal("csharp", diff.GetProperty("language").GetString());
        Assert.NotEmpty(diff.GetProperty("baseText").GetString()!);
        Assert.NotEmpty(diff.GetProperty("workingText").GetString()!);
        Assert.NotEqual(diff.GetProperty("baseText").GetString(), diff.GetProperty("workingText").GetString());
    }

    [SkippableFact]
    public async Task Go_to_definition_resolves_a_click_to_a_declaration()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var dispatcher = NewDispatcher();

        // Find a file that actually mentions the type, and the position of that mention.
        var files = await CallAsync(dispatcher, "searchFiles",
            new { worktreePath = HeatRepo, query = "AgentTurnRunner", limit = 5 });

        var path = files.EnumerateArray().Select(e => e.GetString()!)
            .First(p => p.EndsWith("AgentTurnRunner.cs"));

        var absolute = Path.Combine(HeatRepo, path.Replace('/', '\\'));
        var lines = await File.ReadAllLinesAsync(absolute);

        var lineIndex = Array.FindIndex(lines, l => l.Contains("class AgentTurnRunner"));
        Skip.If(lineIndex < 0, "declaration line not found");

        var column = lines[lineIndex].IndexOf("AgentTurnRunner", StringComparison.Ordinal) + 3;

        var result = await CallAsync(dispatcher, "goToDefinition", new
        {
            worktreePath = HeatRepo,
            path,
            line = lineIndex + 1,
            column,
        });

        var locations = result.EnumerateArray().ToArray();
        Assert.NotEmpty(locations);
        Assert.Contains(locations, l => l.GetProperty("path").GetString()!.EndsWith("AgentTurnRunner.cs"));

        foreach (var field in new[] { "path", "line", "column", "name", "kind" })
            Assert.True(locations[0].TryGetProperty(field, out _), $"missing '{field}' in location payload");
    }

    [SkippableFact]
    public async Task Symbol_and_file_search_return_results_over_the_protocol()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var dispatcher = NewDispatcher();

        var symbols = await CallAsync(dispatcher, "searchSymbols",
            new { worktreePath = HeatRepo, query = "AgentTurnRunner", limit = 20 });
        Assert.Contains(symbols.EnumerateArray(), s => s.GetProperty("name").GetString() == "AgentTurnRunner");

        var files = await CallAsync(dispatcher, "searchFiles",
            new { worktreePath = HeatRepo, query = "AgentTurnRunner", limit = 20 });
        Assert.Contains(files.EnumerateArray(), f => f.GetString()!.EndsWith("AgentTurnRunner.cs"));
    }

    [SkippableFact]
    public async Task Find_references_returns_uses_across_files()
    {
        Skip.IfNot(Directory.Exists(HeatRepo), $"{HeatRepo} not present");

        var dispatcher = NewDispatcher();

        var files = await CallAsync(dispatcher, "searchFiles",
            new { worktreePath = HeatRepo, query = "AgentTurnRunner", limit = 5 });
        var path = files.EnumerateArray().Select(e => e.GetString()!)
            .First(p => p.EndsWith("AgentTurnRunner.cs"));

        var absolute = Path.Combine(HeatRepo, path.Replace('/', '\\'));
        var lines = await File.ReadAllLinesAsync(absolute);
        var lineIndex = Array.FindIndex(lines, l => l.Contains("class AgentTurnRunner"));
        Skip.If(lineIndex < 0, "declaration line not found");

        var result = await CallAsync(dispatcher, "findReferences", new
        {
            worktreePath = HeatRepo,
            path,
            line = lineIndex + 1,
            column = lines[lineIndex].IndexOf("AgentTurnRunner", StringComparison.Ordinal) + 3,
        });

        var locations = result.EnumerateArray().ToArray();
        Assert.NotEmpty(locations);
        Assert.Contains(locations, l => l.GetProperty("kind").GetString() == "declaration");
    }

    [Fact]
    public void Editor_arguments_reuse_an_existing_window()
    {
        // The whole point of the escape hatch: it must land in the window already open,
        // not add another one to the pile this app exists to avoid.
        var rider = Core.Editors.EditorLauncher.BuildArguments("rider", @"C:\wt", @"C:\wt\src\A.cs", 42, 7);
        Assert.Contains("--line", rider);
        Assert.Contains("42", rider);
        Assert.Contains(@"C:\wt", rider);          // solution context, so it is not a lone file

        var code = Core.Editors.EditorLauncher.BuildArguments("vscode", @"C:\wt", @"C:\wt\src\A.cs", 42, 7);
        Assert.Contains("-r", code);               // reuse window
        Assert.Contains(@"C:\wt\src\A.cs:42:7", code);
    }
}
