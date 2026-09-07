using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Scanner.Runtime;

namespace Atlas.Language.Tests;

/// <summary>Modern repos put TargetFramework in Directory.Build.props and versions in Directory.Packages.props.</summary>
public class ProjectInheritanceTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-props").FullName;
    private LanguageAnalysisResult _result = null!;

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="MediatR" Version="12.4.1" />
                <PackageVersion Include="FluentValidation" Version="11.9.0" />
              </ItemGroup>
            </Project>
            """);

        var domain = Path.Combine(_root, "src", "Domain");
        Directory.CreateDirectory(domain);
        File.WriteAllText(Path.Combine(domain, "Domain.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><RootNamespace>App.Domain</RootNamespace></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="MediatR" />
                <PackageReference Include="FluentValidation" VersionOverride="11.10.0" />
                <PackageReference Include="Unknown.Package" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(domain, "Entity.cs"), "namespace App.Domain { public class Entity { } }");

        // A nested override: nearer Directory.Build.props wins for this subtree.
        var legacy = Path.Combine(_root, "src", "Legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Directory.Build.props"),
            "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(legacy, "Legacy.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        _result = await new CSharpLanguageAnalyzer().AnalyzeAsync(new ContainedArtifactReader(_root), CancellationToken.None);
    }

    [Fact]
    public void Inherits_target_framework_from_nearest_directory_build_props()
    {
        Assert.Equal("net10.0", _result.Projects.Single(p => p.Name == "Domain").TargetFramework);
        Assert.Equal("net8.0", _result.Projects.Single(p => p.Name == "Legacy").TargetFramework);
    }

    [Fact]
    public void Resolves_central_package_versions_and_overrides()
    {
        var packages = _result.Projects.Single(p => p.Name == "Domain").PackageReferences;

        Assert.Equal("12.4.1", packages.Single(p => p.Id == "MediatR").Version);
        Assert.Equal("11.10.0", packages.Single(p => p.Id == "FluentValidation").Version);
        Assert.Null(packages.Single(p => p.Id == "Unknown.Package").Version);
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
