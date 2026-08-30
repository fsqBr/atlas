using System.Text;
using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Database;

namespace Atlas.Scanner.Tests.Database;

public class DatabaseScannerTests
{
    private const string Schema = """
        -- customers
        CREATE TABLE [dbo].[Customers] (
            [Id] INT IDENTITY(1,1) NOT NULL,
            [Name] NVARCHAR(200) NOT NULL,
            [Cpf] CHAR(11) NULL,
            [Email] NVARCHAR(320) NULL,
            [CardNumber] VARCHAR(19) NULL,
            CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([Id])
        );
        GO
        CREATE TABLE Orders (
            Id int not null,
            CustomerId int not null,
            Total decimal(18,2) not null
        );
        GO
        CREATE VIEW dbo.vCustomerOrders AS SELECT * FROM Customers;
        GO
        CREATE OR ALTER PROCEDURE dbo.usp_Bill @id INT AS
        BEGIN
            DECLARE cur CURSOR FOR SELECT Id FROM Orders;
        END
        GO
        CREATE TRIGGER trg_Orders_Audit ON Orders AFTER INSERT AS BEGIN SET NOCOUNT ON; END
        GO
        CREATE FUNCTION dbo.fnTax(@v decimal) RETURNS decimal AS BEGIN RETURN @v * 0.1 END
        """;

    private sealed class Sink : IFindingSink
    {
        public List<FindingCandidate> Items { get; } = [];

        public void Emit(FindingCandidate candidate) => Items.Add(candidate);
    }

    private sealed class MemoryReader(Dictionary<string, string> files) : IArtifactReader
    {
        public string RootPath => "/mem";

        public IEnumerable<string> EnumerateFiles(string searchPattern) =>
            files.Keys.Where(f => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(f), ignoreCase: true));

        public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(files[relativePath]);

        public Stream OpenRead(string relativePath) => new MemoryStream(Encoding.UTF8.GetBytes(files[relativePath]));
    }

    private static ScanContext Context(Dictionary<string, string> files, Sink sink, params ProjectFact[] projects) => new()
    {
        AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "r",
        Workspace = new MemoryReader(files),
        Languages = new Dictionary<string, LanguageAnalysisResult>
        {
            ["csharp"] = new("csharp", AnalysisTier.Syntactic, [], projects, [], new LanguageTotals(1, 1, 1, 1, 1, 1), null, [], [], [], []),
        },
        Findings = sink, Today = new DateOnly(2026, 8, 29),
    };

    [Fact]
    public void Parser_reads_objects_columns_and_cursors()
    {
        var facts = SqlSchemaParser.Parse("db/schema.sql", Schema);

        Assert.Equal(["Customers", "Orders"], facts.Objects.Where(o => o.Kind == "table").Select(o => o.Name));
        Assert.Single(facts.Objects, o => o.Kind == "view" && o.Name == "vCustomerOrders");
        Assert.Single(facts.Objects, o => o.Kind == "procedure" && o.Name == "usp_Bill");
        Assert.Single(facts.Objects, o => o.Kind == "trigger" && o.Name == "trg_Orders_Audit");
        Assert.Single(facts.Objects, o => o.Kind == "function" && o.Name == "fnTax");
        Assert.Equal(1, facts.Cursors);

        var customerColumns = facts.Columns.Where(c => c.Table == "Customers").Select(c => c.Name).ToList();
        Assert.Equal(["Id", "Name", "Cpf", "Email", "CardNumber"], customerColumns);
        Assert.Equal(["Id", "CustomerId", "Total"], facts.Columns.Where(c => c.Table == "Orders").Select(c => c.Name));
        Assert.Equal("char", facts.Columns.Single(c => c.Name == "Cpf").Type);
    }

    [Fact]
    public async Task Emits_inventory_pii_columns_trigger_and_unversioned_schema()
    {
        var sink = new Sink();
        var files = new Dictionary<string, string> { ["db/schema.sql"] = Schema, ["src/Legacy/Model.edmx"] = "<edmx/>" };

        var result = await new DatabaseScanner().ExecuteAsync(Context(files, sink), CancellationToken.None);

        Assert.True(result.Succeeded);
        var inventory = Assert.Single(sink.Items, c => c.RuleId == DatabaseScanner.RuleIds.Inventory);
        Assert.Equal("2", inventory.Data!["tables"]);
        Assert.Equal("1", inventory.Data["procedures"]);
        Assert.Equal("1", inventory.Data["edmx"]);

        var pii = sink.Items.Where(c => c.RuleId == DatabaseScanner.RuleIds.PiiColumn).ToList();
        Assert.Equal(["Customers#contact", "Customers#financial", "Customers#identifier"], pii.Select(p => p.Evidence.Symbol).OrderBy(s => s));
        Assert.Equal(Severity.High, pii.Single(p => p.Evidence.Symbol == "Customers#financial").Severity);
        Assert.Equal(Severity.Medium, pii.Single(p => p.Evidence.Symbol == "Customers#identifier").Severity);
        Assert.Equal("Cpf", pii.Single(p => p.Evidence.Symbol == "Customers#identifier").Data!["columns"]);

        Assert.Single(sink.Items, c => c.RuleId == DatabaseScanner.RuleIds.Trigger && c.Evidence.Symbol == "trg_Orders_Audit");
        Assert.Single(sink.Items, c => c.RuleId == DatabaseScanner.RuleIds.EdmxModel);
        Assert.Single(sink.Items, c => c.RuleId == DatabaseScanner.RuleIds.UnversionedSchema);
        Assert.DoesNotContain(sink.Items, c => c.RuleId == DatabaseScanner.RuleIds.LogicInProcedures); // 1 procedure, 1 cursor: below thresholds
    }

    [Fact]
    public async Task Ef_migrations_or_migration_packages_mean_the_schema_is_versioned()
    {
        var sink = new Sink();
        var files = new Dictionary<string, string>
        {
            ["db/schema.sql"] = Schema,
            ["src/App/Migrations/20240101_Init.cs"] = "using Microsoft.EntityFrameworkCore.Migrations; public partial class Init : Migration { }",
            ["src/App/Migrations/AppContextModelSnapshot.cs"] = "public class AppContextModelSnapshot : ModelSnapshot { }",
        };

        await new DatabaseScanner().ExecuteAsync(Context(files, sink), CancellationToken.None);

        Assert.DoesNotContain(sink.Items, c => c.RuleId == DatabaseScanner.RuleIds.UnversionedSchema);
        Assert.Equal("1", sink.Items.Single(c => c.RuleId == DatabaseScanner.RuleIds.Inventory).Data!["efMigrations"]);

        var sink2 = new Sink();
        var project = new ProjectFact("src/App/App.csproj", "App", true, "net8.0", [new PackageReferenceFact("dbup-sqlserver", "5.0.0", PackageReferenceOrigin.PackageReference)], [], []);
        await new DatabaseScanner().ExecuteAsync(Context(new Dictionary<string, string> { ["db/schema.sql"] = Schema }, sink2, project), CancellationToken.None);
        Assert.DoesNotContain(sink2.Items, c => c.RuleId == DatabaseScanner.RuleIds.UnversionedSchema);
    }

    [Fact]
    public async Task Silent_when_the_repository_has_no_database_footprint()
    {
        var sink = new Sink();
        await new DatabaseScanner().ExecuteAsync(Context(new Dictionary<string, string> { ["src/A.cs"] = "class A {}" }, sink), CancellationToken.None);
        Assert.Empty(sink.Items);
    }

    [Fact]
    public async Task Many_procedures_flag_logic_in_the_database()
    {
        var sql = string.Join("\nGO\n", Enumerable.Range(1, 25).Select(i => $"CREATE PROCEDURE dbo.usp_{i} AS SELECT {i}"));
        var sink = new Sink();
        await new DatabaseScanner().ExecuteAsync(Context(new Dictionary<string, string> { ["db/procs.sql"] = sql }, sink), CancellationToken.None);
        var finding = Assert.Single(sink.Items, c => c.RuleId == DatabaseScanner.RuleIds.LogicInProcedures);
        Assert.Equal("25", finding.Data!["procedures"]);
    }

    [Fact]
    public void Rules_are_bilingual_and_in_the_data_category()
    {
        var scanner = new DatabaseScanner();
        Assert.Equal(9, scanner.Rules.Count);
        Assert.All(scanner.Rules, r => Assert.True(r.Localizations!.ContainsKey("pt-BR")));
        Assert.All(scanner.Rules.Where(r => r.Id is not (DatabaseScanner.RuleIds.DynamicSql or DatabaseScanner.RuleIds.ComplexRoutine)), r => Assert.Equal(FindingCategory.Data, r.Category));
        Assert.Equal(FindingCategory.Security, scanner.Rules.Single(r => r.Id == DatabaseScanner.RuleIds.DynamicSql).Category);
        Assert.Equal(FindingCategory.Modernization, scanner.Rules.Single(r => r.Id == DatabaseScanner.RuleIds.ComplexRoutine).Category);
    }
}
