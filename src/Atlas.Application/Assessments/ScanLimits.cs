namespace Atlas.Application.Assessments;

/// <summary>
/// Guard rails for one execution: a per-scanner wall-clock
/// timeout and a workspace size cap. Bound in the worker from Atlas:Scanning;
/// defaults are generous enough for large monoliths and small enough that a
/// pathological repository cannot hold a worker for an hour.
/// </summary>
public sealed record ScanLimits
{
    public int ScannerTimeoutMinutes { get; init; } = 15;

    /// <summary>Maximum files under the workspace after exclusions; 0 disables.</summary>
    public int MaxFiles { get; init; } = 250_000;

    public TimeSpan ScannerTimeout => TimeSpan.FromMinutes(Math.Max(1, ScannerTimeoutMinutes));
}

public sealed class WorkspaceTooLargeException(int files, int limit)
    : InvalidOperationException($"Workspace has {files:N0} files after exclusions, above the limit of {limit:N0}. Narrow the scope (.atlasignore / exclude paths) or raise Atlas:Scanning:MaxFiles.")
{
    public int Files { get; } = files;

    public int Limit { get; } = limit;
}
