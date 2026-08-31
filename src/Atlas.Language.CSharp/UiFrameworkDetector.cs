using System.Xml.Linq;
using Atlas.Language.Abstractions;

namespace Atlas.Language.CSharp;

/// <summary>
/// Names the presentation/hosting framework of a project from its project file
/// alone (SDK, properties, references, packages) plus the presence of .aspx/.svc
/// files next to it. Deterministic vocabulary shared with the modernization
/// engine: WebForms, AspNetMvc5, AspNetWebApi2, AspNetCore, BlazorServer,
/// BlazorWasm, RazorLibrary, WinForms, Wpf, Maui, XamarinForms, Silverlight, Wcf,
/// WindowsService, ConsoleApp, Library.
/// </summary>
public static class UiFrameworkDetector
{
    public const string WebForms = "WebForms";
    public const string AspNetMvc5 = "AspNetMvc5";
    public const string AspNetWebApi2 = "AspNetWebApi2";
    public const string AspNetCore = "AspNetCore";
    public const string BlazorServer = "BlazorServer";
    public const string BlazorWasm = "BlazorWasm";
    public const string RazorLibrary = "RazorLibrary";
    public const string WinForms = "WinForms";
    public const string Wpf = "Wpf";
    public const string Maui = "Maui";
    public const string XamarinForms = "XamarinForms";
    public const string Silverlight = "Silverlight";
    public const string Wcf = "Wcf";
    public const string WindowsService = "WindowsService";
    public const string ConsoleApp = "ConsoleApp";
    public const string Library = "Library";

    /// <summary>Frameworks with no supported path onto modern .NET (rewrite required for that layer).</summary>
    public static readonly IReadOnlySet<string> NoUpgradePath = new HashSet<string>(StringComparer.Ordinal) { WebForms, AspNetMvc5, AspNetWebApi2, Silverlight, Wcf, XamarinForms };

    public static readonly IReadOnlySet<string> Web = new HashSet<string>(StringComparer.Ordinal) { WebForms, AspNetMvc5, AspNetWebApi2, AspNetCore, BlazorServer, BlazorWasm, RazorLibrary };

    public static readonly IReadOnlySet<string> Desktop = new HashSet<string>(StringComparer.Ordinal) { WinForms, Wpf };

    public static string? Detect(XDocument project, IReadOnlyList<PackageReferenceFact> packages, IReadOnlyList<string> assemblyReferences, bool hasAspxFiles, bool hasSvcFiles)
    {
        var ns = project.Root!.GetDefaultNamespace();
        var sdk = project.Root.Attribute("Sdk")?.Value ?? "";
        string Prop(string name) => project.Descendants(ns + name).FirstOrDefault()?.Value.Trim() ?? "";
        bool Package(string prefix) => packages.Any(p => p.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        bool Assembly(string name) => assemblyReferences.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        var outputType = Prop("OutputType");

        if (Prop("TargetFrameworkIdentifier").Equals("Silverlight", StringComparison.OrdinalIgnoreCase))
        {
            return Silverlight;
        }

        if (Prop("UseMaui").Equals("true", StringComparison.OrdinalIgnoreCase) || sdk.Contains("Maui", StringComparison.OrdinalIgnoreCase))
        {
            return Maui;
        }

        if (Package("Xamarin.Forms"))
        {
            return XamarinForms;
        }

        if (Package("Microsoft.AspNetCore.Components.WebAssembly"))
        {
            return BlazorWasm;
        }

        if (sdk.Equals("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase))
        {
            return BlazorWasm;
        }

        if (sdk.Equals("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase))
        {
            return RazorLibrary;
        }

        if (hasAspxFiles || (Assembly("System.Web") && !Package("Microsoft.AspNet.Mvc") && !Package("Microsoft.AspNet.WebApi")))
        {
            return WebForms;
        }

        if (Package("Microsoft.AspNet.Mvc"))
        {
            return AspNetMvc5;
        }

        if (Package("Microsoft.AspNet.WebApi"))
        {
            return AspNetWebApi2;
        }

        if (sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
        {
            return Package("Microsoft.AspNetCore.Components") ? BlazorServer : AspNetCore;
        }

        if (hasSvcFiles || Assembly("System.ServiceModel") || Package("System.ServiceModel.Http") || Package("CoreWCF"))
        {
            return Wcf;
        }

        if (Prop("UseWPF").Equals("true", StringComparison.OrdinalIgnoreCase) || Assembly("PresentationFramework"))
        {
            return Wpf;
        }

        if (Prop("UseWindowsForms").Equals("true", StringComparison.OrdinalIgnoreCase) || Assembly("System.Windows.Forms"))
        {
            return WinForms;
        }

        if (Assembly("System.ServiceProcess") || Package("Microsoft.Extensions.Hosting.WindowsServices") || sdk.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase))
        {
            return WindowsService;
        }

        if (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
        {
            return ConsoleApp;
        }

        return outputType.Length == 0 && sdk.Length == 0 && packages.Count == 0 && assemblyReferences.Count == 0 ? null : Library;
    }
}
