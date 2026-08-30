using Atlas.Api;
using Atlas.Application;
using Atlas.Application.Assessments;
using Atlas.Application.Credentials;
using Atlas.Application.Findings;
using Atlas.Connector.Abstractions;
using Atlas.Connector.AzureDevOps;
using Atlas.Connector.Git;
using Atlas.Connector.GitHub;
using Atlas.Connector.GitLab;
using Atlas.Ai;
using Atlas.Application.Ai;
using Atlas.Application.Security;
using Atlas.Application.Tenants;
using Atlas.Connector.Upload;
using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Language.Sql;
using Atlas.Language.VisualBasic;
using Atlas.Connector.Local;
using Atlas.Contracts.Assessments;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;
using Atlas.Infrastructure;
using Atlas.Infrastructure.Persistence;
using Atlas.Reporting;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Database;
using Atlas.Scanner.JavaScript;
using Atlas.Scanner.Licenses;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Infrastructure;
using Atlas.Scanner.Privacy;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Secrets;
using Atlas.Scanner.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1024L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 1024L * 1024 * 1024);

var connectionString = builder.Configuration.GetConnectionString("AtlasDb")
    ?? throw new InvalidOperationException(
        "Connection string 'AtlasDb' is not configured. " +
        "Set ConnectionStrings__AtlasDb via environment or user secrets — never in appsettings.json for real environments.");

builder.Services.AddAtlasInfrastructure(builder.Configuration, connectionString);
builder.Services.AddSingleton(builder.Configuration.GetSection(Atlas.Domain.Modernization.CostParameters.SectionName).Get<Atlas.Domain.Modernization.CostParameters>() ?? new Atlas.Domain.Modernization.CostParameters());
builder.Services.AddSingleton(builder.Configuration.GetSection(LocalSourcesOptions.SectionName).Get<LocalSourcesOptions>() ?? new LocalSourcesOptions());
builder.Services.AddAtlasApplication();
builder.Services.AddScannerRuntime();
builder.Services.AddCSharpLanguage();
builder.Services.AddVisualBasicLanguage();
builder.Services.AddSqlLanguage();
builder.Services.AddDependencyScanner(builder.Configuration["Atlas:Vulnerabilities:OsvBundlePath"]);

var feedOptions = builder.Configuration.GetSection(VulnerabilityFeedOptions.SectionName).Get<VulnerabilityFeedOptions>() ?? new VulnerabilityFeedOptions();
builder.Services.AddSingleton(feedOptions);
builder.Services.AddHttpClient("osv");
builder.Services.AddHostedService<VulnerabilityFeedSyncService>();
builder.Services.AddHostedService<ScheduledRunsService>();
builder.Services.AddHostedService<UploadGcService>();
builder.Services.AddSecretsScanner(builder.Configuration["Atlas:Secrets:HmacKeyBase64"]);
builder.Services.AddSecurityScanner();
builder.Services.AddQualityScanner();
builder.Services.AddPrivacyScanner();
builder.Services.AddDatabaseScanner();
builder.Services.AddJavaScriptScanner();
builder.Services.AddLicenseScanner(builder.Configuration.GetSection(LicenseOptions.SectionName).Get<LicenseOptions>());
builder.Services.AddInfrastructureScanner();
builder.Services.AddArchitectureScanner();
builder.Services.AddAtlasReporting(
    builder.Configuration.GetSection(ReportOptions.SectionName).Get<ReportOptions>() ?? new ReportOptions());

builder.Services.AddSingleton<ISourceConnector, LocalFolderConnector>();
builder.Services.AddGitConnector(builder.Configuration);
builder.Services.AddGitHubConnector(builder.Configuration);
builder.Services.AddAzureDevOpsConnector(builder.Configuration);
builder.Services.AddGitLabConnector(builder.Configuration);
builder.Services.AddUploadConnector(builder.Configuration);
builder.Services.AddAtlasAi(builder.Configuration);
builder.Services.AddSingleton<BusinessRuleCandidateSource>();
builder.Services.AddSingleton<IBusinessRuleCandidateSource>(sp => new CompositeBusinessRuleCandidateSource([sp.GetRequiredService<BusinessRuleCandidateSource>(), sp.GetRequiredService<Atlas.Language.Sql.SqlBusinessRuleCandidateSource>()]));

builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

var authOptions = builder.AddAtlasAuth();
var tenantOptions = builder.AddAtlasTenants();
var operationsOptions = builder.AddAtlasOperations();

var app = builder.Build();
app.UseAtlasAuth(authOptions);
app.UseAtlasTenants(tenantOptions, authOptions);
// Viewers of a restricted assessment cannot change it: any non-GET under /api/assessments/{id}/… needs edit rights
// (the sharing endpoints check ownership themselves). Hidden assessments are already 404 through the query filter.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var method = context.Request.Method;
    if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method)
        && path.StartsWith("/api/assessments/", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("/access", StringComparison.OrdinalIgnoreCase))
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && Guid.TryParse(segments[2], out var assessmentId))
        {
            var tenant = context.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenant.IsAdmin)
            {
                var access = context.RequestServices.GetRequiredService<AssessmentAccessService>();
                if (!await access.CanEditAsync(assessmentId, context.RequestAborted))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "You have view-only access to this assessment." });
                    return;
                }
            }
        }
    }

    await next(context);
});

app.UseAtlasOperations(operationsOptions);

// Opt-in schema migration for local/self-hosted single-node deployments (Atlas__AutoMigrate=true).
// Clustered deployments must run migrations as an explicit deploy step instead.
if (app.Configuration.GetValue<bool>("Atlas:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<HttpTenantContext>().UseSystemScope();
    scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Database.Migrate();
}

app.MapGet("/", () => Results.Ok(new
{
    name = "Atlas API",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
}));

// Public: what the SPA needs to start the OIDC flow (or to learn that auth is off).
app.MapGet("/api/auth/config", (AuthOptions auth) => Results.Ok(auth.ToPublicConfig()));

app.MapGet("/api/connectors", (IEnumerable<ISourceConnector> connectors) =>
    Results.Ok(connectors.Select(c => c.Descriptor)));

// Which vulnerability data the scanners are using right now (bundle snapshot), and how the last sync went.
app.MapGet("/api/vulnerabilities/status", (Atlas.Scanner.Dependencies.Vulnerabilities.IVulnerabilitySource source, VulnerabilityFeedOptions feed) =>
    Results.Ok(new
    {
        bundle = source.BundleVersion,
        path = feed.OsvBundlePath,
        syncEnabled = feed.SyncEnabled,
        syncUrls = feed.EffectiveUrls,
        lastSync = VulnerabilityFeedSyncService.LastResult,
        lastError = VulnerabilityFeedSyncService.LastError,
    }));

// Folders under the read-only local sources mount (same mount the worker uses).
// Provider discovery: list repositories behind a locator (GitHub owner, Azure DevOps project, local root…)
// so the UI can create one assessment per repository. Credentials are resolved server-side by name.
app.MapPost("/api/sources/discover", async (DiscoverSourcesRequest request, IEnumerable<ISourceConnector> connectors, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceKind) || string.IsNullOrWhiteSpace(request.Locator))
    {
        return Results.BadRequest(new { error = "sourceKind and locator are required." });
    }

    var source = new SourceReference(request.SourceKind.Trim(), request.Locator.Trim(), null,
        string.IsNullOrWhiteSpace(request.CredentialName) ? null : request.CredentialName.Trim());
    var connector = connectors.FirstOrDefault(c => c.CanHandle(source));
    if (connector is null)
    {
        return Results.BadRequest(new { error = $"No connector can handle source kind '{source.Kind}'." });
    }

    try
    {
        var repositories = await connector.DiscoverRepositoriesAsync(source, ct);
        return Results.Ok(repositories.Select(ApiMapping.ToResponse));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/sources/local", (LocalSourcesOptions options) =>
{
    // Top-level folders of every mounted root (kept for compatibility; the UI uses /browse).
    var shallow = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
    var folders = options.EffectiveRoots.Where(r => Directory.Exists(r.Path))
        .SelectMany(r => Directory.EnumerateDirectories(r.Path, "*", new EnumerationOptions { IgnoreInaccessible = true })
            .Select(dir => new LocalSourceResponse(
                Path.GetFileName(dir),
                $"{r.Path.TrimEnd('/')}/{Path.GetFileName(dir)}",
                Directory.EnumerateFiles(dir, "*.csproj", shallow).Any() || Directory.EnumerateFiles(dir, "*.sln", shallow).Any())))
        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    return Results.Ok(folders);
});

// File-dialog over the mounted roots: one level per request, contained to the roots.
app.MapGet("/api/sources/local/browse", (LocalSourcesOptions options, string? path = null) =>
{
    try
    {
        return Results.Ok(LocalSourcesBrowser.Browse(options, path));
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (DirectoryNotFoundException)
    {
        return Results.NotFound();
    }
});

// Browser upload: a zipped folder picked with the native dialog. Stored on the atlas-uploads volume for the worker.
app.MapPost("/api/uploads", async (HttpRequest request, UploadOptions uploads, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "multipart/form-data expected with fields archive (zip), name, files." });
    }

    var form = await request.ReadFormAsync(ct);
    var archive = form.Files.GetFile("archive");
    if (archive is null || archive.Length == 0)
    {
        return Results.BadRequest(new { error = "archive is required." });
    }

    if (archive.Length > uploads.MaxArchiveBytes)
    {
        return Results.Json(new { error = $"Archive is {archive.Length / (1024 * 1024):N0} MB; the limit is {uploads.MaxArchiveBytes / (1024 * 1024):N0} MB." }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    var id = Guid.NewGuid();
    var name = string.Concat((form["name"].ToString() is { Length: > 0 } n ? n : "upload").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')).Trim();
    Directory.CreateDirectory(uploads.Directory);
    var archivePath = UploadConnector.ArchivePath(uploads, id.ToString());
    await using (var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        await archive.CopyToAsync(stream, ct);
    }

    // Validate it is a zip we can open before accepting it.
    try
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
        _ = zip.Entries.Count;
    }
    catch (InvalidDataException)
    {
        File.Delete(archivePath);
        return Results.BadRequest(new { error = "archive is not a valid zip file." });
    }

    var manifest = new UploadManifest(id.ToString("N"), name.Length == 0 ? "upload" : name, archive.Length, int.TryParse(form["files"], out var files) ? files : 0, DateTimeOffset.UtcNow);
    await File.WriteAllTextAsync(UploadConnector.ManifestPath(uploads, id.ToString()), System.Text.Json.JsonSerializer.Serialize(manifest), ct);
    return Results.Ok(new { uploadId = id.ToString(), name = manifest.Name, bytes = manifest.Bytes, files = manifest.Files });
}).DisableAntiforgery().RequireRateLimiting("api");

// Connector credentials for private sources. Write-only: PUT stores/rotates, GET lists metadata, the secret never leaves.
var credentialsGroup = app.MapGroup("/api/credentials").RequireRateLimiting("api");

credentialsGroup.MapGet("/", async (CredentialsService service, ISecretCipher cipher, CancellationToken ct) =>
    Results.Ok(new
    {
        configured = cipher.IsConfigured,
        items = (await service.ListAsync(ct)).Select(ApiMapping.ToResponse),
    }));

credentialsGroup.MapPut("/{name}", async (string name, UpsertCredentialRequest request, CredentialsService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Secret))
    {
        return Results.BadRequest(new { error = "secret is required." });
    }

    try
    {
        var summary = await service.UpsertAsync(name, request.Username, request.Secret, request.Description, ct);
        return Results.Ok(ApiMapping.ToResponse(summary));
    }
    catch (SecretStoreNotConfiguredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

credentialsGroup.MapDelete("/{name}", async (string name, CredentialsService service, CancellationToken ct) =>
{
    try
    {
        return await service.DeleteAsync(name, ct) ? Results.NoContent() : Results.NotFound();
    }
    catch (CredentialInUseException ex)
    {
        return Results.Conflict(new { error = ex.Message, assessments = ex.Assessments });
    }
});

// Estate view: every assessment's latest health, inventory and open findings in one picture.
app.MapGet("/api/portfolio", async (Atlas.Application.Portfolio.PortfolioBuilder portfolio, CancellationToken ct, string? lang = null) =>
    Results.Ok(ApiMapping.ToResponse(await portfolio.BuildAsync(lang, ct))));

// Tenant-wide suppression policies.
app.MapGet("/api/policies", async (ISuppressionPolicyRepository repository, CancellationToken ct) =>
    Results.Ok((await repository.ListAllAsync(ct)).Select(ApiMapping.ToResponse)));

app.MapPost("/api/policies", async (CreatePolicyRequest request, SuppressionPolicyHandler handler, CancellationToken ct) =>
{
    try
    {
        var (policy, _) = await handler.CreateAsync(null, request.RulePattern, request.PathGlob, request.Reason, request.Author, ct);
        return Results.Created($"/api/policies/{policy.Id}", ApiMapping.ToResponse(policy));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/policies/{policyId:guid}", async (Guid policyId, SuppressionPolicyHandler handler, CancellationToken ct) =>
    await handler.DeleteAsync(policyId, ct) ? Results.NoContent() : Results.NotFound());

// Estimated vs actual across the estate: is cost.v1 too optimistic or too conservative here?
app.MapGet("/api/calibration", async (CalibrationBuilder calibration, CancellationToken ct, string? lang = null) =>
    Results.Ok(ApiMapping.ToResponse(await calibration.BuildAsync(ct), lang)));

// Queue visibility: recent jobs and dead-letter retry.
app.MapGet("/api/jobs", async (IScanJobQueue queue, IAssessmentRepository repository, CancellationToken ct, string? state = null, int take = 100) =>
{
    Atlas.Domain.Jobs.ScanJobState? filter = Enum.TryParse<Atlas.Domain.Jobs.ScanJobState>(state, true, out var parsed) ? parsed : null;
    var jobs = await queue.ListRecentAsync(take, filter, ct);
    var names = (await repository.ListRecentAsync(500, ct)).ToDictionary(a => a.Id, a => a.Name);
    return Results.Ok(jobs.Select(j => new JobResponse(j.Id, j.AssessmentId, names.GetValueOrDefault(j.AssessmentId), j.Kind, j.State.ToString(), j.Attempt, j.Error, j.QueuedAtUtc, j.StartedAtUtc, j.FinishedAtUtc, j.LeasedBy)));
});

app.MapPost("/api/jobs/{jobId:guid}/retry", async (Guid jobId, IScanJobQueue queue, RunAgainHandler runAgain, CancellationToken ct) =>
{
    var job = await queue.GetAsync(jobId, ct);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (job.State != Atlas.Domain.Jobs.ScanJobState.DeadLetter)
    {
        return Results.Conflict(new { error = "Only dead-letter jobs can be retried." });
    }

    try
    {
        var newJobId = await runAgain.HandleAsync(job.AssessmentId, ct);
        return Results.Accepted($"/api/jobs/{newJobId}", new RunQueuedResponse(newJobId));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

// Append-only audit trail of state-changing API calls (who, what, when, outcome).
app.MapGet("/api/audit", async (Atlas.Application.Audit.IAuditRepository audit, CancellationToken ct, int take = 200, Guid? assessmentId = null) =>
    Results.Ok((await audit.ListRecentAsync(take, assessmentId, ct)).Select(a => new AuditEntryResponse(a.Id, a.AtUtc, a.Actor, a.Method, a.Path, a.StatusCode, a.AssessmentId, a.Detail))));

var assessments = app.MapGroup("/api/assessments").RequireRateLimiting("api");

assessments.MapGet("/", async (
    IAssessmentRepository repository, IHealthRepository health, IScanJobQueue jobs, CancellationToken ct) =>
{
    var list = await repository.ListRecentAsync(100, ct);
    var ids = list.Select(a => a.Id).ToList();
    var scores = await health.GetLatestForAsync(ids, ct);
    var activeJobs = await jobs.GetActiveJobStatesAsync(ids, ct);
    return Results.Ok(list.Select(a => ApiMapping.ToSummary(
        a, scores.GetValueOrDefault(a.Id), activeJobs.TryGetValue(a.Id, out var s) ? s : null)));
});

assessments.MapPost("/", async (
    CreateAssessmentRequest request,
    CreateAssessmentHandler handler,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.SourceKind)
        || string.IsNullOrWhiteSpace(request.SourceLocator))
    {
        return Results.BadRequest(new { error = "name, sourceKind and sourceLocator are required." });
    }

    try
    {
        var result = await handler.HandleAsync(
            request.Name,
            new SourceReference(request.SourceKind, request.SourceLocator, request.Branch,
                string.IsNullOrWhiteSpace(request.CredentialName) ? null : request.CredentialName.Trim()),
            ct,
            request.ExcludePaths);

        return Results.Accepted(
            $"/api/assessments/{result.AssessmentId}",
            new AssessmentCreatedResponse(result.AssessmentId, result.JobId));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

assessments.MapGet("/{id:guid}", async (
    Guid id,
    IAssessmentRepository repository,
    IScanRepository scans,
    IScanJobQueue jobs,
    CancellationToken ct) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    var scanList = await scans.ListByAssessmentAsync(id, ct);
    var activeJobs = await jobs.GetActiveJobStatesAsync([id], ct);
    return Results.Ok(ApiMapping.ToResponse(assessment, scanList, activeJobs.TryGetValue(id, out var s) ? s : null));
});

assessments.MapPatch("/{id:guid}", async (
    Guid id, RenameAssessmentRequest request, IAssessmentRepository repository, IUnitOfWork unitOfWork, CancellationToken ct) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    try
    {
        assessment.Rename(request.Name);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    await unitOfWork.SaveChangesAsync(ct);
    return Results.Ok(new { id = assessment.Id, name = assessment.Name });
});

// Analysis scope: gitignore-like globs excluded on the next run (on top of defaults and the repo's .atlasignore).
assessments.MapPut("/{id:guid}/scope", async (Guid id, ScopeRequest request, IAssessmentRepository repository, IUnitOfWork unitOfWork, CancellationToken ct) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    assessment.SetExcludeGlobs(request.ExcludePaths);
    await unitOfWork.SaveChangesAsync(ct);
    return Results.Ok(new { id, excludePaths = assessment.ExcludeGlobs, defaults = Atlas.Domain.Workspaces.PathExclusions.DefaultGlobs });
});

// Suppression policies: standing "this rule / this path is noise here" decisions (assessment-scoped here).
assessments.MapGet("/{id:guid}/policies", async (Guid id, ISuppressionPolicyRepository repository, CancellationToken ct) =>
    Results.Ok((await repository.ListForAssessmentAsync(id, ct)).Select(ApiMapping.ToResponse)));

assessments.MapPost("/{id:guid}/policies", async (Guid id, CreatePolicyRequest request, IAssessmentRepository repository, SuppressionPolicyHandler handler, CancellationToken ct) =>
{
    if (await repository.GetAsync(id, ct) is null)
    {
        return Results.NotFound();
    }

    try
    {
        var (policy, applied) = await handler.CreateAsync(id, request.RulePattern, request.PathGlob, request.Reason, request.Author, ct);
        return Results.Created($"/api/policies/{policy.Id}", new PolicyCreatedResponse(ApiMapping.ToResponse(policy), applied));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

assessments.MapDelete("/{id:guid}", async (Guid id, DeleteAssessmentHandler handler, IAssessmentRepository repository, UploadOptions uploads, CancellationToken ct) =>
{
    try
    {
        var existing = await repository.GetAsync(id, ct);
        if (existing is null || !await handler.HandleAsync(id, ct))
        {
            return Results.NotFound();
        }

        if (existing.SourceKind == SourceReference.Kinds.Upload)
        {
            UploadGcService.DeleteUpload(uploads.Directory, existing.SourceLocator);
        }

        return Results.NoContent();
    }
    catch (AssessmentBusyException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});


// ---- CI integration: find the assessment of a repository and evaluate the quality gate ----

// Side-by-side: two assessments, same columns (both must be visible to the caller).
assessments.MapGet("/compare", async (Guid a, Guid b, Atlas.Application.Portfolio.SideBySideComparisonBuilder builder, CancellationToken ct, string? lang = null) =>
{
    if (a == b)
    {
        return Results.BadRequest(new { error = "Pick two different assessments." });
    }

    var comparison = await builder.BuildAsync(a, b, lang, ct);
    return comparison is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(comparison));
});

assessments.MapGet("/by-locator", async (IAssessmentRepository repository, IScanJobQueue jobs, CancellationToken ct, string locator, string kind = "git", string? branch = null) =>
{
    if (string.IsNullOrWhiteSpace(locator))
    {
        return Results.BadRequest(new { error = "locator is required." });
    }

    var assessment = await repository.FindByLocatorAsync(kind.Trim().ToLowerInvariant(), locator.Trim(), branch, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    var active = await jobs.GetActiveJobStatesAsync([assessment.Id], ct);
    return Results.Ok(ApiMapping.ToResponse(assessment, [], active.TryGetValue(assessment.Id, out var st) ? st : null));
});

assessments.MapGet("/{id:guid}/gate", async (Guid id, IAssessmentRepository repository, IHealthRepository health, IFindingRepository findings, CancellationToken ct, string? failOn = null, int? minScore = null) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    var snapshot = await health.GetLatestAsync(id, ct);
    var open = await findings.SummarizeOpenAsync([id], ct);
    var bySeverity = Enum.GetValues<Severity>().ToDictionary(s => s, s => open.Where(o => o.Severity == s).Sum(o => o.Count));
    try
    {
        var result = QualityGate.Evaluate(snapshot?.Score, bySeverity, failOn, minScore, assessment.CompletedAtUtc is not null);
        return Results.Ok(new QualityGateResponse(result.Passed, result.Evaluated, result.Score,
            result.OpenBySeverity.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), result.Violations, result.FailOn, result.MinScore,
            $"/api/assessments/{id}/report"));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Pull-request comment (Markdown) for CI: gate verdict, health and its delta, what the latest run changed, the new findings
// to look at, the gate's reasons and links. ?ai=true adds a short AI paragraph when a provider is configured (skipped silently otherwise).
assessments.MapGet("/{id:guid}/pr-comment", async (
    Guid id, IAssessmentRepository repository, IAssessmentRunRepository runs, RunComparisonBuilder comparisons, IHealthRepository health,
    IFindingRepository findings, AiNarrativeService narratives, IConfiguration configuration, CancellationToken ct,
    string? lang = null, string? failOn = null, int? minScore = null, bool ai = false) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    var snapshot = await health.GetLatestAsync(id, ct);
    var open = await findings.SummarizeOpenAsync([id], ct);
    var bySeverity = Enum.GetValues<Severity>().ToDictionary(s => s, s => open.Where(o => o.Severity == s).Sum(o => o.Count));
    QualityGateResult gate;
    try
    {
        gate = QualityGate.Evaluate(snapshot?.Score, bySeverity, failOn, minScore, assessment.CompletedAtUtc is not null);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var latest = (await runs.ListByAssessmentAsync(id, ct))
        .Where(r => r.Status is Atlas.Domain.Assessments.AssessmentRunStatus.Completed or Atlas.Domain.Assessments.AssessmentRunStatus.CompletedWithWarnings)
        .OrderByDescending(r => r.Number)
        .FirstOrDefault();
    var comparison = latest is null ? null : await comparisons.BuildAsync(id, latest.Id, null, lang, ct);

    string? aiText = null;
    string? aiModel = null;
    if (ai && comparison is not null)
    {
        try
        {
            var summary = await narratives.SummarizeRunAsync(id, comparison, gate, lang, ct);
            aiText = summary.Text;
            aiModel = summary.Model;
        }
        catch (Exception ex) when (ex is AiNotConfiguredException or ChatProviderException or HttpRequestException or TaskCanceledException)
        {
            // the comment stands on the deterministic facts; the AI paragraph is a bonus
        }
    }

    var version = "v" + (typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    var markdown = PrComment.Render(new PrCommentInput(assessment.Name, id, comparison, gate, configuration["Atlas:Notifications:PublicBaseUrl"], version, lang, aiText, aiModel));
    return Results.Text(markdown, "text/markdown; charset=utf-8");
});

assessments.MapGet("/{id:guid}/findings", async (
    Guid id,
    IAssessmentRepository repository,
    IFindingRepository findings,
    IRuleCatalog ruleCatalog,
    ISuppressionRepository suppressions,
    CancellationToken ct,
    int page = 1,
    int pageSize = 50,
    string? severity = null,
    string? category = null,
    string? status = null,
    string? ruleId = null,
    string? search = null,
    string? lang = null) =>
{
    if (await repository.GetAsync(id, ct) is null)
    {
        return Results.NotFound();
    }

    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 200);

    var filter = new FindingFilter(
        Enum.TryParse<Severity>(severity, true, out var sev) ? sev : null,
        Enum.TryParse<FindingCategory>(category, true, out var cat) ? cat : null,
        Enum.TryParse<FindingStatus>(status, true, out var st) ? st : null,
        ruleId,
        search);

    var result = await findings.ListAsync(id, (page - 1) * pageSize, pageSize, ct, filter);
    var rules = await ruleCatalog.GetAllAsync(ct);
    var active = await suppressions.GetActiveForAsync(result.Items.Select(i => i.Finding.Id).ToList(), ct);
    return Results.Ok(new PagedResponse<FindingResponse>(
        result.Items.Select(i => ApiMapping.ToResponse(i, rules, lang, active.GetValueOrDefault(i.Finding.Id))).ToList(),
        page, pageSize, result.Total));
});

// Human triage: suppress (accepted), false positive, or reopen. Auditable; recomputes the health score.
assessments.MapPost("/{id:guid}/findings/{findingId:guid}/triage", async (
    Guid id,
    Guid findingId,
    TriageRequest request,
    TriageFindingHandler handler,
    ISuppressionRepository suppressions,
    IRuleCatalog ruleCatalog,
    IFindingRepository findings,
    CancellationToken ct,
    string? lang = null) =>
{
    if (!Enum.TryParse<TriageAction>(request.Action, true, out var action))
    {
        return Results.BadRequest(new { error = "action must be Suppress, FalsePositive or Reopen." });
    }

    if (action != TriageAction.Reopen && string.IsNullOrWhiteSpace(request.Reason))
    {
        return Results.BadRequest(new { error = "reason is required to suppress or mark a false positive." });
    }

    try
    {
        var finding = await handler.HandleAsync(id, findingId, action, request.Reason, request.Author, ct);
        var page = await findings.ListAsync(id, 0, 1, ct, new FindingFilter(RuleId: finding.RuleId, Search: null));
        var item = page.Items.FirstOrDefault(i => i.Finding.Id == finding.Id)
            ?? new FindingWithLatestOccurrence(finding, null);
        var rules = await ruleCatalog.GetAllAsync(ct);
        return Results.Ok(ApiMapping.ToResponse(item, rules, lang, await suppressions.GetActiveAsync(finding.Id, ct)));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

assessments.MapGet("/{id:guid}/suppressions", async (Guid id, ISuppressionRepository suppressions, CancellationToken ct) =>
    Results.Ok((await suppressions.ListByAssessmentAsync(id, ct)).Select(s => new
    {
        s.Id, s.FindingId, s.Fingerprint, Kind = s.Kind.ToString(), s.Reason, s.Author, s.CreatedAtUtc, s.RevokedAtUtc, s.RevokedBy,
    })));

// Modernization strategy comparison, cost ranges and roadmap — recomputed from persisted facts (deterministic).
assessments.MapGet("/{id:guid}/modernization", async (Guid id, ModernizationPlanBuilder planBuilder, CancellationToken ct, string? lang = null) =>
{
    var plan = await planBuilder.BuildAsync(id, ct);
    return plan is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(plan, lang));
});

// Findings as a file: csv | json | sarif (SARIF 2.1.0 for GitHub/Azure DevOps code scanning).

// SBOM (CycloneDX 1.5) from the components the license scanner recorded on the latest run.
assessments.MapGet("/{id:guid}/sbom", async (Guid id, IAssessmentRepository repository, IFindingRepository findings, CancellationToken ct) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    var page = await findings.ListAsync(id, 0, 5, ct, new FindingFilter(RuleId: SbomBuilder.InventoryRuleId, Search: null));
    var latest = page.Items.OrderByDescending(i => i.Finding.UpdatedAtUtc).FirstOrDefault()?.Latest;
    string? components = null;
    if (latest?.DataJson is not null)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(latest.DataJson);
            if (doc.RootElement.TryGetProperty("components", out var c))
            {
                components = c.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
    }

    var bom = SbomBuilder.Build(assessment, components, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", DateTimeOffset.UtcNow);
    if (bom is null)
    {
        return Results.Json(new { error = "No license inventory yet: run the assessment (license.compliance scanner) first." }, statusCode: StatusCodes.Status404NotFound);
    }

    var safeName = string.Concat(assessment.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')).ToLowerInvariant();
    return Results.File(System.Text.Encoding.UTF8.GetBytes(bom), "application/vnd.cyclonedx+json", $"{safeName}-sbom.cdx.json");
});

assessments.MapGet("/{id:guid}/findings/export", async (
    Guid id,
    IAssessmentRepository repository,
    IFindingRepository findingRepository,
    IRuleCatalog ruleCatalog,
    CancellationToken ct,
    string format = "csv",
    string? lang = null,
    string? status = null) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    FindingStatus? statusFilter = Enum.TryParse<FindingStatus>(status, true, out var parsed) ? parsed : null;
    var page = await findingRepository.ListAsync(id, 0, 50_000, ct, new FindingFilter(Status: statusFilter));
    var rules = await ruleCatalog.GetAllAsync(ct);
    var safeName = string.Concat(assessment.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')).Trim().Replace(' ', '-');
    var stem = $"atlas-findings-{(safeName.Length == 0 ? id.ToString("N")[..8] : safeName)}";

    return format.ToLowerInvariant() switch
    {
        "csv" => Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(FindingExporter.ToCsv(page.Items, rules, lang))).ToArray(), "text/csv; charset=utf-8", stem + ".csv"),
        "json" => Results.File(System.Text.Encoding.UTF8.GetBytes(FindingExporter.ToJson(page.Items, rules, lang)), "application/json", stem + ".json"),
        "sarif" => Results.File(System.Text.Encoding.UTF8.GetBytes(FindingExporter.ToSarif(page.Items, rules, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", lang)), "application/sarif+json", stem + ".sarif"),
        _ => Results.BadRequest(new { error = "format must be csv, json or sarif." }),
    };
});

// Real outcomes for cost calibration: one record per assessment, replaced on re-record.
assessments.MapGet("/{id:guid}/actuals", async (Guid id, IModernizationActualRepository repository, CancellationToken ct, string? lang = null) =>
{
    var actual = await repository.GetAsync(id, ct);
    return actual is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(actual, lang));
});

assessments.MapPut("/{id:guid}/actuals", async (
    Guid id, RecordActualRequest request, IAssessmentRepository assessmentsRepo, IModernizationActualRepository repository, ModernizationPlanBuilder planBuilder, IUnitOfWork unitOfWork, CancellationToken ct, string? lang = null) =>
{
    var assessment = await assessmentsRepo.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    if (!Enum.TryParse<Atlas.Domain.Modernization.ModernizationStrategy>(request.Strategy, true, out var strategy))
    {
        return Results.BadRequest(new { error = "Unknown strategy." });
    }

    try
    {
        // Freeze the estimate now: calibration compares against what was promised, and the plan
        // recomputed after the modernization no longer reflects it.
        var estimatedHours = request.EstimatedHours;
        if (estimatedHours is null)
        {
            var plan = await planBuilder.BuildAsync(id, ct);
            estimatedHours = plan?.Estimates.FirstOrDefault(e => e.Strategy == strategy)?.EffortHours.Likely;
        }

        var existing = await repository.GetAsync(id, ct);
        if (existing is null)
        {
            existing = new Atlas.Domain.Modernization.ModernizationActual(id, assessment.TenantId, strategy, request.ActualHours, request.ActualMonths, request.ActualCost, request.Currency ?? "BRL", request.Notes, request.RecordedBy, estimatedHours);
            repository.Add(existing);
        }
        else
        {
            existing.Update(strategy, request.ActualHours, request.ActualMonths, request.ActualCost, request.Currency ?? existing.Currency, request.Notes, request.RecordedBy, estimatedHours);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return Results.Ok(ApiMapping.ToResponse(existing, lang));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Cadence + webhook for continuous re-assessment.
assessments.MapPut("/{id:guid}/schedule", async (Guid id, ScheduleRequest request, IAssessmentRepository repository, IUnitOfWork unitOfWork, CancellationToken ct) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    try
    {
        assessment.SetSchedule(request.RerunEveryDays, request.WebhookUrl);
        assessment.SetTarget(request.TargetScore, request.TargetDate);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    await unitOfWork.SaveChangesAsync(ct);
    return Results.Ok(new { id, rerunEveryDays = assessment.RerunEveryDays, webhookUrl = assessment.WebhookUrl });
});

// Aggregate views over open findings: by rule (what) and by folder (where).
assessments.MapGet("/{id:guid}/findings/by-rule", async (Guid id, FindingViewsBuilder views, CancellationToken ct, string? lang = null) =>
    Results.Ok((await views.ByRuleAsync(id, lang, ct)).Select(g => new RuleGroupResponse(g.RuleId, g.Title, g.Category.ToString(), g.MaxSeverity.ToString(), g.Count, g.SampleFiles))));

assessments.MapGet("/{id:guid}/findings/heatmap", async (Guid id, FindingViewsBuilder views, CancellationToken ct, int depth = 2) =>
    Results.Ok((await views.HeatmapAsync(id, Math.Clamp(depth, 1, 6), ct)).Select(r => new HeatmapRowResponse(r.Folder, r.Open, r.Critical, r.High, r.Medium, r.Low, r.Informational, r.Files))));

assessments.MapGet("/{id:guid}/runs", async (
    Guid id,
    IAssessmentRepository repository,
    IAssessmentRunRepository runs,
    CancellationToken ct) =>
{
    if (await repository.GetAsync(id, ct) is null)
    {
        return Results.NotFound();
    }

    return Results.Ok((await runs.ListByAssessmentAsync(id, ct)).Select(ApiMapping.ToResponse));
});

// Run again: queue a new run; 409 while one is queued or in progress.


// ---- Tenants: admin-only registry mapping identity-token claims to isolation boundaries ----
app.MapGet("/api/auth/me", (HttpContext http, ITenantContext tenant, AuthOptions auth) => Results.Ok(new
{
    name = http.User.Identity?.IsAuthenticated == true ? (http.User.FindFirst("name")?.Value ?? http.User.FindFirst("preferred_username")?.Value ?? http.User.Identity.Name) : null,
    tenantId = tenant.TenantId,
    tenantName = tenant.TenantName,
    isDefaultTenant = tenant.TenantId == WellKnownTenants.DefaultId,
    roles = http.User.Claims.Where(c => c.Type == auth.RoleClaim || c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).Distinct().ToArray(),
}));

var tenantsGroup = app.MapGroup("/api/tenants").RequireRateLimiting("api");

tenantsGroup.MapGet("/", async (ITenantRepository tenants, CancellationToken ct) =>
    Results.Ok((await tenants.ListAsync(ct)).Select(t => new TenantResponse(t.Id, t.Name, t.ExternalKey, t.CreatedAtUtc, t.Id == WellKnownTenants.DefaultId))));

tenantsGroup.MapPost("/", async (TenantRequest request, ITenantRepository tenants, IUnitOfWork unitOfWork, CancellationToken ct) =>
{
    try
    {
        if (!string.IsNullOrWhiteSpace(request.ExternalKey) && await tenants.GetByExternalKeyAsync(request.ExternalKey.Trim(), ct) is not null)
        {
            return Results.Conflict(new { error = $"External key '{request.ExternalKey}' is already mapped." });
        }

        var tenant = new Tenant(Guid.NewGuid(), request.Name);
        tenant.Update(request.Name, request.ExternalKey);
        tenants.Add(tenant);
        await unitOfWork.SaveChangesAsync(ct);
        return Results.Created($"/api/tenants/{tenant.Id}", new TenantResponse(tenant.Id, tenant.Name, tenant.ExternalKey, tenant.CreatedAtUtc, false));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

tenantsGroup.MapPatch("/{tenantId:guid}", async (Guid tenantId, TenantRequest request, ITenantRepository tenants, IUnitOfWork unitOfWork, CancellationToken ct) =>
{
    var tenant = await tenants.GetAsync(tenantId, ct);
    if (tenant is null)
    {
        return Results.NotFound();
    }

    try
    {
        if (!string.IsNullOrWhiteSpace(request.ExternalKey))
        {
            var clash = await tenants.GetByExternalKeyAsync(request.ExternalKey.Trim(), ct);
            if (clash is not null && clash.Id != tenantId)
            {
                return Results.Conflict(new { error = $"External key '{request.ExternalKey}' is already mapped." });
            }
        }

        tenant.Update(request.Name, request.ExternalKey);
        await unitOfWork.SaveChangesAsync(ct);
        return Results.Ok(new TenantResponse(tenant.Id, tenant.Name, tenant.ExternalKey, tenant.CreatedAtUtc, tenant.Id == WellKnownTenants.DefaultId));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});


// ---- API tokens: machine credentials for CI/scripts (admin-only; the secret is returned once) ----
var tokensGroup = app.MapGroup("/api/tokens").RequireRateLimiting("api");

tokensGroup.MapGet("/", async (ApiTokenService service, CancellationToken ct) =>
    Results.Ok((await service.ListAsync(ct)).Select(ToResponse)));

tokensGroup.MapPost("/", async (HttpContext http, ApiTokenRequest request, ApiTokenService service, CancellationToken ct) =>
{
    try
    {
        var actor = http.User.Identity?.IsAuthenticated == true
            ? http.User.FindFirst("preferred_username")?.Value ?? http.User.FindFirst("email")?.Value ?? http.User.FindFirst("name")?.Value ?? http.User.Identity.Name ?? "authenticated"
            : "anonymous";
        var created = await service.CreateAsync(request.Name, request.Role, actor, request.ExpiresAtUtc, ct);
        return Results.Created($"/api/tokens/{created.Token.Id}", new ApiTokenCreatedResponse(ToResponse(created.Token), created.Secret));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

tokensGroup.MapDelete("/{tokenId:guid}", async (Guid tokenId, ApiTokenService service, CancellationToken ct) =>
    await service.RevokeAsync(tokenId, ct) ? Results.NoContent() : Results.NotFound());

static ApiTokenResponse ToResponse(Atlas.Application.Security.ApiTokenSummary t) =>
    new(t.Id, t.Name, t.Hint, t.Role, t.CreatedBy, t.CreatedAtUtc, t.ExpiresAtUtc, t.LastUsedAtUtc, t.RevokedAtUtc, t.Active);


// ---- Sharing: who can see/edit a restricted assessment (owners + tenant admins manage) ----
assessments.MapGet("/{id:guid}/access", async (Guid id, IAssessmentRepository repository, AssessmentAccessService access, CancellationToken ct) =>
{
    if (await repository.GetAsync(id, ct) is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(ApiMapping.ToResponse(await access.GetAsync(id, ct)));
});

assessments.MapPut("/{id:guid}/access", async (Guid id, AccessGrantRequest request, AssessmentAccessService access, CancellationToken ct) =>
{
    if (!Enum.TryParse<AccessRole>(request.Role, ignoreCase: true, out var role))
    {
        return Results.BadRequest(new { error = "role must be Viewer, Editor or Owner." });
    }

    try
    {
        return Results.Ok(ApiMapping.ToResponse(await access.GrantAsync(id, request.Subject, request.SubjectName, role, ct)));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

assessments.MapDelete("/{id:guid}/access/{entryId:guid}", async (Guid id, Guid entryId, AssessmentAccessService access, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(ApiMapping.ToResponse(await access.RevokeAsync(id, entryId, ct)));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

// ---- AI provider: admin-only, key write-only, opt-in egress ----
// Weekly history of the portfolio, recomputed from the persisted runs (latest completed run of each assessment per week).
app.MapGet("/api/portfolio/trend", async (IAssessmentRunRepository runs, CancellationToken ct, int weeks = 26) =>
{
    var points = Atlas.Application.Portfolio.PortfolioTrend.Compute(await runs.ListCompletedPointsAsync(ct), DateOnly.FromDateTime(DateTime.UtcNow), weeks);
    return Results.Ok(points.Select(p => new PortfolioTrendPointResponse(p.Date, p.AverageScore, p.OpenFindings, p.Assessed)).ToList());
}).RequireRateLimiting("api");

var aiGroup = app.MapGroup("/api/ai").RequireRateLimiting("api");

aiGroup.MapGet("/settings", async (AiSettingsService service, CancellationToken ct) => Results.Ok(await service.GetAsync(ct)));

aiGroup.MapPut("/settings", async (AiSettingsRequest request, AiSettingsService service, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await service.UpsertAsync(request.Provider, request.Model, request.BaseUrl, request.ApiKey, request.Enabled, request.MaxSnippetsPerAnalysis, ct));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (SecretStoreNotConfiguredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

aiGroup.MapDelete("/settings/key", async (AiSettingsService service, CancellationToken ct) => Results.Ok(await service.ClearKeyAsync(ct)));

aiGroup.MapPost("/test", async (AiSettingsService service, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await service.TestAsync(ct));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});


// Perceived quality of the AI features: votes per kind and per model, latest comments.
aiGroup.MapGet("/feedback", async (AiFeedbackService service, CancellationToken ct) =>
{
    var s = await service.SummarizeAsync(ct);
    return Results.Ok(new AiFeedbackSummaryResponse(s.Up, s.Down,
        s.ByKind.Select(b => new FeedbackBucketResponse(b.Key, b.Up, b.Down, b.HelpfulShare)).ToList(),
        s.ByModel.Select(b => new FeedbackBucketResponse(b.Key, b.Up, b.Down, b.HelpfulShare)).ToList(),
        s.Recent.Select(e => new FeedbackEntryResponse(e.Kind, e.Model, e.Rating, e.Comment, e.AssessmentId, e.RatedBy, e.RatedAtUtc, e.Title)).ToList()));
});

// Pre-flight cost of one business-rule analysis at the configured cap (no materialization).
aiGroup.MapGet("/estimate", async (AiSettingsService service, CancellationToken ct, int? methods = null) =>
{
    var settings = await service.GetAsync(ct);
    var e = AiNarrativeService.Estimate(methods ?? settings.MaxSnippetsPerAnalysis, BusinessRuleExtractor.SnippetsPerBatch);
    return Results.Ok(new AiEstimateResponse(e.Methods, e.Requests, e.InputTokens, e.OutputTokens, e.Note));
});

// Cached explanation, if one exists (204 otherwise) — the UI shows it when a finding is expanded without spending tokens.
assessments.MapGet("/{id:guid}/findings/{findingId:guid}/explain", async (Guid id, Guid findingId, IFindingRepository findings, IAiNarrativeRepository narratives, CancellationToken ct, string? lang = null) =>
{
    var finding = await findings.GetAsync(findingId, ct);
    if (finding is null || finding.AssessmentId != id)
    {
        return Results.NotFound();
    }

    var n = await narratives.GetAsync(id, Atlas.Domain.Ai.AiNarrative.Kinds.FindingExplanation, finding.Fingerprint, Atlas.Domain.Ai.AiNarrative.NormalizeLang(lang), ct);
    return n is null ? Results.NoContent() : Results.Ok(new NarrativeResponse(n.Text, n.Model, true, n.CreatedAtUtc, n.Rating, n.FeedbackComment));
});

// Explain one finding with the model (cached per fingerprint + language). No source code is sent.
assessments.MapPost("/{id:guid}/findings/{findingId:guid}/explain", async (Guid id, Guid findingId, AiNarrativeService service, CancellationToken ct, string? lang = null, bool refresh = false) =>
{
    try
    {
        var r = await service.ExplainFindingAsync(id, findingId, lang, refresh, ct);
        return Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (AiNotConfiguredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
    }
    catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

// Fix suggestion: a worker job sends the ~50 lines around the finding (credentials masked; never secrets findings) and stores
// a diagnosis plus a unified diff, cached per fingerprint and language. POST queues, GET returns the suggestion and job state.
assessments.MapPost("/{id:guid}/findings/{findingId:guid}/fix", async (Guid id, Guid findingId, QueueFindingFixHandler handler, CancellationToken ct, string? lang = null) =>
{
    try
    {
        var jobId = await handler.HandleAsync(id, findingId, lang, ct);
        return Results.Accepted($"/api/assessments/{id}/findings/{findingId}/fix", new RunQueuedResponse(jobId));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (AiNotConfiguredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
    }
    catch (FixNotEligibleException ex)
    {
        return Results.UnprocessableEntity(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

assessments.MapGet("/{id:guid}/findings/{findingId:guid}/fix", async (
    Guid id, Guid findingId, IFindingRepository findings, IAiNarrativeRepository narratives, IScanJobQueue queue, CancellationToken ct, string? lang = null) =>
{
    var finding = await findings.GetAsync(findingId, ct);
    if (finding is null || finding.AssessmentId != id)
    {
        return Results.NotFound();
    }

    var language = Atlas.Domain.Ai.AiNarrative.NormalizeLang(lang);
    var narrative = await narratives.GetAsync(id, Atlas.Domain.Ai.AiNarrative.Kinds.FindingFix, finding.Fingerprint, language, ct);
    var key = findingId.ToString();
    var job = (await queue.ListRecentAsync(200, null, ct))
        .Where(j => j.AssessmentId == id && j.Kind == Atlas.Domain.Jobs.ScanJob.Kinds.FindingFix && j.Payload is not null && j.Payload.Contains(key, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(j => j.QueuedAtUtc)
        .FirstOrDefault();
    return Results.Ok(new FindingFixResponse(
        narrative is null ? null : new NarrativeResponse(narrative.Text, narrative.Model, true, narrative.CreatedAtUtc, narrative.Rating, narrative.FeedbackComment),
        job?.State.ToString(), job?.Error));
});

// Executive summary written by the model from the report's own figures; appears on page one once generated.
assessments.MapGet("/{id:guid}/summary", async (Guid id, AiNarrativeService service, CancellationToken ct, string? lang = null) =>
{
    var r = await service.GetSummaryAsync(id, lang, ct);
    return r is null ? Results.NoContent() : Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
});

assessments.MapPost("/{id:guid}/summary/generate", async (Guid id, ReportNarrativeService service, CancellationToken ct, string? lang = null) =>
{
    try
    {
        var r = await service.GenerateSummaryAsync(id, lang, ct);
        return Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (AiNotConfiguredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
    }
    catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

// Migration plan drafted by the model from the modernization plan's own facts (profile, strategy rationale,
// estimate, roadmap). Markdown, cached per language; the report renders it after the strategy section.
assessments.MapGet("/{id:guid}/migration-plan", async (Guid id, AiNarrativeService service, CancellationToken ct, string? lang = null) =>
{
    var r = await service.GetMigrationPlanAsync(id, lang, ct);
    return r is null ? Results.NoContent() : Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
});

assessments.MapPost("/{id:guid}/migration-plan/generate", async (Guid id, ReportNarrativeService service, CancellationToken ct, string? lang = null) =>
{
    try
    {
        var r = await service.GenerateMigrationPlanAsync(id, lang, ct);
        return Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (AiNotConfiguredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
    }
    catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

// The plan as a Markdown file (title, AI label and the text as written), for wikis and pull requests.
assessments.MapGet("/{id:guid}/migration-plan/export", async (Guid id, IAssessmentRepository repository, AiNarrativeService service, CancellationToken ct, string? lang = null) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    var r = await service.GetMigrationPlanAsync(id, lang, ct);
    if (r is null)
    {
        return Results.Json(new { error = "No migration plan yet: generate it on the Modernization tab first." }, statusCode: StatusCodes.Status404NotFound);
    }

    var pt = Atlas.Domain.Ai.AiNarrative.NormalizeLang(lang) == "pt-BR";
    var label = pt
        ? $"Escrito por IA ({r.Model}) a partir dos números do assessment em {r.CreatedAtUtc:yyyy-MM-dd}; revise antes de circular."
        : $"Written by AI ({r.Model}) from the assessment's figures on {r.CreatedAtUtc:yyyy-MM-dd}; review before circulating.";
    var markdown = $"# {assessment.Name} — {(pt ? "Plano de migração (rascunho por IA)" : "Migration plan (AI draft)")}\n\n_{label}_\n\n{r.Text.Trim()}\n";
    var safeName = string.Concat(assessment.Name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')).ToLowerInvariant();
    return Results.File(System.Text.Encoding.UTF8.GetBytes(markdown), "text/markdown; charset=utf-8", $"{safeName}-migration-plan.md");
});

// Thumbs up / down on what the model wrote: finding explanation or fix (findingId), executive summary or migration plan.
assessments.MapPut("/{id:guid}/ai/feedback", async (Guid id, FeedbackRequest body, AiFeedbackService service, CancellationToken ct, string kind = "", Guid? findingId = null, string? lang = null) =>
{
    try
    {
        var n = await service.RateNarrativeAsync(id, kind, findingId, lang, body.Rating, body.Comment, body.Author, ct);
        return Results.Ok(new NarrativeResponse(n.Text, n.Model, true, n.CreatedAtUtc, n.Rating, n.FeedbackComment));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

assessments.MapPut("/{id:guid}/business-rules/{ruleId:guid}/feedback", async (Guid id, Guid ruleId, FeedbackRequest body, AiFeedbackService service, CancellationToken ct, string? lang = null) =>
{
    try
    {
        var rule = await service.RateBusinessRuleAsync(id, ruleId, body.Rating, body.Comment, body.Author, ct);
        return Results.Ok(ApiMapping.ToResponse(rule, lang?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ?? false));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Business rules recovered by the model, per assessment.
assessments.MapGet("/{id:guid}/business-rules", async (
    Guid id, IAssessmentRepository repository, IBusinessRuleRepository rules, IAiSettingsRepository aiSettings, CancellationToken ct, string? lang = null) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    var settings = await aiSettings.GetAsync(assessment.TenantId, ct);
    var analyses = await rules.ListAnalysesAsync(id, 10, ct);
    var items = await rules.ListAsync(id, ct);
    var pt = string.Equals(lang, "pt", StringComparison.OrdinalIgnoreCase) || (lang?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ?? false);
    return Results.Ok(new BusinessRulesResponse(
        settings?.IsUsable ?? false,
        analyses.Select(ApiMapping.ToResponse).ToList(),
        items.Select(r => ApiMapping.ToResponse(r, pt)).ToList()));
});

assessments.MapPost("/{id:guid}/business-rules/analyze", async (Guid id, QueueBusinessRuleAnalysisHandler handler, CancellationToken ct) =>
{
    try
    {
        var jobId = await handler.HandleAsync(id, ct);
        return Results.Accepted($"/api/assessments/{id}/business-rules", new RunQueuedResponse(jobId));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (AiNotConfiguredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

assessments.MapGet("/{id:guid}/business-rules/export", async (
    Guid id, IAssessmentRepository repository, IBusinessRuleRepository rules, CancellationToken ct, string format = "csv", string? lang = null) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    var pt = lang?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ?? false;
    var items = (await rules.ListAsync(id, ct)).Select(r => ApiMapping.ToResponse(r, pt)).ToList();
    var safeName = string.Concat(assessment.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')).ToLowerInvariant();
    if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
    {
        return Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(items, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }),
            "application/json", $"{safeName}-business-rules.json");
    }

    var sb = new System.Text.StringBuilder();
    sb.AppendLine(pt ? "Arquivo,Membro,Linha,Regra,Descricao,Categoria,Condicoes,Confianca,Modelo" : "File,Member,Line,Rule,Description,Category,Conditions,Confidence,Model");
    foreach (var r in items)
    {
        sb.AppendLine(string.Join(",", new[] { r.FilePath, r.Symbol, r.StartLine.ToString(), r.Name, r.Description, r.Category, string.Join(" | ", r.Conditions), r.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), r.Model }
            .Select(v => "\"" + v.Replace("\"", "\"\"") + "\"")));
    }

    return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"{safeName}-business-rules.csv");
});

// Re-upload: point an "upload" assessment at a new archive (already posted to /api/uploads) and queue a run.
assessments.MapPut("/{id:guid}/upload", async (
    Guid id, ReplaceUploadRequest request, IAssessmentRepository repository, IUnitOfWork unitOfWork, RunAgainHandler runAgain, UploadOptions uploads, CancellationToken ct) =>
{
    var assessment = await repository.GetAsync(id, ct);
    if (assessment is null)
    {
        return Results.NotFound();
    }

    string archive;
    try
    {
        archive = UploadConnector.ArchivePath(uploads, request.UploadId);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    if (!File.Exists(archive))
    {
        return Results.BadRequest(new { error = $"Upload '{request.UploadId}' was not found; post the archive to /api/uploads first." });
    }

    var previous = assessment.SourceLocator;
    try
    {
        assessment.ReplaceUpload(request.UploadId);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    await unitOfWork.SaveChangesAsync(ct);
    if (!string.Equals(previous, assessment.SourceLocator, StringComparison.OrdinalIgnoreCase))
    {
        UploadGcService.DeleteUpload(uploads.Directory, previous);
    }

    try
    {
        var jobId = await runAgain.HandleAsync(id, ct);
        return Results.Accepted($"/api/assessments/{id}", new RunQueuedResponse(jobId));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

assessments.MapPost("/{id:guid}/runs", async (Guid id, RunAgainHandler handler, CancellationToken ct) =>
{
    try
    {
        var jobId = await handler.HandleAsync(id, ct);
        return Results.Accepted($"/api/assessments/{id}", new RunQueuedResponse(jobId));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

assessments.MapGet("/{id:guid}/runs/{runId:guid}/comparison", async (
    Guid id,
    Guid runId,
    RunComparisonBuilder builder,
    CancellationToken ct,
    Guid? with = null,
    string? lang = null) =>
{
    var comparison = await builder.BuildAsync(id, runId, with, lang, ct);
    return comparison is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(comparison));
});

assessments.MapGet("/{id:guid}/health", async (
    Guid id,
    IHealthRepository healthRepository,
    CancellationToken ct) =>
{
    var snapshot = await healthRepository.GetLatestAsync(id, ct);
    return snapshot is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(snapshot));
});

assessments.MapGet("/{id:guid}/report", async (
    Guid id,
    ExecutiveReportBuilder reportBuilder,
    ReportOptions reportOptions,
    CancellationToken ct,
    string? lang = null,
    DateTimeOffset? since = null) =>
{
    var locale = ReportLocale.For(lang);
    var report = await reportBuilder.BuildAsync(id, locale, ct, since);
    return report is null
        ? Results.NotFound()
        : Results.Content(HtmlReportRenderer.Render(report, locale, reportOptions), "text/html; charset=utf-8");
});

assessments.MapGet("/{id:guid}/report.pdf", async (
    Guid id,
    ExecutiveReportBuilder reportBuilder,
    IPdfRenderer pdf,
    ReportOptions reportOptions,
    ILogger<Program> logger,
    CancellationToken ct,
    string? lang = null,
    DateTimeOffset? since = null) =>
{
    var locale = ReportLocale.For(lang);
    var report = await reportBuilder.BuildAsync(id, locale, ct, since);
    if (report is null)
    {
        return Results.NotFound();
    }

    try
    {
        var bytes = await pdf.RenderAsync(HtmlReportRenderer.Render(report, locale, reportOptions), ct, HtmlReportRenderer.RenderPdfFooter(report, locale));
        var safeName = string.Concat(report.Header.AssessmentName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')).Trim().Replace(' ', '-');
        return Results.File(bytes, "application/pdf", $"atlas-{(safeName.Length == 0 ? id.ToString("N")[..8] : safeName)}-{DateTime.UtcNow:yyyyMMdd}.pdf");
    }
    catch (PdfRendererUnavailableException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
    {
        logger.LogWarning(ex, "PDF export failed via {Renderer}.", pdf.Description);
        return Results.Json(new { error = $"PDF export failed ({pdf.Description}): {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
    }
});

// Liveness: process is up. Readiness: dependencies (Postgres) reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

/// <summary>Exposes the entry point to in-process integration tests (WebApplicationFactory).</summary>
public partial class Program;
