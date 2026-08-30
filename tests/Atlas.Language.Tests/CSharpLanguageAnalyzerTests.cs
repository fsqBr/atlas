using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Scanner.Runtime;

namespace Atlas.Language.Tests;

/// <summary>
/// Analyzer tests over a self-contained fixture estate: one SDK-style project,
/// one legacy (non-SDK, packages.config) project and a classic .sln — the two
/// project formats the V0.5 market mixes (spike-validated shapes).
/// </summary>
public class CSharpLanguageAnalyzerTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-lang-fixture").FullName;
    private readonly CSharpLanguageAnalyzer _analyzer = new();
    private LanguageAnalysisResult _result = null!;

    public async Task InitializeAsync()
    {
        WriteFixture();
        _result = await _analyzer.AnalyzeAsync(
            new ContainedArtifactReader(_root), CancellationToken.None);
    }

    [Fact]
    public void Achieves_symbols_tier_without_any_build()
    {
        Assert.Equal(AnalysisTier.SyntacticWithSymbols, _result.TierAchieved);
    }

    [Fact]
    public void Discovers_solution_with_both_projects()
    {
        var solution = Assert.Single(_result.Solutions);
        Assert.Equal(2, solution.ProjectPaths.Count);
    }

    [Fact]
    public void Reads_sdk_project_facts_as_data()
    {
        var sdk = Assert.Single(_result.Projects, p => p.Name == "Modern");

        Assert.True(sdk.IsSdkStyle);
        Assert.Equal("net8.0", sdk.TargetFramework);
        var package = Assert.Single(sdk.PackageReferences);
        Assert.Equal("Newtonsoft.Json", package.Id);
        Assert.Equal("13.0.3", package.Version);
        Assert.Equal(PackageReferenceOrigin.PackageReference, package.Origin);
        Assert.Single(sdk.ProjectReferences);
    }

    [Fact]
    public void Reads_legacy_project_and_packages_config()
    {
        var legacy = Assert.Single(_result.Projects, p => p.Name == "Legacy");

        Assert.False(legacy.IsSdkStyle);
        Assert.Equal("v4.5", legacy.TargetFramework);
        Assert.Equal(2, legacy.PackageReferences.Count);
        Assert.All(legacy.PackageReferences, p =>
            Assert.Equal(PackageReferenceOrigin.PackagesConfig, p.Origin));
        Assert.Equal(["System", "System.Web"], legacy.AssemblyReferences);
    }

    [Fact]
    public void Counts_files_lines_types_and_methods()
    {
        Assert.Equal(3, _result.Totals.FileCount);
        Assert.Equal(3, _result.Totals.TypeCount);
        Assert.Equal(4, _result.Totals.MethodCount);
        Assert.True(_result.Totals.TotalLines > 0);
        Assert.All(_result.Files, f => Assert.False(f.HasSyntaxErrors));
    }

    [Fact]
    public void Measures_cyclomatic_complexity()
    {
        // Branchy: 1 + if + for + && = 4.
        var branchy = Assert.Single(_result.Files, f => f.RelativePath.EndsWith("Branchy.cs"));
        Assert.Equal(4, branchy.MaxCyclomaticComplexity);
        Assert.Equal(4, _result.Totals.MaxCyclomaticComplexity);
    }

    [Fact]
    public void Resolves_symbols_from_bundled_reference_assemblies()
    {
        Assert.NotNull(_result.Symbols);
        Assert.True(_result.Symbols.SampledInvocations >= 2);
        Assert.True(
            _result.Symbols.ResolutionRate >= 0.9,
            $"expected >=90% symbol resolution, got {_result.Symbols.ResolutionRate:P0}");
    }

    [Fact]
    public void Cannot_analyze_workspace_without_csharp()
    {
        var empty = Directory.CreateTempSubdirectory("atlas-lang-empty").FullName;
        try
        {
            Assert.False(_analyzer.CanAnalyze(new ContainedArtifactReader(empty)));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    private void WriteFixture()
    {
        var modern = Path.Combine(_root, "src", "Modern");
        var legacy = Path.Combine(_root, "src", "Legacy");
        Directory.CreateDirectory(modern);
        Directory.CreateDirectory(legacy);

        File.WriteAllText(Path.Combine(_root, "Fixture.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Modern", "src\Modern\Modern.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Legacy", "src\Legacy\Legacy.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        File.WriteAllText(Path.Combine(modern, "Modern.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <ProjectReference Include="..\Legacy\Legacy.csproj" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(modern, "Greeter.cs"), """
            namespace Modern;

            public class Greeter
            {
                public string Greet(string name) => string.Concat("Hi ", name.Trim());
            }
            """);

        File.WriteAllText(Path.Combine(modern, "Branchy.cs"), """
            namespace Modern;

            public class Branchy
            {
                public int Score(int x, bool flag)
                {
                    var total = 0;
                    if (x > 0 && flag)
                    {
                        for (var i = 0; i < x; i++)
                        {
                            total += i;
                        }
                    }

                    return total;
                }

                public int Simple() => 42;
            }
            """);

        File.WriteAllText(Path.Combine(legacy, "Legacy.csproj"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="12.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.5</TargetFrameworkVersion>
                <OutputType>Library</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System" />
                <Reference Include="System.Web" />
                <Compile Include="Handler.cs" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(legacy, "packages.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="EntityFramework" version="6.1.3" targetFramework="net45" />
              <package id="log4net" version="2.0.3" targetFramework="net45" />
            </packages>
            """);

        File.WriteAllText(Path.Combine(legacy, "Handler.cs"), """
            namespace Legacy
            {
                public class Handler
                {
                    public string Handle(string input)
                    {
                        return input.ToUpperInvariant();
                    }
                }
            }
            """);
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

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }
}
