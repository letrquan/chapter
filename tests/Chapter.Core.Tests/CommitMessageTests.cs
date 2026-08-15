using Chapter.Core;
using Chapter.Core.Git;

namespace Chapter.Core.Tests;

/// <summary>
/// Message review, trailers and argument building — the half of committing that needs no
/// repository at all, and so is tested without one.
/// </summary>
public class CommitMessageTests
{
    // -----------------------------------------------------------------------
    // Reviewing
    // -----------------------------------------------------------------------

    [Fact]
    public void A_subject_and_body_are_split_on_the_blank_line()
    {
        var review = CommitMessageReader.Review("fix the parser\n\nIt was eating the last token.\nNow it does not.");

        Assert.Equal("fix the parser", review.Subject);
        Assert.Equal("It was eating the last token.\nNow it does not.", review.Body);
        Assert.False(review.HasErrors);
    }

    [Fact]
    public void A_textarea_full_of_CRLF_is_measured_as_git_will_store_it()
    {
        // A message typed into a textarea arrives with CRLF. Counting those as content makes
        // every subject two characters longer than git records, and puts a phantom character
        // on the end of the body.
        var review = CommitMessageReader.Review("subject\r\n\r\nbody line one\r\nbody line two");

        Assert.Equal("subject", review.Subject);
        Assert.Equal("body line one\nbody line two", review.Body);
        Assert.False(review.HasErrors);
    }

    [Fact]
    public void An_empty_message_is_an_error_rather_than_a_warning()
    {
        var review = CommitMessageReader.Review("   \n\n  ");

        Assert.True(review.IsEmpty);
        Assert.True(review.HasErrors);
    }

    [Fact]
    public void A_body_that_starts_on_the_second_line_is_reported()
    {
        var review = CommitMessageReader.Review("subject\nbody with no blank line");

        Assert.True(review.HasErrors);
        Assert.Contains(review.Problems, p => p.Message.Contains("second line must be blank"));
    }

    [Fact]
    public void A_one_line_message_is_not_accused_of_a_missing_blank_line()
    {
        // There is no second line to be blank. Reporting one would flag most good commits.
        var review = CommitMessageReader.Review("fix the parser");

        Assert.False(review.HasErrors);
        Assert.Empty(review.Problems);
    }

    [Theory]
    [InlineData("feat: add the thing", "feat", null, false)]
    [InlineData("fix(parser): stop eating tokens", "fix", "parser", false)]
    [InlineData("refactor(core)!: rename the writer", "refactor", "core", true)]
    public void Conventional_subjects_are_parsed_into_their_parts(
        string subject, string type, string? scope, bool breaking)
    {
        var review = CommitMessageReader.Review(subject);

        Assert.Equal(type, review.Type);
        Assert.Equal(scope, review.Scope);
        Assert.Equal(breaking, review.IsBreaking);
    }

    [Theory]
    [InlineData("Fix: the parser")]           // capitalised — not a conventional type
    [InlineData("update README: add badges")] // a colon, but not in the leading position
    [InlineData("feat:no space after colon")]
    public void A_subject_that_merely_contains_a_colon_is_not_a_conventional_commit(string subject)
    {
        Assert.Null(CommitMessageReader.Review(subject).Type);
    }

    [Fact]
    public void Conventional_commits_are_only_enforced_where_the_repository_asked_for_them()
    {
        const string subject = "just a plain subject";

        Assert.False(CommitMessageReader.Review(subject).HasErrors);

        var strict = new CommitMessagePolicy { RequireConventionalCommit = true };
        Assert.True(CommitMessageReader.Review(subject, strict).HasErrors);
    }

    [Fact]
    public void An_unknown_type_is_rejected_only_against_a_configured_list()
    {
        var policy = new CommitMessagePolicy
        {
            RequireConventionalCommit = true,
            Types = ["feat", "fix"],
        };

        Assert.False(CommitMessageReader.Review("feat: add it", policy).HasErrors);

        var review = CommitMessageReader.Review("chore: tidy up", policy);
        Assert.True(review.HasErrors);
        Assert.Contains(review.Problems, p => p.Message.Contains("chore"));

        // An empty list means "any lower-case word is a type", which is what most repos
        // using the format actually enforce.
        var loose = new CommitMessagePolicy { RequireConventionalCommit = true, Types = [] };
        Assert.False(CommitMessageReader.Review("chore: tidy up", loose).HasErrors);
    }

    [Fact]
    public void A_long_subject_warns_but_never_blocks()
    {
        var review = CommitMessageReader.Review(new string('x', 90));

        Assert.NotEmpty(review.Problems);
        Assert.All(review.Problems, p => Assert.Equal(MessageSeverity.Warning, p.Severity));
        Assert.False(review.HasErrors);
    }

    // -----------------------------------------------------------------------
    // Co-authors
    // -----------------------------------------------------------------------

    [Fact]
    public void A_co_author_round_trips_through_the_trailer_git_recognises()
    {
        var parsed = CoAuthor.Parse("Ada Lovelace <ada@example.com>");

        Assert.NotNull(parsed);
        Assert.Equal("Ada Lovelace", parsed!.Name);
        Assert.Equal("ada@example.com", parsed.Email);
        Assert.Equal("Co-authored-by: Ada Lovelace <ada@example.com>", parsed.ToTrailer());
    }

    [Theory]
    [InlineData("Ada Lovelace")]
    [InlineData("<ada@example.com>")]
    [InlineData("")]
    public void A_co_author_without_both_halves_is_refused_rather_than_guessed(string text)
    {
        // Git accepts a malformed trailer and every tool that reads them ignores it, which
        // looks exactly like the app losing the co-author.
        Assert.Null(CoAuthor.Parse(text));
    }

    // -----------------------------------------------------------------------
    // Arguments
    // -----------------------------------------------------------------------

    [Fact]
    public void A_message_is_passed_as_one_argument_so_its_newlines_survive()
    {
        var args = new CommitRequest { Message = "subject\n\nbody" }.ToArguments();

        var messageIndex = Array.IndexOf(args, "-m");
        Assert.True(messageIndex >= 0);
        Assert.Equal("subject\n\nbody", args[messageIndex + 1]);
    }

    [Fact]
    public void Cleanup_is_stated_explicitly_so_a_repo_setting_cannot_eat_the_message()
    {
        // commit.cleanup=strip or =scissors would silently remove any line starting with
        // '#' — which in a message about an issue number is content, not a comment.
        Assert.Contains("--cleanup=whitespace", new CommitRequest { Message = "x" }.ToArguments());
    }

    [Fact]
    public void Signing_is_left_to_the_repository_unless_explicitly_chosen()
    {
        var byDefault = new CommitRequest { Message = "x" }.ToArguments();
        Assert.DoesNotContain("--gpg-sign", byDefault);
        Assert.DoesNotContain("--no-gpg-sign", byDefault);

        Assert.Contains("--gpg-sign", new CommitRequest { Message = "x", Sign = true }.ToArguments());
        Assert.Contains("--no-gpg-sign", new CommitRequest { Message = "x", Sign = false }.ToArguments());
    }

    [Fact]
    public void Reusing_the_previous_message_sends_no_message_at_all()
    {
        var args = new CommitRequest { Amend = true, ReuseMessage = true }.ToArguments();

        Assert.Contains("--amend", args);
        Assert.Contains("--no-edit", args);
        Assert.DoesNotContain("-m", args);
    }

    [Fact]
    public void Reuse_is_ignored_without_amend_because_there_is_nothing_to_reuse()
    {
        var args = new CommitRequest { Message = "fresh", ReuseMessage = true }.ToArguments();

        Assert.Contains("-m", args);
        Assert.DoesNotContain("--no-edit", args);
    }

    // -----------------------------------------------------------------------
    // Per-repository policy
    // -----------------------------------------------------------------------

    [Fact]
    public void A_worktree_inherits_the_policy_of_the_repository_containing_it()
    {
        var settings = new AppSettings
        {
            CommitPolicies =
            {
                [@"C:\work\app"] = new CommitMessagePolicy { RequireConventionalCommit = true },
            },
        };

        Assert.True(settings.CommitPolicyFor(@"C:\work\app").RequireConventionalCommit);
        Assert.True(settings.CommitPolicyFor(@"C:\work\app\.worktrees\feature").RequireConventionalCommit);

        // A merely similar path is a different project.
        Assert.False(settings.CommitPolicyFor(@"C:\work\app-legacy").RequireConventionalCommit);
        Assert.False(settings.CommitPolicyFor(@"C:\work\other").RequireConventionalCommit);
    }

    [Fact]
    public void The_most_specific_configured_path_wins()
    {
        var settings = new AppSettings
        {
            CommitPolicies =
            {
                [@"C:\work\app"] = new CommitMessagePolicy { SubjectLimit = 72 },
                [@"C:\work\app\.worktrees\strict"] = new CommitMessagePolicy { SubjectLimit = 50 },
            },
        };

        Assert.Equal(50, settings.CommitPolicyFor(@"C:\work\app\.worktrees\strict").SubjectLimit);
        Assert.Equal(72, settings.CommitPolicyFor(@"C:\work\app\.worktrees\other").SubjectLimit);
    }
}
