using Atlas.Connector.Git;

namespace Atlas.Connector.Tests;

public class GitHistoryReaderTests
{
    [Fact]
    public void Parses_numstat_log_into_per_file_facts()
    {
        var log = "ana@example.com\t2026-08-01T10:00:00-03:00\n" +
                  "10\t2\tsrc/Core/OrderService.cs\n" +
                  "3\t0\tREADME.md\n" +
                  "\n" +
                  "Bob@Example.com\t2026-08-10T09:00:00-03:00\n" +
                  "5\t5\tsrc/Core/OrderService.cs\n" +
                  "-\t-\tassets/logo.png\n" +
                  "\n" +
                  "ana@example.com\t2026-08-20T09:00:00-03:00\n" +
                  "1\t1\tsrc/Core/OrderService.cs\n";

        var facts = GitHistoryReader.Parse(log);

        var service = facts.Single(f => f.RelativePath == "src/Core/OrderService.cs");
        Assert.Equal(3, service.Commits);
        Assert.Equal(16, service.LinesAdded);
        Assert.Equal(8, service.LinesDeleted);
        Assert.Equal(2, service.Authors); // case-insensitive e-mail
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero), service.LastChangeUtc!.Value.ToUniversalTime());

        var logo = facts.Single(f => f.RelativePath == "assets/logo.png");
        Assert.Equal(1, logo.Commits);
        Assert.Equal(0, logo.LinesAdded); // binary numstat "-" ignored

        Assert.Equal("src/Core/OrderService.cs", facts[0].RelativePath); // most churned first
    }

    [Fact]
    public async Task Disabled_or_non_repository_yields_nothing()
    {
        Assert.Empty(await new GitHistoryReader(new GitConnectorOptions { HistoryMonths = 0 }).ReadAsync(Path.GetTempPath(), CancellationToken.None));
        Assert.Empty(await new GitHistoryReader(new GitConnectorOptions { HistoryMonths = 12 }).ReadAsync(Path.GetTempPath(), CancellationToken.None));
        Assert.False(new GitHistoryReader().Enabled);
    }
}
