using System.Diagnostics;
using System.Text.Json;
using Atlas.Language.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Language.CSharp;

/// <summary>Tier 2 ("DesignTime") settings: opt-in, needs the .NET SDK inside the worker image.</summary>
public sealed class Tier2Options
{
    public const string SectionName = "Atlas:Scanning:Tier2";

    /// <summary>Off by default: restore evaluates MSBuild logic shipped with the repository (caveat).</summary>
    public bool Enabled { get; set; }

    public string DotnetPath { get; set; } = "dotnet";

    public int TimeoutMinutes { get; set; } = 10;

    /// <summary>Shared NuGet cache between runs; must be writable by the worker (read-only root filesystem otherwise).</summary>
    public string? PackageCache { get; set; }

    /// <summary>Restore at most this many project files per run (solutions count as one).</summary>
    public int MaxProjects { get; set; } = 200;
}

/// <summary>
/// Tier 2 for C#: runs <c>dotnet restore</c> in the workspace (child process, wall-clock
/// limit, no scripts of ours) and turns each project's <c>obj/project.assets.json</c>
/// into metadata references for the Roslyn compilation. Symbol resolution then
/// sees the real package surface instead of netstandard2.0 stubs, which removes a
/// class of false positives in semantic rules. Nothing from the repository is
/// executed by Atlas itself; restore is MSBuild's own evaluation, which is why
/// this stays opt-in and sandboxed in the scan-host process.
/// </summary>
public sealed class RestoredReferences(Tier2Options options, ILogger<RestoredReferences>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<RestoredReferences>.Instance;

    public bool Enabled => options.Enabled;

    /// <summary>Per project: metadata references from its assets file. Projects without assets are absent.</summary>
    public async Task<IReadOnlyDictionary<ProjectFact, IReadOnlyList<MetadataReference>>> RestoreAsync(
        string workspaceRoot, IReadOnlyList<ProjectFact> projects, IReadOnlyList<SolutionFact> solutions, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ProjectFact, IReadOnlyList<MetadataReference>>();
        if (!options.Enabled || projects.Count == 0)
        {
            return result;
        }

        var targets = solutions.Count > 0
            ? solutions.Select(s => s.RelativePath).ToList()
            : projects.Select(p => p.RelativePath).Take(options.MaxProjects).ToList();

        var restored = 0;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await RunRestoreAsync(workspaceRoot, target, cancellationToken))
            {
                restored++;
            }
        }

        if (restored == 0)
        {
            _logger.LogWarning("Tier 2: restore failed for every target; falling back to bundled reference assemblies.");
            return result;
        }

        foreach (var project in projects)
        {
            var assets = Path.Combine(workspaceRoot, Path.GetDirectoryName(project.RelativePath) ?? string.Empty, "obj", "project.assets.json");
            if (!File.Exists(assets))
            {
                continue;
            }

            try
            {
                var references = ReadAssets(await File.ReadAllTextAsync(assets, cancellationToken))
                    .Where(File.Exists)
                    .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                    .ToList();
                if (references.Count > 0)
                {
                    result[project] = references;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or BadImageFormatException)
            {
                _logger.LogDebug(ex, "Tier 2: could not read {Assets}.", assets);
            }
        }

        _logger.LogInformation("Tier 2: {Restored} restore target(s), {Projects} project(s) with resolved references.", restored, result.Count);
        return result;
    }

    private async Task<bool> RunRestoreAsync(string workspaceRoot, string target, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(options.DotnetPath)
        {
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "restore", target, "--ignore-failed-sources", "--nologo", "-v", "q", "-p:TreatWarningsAsErrors=false", "-p:ContinuousIntegrationBuild=false" })
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        if (!string.IsNullOrWhiteSpace(options.PackageCache))
        {
            Directory.CreateDirectory(options.PackageCache);
            info.Environment["NUGET_PACKAGES"] = options.PackageCache;
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, options.TimeoutMinutes)));
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                _logger.LogWarning("Tier 2: restore of {Target} timed out after {Minutes} min.", target, options.TimeoutMinutes);
                return false;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Tier 2: restore of {Target} exited with {Code}: {Error}", target, process.ExitCode, Trim(await stderr + await stdout));
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _logger.LogWarning(ex, "Tier 2: could not start '{Dotnet}'. Is the SDK in the worker image?", options.DotnetPath);
            return false;
        }
    }

    /// <summary>
    /// Compile-time assemblies of the first target in an assets file, resolved against
    /// its packageFolders. Placeholder entries (_._) and missing files are skipped.
    /// </summary>
    public static IReadOnlyList<string> ReadAssets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var folders = root.TryGetProperty("packageFolders", out var pf) && pf.ValueKind == JsonValueKind.Object
            ? pf.EnumerateObject().Select(p => p.Name).ToList()
            : [];
        if (folders.Count == 0 || !root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        // Prefer a target without a runtime identifier (compile-time graph).
        var target = targets.EnumerateObject().OrderBy(t => t.Name.Contains('/') ? 1 : 0).FirstOrDefault();
        if (target.Value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var libraries = root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Object ? libs : default;
        var paths = new List<string>();
        foreach (var library in target.Value.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("compile", out var compile) || compile.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var libraryPath = library.Name.Replace('/', Path.DirectorySeparatorChar);
            if (libraries.ValueKind == JsonValueKind.Object && libraries.TryGetProperty(library.Name, out var meta) && meta.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                libraryPath = p.GetString()!.Replace('/', Path.DirectorySeparatorChar);
            }

            foreach (var asset in compile.EnumerateObject())
            {
                if (asset.Name.EndsWith("_._", StringComparison.Ordinal) || !asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var folder in folders)
                {
                    var candidate = Path.Combine(folder, libraryPath, asset.Name.Replace('/', Path.DirectorySeparatorChar));
                    paths.Add(candidate);
                }
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Trim(string text) => text.Length > 400 ? text[..400] + "…" : text.Trim();
}
