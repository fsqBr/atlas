namespace Atlas.Scanner.Abstractions;

/// <summary>
/// Keys of the per-scan settings the runner passes to scanners through <see cref="ScanContext.Settings"/>.
/// Scanners never read configuration stores themselves (the scan host has no database, the design notes); the
/// runner resolves tenant settings and hands them over as plain strings.
/// </summary>
public static class ScanSettingKeys
{
    /// <summary>Comma-separated provider ids the tenant approved (AI Estate allowlist). Absent or empty = no allowlist.</summary>
    public const string AiApprovedProviders = "ai.approvedProviders";
}
