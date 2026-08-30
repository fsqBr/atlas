using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Scanner.Abstractions;

namespace Atlas.Scanner.Infrastructure;

/// <summary>
/// Infrastructure-as-code and configuration read as data (
/// "Infrastructure Scanner"): Dockerfiles (base image pinning and end of life,
/// root user, secrets in ENV/ARG), docker-compose (privileged, host network,
/// docker socket) and ASP.NET appsettings for production (verbose logging,
/// detailed errors). Never runs anything; every hit points at a file and line.
/// </summary>
public sealed partial class InfrastructureScanner : IScanner
{
    public static class RuleIds
    {
        public const string UnpinnedBase = "infra.docker.unpinned-base";
        public const string EolBase = "infra.docker.eol-base";
        public const string RootUser = "infra.docker.root-user";
        public const string SecretInEnv = "infra.docker.secret-in-env";
        public const string Privileged = "infra.compose.privileged";
        public const string HostNetwork = "infra.compose.host-network";
        public const string DockerSocket = "infra.compose.docker-socket";
        public const string VerboseLogging = "infra.appsettings.verbose-logging";
        public const string DetailedErrors = "infra.appsettings.detailed-errors";
    }

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";

    /// <summary>Image name (without registry) → tags out of support (catalog 2026.08).</summary>
    private static readonly IReadOnlyDictionary<string, string[]> EolTags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet/aspnet"] = ["2.1", "2.2", "3.0", "3.1", "5.0", "6.0", "7.0"],
        ["dotnet/runtime"] = ["2.1", "2.2", "3.0", "3.1", "5.0", "6.0", "7.0"],
        ["dotnet/sdk"] = ["2.1", "2.2", "3.0", "3.1", "5.0", "6.0", "7.0"],
        ["dotnet/core/aspnet"] = ["*"], ["dotnet/core/runtime"] = ["*"], ["dotnet/core/sdk"] = ["*"],
        ["dotnet/framework/aspnet"] = ["4.6.2", "4.7", "4.7.1"],
        ["node"] = ["8", "10", "11", "12", "13", "14", "15", "16", "17", "19", "21"],
        ["python"] = ["2", "2.7", "3.5", "3.6", "3.7", "3.8"],
        ["ubuntu"] = ["14.04", "16.04", "18.04", "trusty", "xenial", "bionic"],
        ["debian"] = ["7", "8", "9", "10", "wheezy", "jessie", "stretch", "buster"],
        ["alpine"] = ["3.10", "3.11", "3.12", "3.13", "3.14", "3.15", "3.16", "3.17", "3.18"],
        ["nginx"] = ["1.14", "1.16", "1.18", "1.20", "1.22"],
        ["postgres"] = ["9.6", "10", "11", "12"],
        ["mysql"] = ["5.5", "5.6", "5.7"],
        ["redis"] = ["3", "4", "5"],
        ["mongo"] = ["3.6", "4.0", "4.2", "4.4"],
    };

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) => new Dictionary<string, RuleLocalization> { [Pt] = pt };

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "infra.config",
        Name: "Infrastructure & Configuration Scanner",
        Version: "0.1.0",
        Category: FindingCategory.Security,
        Capabilities: ["dockerfile", "docker-compose", "appsettings"]);

    public IReadOnlyList<RuleSpec> Rules { get; } =
    [
        new(RuleIds.UnpinnedBase, RulesVersion, FindingCategory.Modernization, Severity.Low,
            "Unpinned container base image", "FROM without a tag (or :latest) makes builds non-reproducible and silently pulls breaking changes.",
            "Pin the base image to a specific tag (and ideally a digest).",
            Loc(new("Imagem base de container não fixada", "FROM sem tag (ou :latest) torna o build irreprodutível e puxa mudanças quebradiças silenciosamente.",
                "Fixe a imagem base em uma tag específica (idealmente com digest).", "Imagem base não fixada: {image}", "FROM {image} em {fileName} (linha {line})."))),
        new(RuleIds.EolBase, RulesVersion, FindingCategory.Modernization, Severity.High,
            "Container base image out of support", "The base image tag is end of life: no security patches for the runtime or the OS layer.",
            "Move to a supported tag (current LTS) and rebuild; check the application runtime version at the same time.",
            Loc(new("Imagem base de container fora de suporte", "A tag da imagem base está em fim de vida: sem patches de segurança para o runtime ou a camada de SO.",
                "Migre para uma tag suportada (LTS atual) e reconstrua; verifique a versão do runtime da aplicação ao mesmo tempo.", "Imagem base fora de suporte: {image}", "FROM {image} em {fileName} (linha {line})."))),
        new(RuleIds.RootUser, RulesVersion, FindingCategory.Security, Severity.Low,
            "Container runs as root", "No USER instruction in the final stage: the process runs as root inside the container, widening the blast radius of any exploit.",
            "Add a non-root USER in the final stage (the .NET images ship an `app` user).",
            Loc(new("Container executa como root", "Sem instrução USER no estágio final: o processo roda como root dentro do container, ampliando o impacto de qualquer exploit.",
                "Adicione um USER não-root no estágio final (as imagens .NET trazem o usuário `app`).", "Container como root: {fileName}", "Nenhuma instrução USER no estágio final de {fileName}."))),
        new(RuleIds.SecretInEnv, RulesVersion, FindingCategory.Security, Severity.High,
            "Secret baked into the image (ENV/ARG)", "A password/token/key is set with a literal value in the Dockerfile: it is stored in every image layer and visible to anyone with the image.",
            "Inject secrets at runtime (environment from a secret store, mounted files); never bake them into layers.",
            Loc(new("Segredo embutido na imagem (ENV/ARG)", "Uma senha/token/chave é definida com valor literal no Dockerfile: fica em todas as camadas da imagem e visível a quem tiver a imagem.",
                "Injete segredos em tempo de execução (variáveis de um cofre, arquivos montados); nunca embuta em camadas.", "Segredo embutido: {name}", "{instruction} {name} com valor literal em {fileName} (linha {line})."))),
        new(RuleIds.Privileged, RulesVersion, FindingCategory.Security, Severity.High,
            "Privileged container in compose", "privileged: true disables container isolation — the container can take over the host.",
            "Remove privileged; grant only the specific capabilities needed.",
            Loc(new("Container privilegiado no compose", "privileged: true desliga o isolamento do container — ele pode tomar o host.",
                "Remova privileged; conceda apenas as capabilities específicas necessárias.", "Container privilegiado: {service}", "Serviço {service} com privileged: true em {fileName} (linha {line})."))),
        new(RuleIds.HostNetwork, RulesVersion, FindingCategory.Security, Severity.Medium,
            "Host network mode in compose", "network_mode: host removes network isolation between the container and the host.",
            "Use bridge networks and publish only the ports needed.",
            Loc(new("Rede do host no compose", "network_mode: host remove o isolamento de rede entre container e host.",
                "Use redes bridge e publique apenas as portas necessárias.", "Rede do host: {service}", "Serviço {service} com network_mode: host em {fileName} (linha {line})."))),
        new(RuleIds.DockerSocket, RulesVersion, FindingCategory.Security, Severity.High,
            "Docker socket mounted into a container", "Mounting /var/run/docker.sock gives the container root on the host.",
            "Avoid mounting the socket; if unavoidable, use a socket proxy with an allow-list.",
            Loc(new("Socket do Docker montado no container", "Montar /var/run/docker.sock dá ao container root no host.",
                "Evite montar o socket; se inevitável, use um proxy de socket com allow-list.", "Socket do Docker montado: {service}", "Serviço {service} monta /var/run/docker.sock em {fileName} (linha {line})."))),
        new(RuleIds.VerboseLogging, RulesVersion, FindingCategory.Security, Severity.Low,
            "Verbose logging in production settings", "Default log level Debug/Trace in a production appsettings file floods logs and leaks internals (and often personal data).",
            "Set Logging:LogLevel:Default to Information or Warning in production.",
            Loc(new("Log verboso em configuração de produção", "Nível de log Default em Debug/Trace num appsettings de produção inunda os logs e vaza internals (e muitas vezes dados pessoais).",
                "Defina Logging:LogLevel:Default como Information ou Warning em produção.", "Log verboso: {fileName}", "Logging:LogLevel:Default = {level} em {fileName}."))),
        new(RuleIds.DetailedErrors, RulesVersion, FindingCategory.Security, Severity.Medium,
            "Detailed errors enabled in production settings", "DetailedErrors: true returns stack traces to clients.",
            "Turn DetailedErrors off outside Development.",
            Loc(new("Erros detalhados habilitados em produção", "DetailedErrors: true devolve stack traces aos clientes.",
                "Desligue DetailedErrors fora de Development.", "Erros detalhados: {fileName}", "DetailedErrors = true em {fileName}."))),
    ];

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var path in context.Workspace.EnumerateFiles("Dockerfile").Concat(context.Workspace.EnumerateFiles("*.Dockerfile")).Concat(context.Workspace.EnumerateFiles("Dockerfile.*")).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadAsync(context, path, cancellationToken);
            if (text is not null)
            {
                ScanDockerfile(context, Normalize(path), text);
            }
        }

        foreach (var path in context.Workspace.EnumerateFiles("docker-compose*.yml").Concat(context.Workspace.EnumerateFiles("docker-compose*.yaml")).Concat(context.Workspace.EnumerateFiles("compose*.yml")).Concat(context.Workspace.EnumerateFiles("compose*.yaml")).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadAsync(context, path, cancellationToken);
            if (text is not null)
            {
                ScanCompose(context, Normalize(path), text);
            }
        }

        foreach (var path in context.Workspace.EnumerateFiles("appsettings*.json"))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Contains("Development", StringComparison.OrdinalIgnoreCase) || fileName.Contains("Local", StringComparison.OrdinalIgnoreCase) || fileName.Contains("Test", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadAsync(context, path, cancellationToken);
            if (text is not null)
            {
                ScanAppSettings(context, Normalize(path), text);
            }
        }

        return ScanResult.Success();
    }

    internal void ScanDockerfile(ScanContext context, string path, string text)
    {
        var lines = text.Split('\n');
        var fileName = Path.GetFileName(path);
        var lastStageStart = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (FromInstruction().IsMatch(lines[i]))
            {
                lastStageStart = i;
            }
        }

        var userInFinalStage = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r').Trim();
            var lineNumber = i + 1;

            var from = FromInstruction().Match(line);
            if (from.Success)
            {
                var image = from.Groups["image"].Value;
                if (image.Contains('$') || from.Groups["alias"].Success && IsStageAlias(lines, image))
                {
                    continue;
                }

                var (name, tag) = SplitImage(image);
                if (tag is null || tag.Equals("latest", StringComparison.OrdinalIgnoreCase))
                {
                    Emit(context, RuleIds.UnpinnedBase, $"Unpinned base image: {image}", $"FROM {image} in {fileName} (line {lineNumber}).", path, lineNumber, image,
                        new() { ["image"] = image, ["fileName"] = fileName });
                }
                else if (IsEol(name, tag))
                {
                    Emit(context, RuleIds.EolBase, $"Base image out of support: {image}", $"FROM {image} in {fileName} (line {lineNumber}).", path, lineNumber, image,
                        new() { ["image"] = image, ["fileName"] = fileName });
                }

                continue;
            }

            if (i >= lastStageStart && line.StartsWith("USER ", StringComparison.OrdinalIgnoreCase) && !line.EndsWith(" root", StringComparison.OrdinalIgnoreCase) && !line.EndsWith(" 0", StringComparison.Ordinal))
            {
                userInFinalStage = true;
            }

            var secret = SecretAssignment().Match(line);
            if (secret.Success)
            {
                var value = secret.Groups["value"].Value.Trim().Trim('"', '\'');
                if (value.Length > 0 && !value.StartsWith('$') && !value.StartsWith("${", StringComparison.Ordinal))
                {
                    var name = secret.Groups["name"].Value;
                    Emit(context, RuleIds.SecretInEnv, $"Secret baked into the image: {name}", $"{secret.Groups["instruction"].Value.ToUpperInvariant()} {name} with a literal value in {fileName} (line {lineNumber}).", path, lineNumber, name,
                        new() { ["name"] = name, ["instruction"] = secret.Groups["instruction"].Value.ToUpperInvariant(), ["fileName"] = fileName });
                }
            }
        }

        if (lastStageStart >= 0 && !userInFinalStage)
        {
            Emit(context, RuleIds.RootUser, $"Container runs as root: {fileName}", $"No USER instruction in the final stage of {fileName}.", path, lastStageStart + 1, "USER",
                new() { ["fileName"] = fileName });
        }
    }

    internal void ScanCompose(ScanContext context, string path, string text)
    {
        var lines = text.Split('\n');
        var fileName = Path.GetFileName(path);
        string? service = null;
        var serviceIndent = -1;
        var inServices = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = raw.Length - raw.TrimStart().Length;
            if (indent == 0)
            {
                inServices = trimmed.StartsWith("services:", StringComparison.Ordinal);
                service = null;
                continue;
            }

            if (!inServices)
            {
                continue;
            }

            if (trimmed.EndsWith(':') && (serviceIndent < 0 || indent <= serviceIndent) && !trimmed.StartsWith('-'))
            {
                service = trimmed.TrimEnd(':').Trim('"', '\'');
                serviceIndent = indent;
                continue;
            }

            var lineNumber = i + 1;
            var svc = service ?? "?";
            if (PrivilegedTrue().IsMatch(trimmed))
            {
                Emit(context, RuleIds.Privileged, $"Privileged container: {svc}", $"Service {svc} with privileged: true in {fileName} (line {lineNumber}).", path, lineNumber, svc, new() { ["service"] = svc, ["fileName"] = fileName });
            }
            else if (HostNetwork().IsMatch(trimmed))
            {
                Emit(context, RuleIds.HostNetwork, $"Host network: {svc}", $"Service {svc} with network_mode: host in {fileName} (line {lineNumber}).", path, lineNumber, svc, new() { ["service"] = svc, ["fileName"] = fileName });
            }
            else if (trimmed.Contains("/var/run/docker.sock", StringComparison.Ordinal))
            {
                Emit(context, RuleIds.DockerSocket, $"Docker socket mounted: {svc}", $"Service {svc} mounts /var/run/docker.sock in {fileName} (line {lineNumber}).", path, lineNumber, svc, new() { ["service"] = svc, ["fileName"] = fileName });
            }
        }
    }

    internal void ScanAppSettings(ScanContext context, string path, string text)
    {
        var fileName = Path.GetFileName(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("Logging", out var logging) && logging.TryGetProperty("LogLevel", out var levels) && levels.TryGetProperty("Default", out var level)
                && level.ValueKind == JsonValueKind.String && level.GetString() is "Debug" or "Trace")
            {
                Emit(context, RuleIds.VerboseLogging, $"Verbose logging: {fileName}", $"Logging:LogLevel:Default = {level.GetString()} in {fileName}.", path, null, "Logging:LogLevel:Default",
                    new() { ["level"] = level.GetString()!, ["fileName"] = fileName });
            }

            if (root.TryGetProperty("DetailedErrors", out var detailed) && (detailed.ValueKind == JsonValueKind.True || (detailed.ValueKind == JsonValueKind.String && detailed.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)))
            {
                Emit(context, RuleIds.DetailedErrors, $"Detailed errors: {fileName}", $"DetailedErrors = true in {fileName}.", path, null, "DetailedErrors",
                    new() { ["fileName"] = fileName });
            }
        }
    }

    private void Emit(ScanContext context, string ruleId, string title, string message, string path, int? line, string symbol, Dictionary<string, string> data)
    {
        var rule = Rules.First(r => r.Id == ruleId);
        context.Findings.Emit(new FindingCandidate(
            ruleId, rule.DefaultSeverity, ConfidenceLevel.High, Title: title, Message: message,
            Evidence: new EvidenceCandidate(FilePath: path, LineStart: line, Symbol: symbol),
            Remediation: rule.Remediation, Data: data));
    }

    internal static (string Name, string? Tag) SplitImage(string image)
    {
        var at = image.IndexOf('@');
        if (at >= 0)
        {
            return (image[..at], "digest");
        }

        var slash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        if (colon > slash)
        {
            return (image[..colon], image[(colon + 1)..]);
        }

        return (image, null);
    }

    internal static bool IsEol(string name, string tag)
    {
        // Strip the registry (mcr.microsoft.com/, docker.io/library/…) and match the longest known suffix.
        var candidates = new List<string> { name };
        var parts = name.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            candidates.Add(string.Join('/', parts[i..]));
        }

        foreach (var candidate in candidates)
        {
            if (!EolTags.TryGetValue(candidate, out var tags))
            {
                continue;
            }

            var version = tag.Split('-')[0]; // 6.0-alpine, 3.1-focal, 16-slim
            return tags.Contains("*") || tags.Any(t => version.Equals(t, StringComparison.OrdinalIgnoreCase) || version.StartsWith(t + ".", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsStageAlias(string[] lines, string image) =>
        lines.Any(l => FromInstruction().Match(l) is { Success: true } m && m.Groups["alias"].Success && m.Groups["alias"].Value.Equals(image, StringComparison.OrdinalIgnoreCase));

    private static async Task<string?> ReadAsync(ScanContext context, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Workspace.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    [GeneratedRegex(@"^\s*FROM\s+(?:--platform=\S+\s+)?(?<image>\S+)(?:\s+AS\s+(?<alias>\S+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FromInstruction();

    [GeneratedRegex(@"^\s*(?<instruction>ENV|ARG)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*(?:PASSWORD|PASSWD|SECRET|TOKEN|API_?KEY|PRIVATE_?KEY|CONNECTION_?STRING)[A-Za-z0-9_]*)\s*[= ]\s*(?<value>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"^privileged\s*:\s*(true|yes)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PrivilegedTrue();

    [GeneratedRegex(@"^network_mode\s*:\s*[""']?host[""']?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HostNetwork();
}
