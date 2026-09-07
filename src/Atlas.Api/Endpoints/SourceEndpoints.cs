using Atlas.Api;
using Atlas.Api.Endpoints;
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
using Atlas.Governance.Infrastructure;
using Atlas.Infrastructure;
using Atlas.Infrastructure.Persistence;
using Atlas.Reporting;
using Atlas.Scanner.Ai;
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

namespace Atlas.Api.Endpoints;

/// <summary>Connectors, vulnerability feed status, source discovery, local source browsing and browser uploads.</summary>
internal static class SourceEndpoints
{
    public static void Map(WebApplication app)
    {
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
    }
}
