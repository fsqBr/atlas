using NuGet.Frameworks;

namespace Atlas.Scanner.Dependencies;

/// <summary>
/// Versioned support-lifecycle catalog for .NET target frameworks. Dates are
/// Microsoft's published end-of-support dates; the catalog version is recorded
/// on every result so a later re-evaluation can explain a changed verdict.
/// </summary>
public static class FrameworkSupportCatalog
{
    public const string Version = "2026.08";

    /// <summary>Frameworks ending within this window are flagged EndingSoon.</summary>
    private const int EndingSoonWindowDays = 183;

    private static readonly IReadOnlyDictionary<Version, DateOnly> NetCoreEndOfLife = new Dictionary<Version, DateOnly>
    {
        [new Version(1, 0)] = new(2019, 6, 27),
        [new Version(1, 1)] = new(2019, 6, 27),
        [new Version(2, 0)] = new(2018, 10, 1),
        [new Version(2, 1)] = new(2021, 8, 21),
        [new Version(2, 2)] = new(2019, 12, 23),
        [new Version(3, 0)] = new(2020, 3, 3),
        [new Version(3, 1)] = new(2022, 12, 13),
        [new Version(5, 0)] = new(2022, 5, 10),
        [new Version(6, 0)] = new(2024, 11, 12),
        [new Version(7, 0)] = new(2024, 5, 14),
        [new Version(8, 0)] = new(2026, 11, 10),
        [new Version(9, 0)] = new(2026, 5, 12),
        [new Version(10, 0)] = new(2028, 11, 14),
    };

    public static IReadOnlyList<ProjectFramework> Evaluate(string projectPath, string? rawMoniker, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(rawMoniker))
        {
            return
            [
                new ProjectFramework(projectPath, "(none)", "Unknown", null, FrameworkSupportStatus.Unknown, null,
                    "No TargetFramework/TargetFrameworkVersion found in the project file."),
            ];
        }

        return rawMoniker
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => EvaluateSingle(projectPath, m, today))
            .ToList();
    }

    private static ProjectFramework EvaluateSingle(string projectPath, string moniker, DateOnly today)
    {
        var framework = Parse(moniker);
        if (framework is null || framework.IsUnsupported || !framework.IsSpecificFramework)
        {
            return new ProjectFramework(projectPath, moniker, "Unknown", null, FrameworkSupportStatus.Unknown, null,
                $"Target framework moniker '{moniker}' was not recognized.");
        }

        var version = framework.Version;
        var versionText = version.Build > 0 ? version.ToString(3) : version.ToString(2);

        return framework.Framework switch
        {
            FrameworkConstants.FrameworkIdentifiers.Net =>
                EvaluateNetFramework(projectPath, moniker, version, versionText, today),

            FrameworkConstants.FrameworkIdentifiers.NetCoreApp =>
                EvaluateNetCore(projectPath, moniker, version, versionText, today),

            FrameworkConstants.FrameworkIdentifiers.NetStandard =>
                version.Major >= 2
                    ? new ProjectFramework(projectPath, moniker, ".NET Standard", versionText,
                        FrameworkSupportStatus.Supported, null,
                        ".NET Standard 2.x libraries load on every supported runtime.")
                    : new ProjectFramework(projectPath, moniker, ".NET Standard", versionText,
                        FrameworkSupportStatus.SupportedLegacy, null,
                        ".NET Standard 1.x is superseded by 2.0; retarget when convenient."),

            _ => new ProjectFramework(projectPath, moniker, framework.Framework, versionText,
                FrameworkSupportStatus.Unknown, null,
                $"No lifecycle data for framework identifier '{framework.Framework}'."),
        };
    }

    private static ProjectFramework EvaluateNetFramework(
        string projectPath, string moniker, Version version, string versionText, DateOnly today)
    {
        const string name = ".NET Framework";

        if (version.Major == 3 && version.Minor == 5)
        {
            return Classify(projectPath, moniker, name, versionText, new DateOnly(2029, 1, 9), today,
                ".NET Framework 3.5 SP1 is serviced with Windows until January 2029, but receives no feature work.",
                legacy: true);
        }

        if (version < new Version(4, 5, 2))
        {
            return new ProjectFramework(projectPath, moniker, name, versionText, FrameworkSupportStatus.EndOfLife,
                new DateOnly(2016, 1, 12), ".NET Framework 4.0–4.5.1 left support on 2016-01-12.");
        }

        if (version < new Version(4, 6, 2))
        {
            return new ProjectFramework(projectPath, moniker, name, versionText, FrameworkSupportStatus.EndOfLife,
                new DateOnly(2022, 4, 26), ".NET Framework 4.5.2, 4.6 and 4.6.1 left support on 2022-04-26.");
        }

        if (version.Major == 4 && version.Minor == 6 && version.Build == 2)
        {
            return Classify(projectPath, moniker, name, versionText, new DateOnly(2027, 1, 12), today,
                ".NET Framework 4.6.2 support ends on 2027-01-12.", legacy: true);
        }

        return new ProjectFramework(projectPath, moniker, name, versionText, FrameworkSupportStatus.SupportedLegacy, null,
            ".NET Framework 4.7+ is serviced with Windows but receives no new features; modernization candidate.");
    }

    private static ProjectFramework EvaluateNetCore(
        string projectPath, string moniker, Version version, string versionText, DateOnly today)
    {
        var name = version.Major >= 5 ? ".NET" : ".NET Core";
        var key = new Version(version.Major, version.Minor);

        if (!NetCoreEndOfLife.TryGetValue(key, out var endOfLife))
        {
            return new ProjectFramework(projectPath, moniker, name, versionText, FrameworkSupportStatus.Unknown, null,
                $"{name} {versionText} is not in the lifecycle catalog {Version} (preview or newer than the catalog).");
        }

        return Classify(projectPath, moniker, name, versionText, endOfLife, today,
            $"{name} {versionText} support ends on {endOfLife:yyyy-MM-dd}.", legacy: false);
    }

    private static ProjectFramework Classify(
        string projectPath, string moniker, string name, string versionText,
        DateOnly endOfLife, DateOnly today, string explanation, bool legacy)
    {
        var status =
            endOfLife <= today ? FrameworkSupportStatus.EndOfLife
            : endOfLife.DayNumber - today.DayNumber <= EndingSoonWindowDays ? FrameworkSupportStatus.EndingSoon
            : legacy ? FrameworkSupportStatus.SupportedLegacy
            : FrameworkSupportStatus.Supported;

        return new ProjectFramework(projectPath, moniker, name, versionText, status, endOfLife, explanation);
    }

    private static NuGetFramework? Parse(string moniker)
    {
        try
        {
            // Legacy csproj stores "v4.5"; NuGet expects the full identifier for that shape.
            var candidate = moniker.StartsWith('v') && moniker.Length > 1 && char.IsDigit(moniker[1])
                ? $".NETFramework,Version={moniker}"
                : moniker;

            return NuGetFramework.Parse(candidate);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
