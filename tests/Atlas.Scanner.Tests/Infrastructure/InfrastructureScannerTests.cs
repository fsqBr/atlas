using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Infrastructure;

namespace Atlas.Scanner.Tests.Infrastructure;

public class InfrastructureScannerTests
{
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

        public Stream OpenRead(string relativePath) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(files[relativePath]));
    }

    private static ScanContext Context(Dictionary<string, string> files, Sink sink) => new()
    {
        AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "r", Workspace = new MemoryReader(files),
        Languages = new Dictionary<string, LanguageAnalysisResult>(), Findings = sink, Today = new DateOnly(2026, 8, 29),
    };

    [Fact]
    public async Task Dockerfile_rules_fire_on_the_final_stage_only()
    {
        var dockerfile = """
            FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
            WORKDIR /src
            COPY . .
            RUN dotnet publish -o /app

            FROM mcr.microsoft.com/dotnet/aspnet:3.1-focal
            ENV DB_PASSWORD=Sup3rS3cret
            ENV API_TOKEN=${API_TOKEN}
            ARG BUILD_ID=123
            COPY --from=build /app .
            ENTRYPOINT ["dotnet", "App.dll"]
            """;
        var sink = new Sink();
        await new InfrastructureScanner().ExecuteAsync(Context(new() { ["deploy/Dockerfile"] = dockerfile }, sink), CancellationToken.None);

        var eol = sink.Items.Where(c => c.RuleId == InfrastructureScanner.RuleIds.EolBase).ToList();
        Assert.Equal(2, eol.Count); // sdk:6.0 and aspnet:3.1-focal are both out of support
        Assert.Contains(eol, c => c.Evidence.Symbol == "mcr.microsoft.com/dotnet/aspnet:3.1-focal");
        var secret = Assert.Single(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.SecretInEnv);
        Assert.Equal("DB_PASSWORD", secret.Data!["name"]);
        Assert.Single(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.RootUser);
        Assert.DoesNotContain(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.UnpinnedBase);
    }

    [Fact]
    public async Task Pinned_supported_image_with_non_root_user_is_clean_and_latest_is_flagged()
    {
        var good = "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nUSER app\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n";
        var bad = "FROM node\nRUN npm ci\n";
        var sink = new Sink();
        await new InfrastructureScanner().ExecuteAsync(Context(new() { ["api/Dockerfile"] = good, ["web/Dockerfile"] = bad }, sink), CancellationToken.None);

        Assert.DoesNotContain(sink.Items, c => c.Evidence.FilePath == "api/Dockerfile");
        Assert.Single(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.UnpinnedBase && c.Evidence.FilePath == "web/Dockerfile");
        Assert.Single(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.RootUser && c.Evidence.FilePath == "web/Dockerfile");
    }

    [Theory]
    [InlineData("mcr.microsoft.com/dotnet/aspnet", "6.0", true)]
    [InlineData("mcr.microsoft.com/dotnet/aspnet", "10.0", false)]
    [InlineData("mcr.microsoft.com/dotnet/core/aspnet", "2.1", true)]
    [InlineData("node", "16-alpine", true)]
    [InlineData("node", "22", false)]
    [InlineData("docker.io/library/python", "3.7-slim", true)]
    [InlineData("ubuntu", "24.04", false)]
    [InlineData("nginx", "1.27", false)]
    public void Eol_catalog(string image, string tag, bool eol) => Assert.Equal(eol, InfrastructureScanner.IsEol(image, tag));

    [Fact]
    public async Task Compose_and_appsettings_rules()
    {
        var compose = """
            services:
              agent:
                image: x
                privileged: true
                volumes:
                  - /var/run/docker.sock:/var/run/docker.sock
              probe:
                network_mode: host
              api:
                image: y
            """;
        var appsettings = """{ "Logging": { "LogLevel": { "Default": "Debug" } }, "DetailedErrors": true }""";
        var dev = """{ "Logging": { "LogLevel": { "Default": "Trace" } } }""";
        var sink = new Sink();
        await new InfrastructureScanner().ExecuteAsync(Context(new()
        {
            ["docker-compose.yml"] = compose,
            ["src/App/appsettings.Production.json"] = appsettings,
            ["src/App/appsettings.Development.json"] = dev,
        }, sink), CancellationToken.None);

        Assert.Equal("agent", Assert.Single(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.Privileged).Data!["service"]);
        Assert.Equal("agent", Assert.Single(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.DockerSocket).Data!["service"]);
        Assert.Equal("probe", Assert.Single(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.HostNetwork).Data!["service"]);
        Assert.Single(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.VerboseLogging && c.Evidence.FilePath!.EndsWith("Production.json"));
        Assert.Single(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.DetailedErrors);
        Assert.DoesNotContain(sink.Items, c => c.Evidence.FilePath!.Contains("Development"));
    }

    [Fact]
    public void Rules_are_bilingual()
    {
        var scanner = new InfrastructureScanner();
        Assert.Equal(9, scanner.Rules.Count);
        Assert.All(scanner.Rules, r => Assert.True(r.Localizations!.ContainsKey("pt-BR")));
    }
}
