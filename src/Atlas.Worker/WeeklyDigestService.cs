using System.Globalization;
using System.Text;
using Atlas.Application.Assessments;
using Atlas.Application.Portfolio;

namespace Atlas.Worker;

/// <summary>
/// Posts the weekly portfolio digest to chat webhooks: average health and open findings vs seven
/// days ago, top movers, goals in danger. Per-tenant settings isolate each tenant's numbers on its
/// own channels; the deployment-wide fallback refuses to mix tenants. Dedupe is a DURABLE ATOMIC
/// claim (system_markers upsert), so worker replicas and restarts never re-send — the winner is
/// decided by the database, not a check-then-set.
/// </summary>
public sealed class WeeklyDigestService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    NotificationOptions options,
    ILogger<WeeklyDigestService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Weekly digest tick failed; next attempt in {Tick}.", Tick);
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsRepository = scope.ServiceProvider.GetRequiredService<ITenantNotificationSettingsRepository>();
        var markers = scope.ServiceProvider.GetRequiredService<ISystemMarkerRepository>();
        var runs = scope.ServiceProvider.GetRequiredService<IAssessmentRunRepository>();
        var assessments = scope.ServiceProvider.GetRequiredService<IAssessmentRepository>();

        var allRows = await settingsRepository.ListAllAsync(cancellationToken);
        var digestRows = allRows
            .Where(s => s.DigestDayOfWeek is not null && (s.SlackWebhookUrl is not null || s.TeamsWebhookUrl is not null))
            .ToList();
        var globalConfigured = !string.IsNullOrWhiteSpace(options.DigestDayOfWeek)
            && (!string.IsNullOrWhiteSpace(options.SlackWebhookUrl) || !string.IsNullOrWhiteSpace(options.TeamsWebhookUrl));
        if (digestRows.Count == 0 && !globalConfigured)
        {
            return;
        }

        IReadOnlyList<Atlas.Domain.Assessments.Assessment>? all = null;
        IReadOnlyList<CompletedRunPoint>? points = null;
        async Task<(IReadOnlyList<Atlas.Domain.Assessments.Assessment>, IReadOnlyList<CompletedRunPoint>)> DataAsync()
        {
            all ??= await assessments.ListRecentAsync(10_000, cancellationToken);
            points ??= await runs.ListCompletedPointsAsync(null, cancellationToken);
            return (all, points);
        }

        // A durable, ATOMIC claim (not check-then-set): two worker replicas waking on the same cron
        // minute cannot both send. The claim window matches the old 3-day resend guard.
        Task<bool> TryClaimAsync(string key) =>
            markers.TryClaimAsync(key, now.ToString("O", CultureInfo.InvariantCulture), now.AddDays(-3), cancellationToken);

        foreach (var row in digestRows)
        {
            if (!Enum.TryParse<DayOfWeek>(row.DigestDayOfWeek!, true, out var day)
                || now.DayOfWeek != day
                || now.Hour != Math.Clamp(row.DigestHourUtc, 0, 23)
                || !await TryClaimAsync($"digest:tenant:{row.TenantId:N}"))
            {
                continue;
            }

            var (list, allPoints) = await DataAsync();
            var tenantAssessments = list.Where(a => a.TenantId == row.TenantId).ToList();
            var ids = tenantAssessments.Select(a => a.Id).ToHashSet();
            var digest = PortfolioDigestBuilder.Build(
                allPoints.Where(p => ids.Contains(p.AssessmentId)).ToList(),
                tenantAssessments.Select(a => new DigestAssessment(a.Id, a.Name, a.TargetScore, a.TargetDate)).ToList(),
                now);
            if (digest is null)
            {
                continue;
            }

            // The claim above already marked this tenant sent for the window.
            await PostAsync(row.SlackWebhookUrl, ChatNotifications.DigestSlack(digest), "Slack", cancellationToken);
            await PostAsync(row.TeamsWebhookUrl, ChatNotifications.DigestTeams(digest), "Teams", cancellationToken);
        }

        if (globalConfigured
            && Enum.TryParse<DayOfWeek>(options.DigestDayOfWeek!.Trim(), true, out var globalDay)
            && now.DayOfWeek == globalDay
            && now.Hour == Math.Clamp(options.DigestHourUtc, 0, 23))
        {
            var (list, allPoints) = await DataAsync();
            // Any tenant with its OWN settings row is isolated from the deployment-wide channels —
            // whether or not the row configures a digest (its run notifications are isolated too).
            var isolatedTenants = allRows.Select(r => r.TenantId).ToHashSet();
            var eligible = list.Where(a => !isolatedTenants.Contains(a.TenantId)).ToList();
            if (eligible.Select(a => a.TenantId).Distinct().Count() > 1)
            {
                logger.LogWarning("Weekly digest (global) skipped: multiple tenants without per-tenant channels; configure Settings → Administration per tenant.");
                return;
            }

            // Claim only after the deterministic eligibility checks, and only once we know we may send.
            if (!await TryClaimAsync("digest:global"))
            {
                return;
            }

            var ids = eligible.Select(a => a.Id).ToHashSet();
            var digest = PortfolioDigestBuilder.Build(
                allPoints.Where(p => ids.Contains(p.AssessmentId)).ToList(),
                eligible.Select(a => new DigestAssessment(a.Id, a.Name, a.TargetScore, a.TargetDate)).ToList(),
                now);
            if (digest is null)
            {
                return;
            }

            await PostAsync(options.SlackWebhookUrl, ChatNotifications.DigestSlack(digest), "Slack", cancellationToken);
            await PostAsync(options.TeamsWebhookUrl, ChatNotifications.DigestTeams(digest), "Teams", cancellationToken);
        }
    }

    private async Task PostAsync(string? url, string body, string channel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return;
        }

        try
        {
            using var http = httpClientFactory.CreateClient(RunNotifier.HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(15);
            using var response = await http.PostAsync(uri, new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
            logger.LogInformation("Weekly digest posted to {Channel}: {Status}.", channel, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Weekly digest to {Channel} failed.", channel);
        }
    }
}
