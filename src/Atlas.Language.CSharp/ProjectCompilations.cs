using Atlas.Language.Abstractions;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Atlas.Language.CSharp;

/// <summary>
/// Tier 1.75 per project: groups syntax trees by the project whose folder
/// contains them, orders projects so referenced ones compile first, and adds
/// each referenced project's compilation as a metadata reference. No MSBuild,
/// no restore — still "code as data" — but symbols now cross project
/// boundaries and unrelated projects stop colliding on type names.
/// </summary>
internal static class ProjectCompilations
{
    private const string LooseAssemblyName = "AtlasAnalysis.Loose";

    public static IReadOnlyList<(CSharpCompilation Compilation, IReadOnlyList<SyntaxTree> Trees)> Build(
        IReadOnlyList<ProjectFact> projects, IReadOnlyList<SyntaxTree> trees,
        IReadOnlyDictionary<ProjectFact, IReadOnlyList<MetadataReference>>? restored = null)
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        if (projects.Count == 0)
        {
            return [(CSharpCompilation.Create("AtlasAnalysis", trees, NetStandard20.References.All, options), trees)];
        }

        // Assign every tree to the project with the longest matching folder prefix.
        var byProject = projects.ToDictionary(p => p, _ => new List<SyntaxTree>());
        var loose = new List<SyntaxTree>();
        var folders = projects
            .Select(p => (Project: p, Folder: Normalize(Path.GetDirectoryName(p.RelativePath) ?? string.Empty)))
            .OrderByDescending(x => x.Folder.Length)
            .ToList();

        foreach (var tree in trees)
        {
            var path = Normalize(tree.FilePath);
            var owner = folders.FirstOrDefault(f => f.Folder.Length == 0 || path.StartsWith(f.Folder + "/", StringComparison.OrdinalIgnoreCase)).Project;
            if (owner is null)
            {
                loose.Add(tree);
            }
            else
            {
                byProject[owner].Add(tree);
            }
        }

        // Topological order over resolved project references (cycles fall back to declaration order).
        var byPath = projects.ToDictionary(p => Normalize(p.RelativePath), p => p, StringComparer.OrdinalIgnoreCase);
        var dependencies = projects.ToDictionary(p => p, p => p.ProjectReferences
            .Select(r => Normalize(Path.GetRelativePath(".", Path.Combine(Path.GetDirectoryName(p.RelativePath) ?? string.Empty, r.Replace('\\', '/')))))
            .Where(byPath.ContainsKey)
            .Select(r => byPath[r])
            .Where(d => !ReferenceEquals(d, p))
            .ToList());

        var ordered = new List<ProjectFact>();
        var state = new Dictionary<ProjectFact, int>(); // 1 = visiting, 2 = done
        void Visit(ProjectFact project)
        {
            if (state.TryGetValue(project, out var s) && s > 0)
            {
                return;
            }

            state[project] = 1;
            foreach (var dependency in dependencies[project])
            {
                Visit(dependency);
            }

            state[project] = 2;
            ordered.Add(project);
        }

        foreach (var project in projects)
        {
            Visit(project);
        }

        var result = new List<(CSharpCompilation, IReadOnlyList<SyntaxTree>)>();
        var compiled = new Dictionary<ProjectFact, CSharpCompilation>();
        foreach (var project in ordered)
        {
            var projectTrees = byProject[project];
            // Tier 2: a restored project brings its real reference set (framework + packages); otherwise the
            // bundled netstandard2.0 surface. Never both — duplicate type definitions would blur resolution.
            var baseline = restored is not null && restored.TryGetValue(project, out var real) && real.Count > 0
                ? real
                : NetStandard20.References.All;
            var references = baseline
                .Concat(dependencies[project].Where(compiled.ContainsKey).Select(d => (MetadataReference)compiled[d].ToMetadataReference()))
                .ToList();
            var compilation = CSharpCompilation.Create(SafeAssemblyName(project.Name), projectTrees, references, options);
            compiled[project] = compilation;
            if (projectTrees.Count > 0)
            {
                result.Add((compilation, projectTrees));
            }
        }

        if (loose.Count > 0)
        {
            var references = NetStandard20.References.All.Concat(compiled.Values.Select(c => (MetadataReference)c.ToMetadataReference())).ToList();
            result.Add((CSharpCompilation.Create(LooseAssemblyName, loose, references, options), loose));
        }

        return result;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/').TrimEnd('/');

    private static string SafeAssemblyName(string name)
    {
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        return safe.Length == 0 ? "AtlasProject" : safe;
    }
}
