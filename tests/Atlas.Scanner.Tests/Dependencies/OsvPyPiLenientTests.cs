using System.Text;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Tests.Dependencies;

/// <summary>
/// PEP 440 semantics on the PyPI ecosystem: post-releases sort ABOVE the release (a patched
/// .post1 pin must not be accused), rc/alpha events parse (so their ranges are not dropped),
/// and epochs stay unjudged.
/// </summary>
public class OsvPyPiLenientTests
{
    private const string Bundle = """
        [
          {
            "id": "OSV-PYPI-FIXED",
            "affected": [ {
              "package": { "ecosystem": "PyPI", "name": "urllib-demo" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "1.26.0" } ] } ]
            } ]
          },
          {
            "id": "OSV-PYPI-RC",
            "affected": [ {
              "package": { "ecosystem": "PyPI", "name": "gateway-demo" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "2.0.0rc1" }, { "fixed": "2.0.2" } ] } ]
            } ]
          }
        ]
        """;

    private static OsvJsonBundleVulnerabilitySource Source() => new(new MemoryStream(Encoding.UTF8.GetBytes(Bundle)));

    [Fact]
    public async Task Post_releases_sort_above_the_fixed_bound()
    {
        var source = Source();
        Assert.Single(await source.FindAsync("PyPI", "urllib-demo", "1.25.9", CancellationToken.None));
        Assert.Empty(await source.FindAsync("PyPI", "urllib-demo", "1.26.0", CancellationToken.None));
        Assert.Empty(await source.FindAsync("PyPI", "urllib-demo", "1.26.0.post1", CancellationToken.None)); // patched: PEP 440 post > release
    }

    [Fact]
    public async Task Rc_events_parse_so_their_ranges_are_not_dropped()
    {
        var source = Source();
        Assert.Single(await source.FindAsync("PyPI", "gateway-demo", "2.0.0", CancellationToken.None));
        Assert.Single(await source.FindAsync("PyPI", "gateway-demo", "2.0.1", CancellationToken.None));
        Assert.Empty(await source.FindAsync("PyPI", "gateway-demo", "2.0.2", CancellationToken.None));
        Assert.Empty(await source.FindAsync("PyPI", "gateway-demo", "1.9.0", CancellationToken.None)); // below introduced rc1
    }

    [Fact]
    public async Task Epoch_versions_stay_unjudged()
    {
        var source = Source();
        Assert.Empty(await source.FindAsync("PyPI", "urllib-demo", "1!2.0", CancellationToken.None));
    }
}
