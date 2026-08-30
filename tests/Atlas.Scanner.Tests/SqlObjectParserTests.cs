using Atlas.Language.Sql;

namespace Atlas.Scanner.Tests;

public class SqlObjectParserTests
{
    private const string Script = """
        CREATE TABLE dbo.Orders (Id INT PRIMARY KEY);
        GO
        CREATE PROCEDURE dbo.usp_ApplyDiscount @OrderId INT, @Filter NVARCHAR(200)
        AS
        BEGIN
            DECLARE @sql NVARCHAR(MAX) = N'SELECT * FROM Orders WHERE ' + @Filter;
            EXEC(@sql);
            IF EXISTS (SELECT 1 FROM Orders WHERE Id = @OrderId)
            BEGIN
                UPDATE Orders SET Total = CASE WHEN Total > 1000 THEN Total * 0.9 ELSE Total END WHERE Id = @OrderId;
            END
            WHILE @@ROWCOUNT > 0 BREAK;
        END
        GO
        CREATE OR ALTER FUNCTION dbo.fn_Tax(@amount MONEY) RETURNS MONEY AS BEGIN RETURN @amount * 0.2; END
        GO
        CREATE TRIGGER trg_Audit ON dbo.Orders AFTER UPDATE AS
        BEGIN
            DECLARE cur CURSOR FOR SELECT Id FROM inserted;
        END
        """;

    [Fact]
    public void Splits_routines_at_go_and_describes_them()
    {
        var routines = SqlObjectParser.Parse("db/schema.sql", Script);

        Assert.Equal(3, routines.Count);
        var proc = routines.Single(r => r.Kind == "procedure");
        Assert.Equal("dbo.usp_ApplyDiscount", proc.Name);
        Assert.Equal(3, proc.Line);
        Assert.True(proc.DynamicSql);
        Assert.True(proc.SelectStar);
        Assert.True(proc.Complexity >= 5, $"complexity {proc.Complexity}"); // IF, CASE, WHEN, WHILE (+1)
        Assert.Equal(0, proc.Cursors);
        Assert.Contains("EXEC(@sql)", proc.Body);
        Assert.DoesNotContain("fn_Tax", proc.Body);

        var fn = routines.Single(r => r.Kind == "function");
        Assert.Equal("dbo.fn_Tax", fn.Name);
        Assert.False(fn.DynamicSql);
        Assert.Equal(1, fn.Complexity);

        var trigger = routines.Single(r => r.Kind == "trigger");
        Assert.Equal(1, trigger.Cursors);
    }

    [Fact]
    public void Dynamic_sql_requires_both_exec_and_concatenation()
    {
        var safe = SqlObjectParser.Parse("a.sql", "CREATE PROC p AS EXEC sp_executesql N'SELECT 1';");
        Assert.False(Assert.Single(safe).DynamicSql);

        var concat = SqlObjectParser.Parse("a.sql", "CREATE PROC p @t NVARCHAR(50) AS EXEC sp_executesql N'SELECT * FROM ' + @t;");
        Assert.True(Assert.Single(concat).DynamicSql);
        Assert.Equal("p", concat[0].Name);
    }

    [Fact]
    public async Task Candidate_source_ranks_decision_heavy_and_domain_named_routines()
    {
        var reader = new FakeReader(new Dictionary<string, string> { ["db/logic.sql"] = Script, ["db/tables.sql"] = "CREATE TABLE T (Id INT);" });

        var candidates = await new SqlBusinessRuleCandidateSource().FindAsync(reader, 10, CancellationToken.None);

        Assert.Equal(2, candidates.Count); // the trigger has complexity 1 and is skipped; fn_Tax matches "tax"
        Assert.Equal("procedure:dbo.usp_ApplyDiscount", candidates[0].Symbol);
        Assert.Equal("db/logic.sql", candidates[0].FilePath);
        Assert.Equal("function:dbo.fn_Tax", candidates[1].Symbol);
        Assert.Contains("CREATE OR ALTER FUNCTION", candidates[1].Snippet);
    }

    private sealed class FakeReader(Dictionary<string, string> files) : Atlas.Domain.Workspaces.IArtifactReader
    {
        public string RootPath => "/mem";

        public IEnumerable<string> EnumerateFiles(string searchPattern) => files.Keys.Where(k => k.EndsWith(searchPattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase));

        public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(files[relativePath]);

        public Stream OpenRead(string relativePath) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(files[relativePath]));
    }
}
