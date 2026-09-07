using System.Globalization;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;

namespace Atlas.Scanner.Architecture;

/// <summary>
/// Architecture scanner (.9): project and namespace dependency
/// graphs from language facts, cycles (Tarjan SCC, in memory), coupling and
/// hotspots — namespaces that are both depended upon and complex, i.e. the
/// components most dangerous to change. No graph database; graphs are small
/// enough to analyze per scan and persist as findings/metrics. Bilingual rules.
/// </summary>
public sealed class ArchitectureScanner : IScanner
{
    public static class RuleIds
    {
        public const string ProjectCycle = "architecture.cycle.project";
        public const string NamespaceCycle = "architecture.cycle.namespace";
        public const string HighFanOut = "architecture.coupling.high-fan-out";
        public const string Hotspot = "architecture.hotspot";
        public const string ChangeHotspot = "architecture.hotspot.change";
        public const string KnowledgeSilo = "architecture.knowledge-silo";
    }

    private const int ChurnMinCommits = 10;
    private const int ChurnMinComplexity = 15;
    private const int SiloMinCommits = 8;

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";
    private const int HighFanOutThreshold = 10;
    private const int HotspotMinFanIn = 3;
    private const int HotspotMinHotMethods = 3;

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) =>
        new Dictionary<string, RuleLocalization> { [Pt] = pt };

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "architecture.graph",
        Name: "Architecture Scanner",
        Version: "0.2.0",
        Category: FindingCategory.Architecture,
        Capabilities: ["project-graph", "namespace-graph", "cycles", "coupling", "hotspots"]);

    public IReadOnlyList<RuleSpec> Rules { get; } =
    [
        new(RuleIds.ProjectCycle, RulesVersion, FindingCategory.Architecture, Severity.High,
            "Project reference cycle", "Projects reference each other in a cycle; they cannot be built, versioned or migrated independently.",
            "Break the cycle with an abstractions project or by moving the shared types.",
            Loc(new("Ciclo de referências entre projetos", "Projetos referenciam uns aos outros em ciclo; não podem ser compilados, versionados ou migrados de forma independente.",
                "Quebre o ciclo com um projeto de abstrações ou movendo os tipos compartilhados.",
                "Ciclo de {size} projetos: {membersShort}", "Projetos no ciclo: {members}."))),
        new(RuleIds.NamespaceCycle, RulesVersion, FindingCategory.Architecture, Severity.Medium,
            "Namespace dependency cycle", "Namespaces depend on each other cyclically; boundaries are not enforceable.",
            "Introduce interfaces at the boundary and invert the dependency (dependency inversion).",
            Loc(new("Ciclo de dependência entre namespaces", "Namespaces dependem uns dos outros ciclicamente; as fronteiras não são aplicáveis.",
                "Introduza interfaces na fronteira e inverta a dependência (inversão de dependência).",
                "Ciclo de {size} namespaces: {membersShort}", "Namespaces no ciclo: {members}."))),
        new(RuleIds.HighFanOut, RulesVersion, FindingCategory.Architecture, Severity.Low,
            "Namespace with high fan-out", "A namespace depends on many others — a coordination point that changes for many reasons.",
            "Split responsibilities; reduce the number of collaborators.",
            Loc(new("Namespace com fan-out alto", "Um namespace depende de muitos outros — um ponto de coordenação que muda por muitas razões.",
                "Divida responsabilidades; reduza o número de colaboradores.",
                "{ns} depende de {ce} namespaces", "Acoplamento eferente {ce}, aferente {ca}."))),
        new(RuleIds.Hotspot, RulesVersion, FindingCategory.Architecture, Severity.Medium,
            "Architectural hotspot", "Central (many dependents) and complex (several high-complexity methods): the most dangerous place to change.",
            "Characterization tests first; then refactor toward smaller, well-tested units.",
            Loc(new("Hotspot arquitetural", "Central (muitos dependentes) e complexo (vários métodos de alta complexidade): o lugar mais perigoso para mudar.",
                "Testes de caracterização primeiro; depois refatore para unidades menores e bem testadas.",
                "Hotspot: {ns}", "{fanIn} namespaces dependem dele e ele contém {hotMethods} métodos de alta complexidade."))),
        new(RuleIds.ChangeHotspot, RulesVersion, FindingCategory.Architecture, Severity.Medium,
            "Change hotspot (high churn × high complexity)", "The file changes often and is complex: every change is risky and expensive — the first candidate for tests and refactoring.",
            "Add characterization tests, then split the file along the reasons it changes.",
            Loc(new("Hotspot de mudança (alto churn × alta complexidade)", "O arquivo muda com frequência e é complexo: cada mudança é arriscada e cara — primeiro candidato a testes e refatoração.",
                "Adicione testes de caracterização e depois divida o arquivo pelos motivos que o fazem mudar.",
                "Hotspot de mudança: {fileName}", "{commits} commits por {authors} autor(es) nos últimos {months} meses; complexidade máxima {complexity}."))),
        new(RuleIds.KnowledgeSilo, RulesVersion, FindingCategory.Architecture, Severity.Low,
            "Knowledge silo (single author)", "A frequently changed file with a single author: bus factor 1 for that part of the system.",
            "Pair or rotate ownership; document the module; add tests that encode its behavior.",
            Loc(new("Silo de conhecimento (autor único)", "Arquivo alterado com frequência por um único autor: bus factor 1 para essa parte do sistema.",
                "Faça pareamento ou rotação de ownership; documente o módulo; adicione testes que registrem o comportamento.",
                "Silo de conhecimento: {fileName}", "{commits} commits de um único autor nos últimos {months} meses."))),
    ];

    public Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var languages = context.Languages.Values.ToList();
        var projects = languages.SelectMany(l => l.Projects).ToList();
        var namespaceEdges = languages.SelectMany(l => l.NamespaceDependencies).ToList();
        var types = languages.SelectMany(l => l.Types).ToList();
        var hotMethods = languages.SelectMany(l => l.HotMethods).ToList();

        EmitProjectCycles(context, projects);
        EmitNamespaceFindings(context, namespaceEdges, types, hotMethods);
        EmitChangeRisk(context, languages.SelectMany(l => l.Files).ToList());

        return Task.FromResult(ScanResult.Success());
    }

    /// <summary>Change risk from connector-provided history: churn × complexity, and single-author files.</summary>
    private static void EmitChangeRisk(ScanContext context, IReadOnlyList<FileFact> files)
    {
        if (context.History.Count == 0)
        {
            return;
        }

        var byPath = files.ToDictionary(f => f.RelativePath.Replace('\\', '/').TrimStart('.', '/'), f => f, StringComparer.OrdinalIgnoreCase);
        // The "last N months" in the message is the real span of the history read: from the oldest
        // commit seen. Using the oldest *last touch* overstated churn rates on active repositories.
        var oldest = context.History
            .Select(h => h.FirstChangeUtc ?? h.LastChangeUtc)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .DefaultIfEmpty(DateTimeOffset.UtcNow)
            .Min();
        var window = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - oldest).TotalDays / 30.0));

        foreach (var change in context.History.OrderByDescending(h => h.Commits))
        {
            if (!byPath.TryGetValue(change.RelativePath.Replace('\\', '/').TrimStart('.', '/'), out var file))
            {
                continue; // not a source file we analyzed
            }

            var data = new Dictionary<string, string>
            {
                ["fileName"] = Path.GetFileName(change.RelativePath),
                ["commits"] = change.Commits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["authors"] = change.Authors.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["months"] = window.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["complexity"] = file.MaxCyclomaticComplexity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            if (change.Commits >= ChurnMinCommits && file.MaxCyclomaticComplexity >= ChurnMinComplexity)
            {
                var severity = change.Commits >= ChurnMinCommits * 2 && file.MaxCyclomaticComplexity >= ChurnMinComplexity * 2 ? Severity.High : Severity.Medium;
                context.Findings.Emit(new FindingCandidate(
                    RuleIds.ChangeHotspot, severity, ConfidenceLevel.High,
                    Title: $"Change hotspot: {Path.GetFileName(change.RelativePath)}",
                    Message: $"{change.Commits} commits by {change.Authors} author(s) in the last {window} months; max complexity {file.MaxCyclomaticComplexity}.",
                    Evidence: new EvidenceCandidate(FilePath: change.RelativePath, Symbol: change.RelativePath),
                    Remediation: "Add characterization tests, then split the file along the reasons it changes.",
                    Data: data));
            }

            if (change.Commits >= SiloMinCommits && change.Authors == 1)
            {
                context.Findings.Emit(new FindingCandidate(
                    RuleIds.KnowledgeSilo, Severity.Low, ConfidenceLevel.High,
                    Title: $"Knowledge silo: {Path.GetFileName(change.RelativePath)}",
                    Message: $"{change.Commits} commits by a single author in the last {window} months.",
                    Evidence: new EvidenceCandidate(FilePath: change.RelativePath, Symbol: change.RelativePath),
                    Remediation: "Pair or rotate ownership; document the module; add tests that encode its behavior.",
                    Data: data));
            }
        }
    }

    private static void EmitProjectCycles(ScanContext context, IReadOnlyList<ProjectFact> projects)
    {
        var byPath = projects.ToDictionary(p => Normalize(p.RelativePath), p => p.RelativePath, StringComparer.OrdinalIgnoreCase);
        var edges = new List<(string From, string To)>();

        foreach (var project in projects)
        {
            var dir = Path.GetDirectoryName(project.RelativePath) ?? string.Empty;
            foreach (var reference in project.ProjectReferences)
            {
                var resolved = Normalize(Path.GetRelativePath(".", Path.Combine(dir, reference.Replace('\\', Path.DirectorySeparatorChar))));
                if (byPath.TryGetValue(resolved, out var target))
                {
                    edges.Add((project.RelativePath, target));
                }
            }
        }

        foreach (var cycle in StronglyConnectedComponents.Find(projects.Select(p => p.RelativePath), edges).Where(c => c.Count > 1))
        {
            var members = cycle.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            var names = members.Select(Path.GetFileNameWithoutExtension).ToList();
            context.Findings.Emit(new FindingCandidate(
                RuleIds.ProjectCycle, Severity.High, ConfidenceLevel.High,
                Title: $"Project cycle of {members.Count}: {string.Join(" ↔ ", names)}",
                Message: $"Projects in cycle: {string.Join(", ", members)}.",
                Evidence: new EvidenceCandidate(FilePath: members[0], Symbol: string.Join("|", members)),
                Data: new Dictionary<string, string>
                {
                    ["size"] = members.Count.ToString(CultureInfo.InvariantCulture),
                    ["membersShort"] = string.Join(" ↔ ", names),
                    ["members"] = string.Join(", ", members),
                }));
        }
    }

    private static void EmitNamespaceFindings(
        ScanContext context,
        IReadOnlyList<NamespaceDependency> edges,
        IReadOnlyList<TypeFact> types,
        IReadOnlyList<MethodFact> hotMethods)
    {
        if (edges.Count == 0)
        {
            return;
        }

        var nodes = edges.Select(e => e.From).Concat(edges.Select(e => e.To)).Distinct(StringComparer.Ordinal).ToList();
        var fanOut = nodes.ToDictionary(n => n, n => edges.Where(e => e.From == n).Select(e => e.To).Distinct().Count(), StringComparer.Ordinal);
        var fanIn = nodes.ToDictionary(n => n, n => edges.Where(e => e.To == n).Select(e => e.From).Distinct().Count(), StringComparer.Ordinal);

        foreach (var cycle in StronglyConnectedComponents.Find(nodes, edges.Select(e => (e.From, e.To))).Where(c => c.Count > 1))
        {
            var members = cycle.OrderBy(m => m, StringComparer.Ordinal).ToList();
            var shown = members.Count <= 6 ? string.Join(" ↔ ", members) : $"{string.Join(" ↔ ", members.Take(6))} … (+{members.Count - 6})";
            context.Findings.Emit(new FindingCandidate(
                RuleIds.NamespaceCycle, Severity.Medium, ConfidenceLevel.High,
                Title: $"Namespace cycle of {members.Count}: {shown}",
                Message: $"Namespaces in cycle: {string.Join(", ", members)}.",
                Evidence: new EvidenceCandidate(Symbol: string.Join("|", members)),
                Data: new Dictionary<string, string>
                {
                    ["size"] = members.Count.ToString(CultureInfo.InvariantCulture),
                    ["membersShort"] = shown,
                    ["members"] = string.Join(", ", members),
                }));
        }

        foreach (var (ns, ce) in fanOut.Where(kv => kv.Value >= HighFanOutThreshold))
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.HighFanOut, Severity.Low, ConfidenceLevel.High,
                Title: $"{ns} depends on {ce} namespaces",
                Message: $"Efferent coupling {ce}, afferent coupling {fanIn[ns]}.",
                Evidence: new EvidenceCandidate(Symbol: ns),
                Data: new Dictionary<string, string>
                {
                    ["ns"] = ns,
                    ["ce"] = ce.ToString(CultureInfo.InvariantCulture),
                    ["ca"] = fanIn[ns].ToString(CultureInfo.InvariantCulture),
                }));
        }

        // Hot methods → namespace via the types declared in the same file.
        var namespaceByFile = types
            .GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Namespace, StringComparer.OrdinalIgnoreCase);
        var hotByNamespace = hotMethods
            .Select(m => namespaceByFile.GetValueOrDefault(m.FilePath))
            .Where(ns => ns is not null)
            .GroupBy(ns => ns!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var ns in nodes.Where(n => fanIn[n] >= HotspotMinFanIn && hotByNamespace.GetValueOrDefault(n) >= HotspotMinHotMethods))
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.Hotspot, Severity.Medium, ConfidenceLevel.Medium,
                Title: $"Hotspot: {ns}",
                Message: $"{fanIn[ns]} namespaces depend on it and it holds {hotByNamespace[ns]} high-complexity methods.",
                Evidence: new EvidenceCandidate(Symbol: ns),
                Data: new Dictionary<string, string>
                {
                    ["ns"] = ns,
                    ["fanIn"] = fanIn[ns].ToString(CultureInfo.InvariantCulture),
                    ["hotMethods"] = hotByNamespace[ns].ToString(CultureInfo.InvariantCulture),
                }));
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');
}

/// <summary>Tarjan's algorithm; returns every strongly connected component (singletons included).</summary>
public static class StronglyConnectedComponents
{
    public static IReadOnlyList<IReadOnlyList<string>> Find(IEnumerable<string> nodes, IEnumerable<(string From, string To)> edges)
    {
        var adjacency = nodes.Distinct(StringComparer.Ordinal).ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (from, to) in edges)
        {
            if (adjacency.TryGetValue(from, out var list) && adjacency.ContainsKey(to))
            {
                list.Add(to);
            }
        }

        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var components = new List<IReadOnlyList<string>>();

        foreach (var node in adjacency.Keys)
        {
            if (!indices.ContainsKey(node))
            {
                StrongConnect(node);
            }
        }

        return components;

        void StrongConnect(string v)
        {
            indices[v] = lowLinks[v] = index++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var w in adjacency[v])
            {
                if (!indices.ContainsKey(w))
                {
                    StrongConnect(w);
                    lowLinks[v] = Math.Min(lowLinks[v], lowLinks[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowLinks[v] = Math.Min(lowLinks[v], indices[w]);
                }
            }

            if (lowLinks[v] != indices[v])
            {
                return;
            }

            var component = new List<string>();
            string popped;
            do
            {
                popped = stack.Pop();
                onStack.Remove(popped);
                component.Add(popped);
            }
            while (popped != v);

            components.Add(component);
        }
    }
}
