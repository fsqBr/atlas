using System.Text.RegularExpressions;

namespace Atlas.Scanner.Database;

public sealed record SqlObject(string Kind, string Name, string FilePath, int Line);

public sealed record SqlColumn(string Table, string Name, string Type, string FilePath, int Line);

public sealed record SqlFileFacts(IReadOnlyList<SqlObject> Objects, IReadOnlyList<SqlColumn> Columns, int Cursors);

/// <summary>
/// Regex-level reading of T-SQL / ANSI DDL scripts as data (no database
/// connection, the design notes.10 offline mode): CREATE TABLE/VIEW/PROCEDURE/
/// FUNCTION/TRIGGER headers, column declarations inside CREATE TABLE and cursor
/// declarations (a proxy for procedural logic in the database).
/// </summary>
public static partial class SqlSchemaParser
{
    public static SqlFileFacts Parse(string filePath, string sql)
    {
        var objects = new List<SqlObject>();
        var columns = new List<SqlColumn>();
        var cursors = 0;

        var lines = sql.Split('\n');
        string? currentTable = null;
        var tableDepth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var create = CreateStatement().Match(trimmed);
            if (create.Success)
            {
                var kind = create.Groups["kind"].Value.ToUpperInvariant() switch
                {
                    "PROC" or "PROCEDURE" => "procedure",
                    "TABLE" => "table",
                    "VIEW" => "view",
                    "FUNCTION" => "function",
                    "TRIGGER" => "trigger",
                    _ => "object",
                };
                var name = create.Groups["name"].Value;
                objects.Add(new SqlObject(kind, name, filePath, i + 1));
                if (kind == "table")
                {
                    currentTable = name;
                    tableDepth = 0;
                }
                else
                {
                    currentTable = null;
                }

                if (kind != "table")
                {
                    continue;
                }
            }

            if (CursorDeclaration().IsMatch(trimmed))
            {
                cursors++;
            }

            if (currentTable is null)
            {
                continue;
            }

            tableDepth += trimmed.Count(c => c == '(') - trimmed.Count(c => c == ')');
            var column = ColumnDeclaration().Match(trimmed);
            if (column.Success && !ConstraintKeyword().IsMatch(trimmed))
            {
                columns.Add(new SqlColumn(currentTable, column.Groups["name"].Value, column.Groups["type"].Value.ToLowerInvariant(), filePath, i + 1));
            }

            if (tableDepth <= 0 && (trimmed.StartsWith(')') || trimmed.EndsWith(");", StringComparison.Ordinal) || trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase)))
            {
                if (!create.Success)
                {
                    currentTable = null;
                }
            }
        }

        return new SqlFileFacts(objects, columns, cursors);
    }

    [GeneratedRegex(@"^\s*CREATE\s+(?:OR\s+(?:ALTER|REPLACE)\s+)?(?<kind>TABLE|VIEW|PROC|PROCEDURE|FUNCTION|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:\[?[\w$]+\]?\s*\.\s*)*\[?(?<name>[\w$]+)\]?", RegexOptions.IgnoreCase)]
    private static partial Regex CreateStatement();

    [GeneratedRegex(@"^\s*,?\s*\[?(?<name>[A-Za-z_][\w]*)\]?\s+\[?(?<type>n?varchar|n?char|int|bigint|smallint|tinyint|bit|decimal|numeric|money|smallmoney|float|real|date|datetime2?|datetimeoffset|smalldatetime|time|uniqueidentifier|text|ntext|varbinary|binary|image|xml|json|serial|bigserial|boolean|timestamp|integer|character varying|bytea|uuid|double precision)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnDeclaration();

    [GeneratedRegex(@"^\s*,?\s*(CONSTRAINT|PRIMARY\s+KEY|FOREIGN\s+KEY|UNIQUE|CHECK|INDEX|PERIOD|WITH)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConstraintKeyword();

    [GeneratedRegex(@"\bDECLARE\s+[\w@]+\s+(?:INSENSITIVE\s+|SCROLL\s+)*CURSOR\b", RegexOptions.IgnoreCase)]
    private static partial Regex CursorDeclaration();
}
