namespace Atlas.Language.Abstractions;

/// <summary>
/// A language-specific code pattern observed syntactically (
/// "SecurityPatterns" capability). Language adapters detect; the security
/// scanner judges. Symbol is the enclosing member so identity survives line moves.
/// </summary>
public sealed record PatternFact(
    string PatternId,
    string FilePath,
    int Line,
    string Symbol,
    string Detail);

/// <summary>
/// Shared vocabulary between language adapters (producers) and the security
/// scanner (consumer). Adding a pattern = a constant here + detection in the
/// adapter + a RuleSpec in the scanner.
/// </summary>
public static class SecurityPatternIds
{
    public const string SqlStringConcatenation = "sec.sql.string-concatenation";
    public const string WeakHash = "sec.crypto.weak-hash";
    public const string WeakSymmetricCipher = "sec.crypto.weak-cipher";
    public const string BinaryFormatter = "sec.deserialization.binaryformatter";
    public const string TypeNameHandling = "sec.deserialization.typenamehandling";
    public const string CertificateValidationDisabled = "sec.tls.certificate-validation-disabled";
    public const string LegacyTlsProtocol = "sec.tls.legacy-protocol";
    public const string XmlDtdProcessing = "sec.xml.dtd-processing";
    public const string RequestValidationDisabled = "sec.web.request-validation-disabled";
    public const string ProcessStartConcatenation = "sec.process.command-concatenation";

    public static readonly IReadOnlyList<string> All =
    [
        SqlStringConcatenation, WeakHash, WeakSymmetricCipher, BinaryFormatter, TypeNameHandling,
        CertificateValidationDisabled, LegacyTlsProtocol, XmlDtdProcessing, RequestValidationDisabled,
        ProcessStartConcatenation,
    ];
}

/// <summary>
/// Sensitive-data facts (.5/§20.6). Field facts use the id
/// `pii.field.&lt;category&gt;` (identifier, contact, financial, health,
/// credential, birth); leak facts carry the sink and the field in Detail.
/// </summary>
public static class PrivacyPatternIds
{
    public const string FieldPrefix = "pii.field.";
    public const string LeakToLog = "pii.leak.log";
    public const string LeakToException = "pii.leak.exception";
}

/// <summary>Quality-related code patterns produced by language adapters (.8).</summary>
public static class QualityPatternIds
{
    /// <summary>Name-based: an API gone, obsolete or discouraged on modern .NET (Detail explains).</summary>
    public const string LegacyApi = "api.legacy";

    /// <summary>Semantic: usage of a symbol carrying [Obsolete] (Detail carries the message).</summary>
    public const string ObsoleteApi = "api.obsolete";

    /// <summary>Semantic: a top-level internal type with no source references anywhere in the estate (dead-code candidate).</summary>
    public const string DeadType = "deadcode.type";

    /// <summary>Semantic: a private member (method, field, property, event) with no source references anywhere (dead-code candidate).</summary>
    public const string DeadMember = "deadcode.member";
}
