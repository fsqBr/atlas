using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Tests.Dependencies;

public class NpmLockfileParserTests
{
    [Fact]
    public void Parses_lockfile_v3_packages_and_nested_node_modules()
    {
        const string json = """
            {
              "name": "web", "lockfileVersion": 3,
              "packages": {
                "": { "name": "web", "version": "1.0.0" },
                "node_modules/lodash": { "version": "4.17.20" },
                "node_modules/express": { "version": "4.18.2" },
                "node_modules/express/node_modules/debug": { "version": "2.6.9" },
                "node_modules/jest": { "version": "29.0.0", "dev": true }
              }
            }
            """;
        var packages = NpmLockfileParser.Parse("web/package-lock.json", json);

        Assert.Equal(["debug", "express", "jest", "lodash"], packages.Select(p => p.Name));
        Assert.Equal("2.6.9", packages.Single(p => p.Name == "debug").Version);
        Assert.True(packages.Single(p => p.Name == "jest").IsDev);
        Assert.False(packages.Single(p => p.Name == "lodash").IsDev);
    }

    [Fact]
    public void Parses_lockfile_v1_dependencies_tree()
    {
        const string json = """
            {
              "lockfileVersion": 1,
              "dependencies": {
                "lodash": { "version": "4.17.11" },
                "express": { "version": "4.16.0", "requires": { "debug": "2.6.9" }, "dependencies": { "debug": { "version": "2.6.9" } } }
              }
            }
            """;
        var packages = NpmLockfileParser.Parse("package-lock.json", json);
        Assert.Equal(["debug", "express", "lodash"], packages.Select(p => p.Name));
    }

    [Fact]
    public void Garbage_is_ignored()
    {
        Assert.Empty(NpmLockfileParser.Parse("x", "{ not json"));
        Assert.Empty(NpmLockfileParser.Parse("x", "[]"));
    }

    [Fact]
    public async Task Osv_bundle_matches_by_ecosystem()
    {
        var bundle = """
            [
              {"id":"GHSA-npm-1","affected":[{"package":{"ecosystem":"npm","name":"lodash"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"4.17.21"}]}]}]},
              {"id":"GHSA-nuget-1","affected":[{"package":{"ecosystem":"NuGet","name":"lodash"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"9.9.9"}]}]}]}
            ]
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bundle));
        var source = new OsvJsonBundleVulnerabilitySource(stream);

        var npm = await source.FindAsync("npm", "lodash", "4.17.20", CancellationToken.None);
        Assert.Equal("GHSA-npm-1", Assert.Single(npm).Id);
        Assert.Empty(await source.FindAsync("npm", "lodash", "4.17.21", CancellationToken.None));

        var nuget = await source.FindAsync("lodash", "1.0.0", CancellationToken.None); // NuGet by default
        Assert.Equal("GHSA-nuget-1", Assert.Single(nuget).Id);
    }
}
