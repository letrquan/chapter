using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Pure parser tests over captured git output. No git process, no filesystem — these are
/// the guards against the failure mode that matters most here: a parsing slip that drops
/// files from the changed set, which looks identical to "the agent didn't touch anything".
/// </summary>
public class WorktreeParsingTests
{
    [Fact]
    public void Parses_multiple_worktrees_with_branches()
    {
        // Captured verbatim from the book repo.
        const string porcelain = """
            worktree I:/MyProject/02-AI-ML-Projects/book
            HEAD 425795f0a21a2b4eceefcdf89f95e72e892193ac
            branch refs/heads/main

            worktree I:/MyProject/02-AI-ML-Projects/book-review
            HEAD 8edf2b39c99e47e344cdee5e2d5667781b4a1465
            branch refs/heads/feat/review

            """;

        var worktrees = WorktreeService.Parse(porcelain);

        Assert.Equal(2, worktrees.Count);
        Assert.True(worktrees[0].IsMain);
        Assert.Equal("main", worktrees[0].Branch);
        Assert.False(worktrees[1].IsMain);
        Assert.Equal("feat/review", worktrees[1].Branch);
        Assert.Equal("feat/review", worktrees[1].DisplayName);
    }

    [Fact]
    public void Flags_prunable_worktree_with_reason()
    {
        // Captured verbatim from the heat repo, which has one of these right now.
        const string porcelain = """
            worktree I:/MyProject/02-AI-ML-Projects/heat
            HEAD 04e6bc9bdafa997d4e6adf59d7161fca83e4f049
            branch refs/heads/migrate/tg-v2-spectre

            worktree I:/MyProject/02-AI-ML-Projects/heat/.worktrees/work-2026-06-23
            HEAD 2e0c7d39bab7a1e6258e19245e4973b5cda53aa0
            branch refs/heads/work-2026-06-23
            prunable gitdir file points to non-existent location

            """;

        var worktrees = WorktreeService.Parse(porcelain);

        Assert.Equal(2, worktrees.Count);
        Assert.False(worktrees[0].IsPrunable);
        Assert.True(worktrees[1].IsPrunable);
        Assert.Equal("gitdir file points to non-existent location", worktrees[1].PrunableReason);
        Assert.False(worktrees[1].IsUsable);
    }

    [Fact]
    public void Handles_detached_and_bare_worktrees()
    {
        const string porcelain = """
            worktree /repo
            bare

            worktree /repo/detached
            HEAD abc1234567890abcdef1234567890abcdef12345
            detached

            """;

        var worktrees = WorktreeService.Parse(porcelain);

        Assert.True(worktrees[0].IsBare);
        Assert.True(worktrees[1].IsDetached);
        Assert.Null(worktrees[1].Branch);
        Assert.Equal("(abc1234)", worktrees[1].DisplayName);
    }

    [Fact]
    public void Windows_paths_are_normalised_to_backslashes()
    {
        const string porcelain = "worktree I:/MyProject/book\nHEAD abc\nbranch refs/heads/main\n";

        var worktree = WorktreeService.Parse(porcelain).Single();

        Assert.Equal(OperatingSystem.IsWindows() ? @"I:\MyProject\book" : "I:/MyProject/book", worktree.Path);
    }
}

public class NameStatusParsingTests
{
    [Fact]
    public void Parses_simple_statuses()
    {
        var output = Nul("M", "src/Foo.cs", "A", "src/New.cs", "D", "src/Gone.cs");

        var files = DiffService.ParseNameStatus(output);

        Assert.Equal(3, files.Count);
        Assert.Equal(ChangeKind.Modified, files[0].Kind);
        Assert.Equal(ChangeKind.Added, files[1].Kind);
        Assert.Equal(ChangeKind.Deleted, files[2].Kind);
    }

    [Fact]
    public void Rename_keeps_both_paths_and_is_not_split_into_add_plus_delete()
    {
        var output = Nul("R100", "src/Old.cs", "src/New.cs", "M", "src/Other.cs");

        var files = DiffService.ParseNameStatus(output);

        Assert.Equal(2, files.Count);
        Assert.Equal(ChangeKind.Renamed, files[0].Kind);
        Assert.Equal("src/Old.cs", files[0].OldPath);
        Assert.Equal("src/New.cs", files[0].Path);
        Assert.Equal(100, files[0].Similarity);

        // The entry after a rename must still parse: consuming the wrong number of
        // fields here would shift every subsequent record.
        Assert.Equal("src/Other.cs", files[1].Path);
    }

    [Fact]
    public void Base_side_uses_old_path_for_renames()
    {
        var file = DiffService.ParseNameStatus(Nul("R090", "a/Old.cs", "b/New.cs")).Single();

        Assert.Equal("a/Old.cs", file.BasePath);
        Assert.True(file.HasBaseSide);
        Assert.True(file.HasWorkingSide);
    }

    private static string Nul(params string[] fields) => string.Join('\0', fields) + '\0';
}

public class NumstatParsingTests
{
    [Fact]
    public void Associates_counts_with_paths()
    {
        var output = "12\t3\tsrc/Foo.cs\0" + "0\t45\tsrc/Bar.cs\0";

        var stats = DiffService.ParseNumstat(output);

        Assert.Equal((12, 3, false), stats["src/Foo.cs"]);
        Assert.Equal((0, 45, false), stats["src/Bar.cs"]);
    }

    [Fact]
    public void Rename_record_keys_on_new_path_and_does_not_desync_later_records()
    {
        // git's rename form: counts, an empty path field, then old and new paths.
        var output = "5\t2\t\0src/Old.cs\0src/New.cs\0" + "7\t1\tsrc/After.cs\0";

        var stats = DiffService.ParseNumstat(output);

        Assert.Equal((5, 2, false), stats["src/New.cs"]);
        Assert.DoesNotContain("src/Old.cs", stats.Keys);

        // The real regression guard: a record following a rename must still line up.
        Assert.Equal((7, 1, false), stats["src/After.cs"]);
    }

    [Fact]
    public void Binary_files_are_flagged_rather_than_counted()
    {
        var stats = DiffService.ParseNumstat("-\t-\tassets/logo.png\0");

        var (added, removed, isBinary) = stats["assets/logo.png"];
        Assert.True(isBinary);
        Assert.Equal(0, added);
        Assert.Equal(0, removed);
    }
}

public class WorkingStateParsingTests
{
    [Fact]
    public void Extracts_untracked_paths()
    {
        var output = "1 .M N... 100644 100644 100644 abc abc src/Changed.cs\0"
                   + "? src/BrandNew.cs\0"
                   + "? docs/notes.md\0";

        var (untracked, _) = DiffService.ParseWorkingState(output);

        Assert.Equal(["src/BrandNew.cs", "docs/notes.md"], untracked);
    }

    [Fact]
    public void Rename_entry_consumes_its_trailing_origin_path_field()
    {
        // Type-2 entries are followed by a separate NUL-terminated origin path. Failing to
        // consume it makes the origin path parse as the next entry.
        var output = "2 R. N... 100644 100644 100644 abc abc R100 src/New.cs\0src/Old.cs\0"
                   + "? src/Untracked.cs\0";

        var (untracked, _) = DiffService.ParseWorkingState(output);

        Assert.Equal(["src/Untracked.cs"], untracked);
    }

    [Fact]
    public void Dirty_set_covers_staged_and_unstaged_but_not_clean_files()
    {
        // XY: X is index-vs-HEAD, Y is worktree-vs-index. Either being set means the file
        // is not fully committed; ".." means it matches HEAD exactly.
        var output = "1 .M N... 100644 100644 100644 abc abc src/Unstaged.cs\0"
                   + "1 M. N... 100644 100644 100644 abc abc src/Staged.cs\0"
                   + "1 MM N... 100644 100644 100644 abc abc src/Both.cs\0"
                   + "1 .. N... 100644 100644 100644 abc abc src/Clean.cs\0";

        var (_, dirty) = DiffService.ParseWorkingState(output);

        Assert.Equal(
            ["src/Both.cs", "src/Staged.cs", "src/Unstaged.cs"],
            dirty.OrderBy(p => p, StringComparer.Ordinal));
        Assert.DoesNotContain("src/Clean.cs", dirty);
    }

    [Fact]
    public void Paths_containing_spaces_survive_field_splitting()
    {
        // Splitting the entry on every space would truncate this path at "My".
        var output = "1 .M N... 100644 100644 100644 abc abc src/My Folder/A File.cs\0";

        var (_, dirty) = DiffService.ParseWorkingState(output);

        Assert.Contains("src/My Folder/A File.cs", dirty);
    }

    [Fact]
    public void Renamed_entry_is_marked_dirty_at_its_new_path()
    {
        var output = "2 R. N... 100644 100644 100644 abc abc R100 src/New.cs\0src/Old.cs\0";

        var (_, dirty) = DiffService.ParseWorkingState(output);

        Assert.Contains("src/New.cs", dirty);
    }
}

public class FileContentTests
{
    [Fact]
    public void Detects_binary_by_nul_byte()
    {
        var content = FileContent.FromBytes([0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

        Assert.True(content.IsBinary);
    }

    [Fact]
    public void Strips_utf8_bom()
    {
        var content = FileContent.FromBytes([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']);

        Assert.Equal("hi", content.Text);
        Assert.False(content.IsBinary);
    }

    [Fact]
    public void Utf16_text_is_decoded_rather_than_treated_as_binary()
    {
        // UTF-16 is riddled with NUL bytes; the BOM has to win over the binary probe.
        byte[] bytes = [0xFF, 0xFE, (byte)'h', 0x00, (byte)'i', 0x00];

        var content = FileContent.FromBytes(bytes);

        Assert.False(content.IsBinary);
        Assert.Equal("hi", content.Text);
    }
}
