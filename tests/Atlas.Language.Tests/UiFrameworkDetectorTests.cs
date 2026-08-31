using System.Xml.Linq;
using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;

namespace Atlas.Language.Tests;

public class UiFrameworkDetectorTests
{
    private static string? Detect(string xml, string[]? packages = null, string[]? assemblies = null, bool aspx = false, bool svc = false) =>
        UiFrameworkDetector.Detect(
            XDocument.Parse(xml),
            (packages ?? []).Select(p => new PackageReferenceFact(p, "1.0.0", PackageReferenceOrigin.PackageReference)).ToList(),
            assemblies ?? [],
            aspx,
            svc);

    [Fact]
    public void Recognizes_classic_and_modern_web_frameworks()
    {
        const string legacy = """<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>""";
        Assert.Equal(UiFrameworkDetector.WebForms, Detect(legacy, assemblies: ["System.Web"], aspx: true));
        Assert.Equal(UiFrameworkDetector.WebForms, Detect(legacy, assemblies: ["System.Web"]));
        Assert.Equal(UiFrameworkDetector.AspNetMvc5, Detect(legacy, packages: ["Microsoft.AspNet.Mvc"], assemblies: ["System.Web"]));
        Assert.Equal(UiFrameworkDetector.AspNetWebApi2, Detect(legacy, packages: ["Microsoft.AspNet.WebApi.Core"], assemblies: ["System.Web"]));
        Assert.Equal(UiFrameworkDetector.Wcf, Detect(legacy, assemblies: ["System.ServiceModel"], svc: true));

        Assert.Equal(UiFrameworkDetector.AspNetCore, Detect("""<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"""));
        Assert.Equal(UiFrameworkDetector.BlazorServer, Detect("""<Project Sdk="Microsoft.NET.Sdk.Web" />""", packages: ["Microsoft.AspNetCore.Components.Web"]));
        Assert.Equal(UiFrameworkDetector.BlazorWasm, Detect("""<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly" />"""));
        Assert.Equal(UiFrameworkDetector.RazorLibrary, Detect("""<Project Sdk="Microsoft.NET.Sdk.Razor" />"""));
    }

    [Fact]
    public void Recognizes_desktop_mobile_services_and_libraries()
    {
        Assert.Equal(UiFrameworkDetector.Wpf, Detect("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><UseWPF>true</UseWPF><OutputType>WinExe</OutputType></PropertyGroup></Project>"""));
        Assert.Equal(UiFrameworkDetector.WinForms, Detect("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><UseWindowsForms>true</UseWindowsForms></PropertyGroup></Project>"""));
        Assert.Equal(UiFrameworkDetector.WinForms, Detect("""<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>""", assemblies: ["System.Windows.Forms", "System.Drawing"]));
        Assert.Equal(UiFrameworkDetector.Maui, Detect("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><UseMaui>true</UseMaui></PropertyGroup></Project>"""));
        Assert.Equal(UiFrameworkDetector.XamarinForms, Detect("""<Project Sdk="Microsoft.NET.Sdk" />""", packages: ["Xamarin.Forms"]));
        Assert.Equal(UiFrameworkDetector.Silverlight, Detect("""<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><TargetFrameworkIdentifier>Silverlight</TargetFrameworkIdentifier></PropertyGroup></Project>"""));
        Assert.Equal(UiFrameworkDetector.WindowsService, Detect("""<Project Sdk="Microsoft.NET.Sdk.Worker" />"""));
        Assert.Equal(UiFrameworkDetector.ConsoleApp, Detect("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>"""));
        Assert.Equal(UiFrameworkDetector.Library, Detect("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"""));
        Assert.Null(Detect("""<Project />"""));
    }

    [Fact]
    public void No_upgrade_path_set_matches_the_modernization_profile()
    {
        Assert.Contains(UiFrameworkDetector.WebForms, UiFrameworkDetector.NoUpgradePath);
        Assert.DoesNotContain(UiFrameworkDetector.WinForms, UiFrameworkDetector.NoUpgradePath);
        Assert.Equal(UiFrameworkDetector.NoUpgradePath.OrderBy(x => x), Atlas.Domain.Modernization.ModernizationProfile.NoUpgradePathFrameworks.OrderBy(x => x));
    }
}
