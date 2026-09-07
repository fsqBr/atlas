using Atlas.Scanner.Quality;

namespace Atlas.Scanner.Tests;

public class OpenCoverParserTests
{
    [Fact]
    public void Parses_opencover_sessions_as_coverage_reports()
    {
        const string xml = """
            <CoverageSession>
              <Summary numSequencePoints="200" visitedSequencePoints="130" sequenceCoverage="65.00" branchCoverage="50.2" />
              <Modules>
                <Module hash="A"><ModuleName>Shop.Core</ModuleName><Summary sequenceCoverage="80.5" /></Module>
                <Module hash="B"><ModuleName>Shop.Web</ModuleName><Summary sequenceCoverage="49.5" /></Module>
                <Module hash="C" skippedDueTo="Filter"><ModuleName>xunit.core</ModuleName><Summary sequenceCoverage="0" /></Module>
              </Modules>
            </CoverageSession>
            """;

        var report = CoberturaParser.TryParse("TestResults/coverage.opencover.xml", xml);

        Assert.NotNull(report);
        Assert.Equal(0.65, report!.LineRate);
        Assert.Equal(["Shop.Core", "Shop.Web"], report.Packages.Select(p => p.Name).ToArray());
        Assert.Equal(0.805, report.Packages[0].LineRate);
    }

    [Fact]
    public void Cobertura_still_parses_and_garbage_is_rejected()
    {
        Assert.NotNull(CoberturaParser.TryParse("c.xml", """<coverage line-rate="0.42"><packages><package name="P" line-rate="0.4"/></packages></coverage>"""));
        Assert.Null(CoberturaParser.TryParse("x.xml", "<CoverageSession><Summary /></CoverageSession>"));
        Assert.Null(CoberturaParser.TryParse("x.xml", "not xml"));
    }
}
