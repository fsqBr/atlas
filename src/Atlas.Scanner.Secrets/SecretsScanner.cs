using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.Logging;

namespace Atlas.Scanner.Secrets;

public sealed class SecretsScannerOptions
{
    public const string SectionName = "Atlas:Secrets";

    /// <summary>
    /// Base64 key for HMAC fingerprints of detected secrets (SECURITY.md). Per
    /// installation, never per tenant-visible. Without it fingerprints are
    /// ephemeral and findings churn between restarts.
    /// </summary>
    public string? HmacKeyBase64 { get; set; }
}

/// <summary>
/// Secrets scanner (.4). The detected value is never stored, logged
/// or previewed: the finding carries an HMAC-SHA256 fingerprint (keyed per
/// installation) so the same secret is recognized across scans and files,
/// while a leaked findings database still reveals nothing (SECURITY.md).
/// Files are read concurrently — on network/bind mounts the scan is I/O bound.
/// </summary>
public sealed class SecretsScanner : IScanner
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxLineLength = 4000;
    private const double GenericEntropyThreshold = 3.0;
    private const int ReadParallelism = 8;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".svg", ".webp", ".zip", ".nupkg",
        ".7z", ".gz", ".tar", ".pdf", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp4", ".mp3", ".wav", ".resources",
        ".snk", ".o", ".so", ".a", ".dylib", ".flat", ".class", ".jar", ".aar", ".apk", ".ipa", ".db", ".sqlite",
        ".sqlite3", ".bin", ".dat", ".pak", ".map", ".pack", ".idx", ".cache",
    };

    private readonly byte[] _hmacKey;
    private readonly ILogger<SecretsScanner> _logger;

    public SecretsScanner(SecretsScannerOptions options, ILogger<SecretsScanner> logger)
    {
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(options.HmacKeyBase64))
        {
            _hmacKey = Convert.FromBase64String(options.HmacKeyBase64);
        }
        else
        {
            _hmacKey = RandomNumberGenerator.GetBytes(32);
            _logger.LogWarning(
                "Atlas:Secrets:HmacKeyBase64 is not configured; secret fingerprints are ephemeral for this process and findings will churn across restarts.");
        }
    }

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "security.secrets",
        Name: "Secrets Scanner",
        Version: "0.2.0",
        Category: FindingCategory.Secrets,
        Capabilities: ["token-formats", "connection-strings", "key-files", "hmac-fingerprints"]);

    public IReadOnlyList<RuleSpec> Rules { get; } =
        SecretDetectors.All
            .Select(d => new RuleSpec(d.Id, SecretDetectors.Version + ".0", FindingCategory.Secrets, d.Severity,
                d.Name, $"Possible {d.Name} committed to source. The value is fingerprinted, never stored.", d.Remediation,
                new Dictionary<string, Atlas.Domain.Rules.RuleLocalization>
                {
                    ["pt-BR"] = new(d.NamePtBr,
                        $"Possível {d.NamePtBr.ToLowerInvariant()} no código-fonte. O valor é fingerprintado, nunca armazenado.",
                        d.RemediationPtBr,
                        "{detectorPt} em {fileName}",
                        "Possível {detectorPtLower} na linha {line}. O valor não é armazenado; fingerprint {fingerprint}."),
                }))
            .Append(new RuleSpec(SecretDetectors.KeyFileRuleId, SecretDetectors.Version + ".0", FindingCategory.Secrets, Severity.High,
                "Key or certificate file in repository",
                "A file whose name indicates private key or certificate material is committed to source.",
                "Remove and rotate; distribute keys through a secret store, not the repository.",
                new Dictionary<string, Atlas.Domain.Rules.RuleLocalization>
                {
                    ["pt-BR"] = new("Arquivo de chave ou certificado no repositório",
                        "Um arquivo cujo nome indica material de chave privada ou certificado está no código-fonte.",
                        "Remova e rotacione; distribua chaves por um cofre de segredos, não pelo repositório.",
                        "Arquivo de chave no repositório: {fileName}",
                        "O nome do arquivo indica material de chave privada ou certificado."),
                }))
            .ToList();

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var files = context.Workspace.EnumerateFiles("*")
            .Where(p => !WorkspaceFilters.IsBuildOrVendorPath(p))
            .ToList();

        var results = new ConcurrentBag<(string Path, List<FindingCandidate> Candidates)>();

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = ReadParallelism, CancellationToken = cancellationToken },
            async (relativePath, ct) =>
            {
                var candidates = await ScanFileAsync(context.Workspace, relativePath, ct);
                if (candidates.Count > 0)
                {
                    results.Add((relativePath, candidates));
                }
            });

        // Deterministic emission order regardless of read scheduling.
        foreach (var (_, candidates) in results.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in candidates)
            {
                context.Findings.Emit(candidate);
            }
        }

        _logger.LogInformation("Secrets scanner read {Files} file(s); {Hits} with candidates.", files.Count, results.Count);
        return ScanResult.Success();
    }

    private async Task<List<FindingCandidate>> ScanFileAsync(IArtifactReader workspace, string relativePath, CancellationToken cancellationToken)
    {
        var candidates = new List<FindingCandidate>();

        if (SecretDetectors.KeyFileName.IsMatch(relativePath))
        {
            candidates.Add(new FindingCandidate(
                SecretDetectors.KeyFileRuleId, Severity.High, ConfidenceLevel.High,
                Title: $"Key file committed: {Path.GetFileName(relativePath)}",
                Message: "File name indicates private key or certificate material.",
                Evidence: new EvidenceCandidate(FilePath: relativePath, Symbol: "key-file"),
                Remediation: "Remove and rotate; distribute keys through a secret store."));
            return candidates;
        }

        if (BinaryExtensions.Contains(Path.GetExtension(relativePath)))
        {
            return candidates;
        }

        string content;
        try
        {
            await using var stream = workspace.OpenRead(relativePath);
            if (stream.Length > MaxFileBytes || await LooksBinaryAsync(stream, cancellationToken))
            {
                return candidates;
            }

            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            content = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Skipping unreadable file {Path}.", relativePath);
            return candidates;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Skipping inaccessible file {Path}.", relativePath);
            return candidates;
        }

        ScanContent(relativePath, content, candidates);
        return candidates;
    }

    private void ScanContent(string relativePath, string content, List<FindingCandidate> candidates)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Length > MaxLineLength ? lines[i][..MaxLineLength] : lines[i];
            if (line.Length < 8)
            {
                continue;
            }

            foreach (var detector in SecretDetectors.All)
            {
                foreach (System.Text.RegularExpressions.Match match in detector.Pattern.Matches(line))
                {
                    var value = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;

                    if (SecretDetectors.Placeholder.IsMatch(value))
                    {
                        continue;
                    }

                    if (detector.RequiresEntropy && SecretDetectors.ShannonEntropy(value) < GenericEntropyThreshold)
                    {
                        continue;
                    }

                    var fingerprint = Fingerprint(value);
                    candidates.Add(new FindingCandidate(
                        detector.Id,
                        detector.Severity,
                        detector.Confidence,
                        Title: $"{detector.Name} in {Path.GetFileName(relativePath)}",
                        Message: $"Possible {detector.Name.ToLowerInvariant()} at line {i + 1}. The value is not stored; fingerprint {fingerprint[..12]}.",
                        Evidence: new EvidenceCandidate(FilePath: relativePath, LineStart: i + 1, Symbol: $"hmac:{fingerprint}"),
                        Remediation: detector.Remediation,
                        Data: new Dictionary<string, string>
                        {
                            ["detector"] = detector.Id,
                            ["detectorPt"] = detector.NamePtBr,
                            ["detectorPtLower"] = detector.NamePtBr.ToLowerInvariant(),
                            ["fingerprint"] = fingerprint[..12],
                            ["catalog"] = SecretDetectors.Version,
                        }));
                }
            }
        }
    }

    /// <summary>Keyed fingerprint: equal secrets collapse to one identity; a leaked database cannot be brute-forced back to values.</summary>
    private string Fingerprint(string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(value)))[..32];

    private static async Task<bool> LooksBinaryAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return buffer.AsSpan(0, read).IndexOf((byte)0) >= 0;
    }
}
