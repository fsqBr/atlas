using System.Text;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Tests.Dependencies;

/// <summary>
/// Maven ecosystem semantics: qualifier versions (RELEASE/Final/GA) must match, and a range with
/// an unparseable event must be dropped whole — never left open-ended (that reported every
/// patched version as vulnerable).
/// </summary>
public class OsvMavenLenientTests
{
    private const string Bundle = """
        [
          {
            "id": "OSV-MAVEN-QUALIFIED",
            "affected": [ {
              "package": { "ecosystem": "Maven", "name": "org.example:core" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "5.2.20.RELEASE" } ] } ]
            } ]
          },
          {
            "id": "OSV-MAVEN-BROKEN",
            "affected": [ {
              "package": { "ecosystem": "Maven", "name": "org.example:broken" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "not##parseable" } ] } ]
            } ]
          }
        ]
        """;

    private static OsvJsonBundleVulnerabilitySource Source() => new(new MemoryStream(Encoding.UTF8.GetBytes(Bundle)));

    [Fact]
    public async Task Maven_release_qualifiers_match_and_respect_the_fixed_bound()
    {
        var source = Source();
        Assert.Single(await source.FindAsync("Maven", "org.example:core", "4.3.30.RELEASE", CancellationToken.None));
        Assert.Single(await source.FindAsync("Maven", "org.example:core", "5.2.19", CancellationToken.None));
        Assert.Empty(await source.FindAsync("Maven", "org.example:core", "5.2.20.RELEASE", CancellationToken.None)); // equals the fixed bound
        Assert.Empty(await source.FindAsync("Maven", "org.example:core", "5.3.39", CancellationToken.None)); // patched: never open-ended
    }

    [Fact]
    public async Task Ranges_with_unparseable_events_are_dropped_entirely()
    {
        var source = Source();
        Assert.Empty(await source.FindAsync("Maven", "org.example:broken", "1.0.0", CancellationToken.None));
        Assert.Empty(await source.FindAsync("Maven", "org.example:broken", "99.0.0", CancellationToken.None));
    }

    [Fact]
    public void Lenient_parser_understands_maven_qualifiers()
    {
        Assert.Equal("4.3.30", OsvJsonBundleVulnerabilitySource.ParseVersionLenient("4.3.30.RELEASE")!.ToNormalizedString());
        Assert.Equal("4.3.11", OsvJsonBundleVulnerabilitySource.ParseVersionLenient("4.3.11.Final")!.ToNormalizedString());
        Assert.True(OsvJsonBundleVulnerabilitySource.ParseVersionLenient("5.0.0-rc1")! < OsvJsonBundleVulnerabilitySource.ParseVersionLenient("5.0.0")!);
        Assert.Null(OsvJsonBundleVulnerabilitySource.ParseVersionLenient("${spring.version}"));
    }
}
