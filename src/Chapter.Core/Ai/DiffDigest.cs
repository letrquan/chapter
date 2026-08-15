using System.Text;
using Chapter.Core.Git;

namespace Chapter.Core.Ai;

/// <summary>What happened to one file when the diff was assembled for the model.</summary>
public enum DiffFileState
{
    /// <summary>Its whole patch was sent.</summary>
    Included,

    /// <summary>Some of its hunks were sent; the model is told the rest were cut.</summary>
    Truncated,

    /// <summary>Only its line counts were sent — no budget left, or nothing readable to send.</summary>
    Summarised,
}

/// <summary>One file's line in the summary, and what became of its patch.</summary>
public sealed record DiffFileNote
{
    public required string Path { get; init; }
    public required DiffFileState State { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    public bool IsBinary { get; init; }

    /// <summary>Previous path, when git reported this as a rename or copy.</summary>
    public string? OldPath { get; init; }

    /// <summary>Why the patch was left out, in the words the model is shown.</summary>
    public string? Reason { get; init; }

    /// <summary>The stat line, in the shape <c>git diff --stat</c> would print it.</summary>
    public string StatLine =>
        (IsBinary ? $"{Describe()} | binary" : $"{Describe()} | +{LinesAdded} -{LinesRemoved}")
        + (Reason is null ? "" : $"  ({Reason})");

    private string Describe() => OldPath is null ? Path : $"{OldPath} => {Path}";
}

/// <summary>
/// The staged change, cut down to something worth sending.
///
/// The whole reason this type exists: a single staged file can be fourteen thousand lines,
/// and "send the diff" then blows the context window and the bill on a request whose answer
/// is one sentence long. What the model actually needs is the shape of the change — every
/// file, with its line counts — and the content of as much of it as fits.
/// </summary>
public sealed record DiffDigest
{
    /// <summary>Every file in the change, whether or not its patch survived the budget.</summary>
    public required IReadOnlyList<DiffFileNote> Files { get; init; }

    /// <summary>The patches that fitted, already assembled.</summary>
    public required string Body { get; init; }

    /// <summary>Whether anything was cut. Stated to the model rather than left implicit.</summary>
    public bool IsTruncated => Files.Any(f => f.State is not DiffFileState.Included);

    public int LinesAdded => Files.Sum(f => f.LinesAdded);
    public int LinesRemoved => Files.Sum(f => f.LinesRemoved);
    public bool IsEmpty => Files.Count == 0;

    /// <summary>
    /// The user message: the summary first, then the patches.
    ///
    /// Summary first is deliberate. If anything at the end is going to be lost — to a
    /// truncation here, to a context limit anywhere downstream — the part that must survive
    /// is the list of what changed, not the last hunk of the last file.
    /// </summary>
    public string ToPrompt()
    {
        var builder = new StringBuilder();

        builder.Append("Files in this change (").Append(Files.Count)
            .Append(Files.Count == 1 ? " file, +" : " files, +")
            .Append(LinesAdded).Append(" -").Append(LinesRemoved).Append("):\n");

        foreach (var file in Files) builder.Append("  ").Append(file.StatLine).Append('\n');

        if (IsTruncated)
        {
            // Said plainly, because a model shown a partial diff with no warning will happily
            // write "renames the parser and updates its tests" about the half it can see, and
            // there is no way to tell from the message that it never saw the rest.
            builder.Append(
                "\nThis diff is INCOMPLETE — the patches below were cut to fit a budget. "
                + "Files marked above are summarised or truncated. Describe the change from "
                + "the file list as a whole; do not claim to have reviewed every line, and do "
                + "not describe a file whose patch you were not shown.\n");
        }

        if (Body.Length > 0) builder.Append("\nPatches:\n\n").Append(Body);

        return builder.ToString();
    }
}

/// <summary>
/// Chooses what of a staged change to send, and says what it left out.
///
/// Three rules, in order. Generated files go first and entirely — a refreshed lockfile is
/// ten thousand lines that say nothing a human would write in a commit message, and it is
/// the single largest source of wasted budget. Binaries follow, since there is no text to
/// send. What remains shares the budget by water-filling: small files are sent whole, and
/// the large ones split whatever is left equally, so one enormous file cannot crowd out the
/// nine others in the same commit.
/// </summary>
public static class DiffDigestBuilder
{
    /// <summary>
    /// Files whose patch is never worth a token, matched on the whole repo-relative path.
    ///
    /// Everything here is machine-written: nobody reads a lockfile diff to find out what a
    /// commit did, and every line of one displaces a line of the change that actually needs
    /// describing. They stay in the summary — "package-lock.json | +4,102 -3,988" is a fact
    /// about the commit — only their content is dropped.
    /// </summary>
    private static readonly string[] GeneratedNames =
    [
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml",
        "bun.lockb", "composer.lock", "gemfile.lock", "poetry.lock", "pdm.lock",
        "cargo.lock", "go.sum", "packages.lock.json", "paket.lock", "mix.lock",
        "podfile.lock", "flake.lock", "pubspec.lock",
    ];

    private static readonly string[] GeneratedSuffixes =
    [
        ".min.js", ".min.css", ".map", ".designer.cs", ".g.cs", ".g.i.cs",
        ".generated.cs", ".feature.cs", "_pb2.py", "_pb2_grpc.py", ".pb.go",
        ".pb.cc", ".pb.h", ".snap",
    ];

    /// <summary>Path segments that mark a whole tree as built rather than written.</summary>
    private static readonly string[] GeneratedDirectories =
    [
        "node_modules", "dist", "bin", "obj", "vendor", "__pycache__",
        ".venv", "target", "packages",
    ];

    /// <summary>
    /// Whether a file's content is machine-written and not worth sending.
    ///
    /// Deliberately a pure function of the path. Sniffing content would be more accurate and
    /// would also mean reading every file in the change to decide whether to read it, and the
    /// cost of a wrong answer here is one file's patch, not a wrong commit message.
    /// </summary>
    public static bool IsGenerated(string path)
    {
        var normalised = path.Replace('\\', '/');
        var name = normalised[(normalised.LastIndexOf('/') + 1)..];

        if (GeneratedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

        foreach (var suffix in GeneratedSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Segment-wise rather than substring: "dist" must not match "distribution/api.cs",
        // and a file genuinely called "obj.cs" is not in an obj directory.
        var segments = normalised.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (GeneratedDirectories.Contains(segments[i], StringComparer.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>One entry of <c>git diff --numstat -z</c>.</summary>
    public sealed record StatEntry(string Path, string? OldPath, int Added, int Removed, bool IsBinary);

    /// <summary>
    /// Reads <c>--numstat -z</c>.
    ///
    /// The NUL form rather than the readable one, because renames are the case that breaks
    /// the readable one: git prints them as <c>src/{a =&gt; b}/file.cs</c>, a compaction that
    /// has to be undone to get a path back, and there is no way to tell it from a file
    /// genuinely containing braces. Under <c>-z</c> a rename is simply an empty third field
    /// followed by the two paths, each NUL-terminated.
    ///
    /// A binary file reports "-" for both counts, which is also how binaries are detected —
    /// no second command needed.
    /// </summary>
    public static IReadOnlyList<StatEntry> ParseNumstat(string output)
    {
        var entries = new List<StatEntry>();
        var fields = output.Split('\0');

        for (var i = 0; i < fields.Length; i++)
        {
            var record = fields[i];
            if (record.Length == 0) continue;

            var firstTab = record.IndexOf('\t');
            if (firstTab < 0) continue;

            var secondTab = record.IndexOf('\t', firstTab + 1);
            if (secondTab < 0) continue;

            var addedText = record[..firstTab];
            var removedText = record[(firstTab + 1)..secondTab];
            var path = record[(secondTab + 1)..];

            string? oldPath = null;

            // An empty path field means the two that follow are the old and new names.
            if (path.Length == 0)
            {
                if (i + 2 >= fields.Length) break;
                oldPath = fields[i + 1];
                path = fields[i + 2];
                i += 2;
            }

            var isBinary = addedText == "-" || removedText == "-";

            entries.Add(new StatEntry(
                path,
                oldPath,
                isBinary ? 0 : int.TryParse(addedText, out var added) ? added : 0,
                isBinary ? 0 : int.TryParse(removedText, out var removed) ? removed : 0,
                isBinary));
        }

        return entries;
    }

    /// <summary>
    /// Splits a character budget between files so that no one file can crowd out the rest.
    ///
    /// Water-filling: everything that fits in an equal share is granted in full, and whatever
    /// that leaves over is shared again among the files still over their share. The result is
    /// that eight small files and one huge one all get sent, with the huge one truncated —
    /// rather than the huge one arriving whole and the eight being dropped, which is the
    /// outcome of any first-come allocation and the exact failure the roadmap describes.
    /// </summary>
    /// <returns>The number of characters granted to each file, in the order given.</returns>
    public static int[] Allocate(IReadOnlyList<int> sizes, int budget)
    {
        var grants = new int[sizes.Count];
        if (sizes.Count == 0 || budget <= 0) return grants;

        // Ascending, so the cheapest files are satisfied first and release their surplus.
        var order = Enumerable.Range(0, sizes.Count).OrderBy(i => sizes[i]).ToArray();

        var remaining = budget;

        for (var position = 0; position < order.Length; position++)
        {
            var left = order.Length - position;
            var share = remaining / left;
            var index = order[position];

            if (sizes[index] <= share)
            {
                grants[index] = sizes[index];
                remaining -= sizes[index];
                continue;
            }

            // From here every remaining file wants more than its share, so they all take
            // exactly it and there is nothing left to redistribute.
            for (; position < order.Length; position++)
            {
                grants[order[position]] = remaining / (order.Length - position);
                remaining -= remaining / (order.Length - position);
            }

            break;
        }

        return grants;
    }

    /// <summary>
    /// Cuts a file's patch to fit, on hunk boundaries.
    ///
    /// Whole hunks or nothing: half a hunk is not a diff, and a model shown one reads the
    /// truncation as part of the change. When not even the first hunk fits, the header alone
    /// is kept and the file is reported as summarised — knowing that <c>Parser.cs</c> changed
    /// is worth more than knowing what its first forty lines looked like.
    /// </summary>
    /// <returns>The kept text, and whether anything was dropped.</returns>
    public static (string Text, int HunksKept) Truncate(FilePatch patch, int budget)
    {
        var builder = new StringBuilder();
        var kept = 0;

        foreach (var hunk in patch.Hunks)
        {
            var length = hunk.Header.Length + 1 + hunk.Lines.Sum(l => l.Length + 1);
            if (builder.Length + length > budget) break;

            builder.Append(hunk.Header).Append('\n');
            foreach (var line in hunk.Lines) builder.Append(line).Append('\n');
            kept++;
        }

        return (builder.ToString(), kept);
    }

    /// <summary>
    /// Reads the staged change and assembles what to send.
    /// </summary>
    /// <param name="baseRef">
    /// What the index is compared against. Null means HEAD — an ordinary commit. An amend
    /// passes <c>HEAD~1</c>, because the message has to describe the commit that will exist
    /// afterwards, which is the index against the commit being replaced's parent, not the
    /// handful of files added since.
    /// </param>
    public static async Task<DiffDigest> ReadAsync(
        GitCli git,
        string worktreePath,
        int characterBudget,
        string? baseRef = null,
        int maxFilePatches = 40,
        CancellationToken ct = default)
    {
        string[] statArgs = baseRef is null
            ? ["diff", "--cached", "--numstat", "-z", "--no-color"]
            : ["diff", "--cached", "--numstat", "-z", "--no-color", baseRef];

        var stat = await git.TryRunAsync(worktreePath, ct, statArgs).ConfigureAwait(false);
        if (!stat.Success) return new DiffDigest { Files = [], Body = "" };

        var entries = ParseNumstat(stat.StandardOutput);
        if (entries.Count == 0) return new DiffDigest { Files = [], Body = "" };

        // Ordered biggest first only for the purpose of choosing which files are worth
        // fetching at all. The summary below is rebuilt in git's own order, which is
        // alphabetical and reads like a file tree rather than a leaderboard.
        var candidates = entries
            .Where(e => !e.IsBinary && !IsGenerated(e.Path))
            .OrderByDescending(e => e.Added + e.Removed)
            .Take(maxFilePatches)
            .ToArray();

        var patches = await ReadPatchesAsync(git, worktreePath, candidates, baseRef, ct).ConfigureAwait(false);

        var sizes = candidates
            .Select(e => patches.TryGetValue(e.Path, out var p) ? PatchLength(p) : 0)
            .ToArray();

        var grants = Allocate(sizes, characterBudget);

        var notes = new List<DiffFileNote>(entries.Count);
        var body = new StringBuilder();

        foreach (var entry in entries)
        {
            var index = Array.FindIndex(candidates, c => c.Path == entry.Path);

            var state = DiffFileState.Summarised;
            string? reason = null;

            if (entry.IsBinary)
            {
                reason = "binary";
            }
            else if (IsGenerated(entry.Path))
            {
                reason = "generated — patch not sent";
            }
            else if (index < 0)
            {
                reason = "patch not sent — too many files changed";
            }
            else if (patches.TryGetValue(entry.Path, out var patch) && patch.Hunks.Count > 0)
            {
                var (text, hunksKept) = Truncate(patch, grants[index]);

                if (hunksKept == 0)
                {
                    reason = "patch not sent — no budget left";
                }
                else
                {
                    state = hunksKept == patch.Hunks.Count ? DiffFileState.Included : DiffFileState.Truncated;
                    if (state is DiffFileState.Truncated)
                        reason = $"showing {hunksKept} of {patch.Hunks.Count} hunks";

                    body.Append("--- ").Append(entry.Path).Append(" ---\n").Append(text);

                    if (state is DiffFileState.Truncated)
                        body.Append("... (").Append(patch.Hunks.Count - hunksKept).Append(" more hunks not shown)\n");

                    body.Append('\n');
                }
            }
            else
            {
                reason = "no textual diff";
            }

            notes.Add(new DiffFileNote
            {
                Path = entry.Path,
                OldPath = entry.OldPath,
                State = state,
                LinesAdded = entry.Added,
                LinesRemoved = entry.Removed,
                IsBinary = entry.IsBinary,
                Reason = reason,
            });
        }

        return new DiffDigest { Files = notes, Body = body.ToString() };
    }

    /// <summary>
    /// Fetches each candidate's patch, a few at a time.
    ///
    /// One <c>git diff</c> per file rather than one for all of them, because the combined
    /// output has to be split back apart on <c>diff --git a/x b/x</c> lines — and that line
    /// is ambiguous for any path containing a space. Paying for a few extra processes buys
    /// a pathspec git parses unambiguously and the same reader hunk staging already uses.
    /// </summary>
    private static async Task<Dictionary<string, FilePatch>> ReadPatchesAsync(
        GitCli git, string worktreePath, StatEntry[] candidates, string? baseRef, CancellationToken ct)
    {
        var patches = new Dictionary<string, FilePatch>(StringComparer.Ordinal);

        // Bounded rather than all at once: forty concurrent git processes on a cold repo is
        // its own kind of stall, and this runs while the user is watching a button — very
        // possibly on a machine where an agent is already running a build.
        const int batchSize = 4;

        for (var start = 0; start < candidates.Length; start += batchSize)
        {
            var batch = candidates.Skip(start).Take(batchSize).ToArray();

            var tasks = batch
                .Select(entry => PatchBuilder.ReadAsync(
                    git, worktreePath, entry.Path, DiffSide.Staged, baseRef, ct))
                .ToArray();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            for (var i = 0; i < batch.Length; i++) patches[batch[i].Path] = results[i];
        }

        return patches;
    }

    private static int PatchLength(FilePatch patch) =>
        patch.Hunks.Sum(h => h.Header.Length + 1 + h.Lines.Sum(l => l.Length + 1));
}
