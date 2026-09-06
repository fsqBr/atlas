using System.Text.RegularExpressions;
using System.Xml.Linq;
using Atlas.Language.Abstractions;
using Atlas.Domain.Workspaces;

namespace Atlas.Language.CSharp;

/// <summary>
/// Tier 1.5: .sln (classic text format) and .slnx (XML) read as data —
/// MSBuild's SolutionFile parser is never loaded.
/// </summary>
public static partial class SolutionFileParser
{
    [GeneratedRegex("""Project\("\{[^}]+\}"\)\s*=\s*"[^"]+",\s*"(?<path>[^"]+\.csproj)""", RegexOptions.IgnoreCase)]
    private static partial Regex ClassicSlnProjectRegex();

    public static async Task<IReadOnlyList<SolutionFact>> ParseAllAsync(
        IArtifactReader workspace,
        CancellationToken cancellationToken)
    {
        var solutions = new List<SolutionFact>();

        foreach (var relativePath in workspace.SourceFiles("*.sln"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await workspace.ReadAllTextAsync(relativePath, cancellationToken);

            var projects = ClassicSlnProjectRegex().Matches(content)
                .Select(m => NormalizeRelativeTo(relativePath, m.Groups["path"].Value))
                .ToList();

            solutions.Add(new SolutionFact(relativePath, projects));
        }

        foreach (var relativePath in workspace.SourceFiles("*.slnx"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await workspace.ReadAllTextAsync(relativePath, cancellationToken);

            List<string> projects;
            try
            {
                projects = XDocument.Parse(content)
                    .Descendants("Project")
                    .Select(p => p.Attribute("Path")?.Value)
                    .Where(p => p is not null && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    .Select(p => NormalizeRelativeTo(relativePath, p!))
                    .ToList();
            }
            catch (System.Xml.XmlException)
            {
                projects = [];
            }

            solutions.Add(new SolutionFact(relativePath, projects));
        }

        return solutions;
    }

    private static string NormalizeRelativeTo(string solutionRelativePath, string projectPath)
    {
        var solutionDir = Path.GetDirectoryName(solutionRelativePath) ?? string.Empty;
        var combined = Path.Combine(solutionDir, projectPath.Replace('\\', Path.DirectorySeparatorChar));
        return Path.GetRelativePath(".", combined);
    }
}
