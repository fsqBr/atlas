using System.Text;
using Atlas.Application.Assessments;
using Atlas.Application.Portfolio;

namespace Atlas.Worker;

/// <summary>
/// Posts the weekly portfolio digest to the configured Slack/Teams webhooks: average health and
/// open findings vs seven days ago, top movers, goals in danger. Opt-in via
/// Atlas:Notifications:DigestDayOfWeek; fires once in the configured UTC hour. Counts only,
/// never finding contents — same rule as every other notification.
/// </summary>
public sealed class WeeklyDigestService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    NotificationOptions options,
    ILogger<WeeklyDigestService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);
    private DateTimeOffset? _lastSentUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.DigestDayOfWeek)
            || (string.IsNullOrWhiteSpace(options.SlackWebhookUrl) && string.IsNullOrWhiteSpace(options.TeamsWebhookUrl)))
        {
            return; // not configured
        }

        if (!Enum.TryParse<DayOfWeek>(options.DigestDayOfWeek.Trim(), true, out var day))
        {
            logger.LogWarning("Atlas:Notifications:DigestDayOfWeek '{Value}' is not a day of week; digest disabled.", options.DigestDayOfWeek);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var due = now.DayOfWeek == day
                    && now.Hour == Math.Clamp(options.DigestHourUtc, 0, 23)
                    && (_lastSentUtc is null || now - _lastSentUtc > TimeSpan.FromDays(3));
                if (due)
                {
                    await SendAsync(now, stoppingToken);
                    _lastSentUtc = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Weekly digest failed; next attempt in {Tick}.", Tick);
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

    private async Task SendAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IAssessmentRunRepository>();
        var assessments = scope.ServiceProvider.GetRequiredService<IAssessmentRepository>();

        var points = await runs.ListCompletedPointsAsync(cancellationToken);
        var list = (await assessments.ListRecentAsync(2000, cancellationToken))
            .Select(a => new DigestAssessment(a.Id, a.Name, a.TargetScore, a.TargetDate))
            .ToList();

        var digest = PortfolioDigestBuilder.Build(points, list, now);
        if (digest is null)
        {
            logger.LogInformation("Weekly digest skipped: nothing assessed yet.");
            return;
        }

        await PostAsync(options.SlackWebhookUrl, ChatNotifications.DigestSlack(digest), "Slack", cancellationToken);
        await PostAsync(options.TeamsWebhookUrl, ChatNotifications.DigestTeams(digest), "Teams", cancellationToken);
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
