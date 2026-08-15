using System.Text.Json;
using Chapter.Core.Ai;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Everything about generated commit messages that can be decided without a network — which
/// is most of it, and deliberately so. Budgeting, the streaming JSON reader, the schema and
/// the cost arithmetic are all pure functions, and none of them is worth testing against a
/// live API even if one were reachable from a test run.
/// </summary>
public class DiffBudgetTests
{
    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("web/yarn.lock")]
    [InlineData("src/Cargo.lock")]
    [InlineData("go.sum")]
    [InlineData("app/bundle.min.js")]
    [InlineData("app/site.css.map")]
    [InlineData("Views/MainWindow.Designer.cs")]
    [InlineData("Generated/Parser.g.cs")]
    [InlineData("api/service_pb2.py")]
    [InlineData("node_modules/left-pad/index.js")]
    [InlineData("src/obj/Debug/net10.0/App.AssemblyInfo.cs")]
    [InlineData("__snapshots__/Button.test.js.snap")]
    public void Machine_written_files_are_recognised(string path) =>
        Assert.True(DiffDigestBuilder.IsGenerated(path));

    [Theory]
    [InlineData("src/Chapter.Core/Git/GitCli.cs")]
    [InlineData("README.md")]
    [InlineData("distribution/Release.md")]
    [InlineData("src/objects/Mesh.cs")]
    [InlineData("binary-search.py")]
    [InlineData("Lockfile.md")]
    public void Ordinary_source_is_not(string path)
    {
        // "dist" must not match "distribution", and "obj" must not match "objects" — the
        // check is segment-wise for exactly this reason. A file wrongly classed as generated
        // is silently left out of the diff the message is written from.
        Assert.False(DiffDigestBuilder.IsGenerated(path));
    }

    [Fact]
    public void Numstat_reads_counts_and_paths()
    {
        var entries = DiffDigestBuilder.ParseNumstat("12\t3\tsrc/App.cs\0" + "0\t7\tREADME.md\0");

        Assert.Equal(2, entries.Count);
        Assert.Equal("src/App.cs", entries[0].Path);
        Assert.Equal(12, entries[0].Added);
        Assert.Equal(3, entries[0].Removed);
        Assert.False(entries[0].IsBinary);
        Assert.Equal(7, entries[1].Removed);
    }

    [Fact]
    public void Numstat_reads_a_rename_as_two_paths_rather_than_one_compacted_one()
    {
        // The whole reason for -z. Without it git prints "src/{a => b}/File.cs", which has to
        // be un-compacted to get a path back and is indistinguishable from a file whose name
        // genuinely contains braces — and the path is what the follow-up `git diff` is given.
        var entries = DiffDigestBuilder.ParseNumstat("4\t4\t\0src/a/File.cs\0src/b/File.cs\0" + "1\t0\tNew.cs\0");

        Assert.Equal(2, entries.Count);
        Assert.Equal("src/b/File.cs", entries[0].Path);
        Assert.Equal("src/a/File.cs", entries[0].OldPath);

        // The reader has to resume correctly after consuming three fields instead of one.
        Assert.Equal("New.cs", entries[1].Path);
        Assert.Null(entries[1].OldPath);
    }

    [Fact]
    public void Numstat_reads_a_binary_file_from_its_dashes()
    {
        var entries = DiffDigestBuilder.ParseNumstat("-\t-\tassets/logo.png\0");

        var entry = Assert.Single(entries);
        Assert.True(entry.IsBinary);
        Assert.Equal(0, entry.Added);
    }

    [Fact]
    public void A_huge_file_cannot_crowd_out_the_small_ones_beside_it()
    {
        // The failure this allocation exists to prevent. First-come would spend the entire
        // budget on the 100,000-character file and send none of the other eight, producing a
        // message about one file in a nine-file commit.
        var sizes = new[] { 100_000, 200, 150, 300, 120, 90, 400, 260, 180 };
        var grants = DiffDigestBuilder.Allocate(sizes, 5_000);

        for (var i = 1; i < sizes.Length; i++)
            Assert.Equal(sizes[i], grants[i]);

        Assert.True(grants[0] > 0, "the large file should still get whatever is left over");
        Assert.True(grants.Sum() <= 5_000);
    }

    [Fact]
    public void Files_that_all_fit_are_all_sent_whole()
    {
        var sizes = new[] { 10, 20, 30 };
        Assert.Equal(sizes, DiffDigestBuilder.Allocate(sizes, 1_000));
    }

    [Fact]
    public void Files_that_none_fit_split_the_budget_evenly()
    {
        var grants = DiffDigestBuilder.Allocate([1_000, 1_000, 1_000], 300);

        Assert.Equal([100, 100, 100], grants);
    }

    [Fact]
    public void Nothing_is_granted_from_nothing()
    {
        Assert.Empty(DiffDigestBuilder.Allocate([], 500));
        Assert.Equal([0, 0], DiffDigestBuilder.Allocate([10, 10], 0));
    }

    private const string TwoHunkDiff = """
        diff --git a/A.txt b/A.txt
        index 1234567..89abcde 100644
        --- a/A.txt
        +++ b/A.txt
        @@ -1,3 +1,3 @@
         one
        -two
        +TWO
         three
        @@ -10,3 +10,3 @@
         ten
        -eleven
        +ELEVEN
         twelve

        """;

    [Fact]
    public void Truncation_stops_on_a_hunk_boundary()
    {
        var patch = PatchBuilder.Parse(TwoHunkDiff);
        Assert.Equal(2, patch.Hunks.Count);

        var firstLength = patch.Hunks[0].Header.Length + 1
                          + patch.Hunks[0].Lines.Sum(l => l.Length + 1);

        var (text, kept) = DiffDigestBuilder.Truncate(patch, firstLength + 5);

        // Half a hunk is not a diff — a model shown one reads the cut as part of the change.
        Assert.Equal(1, kept);
        Assert.Contains("+TWO", text);
        Assert.DoesNotContain("ELEVEN", text);
    }

    [Fact]
    public void A_budget_too_small_for_even_one_hunk_keeps_nothing()
    {
        var patch = PatchBuilder.Parse(TwoHunkDiff);
        var (text, kept) = DiffDigestBuilder.Truncate(patch, 5);

        Assert.Equal(0, kept);
        Assert.Equal("", text);
    }

    [Fact]
    public void A_complete_digest_says_nothing_about_truncation()
    {
        var digest = new DiffDigest
        {
            Files =
            [
                new DiffFileNote { Path = "A.cs", State = DiffFileState.Included, LinesAdded = 3 },
            ],
            Body = "--- A.cs ---\n@@ -1 +1 @@\n+x\n",
        };

        Assert.False(digest.IsPartial);
        Assert.False(digest.WasCutForSize);
        Assert.DoesNotContain("INCOMPLETE", digest.ToPrompt());
        Assert.Contains("A.cs | +3 -0", digest.ToPrompt());
    }

    [Fact]
    public void A_binary_alongside_a_complete_diff_is_not_an_incomplete_diff()
    {
        // There is no patch anybody could have shown for a PNG, so nothing is missing. Warning
        // about incompleteness here would put "I could not see the whole change" on every
        // commit that touches an image, and a warning that fires constantly is one nobody
        // reads on the day it matters.
        var digest = new DiffDigest
        {
            Files =
            [
                new DiffFileNote { Path = "A.cs", State = DiffFileState.Included, LinesAdded = 3 },
                new DiffFileNote
                {
                    Path = "assets/logo.png",
                    State = DiffFileState.Summarised,
                    Omission = DiffOmission.Nothing,
                    IsBinary = true,
                    Reason = "binary",
                },
            ],
            Body = "--- A.cs ---\n@@ -1 +1 @@\n+x\n",
        };

        Assert.False(digest.IsPartial);
        Assert.False(digest.WasCutForSize);
        Assert.DoesNotContain("INCOMPLETE", digest.ToPrompt());
    }

    [Fact]
    public void A_withheld_generated_file_is_incomplete_but_not_over_budget()
    {
        // Not decoration. A model handed a partial diff with no warning describes the half it
        // was shown as though it were the whole change, and nothing in the resulting message
        // reveals that it never saw the rest.
        //
        // But the *cause* has to be right too: a lockfile is dropped on purpose and would be
        // dropped at any size, so telling the model it was "cut to fit a budget" makes the
        // prompt say a false thing about its own contents — a poor place to ask for accuracy.
        var digest = new DiffDigest
        {
            Files =
            [
                new DiffFileNote { Path = "A.cs", State = DiffFileState.Included, LinesAdded = 3 },
                new DiffFileNote
                {
                    Path = "package-lock.json",
                    State = DiffFileState.Summarised,
                    Omission = DiffOmission.Policy,
                    LinesAdded = 4_102,
                    LinesRemoved = 3_988,
                    Reason = "generated — patch not sent",
                },
            ],
            Body = "--- A.cs ---\n@@ -1 +1 @@\n+x\n",
        };

        var prompt = digest.ToPrompt();

        Assert.True(digest.IsPartial);
        Assert.False(digest.WasCutForSize);
        Assert.Contains("INCOMPLETE", prompt);
        Assert.Contains("generated files' patches are never sent", prompt);
        Assert.DoesNotContain("size budget", prompt);

        // The file list comes before the patches, so a cut anywhere downstream loses hunks
        // rather than losing the shape of the change.
        Assert.True(prompt.IndexOf("package-lock.json", StringComparison.Ordinal)
                    < prompt.IndexOf("Patches:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_cut_to_fit_says_it_was_cut_to_fit()
    {
        var digest = new DiffDigest
        {
            Files =
            [
                new DiffFileNote
                {
                    Path = "Parser.cs",
                    State = DiffFileState.Truncated,
                    Omission = DiffOmission.Budget,
                    LinesAdded = 9_000,
                    Reason = "showing 2 of 40 hunks",
                },
            ],
            Body = "--- Parser.cs ---\n@@ -1 +1 @@\n+x\n",
        };

        Assert.True(digest.IsPartial);
        Assert.True(digest.WasCutForSize);
        Assert.Contains("size budget", digest.ToPrompt());
    }
}

/// <summary>
/// The reader that lets a structured response stream into the message box.
///
/// Every test here feeds the buffer one character at a time as well as whole, because that is
/// what the network does: escape sequences and surrogate pairs land either side of a frame
/// boundary routinely, and a reader that cannot see a boundary cannot get one wrong.
/// </summary>
public class PartialJsonTests
{
    /// <summary>Asserts the reader agrees with itself at every prefix length.</summary>
    private static void AssertStreams(string json, string property, string expected)
    {
        Assert.Equal(expected, PartialJson.ReadString(json, property));

        string? last = null;
        for (var length = 1; length <= json.Length; length++)
        {
            // The only hard requirement mid-stream is that it never throws and never
            // overshoots the final answer.
            last = PartialJson.ReadString(json[..length], property);
            if (last is not null) Assert.StartsWith(last, expected, StringComparison.Ordinal);
        }

        Assert.Equal(expected, last);
    }

    [Fact]
    public void Reads_a_plain_value() =>
        AssertStreams("""{"subject":"add the parser"}""", "subject", "add the parser");

    [Fact]
    public void Decodes_the_escapes_a_commit_body_is_full_of() =>
        AssertStreams(
            """{"body":"First line.\n\nSecond \"quoted\" line.\nA backslash: \\ and a tab:\there."}""",
            "body",
            "First line.\n\nSecond \"quoted\" line.\nA backslash: \\ and a tab:\there.");

    [Fact]
    public void Decodes_a_unicode_escape() =>
        AssertStreams("""{"subject":"tidy the café parser"}""", "subject", "tidy the café parser");

    [Fact]
    public void Reads_a_value_that_follows_other_properties() =>
        AssertStreams(
            """{"type":"feat","scope":"core","subject":"add it","body":"why"}""",
            "subject",
            "add it");

    [Fact]
    public void A_property_name_appearing_inside_a_value_is_not_mistaken_for_the_key()
    {
        // "subject" occurs inside the type's value first. A reader that searched for the text
        // rather than tracking string boundaries would return "" here.
        const string json = """{"scope":"the subject line","subject":"real answer"}""";

        Assert.Equal("real answer", PartialJson.ReadString(json, "subject"));
    }

    [Fact]
    public void A_matching_key_nested_deeper_is_ignored()
    {
        // Only the top level is a message. A key of the same name inside a nested object
        // belongs to something else.
        const string json = """{"options":[{"subject":"nested"}],"subject":"top"}""";

        Assert.Equal("top", PartialJson.ReadString(json, "subject"));
    }

    [Fact]
    public void An_absent_property_reads_as_null() =>
        Assert.Null(PartialJson.ReadString("""{"subject":"x"}""", "body"));

    [Fact]
    public void A_half_arrived_escape_is_not_guessed_at()
    {
        // The buffer ends on the backslash. The next fragment says whether it was \n or \\,
        // and inventing either would put a character in the box that then has to be removed.
        Assert.Equal("line", PartialJson.ReadString("""{"body":"line\""", "body"));
        Assert.Equal("line", PartialJson.ReadString("""{"body":"line\u00""", "body"));
    }

    [Fact]
    public void Nothing_arriving_at_all_is_not_an_error()
    {
        Assert.Null(PartialJson.ReadString("", "subject"));
        Assert.Null(PartialJson.ReadString("{", "subject"));
        Assert.Null(PartialJson.ReadString("""{"subj""", "subject"));
    }
}

public class GeneratedMessageTests
{
    [Fact]
    public void A_conventional_message_is_assembled_from_its_parts()
    {
        var message = new GeneratedMessage
        {
            Type = "feat",
            Scope = "core",
            Subject = "read the index directly",
            Body = "Because the review scan and the index disagree.",
        };

        Assert.Equal(
            "feat(core): read the index directly\n\nBecause the review scan and the index disagree.",
            message.Message);
    }

    [Fact]
    public void A_repository_without_the_convention_gets_no_prefix()
    {
        var message = new GeneratedMessage { Subject = "Read the index directly", Body = "" };

        Assert.Equal("Read the index directly", message.Message);
    }

    [Fact]
    public void A_scope_free_type_still_prefixes()
    {
        var message = new GeneratedMessage { Type = "fix", Subject = "stop the leak" };

        Assert.Equal("fix: stop the leak", message.Message);
    }

    [Fact]
    public void Breaking_changes_carry_the_marker_git_tooling_looks_for()
    {
        var message = new GeneratedMessage
        {
            Type = "feat",
            Scope = "api",
            Subject = "drop the v1 endpoint",
            IsBreaking = true,
        };

        Assert.Equal("feat(api)!: drop the v1 endpoint", message.Message);
    }

    [Fact]
    public void The_subject_and_body_are_always_separated_by_exactly_one_blank_line()
    {
        // Git treats everything after the first blank line as the body, and the app's own
        // reviewer flags a non-blank second line as an error. A generated message must not be
        // the thing that trips it.
        var message = new GeneratedMessage
        {
            Subject = "  tidy the parser  ",
            Body = "\n\n\nWhy it needed tidying.\n\n",
        };

        Assert.Equal("tidy the parser\n\nWhy it needed tidying.", message.Message);

        var review = CommitMessageReader.Review(message.Message);
        Assert.DoesNotContain(review.Problems, p => p.Severity is MessageSeverity.Error);
    }

    [Fact]
    public void One_object_reads_as_one_option()
    {
        var options = GeneratedMessage.ReadAll("""{"type":"fix","subject":"stop the leak","body":""}""");

        var only = Assert.Single(options);
        Assert.Equal("fix: stop the leak", only.Message);
    }

    [Fact]
    public void An_options_array_reads_as_several()
    {
        const string json = """
            {"options":[
              {"subject":"first framing","body":"a"},
              {"subject":"second framing","body":"b"},
              {"subject":"third framing","body":"c"}]}
            """;

        var options = GeneratedMessage.ReadAll(json);

        Assert.Equal(3, options.Count);
        Assert.Equal("first framing", options[0].Subject);
        Assert.Equal("third framing", options[2].Subject);
    }

    [Fact]
    public void Anything_that_is_not_a_message_reads_as_none()
    {
        // The shapes a refusal, a truncated reply and a model having a bad day arrive in.
        // None of them may throw: the caller turns an empty list into "write one yourself".
        Assert.Empty(GeneratedMessage.ReadAll(""));
        Assert.Empty(GeneratedMessage.ReadAll("I cannot help with that."));
        Assert.Empty(GeneratedMessage.ReadAll("""{"subject":"   "}"""));
        Assert.Empty(GeneratedMessage.ReadAll("""{"type":"feat","subje"""));
        Assert.Empty(GeneratedMessage.ReadAll("""{"options":[]}"""));
    }

    [Fact]
    public void An_enforcing_repository_gets_its_own_types_as_the_schema_enum()
    {
        // This is what makes conventional-commit conformance mechanical rather than a check
        // run afterwards: the API will not return a type outside the list.
        var policy = new CommitMessagePolicy
        {
            RequireConventionalCommit = true,
            Types = ["feat", "fix", "chore"],
        };

        var json = JsonSerializer.Serialize(GeneratedMessage.Schema(policy, 1));

        Assert.Contains("\"enum\":[\"feat\",\"fix\",\"chore\"]", json);
        Assert.Contains("\"required\":[\"type\",\"subject\",\"body\"]", json);
    }

    [Fact]
    public void A_repository_without_the_convention_does_not_have_a_type_forced_on_it()
    {
        var json = JsonSerializer.Serialize(GeneratedMessage.Schema(new CommitMessagePolicy(), 1));

        Assert.Contains("\"required\":[\"subject\",\"body\"]", json);
    }

    [Fact]
    public void Asking_for_several_wraps_the_schema_in_an_array_and_asks_for_the_count_in_prose()
    {
        var json = JsonSerializer.Serialize(GeneratedMessage.Schema(new CommitMessagePolicy(), 3));

        Assert.Contains("\"options\"", json);
        Assert.Contains("Exactly 3 genuinely different framings", json);

        // Array-size constraints are not part of the structured-output dialect: minItems
        // above 1 is unsupported and maxItems is not recognised at all. Sending either gets
        // the whole request rejected, so the count has to be asked for in words.
        Assert.DoesNotContain("minItems", json);
        Assert.DoesNotContain("maxItems", json);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Every_object_in_the_schema_closes_itself_to_extra_properties(int optionCount)
    {
        // The rule that makes the difference between a working feature and one that rejects
        // every request: `additionalProperties: false` is required on *each* object, nested
        // ones included. Its absence comes back as a bad request, which the UI would report
        // as the diff being too large — sending the user off to shrink something that was
        // never the problem.
        var schema = JsonSerializer.SerializeToElement(
            GeneratedMessage.Schema(new CommitMessagePolicy(), optionCount));

        var objects = 0;
        Walk(schema, ref objects);

        Assert.Equal(optionCount <= 1 ? 1 : 2, objects);

        static void Walk(JsonElement element, ref int objects)
        {
            if (element.ValueKind is JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Walk(item, ref objects);
                return;
            }

            if (element.ValueKind is not JsonValueKind.Object) return;

            if (element.TryGetProperty("type", out var type)
                && type.ValueKind is JsonValueKind.String
                && type.GetString() == "object")
            {
                objects++;
                Assert.True(
                    element.TryGetProperty("additionalProperties", out var extra)
                    && extra.ValueKind is JsonValueKind.False,
                    "every object in the schema needs additionalProperties: false");
            }

            foreach (var property in element.EnumerateObject()) Walk(property.Value, ref objects);
        }
    }
}

public class GenerationCostTests
{
    [Fact]
    public void A_known_model_is_priced()
    {
        var cost = GenerationCost.For("claude-opus-5", inputTokens: 10_000, outputTokens: 200,
            cacheReadTokens: 0, cacheWriteTokens: 0);

        // 10,000 × $5/MTok + 200 × $25/MTok.
        Assert.Equal(0.055m, cost.Usd);
        Assert.Equal(10_200, cost.TotalTokens);
    }

    [Fact]
    public void Cached_input_is_charged_at_its_own_rate()
    {
        // The whole reason the roadmap asks for caching: a regenerate re-reads the system
        // prompt at a tenth of the input price. If cache tokens were priced as input, the
        // saving would be invisible in the number shown to the user.
        var cached = GenerationCost.For("claude-opus-5", 0, 0, cacheReadTokens: 10_000, cacheWriteTokens: 0);
        var fresh = GenerationCost.For("claude-opus-5", 10_000, 0, 0, 0);

        Assert.Equal(fresh.Usd!.Value / 10m, cached.Usd);

        var written = GenerationCost.For("claude-opus-5", 0, 0, 0, cacheWriteTokens: 10_000);
        Assert.Equal(fresh.Usd!.Value * 1.25m, written.Usd);
    }

    [Fact]
    public void A_dated_snapshot_prices_as_the_model_it_is() =>
        Assert.Equal(
            ModelPrices.For("claude-haiku-4-5"),
            ModelPrices.For("claude-haiku-4-5-20251001"));

    [Fact]
    public void An_unrecognised_model_reports_tokens_and_no_price()
    {
        // settings.json is hand-edited and the model list moves faster than this app ships.
        // An invented price presented confidently is worse than an honest omission.
        var cost = GenerationCost.For("claude-something-not-shipped-yet", 1_000, 100, 0, 0);

        Assert.Null(cost.Usd);
        Assert.Equal(1_100, cost.TotalTokens);
        Assert.Null(ModelPrices.For("gpt-4"));
    }

    [Theory]
    [InlineData("low", Anthropic.Models.Messages.Effort.Low)]
    [InlineData("HIGH", Anthropic.Models.Messages.Effort.High)]
    [InlineData("xhigh", Anthropic.Models.Messages.Effort.Xhigh)]
    [InlineData("", Anthropic.Models.Messages.Effort.Low)]
    [InlineData("enthusiastic", Anthropic.Models.Messages.Effort.Low)]
    public void Effort_falls_back_rather_than_failing_on_a_hand_edited_value(
        string value, Anthropic.Models.Messages.Effort expected) =>
        Assert.Equal(expected, CommitMessageGenerator.ParseEffort(value));
}

/// <summary>
/// Where the key lives. Every test here uses a temp file and a stubbed environment, so none
/// of them can read, write or depend on the key belonging to whoever ran the suite.
/// </summary>
public class ApiKeyStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), "chapter-key-" + Guid.NewGuid().ToString("N")[..8] + ".dat");

    private ApiKeyStore Store(string? environment = null) =>
        new(_file, _ => environment);

    public void Dispose()
    {
        try
        {
            if (File.Exists(_file)) File.Delete(_file);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_stored_key_round_trips_and_is_not_on_disk_in_the_clear()
    {
        var store = Store();

        Assert.Null(store.Store("sk-ant-api03-not-a-real-key-0000"));
        Assert.Equal("sk-ant-api03-not-a-real-key-0000", store.ReadKey());

        // The point of DPAPI. Reading the file must not hand anybody the key.
        var raw = File.ReadAllBytes(_file);
        Assert.DoesNotContain("sk-ant", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_typed_into_chapter_beats_one_inherited_from_the_environment()
    {
        // Both present means the user deliberately typed one while the variable was already
        // set, and the deliberate act wins. The UI names the source either way.
        var store = Store(environment: "sk-env-inherited-from-parent");
        store.Store("sk-typed-into-chapter-app");

        Assert.Equal("sk-typed-into-chapter-app", store.ReadKey());
        Assert.Equal(ApiKeySource.Stored, store.Read().Source);
    }

    [Fact]
    public void The_environment_is_used_when_nothing_was_typed()
    {
        var store = Store(environment: "sk-env-inherited-from-parent");

        Assert.Equal("sk-env-inherited-from-parent", store.ReadKey());
        Assert.Equal(ApiKeySource.Environment, store.Read().Source);
    }

    [Fact]
    public void Clearing_forgets_the_stored_key_and_falls_back()
    {
        var store = Store(environment: "sk-env-inherited-from-parent");
        store.Store("sk-typed-into-chapter-app");

        // An empty key is "forget it", not "store an empty one".
        Assert.Null(store.Store("   "));
        Assert.Equal(ApiKeySource.Environment, store.Read().Source);

        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void Nothing_configured_reads_as_nothing()
    {
        var store = Store();

        Assert.Null(store.ReadKey());
        Assert.False(store.Read().HasKey);
        Assert.Equal(ApiKeySource.None, store.Read().Source);
    }

    [Fact]
    public void A_key_that_cannot_be_decrypted_is_absent_rather_than_fatal()
    {
        // What a blob written by another Windows account looks like. A credential problem
        // must never be what stops the commit box rendering.
        File.WriteAllBytes(_file, [0x01, 0x02, 0x03, 0x04]);

        var store = Store(environment: "sk-env-inherited-from-parent");

        Assert.Equal("sk-env-inherited-from-parent", store.ReadKey());
    }

    [Fact]
    public void The_hint_identifies_a_key_without_revealing_one()
    {
        const string key = "sk-ant-api03-abcdefghijklmnop-WXYZ";

        var hint = ApiKeyStore.Hint(key);

        Assert.Equal("…WXYZ", hint);
        Assert.DoesNotContain("abcdefgh", hint, StringComparison.Ordinal);

        // Something too short to be a key is more likely a typo, and echoing most of it back
        // would defeat the purpose.
        Assert.Equal("…", ApiKeyStore.Hint("short"));
    }
}
