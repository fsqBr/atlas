using Atlas.Language.CSharp;

namespace Atlas.Language.Tests;

public class RestoredReferencesTests
{
    [Fact]
    public void Reads_compile_assets_of_the_first_runtime_less_target_against_package_folders()
    {
        var sep = Path.DirectorySeparatorChar;
        const string assets = """
            {
              "version": 3,
              "targets": {
                "net8.0": {
                  "Newtonsoft.Json/13.0.3": { "type": "package", "compile": { "lib/net6.0/Newtonsoft.Json.dll": {} } },
                  "Microsoft.NETCore.Platforms/1.1.0": { "type": "package", "compile": { "lib/netstandard1.0/_._": {} } },
                  "Shop.Core/1.0.0": { "type": "project", "compile": { "bin/placeholder/Shop.Core.dll": {} } }
                },
                "net8.0/win-x64": { "Newtonsoft.Json/13.0.3": { "type": "package", "compile": { "lib/net6.0/Newtonsoft.Json.dll": {} } } }
              },
              "libraries": {
                "Newtonsoft.Json/13.0.3": { "type": "package", "path": "newtonsoft.json/13.0.3" },
                "Shop.Core/1.0.0": { "type": "project", "path": "../Shop.Core/Shop.Core.csproj" }
              },
              "packageFolders": { "/home/app/.nuget/packages/": {}, "/usr/share/dotnet/sdk/NuGetFallbackFolder": {} }
            }
            """;

        var paths = RestoredReferences.ReadAssets(assets);

        Assert.Contains(paths, p => p.Replace(sep, '/').EndsWith("newtonsoft.json/13.0.3/lib/net6.0/Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase) && p.StartsWith("/home/app/.nuget/packages", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.StartsWith("/usr/share/dotnet/sdk/NuGetFallbackFolder", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.EndsWith("_._", StringComparison.Ordinal));
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Empty_or_malformed_assets_yield_nothing()
    {
        Assert.Empty(RestoredReferences.ReadAssets("""{"version":3}"""));
        Assert.Empty(RestoredReferences.ReadAssets("""{"targets":{},"packageFolders":{"/x/":{}}}"""));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => RestoredReferences.ReadAssets("{nope"));
    }

    [Fact]
    public async Task Disabled_tier2_restores_nothing_and_a_missing_sdk_fails_soft()
    {
        var disabled = new RestoredReferences(new Tier2Options { Enabled = false });
        Assert.Empty(await disabled.RestoreAsync(Path.GetTempPath(), [new Atlas.Language.Abstractions.ProjectFact("a.csproj", "a", true, "net8.0", [], [], [])], [], CancellationToken.None));

        var missingSdk = new RestoredReferences(new Tier2Options { Enabled = true, DotnetPath = "definitely-not-dotnet-" + Guid.NewGuid().ToString("N"), TimeoutMinutes = 1 });
        Assert.Empty(await missingSdk.RestoreAsync(Path.GetTempPath(), [new Atlas.Language.Abstractions.ProjectFact("a.csproj", "a", true, "net8.0", [], [], [])], [], CancellationToken.None));
    }
}
