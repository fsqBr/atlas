using Atlas.Connector.Git;

namespace Atlas.Connector.Tests;

/// <summary>Regressions for the 2026-08 rule audit: renames, bot authors and identity aliases in git history.</summary>
public class GitHistoryRenameTests
{
    private static string Commit(string email, string date, params string[] numstats) =>
        "" + email + "\t" + date + "\n" + string.Join("\n", numstats) + "\n";

    [Fact]
    public void Renames_fold_history_into_the_current_path()
    {
        // Newest first, as git log prints: a rename commit, then older commits on the old path.
        var log =
            Commit("ana@corp.com", "2026-08-20T10:00:00-03:00", "3\t1\tsrc/{Old => New}/Billing.cs") +
            Commit("ana@corp.com", "2026-05-10T10:00:00-03:00", "10\t2\tsrc/Old/Billing.cs") +
            Commit("ana@corp.com", "2026-02-01T10:00:00-03:00", "50\t0\tsrc/Old/Billing.cs");

        var fact = Assert.Single(GitHistoryReader.Parse(log));
        Assert.Equal("src/New/Billing.cs", fact.RelativePath);
        Assert.Equal(3, fact.Commits);           // history survives the reorganization
        Assert.Equal(63, fact.LinesAdded);
        Assert.Equal(1, fact.Authors);
        Assert.Equal(2026, fact.FirstChangeUtc!.Value.Year);
        Assert.Equal(2, fact.FirstChangeUtc.Value.Month); // window start = oldest commit, kept per file
    }

    [Fact]
    public void Bots_and_email_aliases_do_not_break_the_single_author_signal()
    {
        var log =
            Commit("49699333+dependabot[bot]@users.noreply.github.com", "2026-08-20T10:00:00Z", "1\t1\tsrc/App.cs") +
            Commit("12345+ana@users.noreply.github.com", "2026-07-01T10:00:00Z", "5\t0\tsrc/App.cs") +
            Commit("ANA@users.noreply.github.com", "2026-06-01T10:00:00Z", "2\t0\tsrc/App.cs");

        var fact = Assert.Single(GitHistoryReader.Parse(log));
        Assert.Equal(3, fact.Commits);
        // dependabot is not an author; 12345+ana and ANA(+tag) are the same GitHub identity.
        Assert.Equal(1, fact.Authors);
    }
}
