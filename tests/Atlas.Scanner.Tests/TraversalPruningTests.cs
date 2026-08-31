using Atlas.Domain.Workspaces;
using Atlas.Scanner.Runtime;

namespace Atlas.Scanner.Tests;

public class TraversalPruningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-prune").FullName;

    public TraversalPruningTests()
    {
        Write("src/App/App.cs");
        Write("src/App/bin/Debug/App.dll.cs");
        Write("frontend/node_modules/left-pad/index.js");
        Write("frontend/app.js");
        Write("TestResults/run1/coverage.cobertura.xml");
        Write(".git/objects/aa/bb");
        Write("packages/Newtonsoft.Json.13.0.3/lib/net45/Newtonsoft.Json.xml"); // NuGet cache signature
        Write("mono/packages/shared-ui/index.ts");                               // monorepo packages: keep
    }

    [Fact]
    public void Prunes_vcs_build_output_and_node_modules_during_traversal()
    {
        var files = new ContainedArtifactReader(_root).EnumerateFiles("*").ToList();

        Assert.Contains(files, f => f.EndsWith("App.cs"));
        Assert.Contains(files, f => f.EndsWith("app.js"));
        Assert.DoesNotContain(files, f => f.Contains("node_modules"));
        Assert.DoesNotContain(files, f => f.Contains(".git"));
        Assert.DoesNotContain(files, f => f.Contains("bin"));
    }

    [Fact]
    public void Keeps_test_results_traversable_for_coverage_reports()
    {
        var files = new ContainedArtifactReader(_root).EnumerateFiles("*.cobertura.xml").ToList();

        Assert.Single(files);
        Assert.True(WorkspaceFilters.IsBuildOrVendorPath(files[0]), "TestResults is not source, but must stay readable");
    }

    [Fact]
    public void Distinguishes_nuget_cache_from_monorepo_packages_folder()
    {
        var files = new ContainedArtifactReader(_root).EnumerateFiles("*").ToList();

        Assert.DoesNotContain(files, f => f.Contains("Newtonsoft.Json.13.0.3"));
        Assert.Contains(files, f => f.EndsWith("index.ts"));
        Assert.True(WorkspaceFilters.IsNuGetPackagesFolder(Path.Combine(_root, "packages")));
        Assert.False(WorkspaceFilters.IsNuGetPackagesFolder(Path.Combine(_root, "mono", "packages")));
    }

    private void Write(string relative)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
