using System.Text.RegularExpressions;

namespace Atlas.Language.Sql;

/// <summary>A stored procedure, function or trigger with the facts the scanners and the AI need.</summary>
public sealed record SqlRoutine(
    string Kind,
    string Name,
    string FilePath,
    int Line,
    int EndLine,
    string Body,
    int Complexity,
    bool DynamicSql,
    int Cursors,
    bool SelectStar,
    int Statements);

/// <summary>
/// Reads T-SQL / PL/pgSQL-flavoured scripts as text (no database, no execution):
/// routines are cut at GO separators or the next CREATE, and described by
/// branching density, dynamic SQL, cursors and SELECT *. Deliberately regex-based:
/// good enough to find the business logic hiding in the database and to hand it
/// to the model; not a SQL compiler.
/// </summary>
public static partial class SqlObjectParser
{
    [GeneratedRegex(@"(?im)^\s*CREATE\s+(?:OR\s+(?:ALTER|REPLACE)\s+)?(PROCEDURE|PROC|FUNCTION|TRIGGER)\s+([\[\]""\w\.]+)")]
    private static partial Regex RoutineStart();

    [GeneratedRegex(@"(?im)^\s*GO\s*;?\s*$")]
    private static partial Regex GoSeparator();

    [GeneratedRegex(@"(?i)\b(IF|WHILE|CASE|WHEN|ELSIF|ELSEIF|LOOP)\b")]
    private static partial Regex Decision();

    [GeneratedRegex(@"(?i)\b(EXEC|EXECUTE)\s*\(|\bsp_executesql\b|\bEXECUTE\s+IMMEDIATE\b")]
    private static partial Regex DynamicExec();

    [GeneratedRegex(@"(?i)\bDECLARE\s+\w+\s+(?:INSENSITIVE\s+|SCROLL\s+)*CURSOR\b|\bCURSOR\s+\w+\s+(?:IS|FOR)\b")]
    private static partial Regex CursorDeclaration();

    [GeneratedRegex(@"(?i)\bSELECT\s+\*")]
    private static partial Regex SelectStar();

    [GeneratedRegex(@"(?i)\b(SELECT|INSERT|UPDATE|DELETE|MERGE|SET|EXEC|EXECUTE|RETURN|RAISERROR|THROW|BEGIN|COMMIT|ROLLBACK)\b")]
    private static partial Regex Statement();

    public const int MaxBodyChars = 20_000;

    public static IReadOnlyList<SqlRoutine> Parse(string filePath, string sql)
    {
        var routines = new List<SqlRoutine>();
        var matches = RoutineStart().Matches(sql);
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var start = match.Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : sql.Length;
            var go = GoSeparator().Match(sql, start, end - start);
            if (go.Success)
            {
                end = go.Index;
            }

            var body = sql[start..end].TrimEnd();
            var kind = match.Groups[1].Value.ToUpperInvariant() switch { "PROC" => "procedure", var k => k.ToLowerInvariant() };
            var name = match.Groups[2].Value.Replace("[", "").Replace("]", "").Replace("\"", "");
            var line = LineOf(sql, start);
            var dynamic = DynamicExec().IsMatch(body) && (body.Contains('+') || body.Contains("||", StringComparison.Ordinal) || body.Contains("CONCAT(", StringComparison.OrdinalIgnoreCase));
            routines.Add(new SqlRoutine(
                kind, name, filePath, line, line + body.Count(c => c == '\n'),
                body.Length > MaxBodyChars ? body[..MaxBodyChars] + "\n-- … truncated …" : body,
                1 + Decision().Matches(body).Count,
                dynamic,
                CursorDeclaration().Matches(body).Count,
                SelectStar().IsMatch(body),
                Statement().Matches(body).Count));
        }

        return routines;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
