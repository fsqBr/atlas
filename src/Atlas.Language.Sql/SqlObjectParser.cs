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

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentNoise();

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"(?i)\b(?:SET|SELECT)\s+(?<variable>@\w+)\s*\+?=|\bDECLARE\s+(?<variable>@\w+)\s+[^=\n;]*=")]
    private static partial Regex VariableAssignment();

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
            // Comments are stripped for every count (an IF in a comment is not a branch). String
            // literals stay for the keyword counts — a SELECT * inside a dynamic-SQL string still
            // executes — but are stripped for concatenation detection, where '+' inside a literal
            // is text, not an operator.
            var withoutComments = CommentNoise().Replace(body, string.Empty);
            var executable = StripNoise(body);
            routines.Add(new SqlRoutine(
                kind, name, filePath, line, line + body.Count(c => c == '\n'),
                body.Length > MaxBodyChars ? body[..MaxBodyChars] + "\n-- … truncated …" : body,
                1 + Decision().Matches(withoutComments).Count,
                IsDynamicSql(executable),
                CursorDeclaration().Matches(withoutComments).Count,
                SelectStar().IsMatch(withoutComments),
                Statement().Matches(withoutComments).Count));
        }

        return routines;
    }

    private static string StripNoise(string body) =>
        CommentNoise().Replace(StringLiteral().Replace(body, "''"), string.Empty);

    /// <summary>
    /// Dynamic SQL is only reported when the concatenation happens at the executed expression —
    /// inline in the EXEC(...) argument, or in an assignment to the variable handed to it. A
    /// parameterized sp_executesql next to an unrelated "SET @retry = @retry + 1" is not injection.
    /// </summary>
    private static bool IsDynamicSql(string analyzable)
    {
        foreach (Match exec in DynamicExec().Matches(analyzable))
        {
            var windowEnd = Math.Min(analyzable.Length, exec.Index + exec.Length + 400);
            var argument = analyzable[(exec.Index + exec.Length)..windowEnd];
            var newline = argument.IndexOf('\n');
            var statement = newline >= 0 && argument.Contains(';') && argument.IndexOf(';') < newline
                ? argument[..argument.IndexOf(';')]
                : newline >= 0 ? argument[..newline] : argument;

            if (HasConcatenation(statement))
            {
                return true;
            }

            var variable = System.Text.RegularExpressions.Regex.Match(statement, @"@\w+");
            if (variable.Success)
            {
                foreach (Match assignment in VariableAssignment().Matches(analyzable))
                {
                    if (!assignment.Groups["variable"].Value.Equals(variable.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var assignEnd = analyzable.IndexOf('\n', assignment.Index);
                    var assignStatement = analyzable[assignment.Index..(assignEnd < 0 ? analyzable.Length : assignEnd)];
                    if (HasConcatenation(assignStatement) || assignStatement.Contains("+=", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool HasConcatenation(string statement) =>
        statement.Contains('+') || statement.Contains("||", StringComparison.Ordinal) || statement.Contains("CONCAT(", StringComparison.OrdinalIgnoreCase);

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
