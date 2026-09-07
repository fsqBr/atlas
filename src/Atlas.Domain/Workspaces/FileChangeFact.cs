namespace Atlas.Domain.Workspaces;

/// <summary>
/// Change history of one file over the analysis window, as read from the
/// source control system by a connector (never from the code itself). Feeds
/// change-risk rules: churn × complexity, knowledge silos (.9).
/// </summary>
public sealed record FileChangeFact(
    string RelativePath,
    int Commits,
    int LinesAdded,
    int LinesDeleted,
    int Authors,
    DateTimeOffset? LastChangeUtc,
    DateTimeOffset? FirstChangeUtc = null);
