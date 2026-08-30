using System.Text.RegularExpressions;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;

namespace Atlas.Language.Sql;

/// <summary>
/// Stored procedures and functions are where a lot of legacy business logic lives.
/// This picks the decision-heavy routines (and the ones with domain-flavoured
/// names) so the AI can recover their rules alongside the C# candidates.
/// </summary>
public sealed partial class SqlBusinessRuleCandidateSource : IBusinessRuleCandidateSource
{
    public const int MinComplexity = 3;
    public const int MaxSnippetChars = 6_000;
    private const int MaxSqlBytes = 5 * 1024 * 1024;

    [GeneratedRegex(@"(?i)(valid|calc|price|pric|discount|tax|fee|rate|eligib|approv|authoriz|limit|threshold|quota|rule|policy|penalt|interest|premium|coverage|claim|score|status|workflow|apply|process|check|verify|bill|invoice|payment|commission|bonus)")]
    private static partial Regex DomainName();

    public async Task<IReadOnlyList<BusinessRuleCandidate>> FindAsync(IArtifactReader workspace, int max, CancellationToken cancellationToken)
    {
        var candidates = new List<BusinessRuleCandidate>();
        foreach (var path in workspace.SourceFiles("*.sql"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sql;
            try
            {
                using var stream = workspace.OpenRead(path);
                if (stream.CanSeek && stream.Length > MaxSqlBytes)
                {
                    continue;
                }

                using var reader = new StreamReader(stream);
                sql = await reader.ReadToEndAsync(cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var routine in SqlObjectParser.Parse(path, sql))
            {
                if (routine.Kind == "trigger" && routine.Complexity < MinComplexity)
                {
                    continue;
                }

                var domain = DomainName().IsMatch(routine.Name);
                if (routine.Complexity < MinComplexity && !domain)
                {
                    continue;
                }

                var score = routine.Complexity + (domain ? 4 : 0) + (routine.DynamicSql ? 1 : 0) + Math.Min(routine.Statements, 20) * 0.1;
                var snippet = routine.Body.Length > MaxSnippetChars ? routine.Body[..MaxSnippetChars] + "\n-- … truncated …" : routine.Body;
                candidates.Add(new BusinessRuleCandidate(path, $"{routine.Kind}:{routine.Name}", routine.Line, routine.EndLine, routine.Complexity, Math.Round(score, 2), snippet));
            }
        }

        return candidates.OrderByDescending(c => c.Score).ThenBy(c => c.FilePath, StringComparer.Ordinal).ThenBy(c => c.StartLine).Take(Math.Max(1, max)).ToList();
    }
}
