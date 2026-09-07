using Atlas.Application.Assessments;
using Atlas.Connector.Upload;
using Atlas.Domain.Sources;

namespace Atlas.Api;

/// <summary>
/// Removes browser-uploaded archives no assessment references any more (deleted
/// assessments, replaced uploads, uploads whose assessment was never created),
/// once they are older than <see cref="UploadOptions.OrphanRetentionHours"/>.
/// Archives still referenced are kept: re-runs need them.
/// </summary>
public sealed class UploadGcService(IServiceScopeFactory scopeFactory, UploadOptions options, ILogger<UploadGcService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<HttpTenantContext>().UseSystemScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAssessmentRepository>();
                var referenced = await repository.ListSourceLocatorsAsync(SourceReference.Kinds.Upload, stoppingToken);
                var removed = Sweep(options.Directory, referenced, TimeSpan.FromHours(options.OrphanRetentionHours), DateTimeOffset.UtcNow);
                if (removed > 0)
                {
                    logger.LogInformation("Upload GC removed {Count} orphaned archive(s).", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Upload GC failed; retrying in {Interval}.", Interval);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Deletes unreferenced archives (and their manifests) older than <paramref name="retention"/>. Returns how many.</summary>
    public static int Sweep(string directory, IEnumerable<string> referencedLocators, TimeSpan retention, DateTimeOffset now)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var keep = new HashSet<string>(
            referencedLocators.Select(l => Guid.TryParse(l, out var g) ? g.ToString("N") : l),
            StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var zip in Directory.EnumerateFiles(directory, "*.zip"))
        {
            var id = Path.GetFileNameWithoutExtension(zip);
            if (keep.Contains(id) || now - File.GetLastWriteTimeUtc(zip) < retention)
            {
                continue;
            }

            DeleteUpload(directory, id);
            removed++;
        }

        return removed;
    }

    public static void DeleteUpload(string directory, string id)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return;
        }

        foreach (var ext in new[] { ".zip", ".json" })
        {
            var path = Path.Combine(directory, guid.ToString("N") + ext);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
