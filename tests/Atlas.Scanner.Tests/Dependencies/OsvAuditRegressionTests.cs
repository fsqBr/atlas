using System.Text;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Tests.Dependencies;

/// <summary>Regressions for the 2026-08 rule audit: version notations that were silently skipped and
/// multi-branch advisories that double-counted.</summary>
public class OsvAuditRegressionTests
{
    private const string Bundle = """
        [
          {
            "id": "GHSA-DUP",
            "affected": [
              {
                "package": { "ecosystem": "NuGet", "name": "Acme.Multi" },
                "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "2.1.0" }, { "fixed": "2.1.15" } ] } ]
              },
              {
                "package": { "ecosystem": "NuGet", "name": "Acme.Multi" },
                "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "2.2.0" }, { "fixed": "2.2.8" } ] } ]
              }
            ]
          },
          {
            "id": "GHSA-UNSORTED",
            "affected": [ {
              "package": { "ecosystem": "NuGet", "name": "Acme.Unsorted" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "fixed": "3.0.0" }, { "introduced": "1.0.0" } ] } ]
            } ]
          },
          {
            "id": "GHSA-JSON",
            "affected": [ {
              "package": { "ecosystem": "NuGet", "name": "Newtonsoft.Json" },
              "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "13.0.1" } ] } ]
            } ]
          }
        ]
        """;

    private static OsvJsonBundleVulnerabilitySource Source() =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(Bundle)));

    [Fact]
    public async Task Exact_pin_and_floating_versions_are_matched_not_silently_skipped()
    {
        var source = Source();
        Assert.Single(await source.FindAsync("NuGet", "Newtonsoft.Json", "[12.0.1]", CancellationToken.None));
        Assert.Single(await source.FindAsync("NuGet", "Newtonsoft.Json", "12.*", CancellationToken.None));
        Assert.Empty(await source.FindAsync("NuGet", "Newtonsoft.Json", "[13.0.1]", CancellationToken.None));
        Assert.Empty(await source.FindAsync("NuGet", "Newtonsoft.Json", "$(JsonVersion)", CancellationToken.None));
    }

    [Fact]
    public async Task A_multi_branch_advisory_matches_once_not_once_per_affected_block()
    {
        var matches = await Source().FindAsync("NuGet", "Acme.Multi", "2.1.0", CancellationToken.None);
        Assert.Single(matches);
        Assert.Equal("GHSA-DUP", matches[0].Id);
    }

    [Fact]
    public async Task Out_of_order_events_still_form_a_bounded_range()
    {
        var source = Source();
        Assert.Single(await source.FindAsync("NuGet", "Acme.Unsorted", "2.0.0", CancellationToken.None));
        Assert.Empty(await source.FindAsync("NuGet", "Acme.Unsorted", "3.5.0", CancellationToken.None)); // fixed at 3.0.0 — not an open range
    }
}
