using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// The pure half of Phase 3: turning what git prints about branches, stashes and tags into
/// something the UI can render, with no repository involved.
///
/// The sample output here was captured from git 2.47 rather than written from memory. Two
/// of these parsers exist because the two commands they read disagree about how to spell a
/// field separator, and that is precisely the kind of thing a hand-written fixture would
/// paper over.
/// </summary>
public class RefParsingTests
{
    /// <summary>The byte both <c>%1f</c> and <c>%x1f</c> produce.</summary>
    private const char Sep = '\u001f';

    private static string Row(params string[] fields) => string.Join(Sep, fields);

    // -----------------------------------------------------------------------
    // Branches
    // -----------------------------------------------------------------------

    [Fact]
    public void Reads_a_branch_row_into_every_field_the_ui_shows()
    {
        var output = Row(
            "refs/heads/main", "adb28de9b77a717fc1b013f1880d8f06de71b453",
            "origin/main", "[ahead 3, behind 1]",
            "C:/repos/chapter", "*", "2026-08-17T10:56:41+07:00", "feat: drive the app") + "\n";

        var branch = Assert.Single(BranchService.Parse(output));

        Assert.Equal("main", branch.Name);
        Assert.False(branch.IsRemote);
        Assert.True(branch.IsCurrent);
        Assert.Equal("origin/main", branch.Upstream);
        Assert.Equal(3, branch.Ahead);
        Assert.Equal(1, branch.Behind);
        Assert.False(branch.IsUpstreamGone);
        Assert.Equal("feat: drive the app", branch.Subject);
        Assert.Equal("adb28de", branch.ShortSha);
    }

    [Fact]
    public void Separates_local_from_remote_by_namespace_rather_than_by_the_slash()
    {
        // The trap this guards: a local branch may legitimately be called `feature/login`,
        // so a slash in the shortened name says nothing about which namespace it came from.
        // Reading `refs/heads/` vs `refs/remotes/` is the only thing that settles it.
        var output = string.Join("\n",
            Row("refs/heads/feature/login", "aaa1111", "", "", "", " ", "", "local work"),
            Row("refs/remotes/origin/main", "bbb2222", "", "", "", " ", "", "remote work"));

        var branches = BranchService.Parse(output);

        var local = branches.Single(b => b.Name == "feature/login");
        var remote = branches.Single(b => b.Name == "origin/main");

        Assert.False(local.IsRemote);
        Assert.True(remote.IsRemote);
    }

    [Fact]
    public void Drops_the_symbolic_origin_head_rather_than_listing_it_as_a_branch()
    {
        // origin/HEAD points at another row in the same list. Rendering it produces a
        // duplicate that cannot be checked out and whose name means something different
        // from every other row's.
        var output = string.Join("\n",
            Row("refs/remotes/origin/HEAD", "bbb2222", "", "", "", " ", "", "remote work"),
            Row("refs/remotes/origin/main", "bbb2222", "", "", "", " ", "", "remote work"));

        var branch = Assert.Single(BranchService.Parse(output));
        Assert.Equal("origin/main", branch.Name);
    }

    [Fact]
    public void Carries_the_worktree_holding_a_branch_so_the_ui_can_offer_to_go_there()
    {
        var output = string.Join("\n",
            Row("refs/heads/main", "aaa1111", "", "", "C:/repos/chapter", "*", "", "here"),
            Row("refs/heads/feature", "bbb2222", "", "", "C:/repos/wt-feature", " ", "", "elsewhere"),
            Row("refs/heads/idle", "ccc3333", "", "", "", " ", "", "nowhere"));

        var branches = BranchService.Parse(output);

        var current = branches.Single(b => b.Name == "main");
        var elsewhere = branches.Single(b => b.Name == "feature");
        var idle = branches.Single(b => b.Name == "idle");

        // Checked out here, so it is not "checked out elsewhere" despite having a path.
        Assert.True(current.IsCurrent);
        Assert.False(current.IsCheckedOutElsewhere);

        Assert.True(elsewhere.IsCheckedOutElsewhere);
        Assert.Equal(RepoPathsProbe("C:/repos/wt-feature"), elsewhere.CheckedOutIn);

        Assert.Null(idle.CheckedOutIn);
        Assert.False(idle.IsCheckedOutElsewhere);
    }

    /// <summary>Mirrors the normalisation the parser applies, so the test is not OS-specific.</summary>
    private static string RepoPathsProbe(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    [Theory]
    [InlineData("", null, null, false)]
    [InlineData("[ahead 1]", 1, null, false)]
    [InlineData("[behind 2]", null, 2, false)]
    [InlineData("[ahead 3, behind 1]", 3, 1, false)]
    [InlineData("[gone]", null, null, true)]
    public void Reads_the_ahead_behind_summary_git_prints(string track, int? ahead, int? behind, bool gone)
    {
        var parsed = BranchService.ParseTrack(track);

        Assert.Equal(ahead, parsed.Ahead);
        Assert.Equal(behind, parsed.Behind);
        Assert.Equal(gone, parsed.Gone);
    }

    [Fact]
    public void A_subject_containing_the_separator_is_kept_whole()
    {
        // The subject is the last field and is taken as the remainder of the line. Splitting
        // into a fixed count and reading index 7 would truncate it at the stray byte.
        var output = Row(
            "refs/heads/main", "aaa1111", "", "", "", " ", "",
            $"fix: strip {Sep} from output");

        var branch = Assert.Single(BranchService.Parse(output));
        Assert.Equal($"fix: strip {Sep} from output", branch.Subject);
    }

    [Fact]
    public void An_empty_or_short_row_is_skipped_rather_than_throwing()
    {
        // git prints nothing at all for a repository with no refs, and a truncated read is
        // always possible. Neither should take the branch list down.
        Assert.Empty(BranchService.Parse(""));
        Assert.Empty(BranchService.Parse("\n\n"));
        Assert.Empty(BranchService.Parse(Row("refs/heads/main", "aaa1111")));
    }

    // -----------------------------------------------------------------------
    // Branch names
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("feature/login")]
    [InlineData("release-1.2")]
    [InlineData("fix_thing")]
    public void Accepts_names_git_accepts(string name) => Assert.Null(BranchService.Validate(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("has~tilde")]
    [InlineData("has^caret")]
    [InlineData("has:colon")]
    [InlineData("has?question")]
    [InlineData("has*star")]
    [InlineData("has[bracket")]
    [InlineData("has\\backslash")]
    [InlineData("double..dot")]
    [InlineData("double//slash")]
    [InlineData("at@{brace")]
    [InlineData("@")]
    [InlineData("ends.lock")]
    [InlineData("/leading-slash")]
    [InlineData("trailing-slash/")]
    [InlineData(".leading-dot")]
    [InlineData("nested/.dot")]
    [InlineData("trailing-dot.")]
    public void Rejects_names_git_would_reject(string name) => Assert.NotNull(BranchService.Validate(name));

    // -----------------------------------------------------------------------
    // Stashes
    // -----------------------------------------------------------------------

    [Fact]
    public void Reads_a_stash_list_with_its_origin_branch_split_out()
    {
        var output = string.Join("\n",
            $"f7005b4{Sep}On feature: switching to main{Sep}2026-08-17 10:57:09 +0700",
            $"0eff83b{Sep}On main: from the main worktree{Sep}2026-08-17 10:57:08 +0700");

        var stashes = StashService.Parse(output);

        Assert.Equal(2, stashes.Count);

        Assert.Equal(0, stashes[0].Index);
        Assert.Equal("stash@{0}", stashes[0].Selector);
        Assert.Equal("feature", stashes[0].Branch);
        Assert.Equal("switching to main", stashes[0].Message);

        Assert.Equal(1, stashes[1].Index);
        Assert.Equal("stash@{1}", stashes[1].Selector);
        Assert.Equal("main", stashes[1].Branch);
    }

    [Theory]
    // The form git writes when `stash push` was given a message.
    [InlineData("On main: my message", "main", "my message")]
    // The auto-generated form, when it was not.
    [InlineData("WIP on main: 1234abc the commit subject", "main", "1234abc the commit subject")]
    // A detached HEAD has no branch name to record.
    [InlineData("On (no branch): rescued work", "(no branch)", "rescued work")]
    // Anything else is a message in its own right rather than a parse failure.
    [InlineData("restored 54188b9", null, "restored 54188b9")]
    public void Splits_the_stash_subject_git_stores(string subject, string? branch, string message)
    {
        var parsed = StashService.SplitSubject(subject);

        Assert.Equal(branch, parsed.Branch);
        Assert.Equal(message, parsed.Message);
    }

    [Fact]
    public void A_stash_message_containing_a_colon_keeps_all_of_it()
    {
        // Split on the *first* colon, which is the one after the branch name. Splitting on
        // the last would eat any message written as "fix: something".
        var parsed = StashService.SplitSubject("On main: fix: the parser");

        Assert.Equal("main", parsed.Branch);
        Assert.Equal("fix: the parser", parsed.Message);
    }

    // -----------------------------------------------------------------------
    // Tags
    // -----------------------------------------------------------------------

    [Fact]
    public void Reports_the_commit_a_tag_names_rather_than_the_tag_object()
    {
        // An annotated tag's ref points at a tag object, which points at the commit.
        // Showing the tag object's sha would give a hash matching nothing in the history.
        var output = string.Join("\n",
            Row("v1.0", "tag", "b4fc30cb8efda36e1be50da497a38f0d34a94c20",
                "97be046a959fa89018deb01c3725b6b00f289771", "2026-08-17T10:57:42+07:00", "the 1.0 release"),
            Row("nightly", "commit", "97be046a959fa89018deb01c3725b6b00f289771", "",
                "2026-08-17T10:57:26+07:00", "main side"));

        var tags = TagService.Parse(output);

        var annotated = tags.Single(t => t.Name == "v1.0");
        var lightweight = tags.Single(t => t.Name == "nightly");

        Assert.True(annotated.IsAnnotated);
        Assert.Equal("97be046a959fa89018deb01c3725b6b00f289771", annotated.Sha);
        Assert.Equal("the 1.0 release", annotated.Subject);

        Assert.False(lightweight.IsAnnotated);
        Assert.Equal("97be046a959fa89018deb01c3725b6b00f289771", lightweight.Sha);

        // Both name the same commit, which is the point: one is annotated and one is not,
        // and that difference must not change which commit the UI reports.
        Assert.Equal(annotated.Sha, lightweight.Sha);
    }

    // -----------------------------------------------------------------------
    // Failure classification added for this phase
    // -----------------------------------------------------------------------

    [Theory]
    // `git switch` phrases it one way…
    [InlineData("fatal: 'feature' is already used by worktree at 'C:/repos/wt-feature'")]
    // …and `git branch -d` another. Both contain the phrase the classifier matches.
    [InlineData("error: cannot delete branch 'feature' used by worktree at 'C:/repos/wt-feature'")]
    public void Recognises_a_branch_held_by_another_worktree(string stderr) =>
        Assert.Equal(GitFailure.CheckedOutElsewhere, GitFailureClassifier.Classify(stderr));

    [Fact]
    public void Deleting_an_unmerged_branch_reads_as_losing_work_rather_than_unknown()
    {
        // The distinction that matters: `-d` refusing here is the only thing separating
        // "delete a merged branch" from "abandon commits", and the UI's answer is to offer
        // the forced delete deliberately.
        Assert.Equal(
            GitFailure.WouldLoseChanges,
            GitFailureClassifier.Classify("error: the branch 'wip' is not fully merged"));
    }

    [Fact]
    public void A_successful_mutation_with_something_to_add_says_it_rather_than_just_succeeded()
    {
        // The case this exists for: a stash-and-switch whose restore conflicted. The switch
        // happened, so it is a success — but reporting only "succeeded" would leave the user
        // believing their changes came across when they are still in the stash.
        var mutation = new GitMutation
        {
            Operation = "stash and switch to main",
            WorktreePath = @"C:\repo",
            CommandLine = "git stash pop",
            ExitCode = 0,
            Detail = "Switched to main, but the stashed changes did not restore cleanly. "
                     + "They are still in the stash.",
        };

        Assert.True(mutation.Success);
        Assert.Contains("still in the stash", mutation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("succeeded", mutation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_success_still_reads_as_succeeded()
    {
        var mutation = new GitMutation
        {
            Operation = "switch to main",
            WorktreePath = @"C:\repo",
            CommandLine = "git switch -- main",
            ExitCode = 0,
        };

        Assert.Equal("switch to main succeeded", mutation.Message);
    }

    [Fact]
    public void The_worktree_refusal_wins_over_the_conflict_wording_around_it()
    {
        // git's switch refusal can arrive alongside checkout advice that the conflict and
        // would-lose-changes matchers also hit. The worktree case has a specific, actionable
        // answer — go to that worktree — so it has to be the one reported.
        const string stderr =
            "fatal: 'feature' is already used by worktree at 'C:/repos/wt-feature'\n" +
            "hint: Please commit your changes or stash them before you switch branches.";

        Assert.Equal(GitFailure.CheckedOutElsewhere, GitFailureClassifier.Classify(stderr));
    }
}
