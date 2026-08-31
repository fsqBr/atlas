using System.Globalization;
using System.Xml.Linq;
using Atlas.Domain.Workspaces;

namespace Atlas.Scanner.Quality;

public sealed record CoverageReport(string Path, double LineRate, IReadOnlyList<CoveragePackage> Packages);

public sealed record CoveragePackage(string Name, double LineRate);

/// <summary>
/// Ingests Cobertura-format coverage reports already present in the workspace
/// (coverlet's default output). Atlas never runs the customer's tests:
/// coverage is a fact the customer's own CI produced, read as data.
/// </summary>
public static class CoberturaParser
{
    private static readonly string[] Patterns = ["*.cobertura.xml", "coverage.xml", "cobertura*.xml", "coverage.*.xml", "*.opencover.xml", "opencover*.xml", "results.xml"];

    public static async Task<IReadOnlyList<CoverageReport>> FindAndParseAsync(
        IArtifactReader workspace, CancellationToken cancellationToken)
    {
        var reports = new List<CoverageReport>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in Patterns)
        {
            foreach (var path in workspace.EnumerateFiles(pattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(path))
                {
                    continue;
                }

                var report = TryParse(path, await workspace.ReadAllTextAsync(path, cancellationToken));
                if (report is not null)
                {
                    reports.Add(report);
                }
            }
        }

        return reports.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static CoverageReport? TryParse(string path, string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var root = document.Root;
        if (root is not null && root.Name.LocalName == "CoverageSession")
        {
            return TryParseOpenCover(path, root);
        }

        if (root is null || root.Name.LocalName != "coverage" || !TryRate(root.Attribute("line-rate"), out var lineRate))
        {
            return null;
        }

        var packages = root.Descendants("package")
            .Select(p => (Name: p.Attribute("name")?.Value, Ok: TryRate(p.Attribute("line-rate"), out var rate), Rate: rate))
            .Where(p => p.Name is not null && p.Ok)
            .Select(p => new CoveragePackage(p.Name!, p.Rate))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CoverageReport(path, lineRate, packages);
    }

    private static bool TryRate(XAttribute? attribute, out double rate) =>
        double.TryParse(attribute?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate) && rate is >= 0 and <= 1;

    /// <summary>OpenCover: Summary/@sequenceCoverage is a percentage; one entry per module.</summary>
    private static CoverageReport? TryParseOpenCover(string path, XElement root)
    {
        var summary = root.Element("Summary");
        if (summary is null || !TryPercent(summary.Attribute("sequenceCoverage"), out var rate))
        {
            return null;
        }

        var modules = root.Descendants("Module")
            .Where(m => m.Attribute("skippedDueTo") is null)
            .Select(m => (Name: m.Element("ModuleName")?.Value, Ok: TryPercent(m.Element("Summary")?.Attribute("sequenceCoverage"), out var r), Rate: r))
            .Where(m => m.Name is not null && m.Ok)
            .Select(m => new CoveragePackage(m.Name!, m.Rate))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CoverageReport(path, rate, modules);
    }

    private static bool TryPercent(XAttribute? attribute, out double rate)
    {
        rate = 0;
        if (!double.TryParse(attribute?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 100)
        {
            return false;
        }

        rate = Math.Round(percent / 100.0, 4);
        return true;
    }
}
