using System.Text;

namespace Chapter.Core.Git;

/// <summary>One hunk of a unified diff, kept as the lines git emitted.</summary>
public sealed record PatchHunk
{
    /// <summary>The <c>@@ -a,b +c,d @@</c> line, including any trailing section heading.</summary>
    public required string Header { get; init; }

    public required int OldStart { get; init; }
    public required int OldCount { get; init; }
    public required int NewStart { get; init; }
    public required int NewCount { get; init; }

    /// <summary>
    /// The hunk's body, verbatim and including the leading marker on each line — a space for
    /// context, <c>+</c> or <c>-</c> for a change, and <c>\</c> for git's
    /// "No newline at end of file" note.
    /// </summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>Text after the second <c>@@</c>, which git fills with the enclosing function.</summary>
    public string Section { get; init; } = "";

    public int AddedLines => Lines.Count(l => l.StartsWith('+'));
    public int RemovedLines => Lines.Count(l => l.StartsWith('-'));
}

/// <summary>A file's unified diff, split into the header git needs and the hunks the user picks from.</summary>
public sealed record FilePatch
{
    /// <summary>Everything before the first hunk — <c>diff --git</c>, mode lines, <c>---</c>/<c>+++</c>.</summary>
    public required IReadOnlyList<string> Header { get; init; }

    public required IReadOnlyList<PatchHunk> Hunks { get; init; }

    /// <summary>True when git said the file is binary, which cannot be staged by hunk.</summary>
    public bool IsBinary { get; init; }

    /// <summary>
    /// Identifies the exact diff these hunks came from.
    ///
    /// This is the app's answer to the race it exists to care about. The user picks hunk 2
    /// of a diff shown at some moment; by the time the click arrives the agent working in
    /// that worktree may have rewritten the file, and "hunk 2" of the re-read diff is then a
    /// different change than the one approved. The front-end sends this back with the
    /// selection and the backend refuses on a mismatch, so the worst case is being asked to
    /// look again rather than silently staging something nobody chose.
    /// </summary>
    public string Fingerprint { get; init; } = "";
}

/// <summary>
/// Builds the partial patches that make hunk and line staging work.
///
/// The one rule this file exists to enforce: a patch is only ever assembled from git's own
/// <c>diff</c> output, never from the text in the editor. They are not the same bytes. Under
/// <c>core.autocrlf</c> the working tree holds CRLF and the index holds LF, so a patch
/// generated from what Monaco is displaying fails to apply — or, worse, applies and rewrites
/// every line ending in the file. Git already knows how to describe the difference; the job
/// here is to take a subset of that description and keep it a valid patch.
/// </summary>
public static class PatchBuilder
{
    /// <summary>
    /// Diffs are read and written as Latin-1, which is not a claim about the file's encoding
    /// — it is the absence of one.
    ///
    /// Every byte from 0x00 to 0xFF maps to exactly one character and back, so decoding,
    /// editing only the ASCII diff markers, and re-encoding returns the original bytes
    /// untouched. Reading as UTF-8 would replace every invalid sequence in a Latin-1 or
    /// Shift-JIS source file with U+FFFD, and the patch written back would corrupt the file
    /// it was meant to stage.
    /// </summary>
    private static readonly Encoding PatchEncoding = Encoding.Latin1;

    /// <summary>
    /// Parses a unified diff for a single file.
    ///
    /// Tolerant of the several shapes git emits — new files, deletions, renames, mode
    /// changes — because all of those differ only in the header, which is copied through
    /// verbatim rather than interpreted.
    /// </summary>
    public static FilePatch Parse(string diff)
    {
        var lines = diff.Split('\n');
        var header = new List<string>();
        var hunks = new List<PatchHunk>();

        var index = 0;

        // Header runs until the first hunk. "Binary files ... differ" appears here instead
        // of any hunk at all, which is how a binary file announces itself.
        var isBinary = false;

        for (; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal)) break;

            if (line.StartsWith("Binary files ", StringComparison.Ordinal)
                || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                isBinary = true;
            }

            // A trailing empty element from the final newline is not a header line.
            if (line.Length > 0 || index < lines.Length - 1) header.Add(line);
        }

        while (index < lines.Length)
        {
            var headerLine = lines[index].TrimEnd('\r');
            if (!headerLine.StartsWith("@@", StringComparison.Ordinal)) break;

            var parsed = ParseHunkHeader(headerLine);
            if (parsed is null) break;

            index++;
            var body = new List<string>();

            // The body ends at the next hunk, the next file, or the end of the diff.
            for (; index < lines.Length; index++)
            {
                var line = lines[index].TrimEnd('\r');

                if (line.StartsWith("@@", StringComparison.Ordinal)) break;
                if (line.StartsWith("diff --git ", StringComparison.Ordinal)) break;

                // The final split element after a trailing newline is empty and is not a
                // context line; a genuine empty context line is " ", with the marker.
                if (line.Length == 0 && index == lines.Length - 1) break;

                if (line.Length == 0) continue;

                body.Add(line);
            }

            hunks.Add(new PatchHunk
            {
                Header = headerLine,
                OldStart = parsed.Value.OldStart,
                OldCount = parsed.Value.OldCount,
                NewStart = parsed.Value.NewStart,
                NewCount = parsed.Value.NewCount,
                Section = parsed.Value.Section,
                Lines = body,
            });
        }

        return new FilePatch
        {
            Header = header,
            Hunks = hunks,
            IsBinary = isBinary,
            Fingerprint = Fingerprint(diff),
        };
    }

    /// <summary>
    /// A short, stable identifier for a diff's exact text. Not a security boundary — it
    /// exists to notice an agent's edit landing between the render and the click, and any
    /// change to the diff at all changes it.
    /// </summary>
    private static string Fingerprint(string diff)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(PatchEncoding.GetBytes(diff));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// Reads <c>@@ -oldStart,oldCount +newStart,newCount @@ section</c>.
    /// A count is omitted when it is 1, which is the case this has to get right.
    /// </summary>
    private static (int OldStart, int OldCount, int NewStart, int NewCount, string Section)? ParseHunkHeader(
        string header)
    {
        var end = header.IndexOf("@@", 2, StringComparison.Ordinal);
        if (end < 0) return null;

        var ranges = header[2..end].Trim().Split(' ');
        if (ranges.Length < 2) return null;

        var old = ParseRange(ranges[0], '-');
        var fresh = ParseRange(ranges[1], '+');
        if (old is null || fresh is null) return null;

        var section = header.Length > end + 2 ? header[(end + 2)..].TrimStart() : "";

        return (old.Value.Start, old.Value.Count, fresh.Value.Start, fresh.Value.Count, section);
    }

    private static (int Start, int Count)? ParseRange(string text, char sign)
    {
        if (text.Length < 2 || text[0] != sign) return null;

        var body = text[1..];
        var comma = body.IndexOf(',');

        if (comma < 0)
            return int.TryParse(body, out var only) ? (only, 1) : null;

        return int.TryParse(body[..comma], out var start) && int.TryParse(body[(comma + 1)..], out var count)
            ? (start, count)
            : null;
    }

    /// <summary>
    /// Which lines of which hunks to include. An empty
    /// <see cref="Lines"/> means whole hunks.
    /// </summary>
    public sealed record Selection
    {
        public IReadOnlyList<int> Hunks { get; init; } = [];

        /// <summary>Hunk index to the positions within that hunk's body the user picked.</summary>
        public IReadOnlyDictionary<int, HashSet<int>> Lines { get; init; } =
            new Dictionary<int, HashSet<int>>();

        public bool IsLineLevel => Lines.Count > 0;
    }

    /// <summary>
    /// Assembles a patch containing only what was selected.
    ///
    /// The line rules are the whole trick, and they are not symmetric. Applying forwards, an
    /// unselected addition is *dropped* — it is not in the file being patched and must not
    /// appear on either side — while an unselected deletion becomes *context*, because the
    /// line is still there and has to be accounted for. Reversing swaps the two, since the
    /// file being patched is then the other side of the comparison. Getting this backwards
    /// produces a patch that applies cleanly and stages the opposite of what was asked.
    /// </summary>
    /// <returns>The patch text, or null when nothing was selected.</returns>
    public static string? Build(FilePatch patch, Selection selection, bool reverse)
    {
        if (patch.IsBinary || patch.Hunks.Count == 0) return null;

        var wanted = selection.Hunks.Count > 0 || selection.IsLineLevel
            ? selection.Hunks.ToHashSet()
            : [.. Enumerable.Range(0, patch.Hunks.Count)];

        // A line-level selection implies its hunks, so the caller does not have to send both.
        foreach (var hunk in selection.Lines.Keys) wanted.Add(hunk);

        var builder = new StringBuilder();
        foreach (var line in patch.Header) builder.Append(line).Append('\n');

        var emitted = 0;

        // Tracks how far the new-side line numbers have drifted from the old side, because
        // hunks the user did not select are not being applied: every later hunk starts where
        // the partial application actually left the file, not where a full application would
        // have. Git tolerates a lot here, but not this.
        var offset = 0;

        for (var i = 0; i < patch.Hunks.Count; i++)
        {
            var hunk = patch.Hunks[i];

            // A skipped hunk contributes nothing. Its old-side lines stay exactly where they
            // were, and since every new-side start below is measured from an old-side start,
            // there is no drift to compensate for — only the hunks actually emitted move the
            // file's line numbering.
            if (!wanted.Contains(i)) continue;

            selection.Lines.TryGetValue(i, out var picked);
            var body = SelectLines(hunk, picked, reverse);

            // A hunk whose every change was deselected reduces to pure context, which is a
            // no-op that git rejects as a corrupt patch rather than ignoring.
            if (!body.Any(l => l.StartsWith('+') || l.StartsWith('-'))) continue;

            var oldCount = body.Count(l => l.StartsWith(' ') || l.StartsWith('-'));
            var newCount = body.Count(l => l.StartsWith(' ') || l.StartsWith('+'));

            var newStart = hunk.OldStart + offset;

            // An empty side is anchored to the line it comes *after*, which is why these
            // subtract one — "-5,0" means "insert after line 5".
            //
            // The clamp is not defensive tidying. Git writes `@@ -0,0 +1,N @@` for a file
            // that does not exist on the old side at all, and 0 - 1 produces the literal
            // text `--1,0`, which git rejects as a corrupt patch. That is every unstage of
            // a newly added file.
            var oldRange = oldCount == 0
                ? $"-{Math.Max(0, hunk.OldStart - 1)},0"
                : $"-{hunk.OldStart},{oldCount}";

            var newRange = newCount == 0
                ? $"+{Math.Max(0, newStart - 1)},0"
                : $"+{newStart},{newCount}";

            var section = hunk.Section.Length > 0 ? " " + hunk.Section : "";
            builder.Append("@@ ").Append(oldRange).Append(' ').Append(newRange).Append(" @@")
                .Append(section).Append('\n');

            foreach (var line in body) builder.Append(line).Append('\n');

            offset += newCount - oldCount;
            emitted++;
        }

        return emitted == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// Rewrites a hunk body to contain only the selected changes, turning the rest into
    /// context or dropping it.
    /// </summary>
    private static List<string> SelectLines(PatchHunk hunk, HashSet<int>? picked, bool reverse)
    {
        // No line filter means the whole hunk, which needs no rewriting at all.
        if (picked is null) return [.. hunk.Lines];

        var body = new List<string>(hunk.Lines.Count);

        for (var position = 0; position < hunk.Lines.Count; position++)
        {
            var line = hunk.Lines[position];
            if (line.Length == 0) continue;

            // "\ No newline at end of file" belongs to the line above it. Kept whenever that
            // line was kept, dropped with it otherwise — orphaned, it makes the patch
            // invalid; missing, git silently appends a newline the file never had.
            if (line[0] == '\\')
            {
                if (body.Count > 0) body.Add(line);
                continue;
            }

            var isSelected = picked.Contains(position);

            switch (line[0])
            {
                case ' ':
                    body.Add(line);
                    break;

                case '+' when isSelected:
                case '-' when isSelected:
                    body.Add(line);
                    break;

                // Unselected. Which way it goes depends on which side of the comparison the
                // file being patched is on.
                case '+':
                    // Forwards the line does not exist yet, so it is dropped. Reversing, it
                    // is in the file and stays as context.
                    if (reverse) body.Add(' ' + line[1..]);
                    break;

                case '-':
                    // The mirror image: forwards the line is present and becomes context;
                    // reversing it is already gone and is dropped.
                    if (!reverse) body.Add(' ' + line[1..]);
                    break;
            }
        }

        return body;
    }

    // -----------------------------------------------------------------------
    // Reading and applying
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the unified diff for one file, on one side of the index.
    ///
    /// Read as bytes and decoded as Latin-1 rather than through the usual UTF-8 path, so a
    /// file that is not UTF-8 survives the round trip.
    /// </summary>
    /// <param name="baseRef">
    /// What the index is compared against, for the staged side only. Null means HEAD, which
    /// is every staging operation. Phase 2 passes <c>HEAD~1</c> to read what an amended
    /// commit would contain.
    /// </param>
    public static async Task<FilePatch> ReadAsync(
        GitCli git, string worktreePath, string path, DiffSide side,
        string? baseRef = null, CancellationToken ct = default)
    {
        // The prefixes are pinned rather than left to the user's config, and this is not
        // cosmetic. `git apply` strips one leading path component by default, so it needs
        // the `a/`…`b/` form — but `diff.noprefix=true` emits none at all and
        // `diff.mnemonicPrefix` emits `i/`…`w/`. Either setting turns every hunk operation
        // into "git diff header lacks filename information", for that user only, on a
        // config line they set years ago for readability.
        string[] common =
        [
            "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", "-U3",
            "--", StagingService.Literal(path),
        ];

        // The base ref goes before the `--`, where git expects a revision; passing it on the
        // unstaged side would compare the working tree against a commit, which is a different
        // question from the one every caller here is asking.
        string[] args = side is DiffSide.Staged
            ? baseRef is null
                ? ["diff", "--cached", .. common]
                : ["diff", "--cached", baseRef, .. common]
            : ["diff", .. common];

        var result = await git.RunBytesAsync(worktreePath, ct, args).ConfigureAwait(false);

        if (!result.Success) return new FilePatch { Header = [], Hunks = [] };

        return Parse(PatchEncoding.GetString(result.StandardOutput));
    }

    /// <summary>
    /// Feeds a patch to <c>git apply</c>.
    ///
    /// Through a temp file rather than stdin, which <see cref="GitCli"/> closes on every
    /// invocation — and outside the worktree, because a patch file written inside it would
    /// appear in the very file list this is about to refresh, and an agent's
    /// <c>git add -A</c> would commit it.
    /// </summary>
    public static async Task<GitMutation> ApplyAsync(
        GitWriter writer,
        string worktreePath,
        string patch,
        string operation,
        bool reverse,
        bool applyToWorkingTree,
        CancellationToken ct = default)
    {
        var file = Path.Combine(
            Path.GetTempPath(), $"chapter-patch-{Guid.NewGuid():N}.diff");

        try
        {
            await File.WriteAllBytesAsync(file, PatchEncoding.GetBytes(patch), ct).ConfigureAwait(false);

            var args = new List<string> { "apply" };

            // Three modes, and only two of them are wanted here. `--cached` changes the index
            // and nothing on disk, which is staging and unstaging. Bare `apply` changes the
            // working tree and nothing else, which is discarding.
            //
            // `--index` — both at once — is the wrong tool for a discard and was the first
            // thing tried: it insists the patch applies to the index as well, and the whole
            // premise of discarding an unstaged hunk is that the change is not in the index.
            // It fails with "does not match index" on exactly the case it was meant for.
            if (!applyToWorkingTree) args.Add("--cached");

            if (reverse) args.Add("--reverse");

            // Line counts in the headers above are computed, and --recount tells git to
            // verify them from the body rather than trust them. It costs nothing and turns a
            // whole class of off-by-one bug into a correct application.
            args.Add("--recount");

            // Whitespace in the patch came out of git's own diff of these exact files, so
            // any "fix" here would be the app editing the user's content.
            args.Add("--whitespace=nowarn");

            args.Add(file);

            return await writer
                .RunAsync(worktreePath, operation, WriteKind.WorkingTree, ct, [.. args])
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover patch in the temp directory is not worth failing the stage over.
            }
        }
    }
}
