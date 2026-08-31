namespace Atlas.Domain.Sources;

/// <summary>
/// Provider-neutral pointer to a source of code. The core never knows
/// whether a source is a local folder, a git URL or a provider repository —
/// connectors interpret the locator for their own kind.
/// <para>
/// <see cref="CredentialName"/> names a stored connector credential (never the
/// secret itself): connectors resolve it through ICredentialProvider at
/// materialization time, so secrets are never copied into assessments or logs.
/// </para>
/// </summary>
public sealed record SourceReference(string Kind, string Locator, string? Branch = null, string? CredentialName = null, Guid? TenantId = null)
{
    public static class Kinds
    {
        public const string LocalFolder = "local";
        public const string Git = "git";
        public const string GitHub = "github";
        public const string AzureDevOps = "azure-devops";
        public const string GitLab = "gitlab";
        public const string Upload = "upload";
    }
}
