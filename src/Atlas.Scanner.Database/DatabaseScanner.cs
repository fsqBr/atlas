using System.Globalization;
using System.Text.RegularExpressions;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;

namespace Atlas.Scanner.Database;

/// <summary>
/// Database footprint from the repository alone (.10, offline):
/// DDL scripts, EF migrations, EDMX/DBML designer models and schema-versioning
/// tooling. Says how much logic and schema live in the database, whether the
/// schema is versioned, and which columns carry personal data. Connecting to a
/// live SQL Server is a later, opt-in capability.
/// </summary>
public sealed partial class DatabaseScanner : IScanner
{
    public static class RuleIds
    {
        public const string Inventory = "database.inventory";
        public const string LogicInProcedures = "database.logic-in-procedures";
        public const string Trigger = "database.trigger";
        public const string EdmxModel = "database.edmx-model";
        public const string LinqToSql = "database.linq-to-sql";
        public const string UnversionedSchema = "database.unversioned-schema";
        public const string PiiColumn = "database.pii-column";
        public const string DynamicSql = "database.dynamic-sql";
        public const string ComplexRoutine = "database.complex-routine";
    }

    private const int ComplexRoutineThreshold = 10;
    private const int MaxRoutineFindings = 100;

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";
    private const int ProcedureThreshold = 20;
    private const int CursorThreshold = 5;
    private const int MaxSqlBytes = 5 * 1024 * 1024;
    private const int MaxColumnFindings = 300;

    private static readonly string[] MigrationPackages = ["dbup", "fluentmigrator", "evolve", "roundhouse", "grate", "yuniql"];

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) => new Dictionary<string, RuleLocalization> { [Pt] = pt };

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "database.schema",
        Name: "Database Scanner (offline)",
        Version: "0.1.0",
        Category: FindingCategory.Data,
        Capabilities: ["ddl-inventory", "ef-migrations", "designer-models", "schema-versioning", "pii-columns"]);

    public IReadOnlyList<RuleSpec> Rules { get; } =
    [
        new(RuleIds.Inventory, RulesVersion, FindingCategory.Data, Severity.Informational,
            "Database footprint", "What the repository says about the database: DDL objects, EF migrations and designer models.",
            null,
            Loc(new("Pegada de banco de dados", "O que o repositório revela sobre o banco: objetos DDL, migrations EF e modelos de designer.", null,
                "Pegada de banco de dados",
                "{tables} tabela(s), {views} view(s), {procedures} procedure(s), {functions} function(s), {triggers} trigger(s) em {sqlFiles} arquivo(s) SQL; {efMigrations} migration(s) EF; {edmx} modelo(s) EDMX; {dbml} DBML."))),
        new(RuleIds.LogicInProcedures, RulesVersion, FindingCategory.Data, Severity.Medium,
            "Business logic in stored procedures", "Many procedures and/or cursors indicate logic living in the database: hard to test, version and migrate, and invisible to the code analysis above.",
            "Inventory the procedures, cover the critical ones with database tests, and move logic to the application layer as part of the modernization roadmap.",
            Loc(new("Lógica de negócio em stored procedures", "Muitas procedures e/ou cursores indicam lógica vivendo no banco: difícil de testar, versionar e migrar, e invisível à análise de código acima.",
                "Inventarie as procedures, cubra as críticas com testes de banco e mova lógica para a aplicação como parte do roadmap.",
                "Lógica de negócio em stored procedures",
                "{procedures} procedure(s) e {cursors} cursor(es) declarados em scripts SQL."))),
        new(RuleIds.Trigger, RulesVersion, FindingCategory.Data, Severity.Low,
            "Database trigger", "Triggers run hidden logic on data changes; they are a frequent source of surprises during migration and of untestable behavior.",
            "Document each trigger's purpose; prefer explicit application logic or outbox/CDC patterns.",
            Loc(new("Trigger de banco de dados", "Triggers executam lógica oculta em alterações de dados; fonte frequente de surpresas na migração e de comportamento não testável.",
                "Documente a finalidade de cada trigger; prefira lógica explícita na aplicação ou padrões outbox/CDC.",
                "Trigger: {name}", "Trigger {name} definido em {fileName} (linha {line})."))),
        new(RuleIds.EdmxModel, RulesVersion, FindingCategory.Data, Severity.High,
            "Entity Framework EDMX designer model", "EDMX (database-first / model-first EF6) has no equivalent in EF Core; the model must be regenerated as code (scaffolding) and mappings re-validated.",
            "Scaffold the model with EF Core (dotnet ef dbcontext scaffold), compare the mapping, then retire the EDMX.",
            Loc(new("Modelo EDMX do Entity Framework", "EDMX (EF6 database-first / model-first) não tem equivalente no EF Core; o modelo precisa ser regenerado como código (scaffolding) e os mapeamentos revalidados.",
                "Faça o scaffold com EF Core (dotnet ef dbcontext scaffold), compare o mapeamento e aposente o EDMX.",
                "Modelo EDMX: {fileName}", "Modelo EDMX em {file}."))),
        new(RuleIds.LinqToSql, RulesVersion, FindingCategory.Data, Severity.High,
            "LINQ to SQL (DBML) model", "LINQ to SQL is not available on modern .NET; the data layer must be rewritten (EF Core or Dapper).",
            "Replace the DataContext with EF Core or Dapper; regenerate the model from the database.",
            Loc(new("Modelo LINQ to SQL (DBML)", "LINQ to SQL não existe no .NET moderno; a camada de dados precisa ser reescrita (EF Core ou Dapper).",
                "Substitua o DataContext por EF Core ou Dapper; regenere o modelo a partir do banco.",
                "Modelo DBML: {fileName}", "Modelo LINQ to SQL em {file}."))),
        new(RuleIds.UnversionedSchema, RulesVersion, FindingCategory.Data, Severity.Medium,
            "Database schema without versioned migrations", "DDL scripts exist but no migration mechanism was found (EF migrations, SSDT project, DbUp/FluentMigrator/Evolve…): schema drift between environments is likely and deployments are manual.",
            "Adopt a migration tool and put every schema change under version control with a repeatable deployment step.",
            Loc(new("Schema de banco sem migrations versionadas", "Há scripts DDL mas nenhum mecanismo de migration foi encontrado (EF migrations, projeto SSDT, DbUp/FluentMigrator/Evolve…): drift de schema entre ambientes é provável e o deploy é manual.",
                "Adote uma ferramenta de migrations e coloque toda mudança de schema sob versionamento com um passo de deploy repetível.",
                "Schema de banco sem migrations versionadas",
                "{tables} tabela(s) definidas em scripts SQL sem mecanismo de migrations detectado."))),
        new(RuleIds.DynamicSql, RulesVersion, FindingCategory.Security, Severity.High,
            "Dynamic SQL built by concatenation in a routine", "EXEC(...)/sp_executesql over a concatenated string inside a stored procedure or function: SQL injection surface and a query the optimizer cannot cache.",
            "Parameterize with sp_executesql and typed parameters, or rewrite as static SQL; validate any identifier that must be dynamic against an allow-list.",
            Loc(new("SQL dinâmico por concatenação em rotina", "EXEC(...)/sp_executesql sobre string concatenada dentro de procedure ou function: superfície de SQL injection e consulta que o otimizador não cacheia.",
                "Parametrize com sp_executesql e parâmetros tipados, ou reescreva como SQL estático; valide identificadores dinâmicos contra uma allow-list.",
                "SQL dinâmico em {name}", "{kind} {name} monta SQL dinâmico por concatenação — {fileName} (linha {line})."))),
        new(RuleIds.ComplexRoutine, RulesVersion, FindingCategory.Modernization, Severity.Medium,
            "Business logic concentrated in a database routine", "A stored procedure or function with many branches (IF/WHILE/CASE) holds decision logic the application cannot test or evolve; it must be understood before any data-layer modernization.",
            "Document the rules it encodes (the AI business-rule analysis can help), cover it with database tests, and plan its move to the application layer.",
            Loc(new("Lógica de negócio concentrada em rotina de banco", "Procedure ou function com muitos ramos (IF/WHILE/CASE) guarda decisões que a aplicação não testa nem evolui; precisa ser entendida antes de qualquer modernização da camada de dados.",
                "Documente as regras que ela codifica (a análise de regras com IA ajuda), cubra com testes de banco e planeje a migração para a aplicação.",
                "Rotina complexa: {name}", "{kind} {name} tem complexidade {complexity} ({statements} instruções, {cursors} cursor(es)) — {fileName} (linha {line})."))),
        new(RuleIds.PiiColumn, RulesVersion, FindingCategory.Data, Severity.Medium,
            "Personal data columns in schema", "Column names indicate personal data (LGPD art. 5 / art. 11): it belongs in the data inventory and needs protection at rest and access control.",
            "Inventory the column, encrypt or tokenize sensitive categories, restrict and log access.",
            Loc(new("Coluna com dado pessoal no schema", "O nome da coluna indica dado pessoal (LGPD art. 5 / art. 11): deve constar no inventário de dados e ter proteção em repouso e controle de acesso.",
                "Inventarie a coluna, cifre ou tokenize as categorias sensíveis, restrinja e registre o acesso.",
                "Colunas com dado pessoal em {table}: {count} ({dataCategory})", "A tabela {table} tem {count} coluna(s) da categoria {dataCategory}: {columns} — {fileName} (linha {line})."))),
    ];

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var objects = new List<SqlObject>();
        var columns = new List<SqlColumn>();
        var routines = new List<Atlas.Language.Sql.SqlRoutine>();
        var cursors = 0;
        var sqlFiles = 0;

        foreach (var path in context.Workspace.EnumerateFiles("*.sql"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sql;
            try
            {
                using var stream = context.Workspace.OpenRead(path);
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

            sqlFiles++;
            var facts = SqlSchemaParser.Parse(Normalize(path), sql);
            routines.AddRange(Atlas.Language.Sql.SqlObjectParser.Parse(Normalize(path), sql));
            objects.AddRange(facts.Objects);
            columns.AddRange(facts.Columns);
            cursors += facts.Cursors;
        }

        var edmx = context.Workspace.EnumerateFiles("*.edmx").Select(Normalize).ToList();
        var dbml = context.Workspace.EnumerateFiles("*.dbml").Select(Normalize).ToList();
        var sqlproj = context.Workspace.EnumerateFiles("*.sqlproj").Any();
        var efMigrations = await CountEfMigrationsAsync(context, cancellationToken);
        var migrationPackage = context.Languages.Values.SelectMany(l => l.Projects).SelectMany(p => p.PackageReferences)
            .Any(p => MigrationPackages.Any(m => p.Id.Contains(m, StringComparison.OrdinalIgnoreCase)));
        var versionedSqlFiles = context.Workspace.EnumerateFiles("V*__*.sql").Any();

        var tables = objects.Count(o => o.Kind == "table");
        var procedures = objects.Count(o => o.Kind == "procedure");
        var triggers = objects.Where(o => o.Kind == "trigger").ToList();

        if (sqlFiles + edmx.Count + dbml.Count + efMigrations == 0)
        {
            return ScanResult.Success(); // no database footprint in this repository: nothing to say
        }

        var inventoryData = new Dictionary<string, string>
        {
            ["tables"] = Inv(tables), ["views"] = Inv(objects.Count(o => o.Kind == "view")), ["procedures"] = Inv(procedures),
            ["functions"] = Inv(objects.Count(o => o.Kind == "function")), ["triggers"] = Inv(triggers.Count), ["sqlFiles"] = Inv(sqlFiles),
            ["efMigrations"] = Inv(efMigrations), ["edmx"] = Inv(edmx.Count), ["dbml"] = Inv(dbml.Count), ["cursors"] = Inv(cursors),
        };
        context.Findings.Emit(new FindingCandidate(RuleIds.Inventory, Severity.Informational, ConfidenceLevel.High,
            Title: "Database footprint",
            Message: $"{tables} table(s), {inventoryData["views"]} view(s), {procedures} procedure(s), {inventoryData["functions"]} function(s), {triggers.Count} trigger(s) across {sqlFiles} SQL file(s); {efMigrations} EF migration(s); {edmx.Count} EDMX model(s); {dbml.Count} DBML.",
            Evidence: new EvidenceCandidate(Symbol: "database"),
            Data: inventoryData));

        if (procedures >= ProcedureThreshold || cursors >= CursorThreshold)
        {
            context.Findings.Emit(new FindingCandidate(RuleIds.LogicInProcedures, Severity.Medium, ConfidenceLevel.Medium,
                Title: "Business logic in stored procedures",
                Message: $"{procedures} procedure(s) and {cursors} cursor(s) declared in SQL scripts.",
                Evidence: new EvidenceCandidate(Symbol: "database.procedures"),
                Remediation: Rules.First(r => r.Id == RuleIds.LogicInProcedures).Remediation,
                Data: new Dictionary<string, string> { ["procedures"] = Inv(procedures), ["cursors"] = Inv(cursors) }));
        }

        foreach (var trigger in triggers.Take(50))
        {
            context.Findings.Emit(new FindingCandidate(RuleIds.Trigger, Severity.Low, ConfidenceLevel.High,
                Title: $"Trigger: {trigger.Name}",
                Message: $"Trigger {trigger.Name} defined in {Path.GetFileName(trigger.FilePath)} (line {trigger.Line}).",
                Evidence: new EvidenceCandidate(FilePath: trigger.FilePath, LineStart: trigger.Line, Symbol: trigger.Name),
                Remediation: Rules.First(r => r.Id == RuleIds.Trigger).Remediation,
                Data: new Dictionary<string, string> { ["name"] = trigger.Name }));
        }

        foreach (var routine in routines.Where(r => r.DynamicSql).Take(MaxRoutineFindings))
        {
            context.Findings.Emit(new FindingCandidate(RuleIds.DynamicSql, Severity.High, ConfidenceLevel.Medium,
                Title: $"Dynamic SQL in {routine.Name}",
                Message: $"{routine.Kind} {routine.Name} builds dynamic SQL by concatenation — {Path.GetFileName(routine.FilePath)} (line {routine.Line}).",
                Evidence: new EvidenceCandidate(FilePath: routine.FilePath, LineStart: routine.Line, LineEnd: routine.EndLine, Symbol: $"{routine.Kind}:{routine.Name}"),
                Remediation: Rules.First(r => r.Id == RuleIds.DynamicSql).Remediation,
                Data: new Dictionary<string, string> { ["name"] = routine.Name, ["kind"] = routine.Kind }));
        }

        foreach (var routine in routines.Where(r => r.Kind != "trigger" && r.Complexity >= ComplexRoutineThreshold).OrderByDescending(r => r.Complexity).Take(MaxRoutineFindings))
        {
            context.Findings.Emit(new FindingCandidate(RuleIds.ComplexRoutine, Severity.Medium, ConfidenceLevel.Medium,
                Title: $"Complex routine: {routine.Name}",
                Message: $"{routine.Kind} {routine.Name} has complexity {routine.Complexity} ({routine.Statements} statements, {routine.Cursors} cursor(s)) — {Path.GetFileName(routine.FilePath)} (line {routine.Line}).",
                Evidence: new EvidenceCandidate(FilePath: routine.FilePath, LineStart: routine.Line, LineEnd: routine.EndLine, Symbol: $"{routine.Kind}:{routine.Name}"),
                Remediation: Rules.First(r => r.Id == RuleIds.ComplexRoutine).Remediation,
                Data: new Dictionary<string, string> { ["name"] = routine.Name, ["kind"] = routine.Kind, ["complexity"] = Inv(routine.Complexity), ["statements"] = Inv(routine.Statements), ["cursors"] = Inv(routine.Cursors) }));
        }

        foreach (var file in edmx)
        {
            context.Findings.Emit(new FindingCandidate(RuleIds.EdmxModel, Severity.High, ConfidenceLevel.High,
                Title: $"EDMX model: {Path.GetFileName(file)}", Message: $"EDMX model at {file}.",
                Evidence: new EvidenceCandidate(FilePath: file, Symbol: Path.GetFileName(file)),
                Remediation: Rules.First(r => r.Id == RuleIds.EdmxModel).Remediation));
        }

        foreach (var file in dbml)
        {
            context.Findings.Emit(new FindingCandidate(RuleIds.LinqToSql, Severity.High, ConfidenceLevel.High,
                Title: $"DBML model: {Path.GetFileName(file)}", Message: $"LINQ to SQL model at {file}.",
                Evidence: new EvidenceCandidate(FilePath: file, Symbol: Path.GetFileName(file)),
                Remediation: Rules.First(r => r.Id == RuleIds.LinqToSql).Remediation));
        }

        if (tables > 0 && efMigrations == 0 && !sqlproj && !migrationPackage && !versionedSqlFiles)
        {
            context.Findings.Emit(new FindingCandidate(RuleIds.UnversionedSchema, Severity.Medium, ConfidenceLevel.Medium,
                Title: "Database schema without versioned migrations",
                Message: $"{tables} table(s) defined in SQL scripts with no migration mechanism detected.",
                Evidence: new EvidenceCandidate(Symbol: "database.schema"),
                Remediation: Rules.First(r => r.Id == RuleIds.UnversionedSchema).Remediation,
                Data: new Dictionary<string, string> { ["tables"] = Inv(tables) }));
        }

        // One finding per (table, category): "Customers holds 3 contact columns", not one per column.
        var piiGroups = columns
            .Select(c => (Column: c, Category: SensitiveNameClassifier.Classify(c.Name)))
            .Where(x => x.Category is not null)
            .GroupBy(x => (x.Column.Table, Category: x.Category!))
            .OrderBy(g => g.Key.Table, StringComparer.Ordinal).ThenBy(g => g.Key.Category, StringComparer.Ordinal)
            .Take(MaxColumnFindings);
        foreach (var group in piiGroups)
        {
            var (table, category) = group.Key;
            var first = group.OrderBy(x => x.Column.Line).First().Column;
            var names = group.Select(x => x.Column.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var severity = category is "health" or "financial" ? Severity.High : Severity.Medium;
            context.Findings.Emit(new FindingCandidate(RuleIds.PiiColumn, severity, ConfidenceLevel.Medium,
                Title: $"Personal data columns in {table}: {names.Count} ({category})",
                Message: $"Table {table} has {names.Count} {category} column(s): {string.Join(", ", names)} — {Path.GetFileName(first.FilePath)} (line {first.Line}).",
                Evidence: new EvidenceCandidate(FilePath: first.FilePath, LineStart: first.Line, Symbol: $"{table}#{category}"),
                Remediation: Rules.First(r => r.Id == RuleIds.PiiColumn).Remediation,
                Data: new Dictionary<string, string>
                {
                    ["table"] = table, ["columns"] = string.Join(", ", names), ["count"] = Inv(names.Count),
                    ["dataCategory"] = category, ["fileName"] = Path.GetFileName(first.FilePath),
                }));
        }

        return ScanResult.Success();
    }

    private static async Task<int> CountEfMigrationsAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var path in context.Workspace.EnumerateFiles("*.cs"))
        {
            var normalized = Normalize(path);
            if (!normalized.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = await context.Workspace.ReadAllTextAsync(path, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }

            if (MigrationClass().IsMatch(text))
            {
                count++;
            }
        }

        return count;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"class\s+\w+\s*:\s*(?:\w+\.)*(Migration|DbMigration)\b")]
    private static partial Regex MigrationClass();
}
