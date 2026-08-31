using System.Reflection;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.ArchitectureTests;

/// <summary>
/// The lowest-risk guard for "a scanner exists but nobody wired it": rather than replace the
/// explicit DI list with reflection-based auto-registration (which several config-taking scanners
/// can't do cleanly, and which hides what actually runs), we keep the explicit list and simply
/// ASSERT it is complete. Every concrete IScanner / ILanguageAnalyzer found in the shipped
/// assemblies must be resolvable from the worker's composition root — which also proves each one is
/// constructible. A new scanner that is added but never registered fails here instead of silently
/// not running.
/// </summary>
public class RegistrationCompletenessTests
{
    private static ServiceProvider BuildScanningProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Empty configuration: every registration tolerates absent config (null bundle path/keys →
        // null sources), so this exercises the same wiring the worker uses, minus external secrets.
        services.AddAtlasScanning(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static IReadOnlyList<Type> Discover<TService>(string assemblyPrefix)
    {
        var found = new List<Type>();
        foreach (var dll in Directory.GetFiles(AppContext.BaseDirectory, $"{assemblyPrefix}*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (name.EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(dll);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            found.AddRange(assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(TService).IsAssignableFrom(t)));
        }

        return found;
    }

    [Fact]
    public void Every_scanner_in_the_assemblies_is_registered_and_constructible()
    {
        using var provider = BuildScanningProvider();
        var registered = provider.GetServices<IScanner>().Select(s => s.GetType()).ToHashSet();
        var discovered = Discover<IScanner>("Atlas.Scanner.");

        Assert.True(discovered.Count >= 12, $"expected the scanners to be discovered, found {discovered.Count}");
        var missing = discovered.Where(t => !registered.Contains(t)).Select(t => t.FullName).ToList();
        Assert.True(missing.Count == 0, "IScanner implementations present in the assemblies but NOT registered in AddAtlasScanning: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_language_analyzer_in_the_assemblies_is_registered_and_constructible()
    {
        using var provider = BuildScanningProvider();
        var registered = provider.GetServices<ILanguageAnalyzer>().Select(s => s.GetType()).ToHashSet();
        var discovered = Discover<ILanguageAnalyzer>("Atlas.Language.");

        Assert.True(discovered.Count >= 4, $"expected the language analyzers to be discovered, found {discovered.Count}");
        var missing = discovered.Where(t => !registered.Contains(t)).Select(t => t.FullName).ToList();
        Assert.True(missing.Count == 0, "ILanguageAnalyzer implementations present in the assemblies but NOT registered in AddAtlasScanning: " + string.Join(", ", missing));
    }
}
