using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.Application.Assessments;

namespace Atlas.Worker;

public sealed class NotificationOptions
{
    public const string SectionName = "Atlas:Notifications";

    /// <summary>Tenant-wide webhook receiving every completed run (per-assessment URLs are added on top).</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>When set, payloads are signed: X-Atlas-Signature: sha256=HMAC(body).</summary>
    public string? Secret { get; set; }

    /// <summary>Base URL of the web UI used to build links in payloads (e.g. http://atlas.internal:3000).</summary>
    public string? PublicBaseUrl { get; set; }
}

/// <summary>What a webhook receives when a run completes: enough to route/alert, never finding contents.</summary>
public sealed record RunCompletedPayload(
    string Event,
    Guid AssessmentId,
    string AssessmentName,
    int RunNumber,
    string Status,
    int? HealthScore,
    int? PreviousHealthScore,
    int? HealthDelta,
    int? OpenFindings,
    int FindingsNew,
    int FindingsResolved,
    int FindingsRegressed,
    string? Url,
    DateTimeOffset CompletedAtUtc,
    int? TargetScore = null,
    DateTimeOffset? TargetDate = null,
    string? TargetStatus = null);

/// <summary>
/// Posts a run-completed event to the assessment's webhook and to the tenant-wide
/// one ("Continuous Intelligence", first step). Best effort: a
/// failing endpoint is logged, never fails the run.
/// </summary>
public sealed class RunNotifier(IHttpClientFactory httpClientFactory, NotificationOptions options, ILogger<RunNotifier> logger)
{
    public const string HttpClientName = "webhooks";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task NotifyAsync(RunCompletedPayload payload, string? assessmentWebhook, CancellationToken cancellationToken)
    {
        var targets = new[] { assessmentWebhook, options.WebhookUrl }
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var body = JsonSerializer.Serialize(payload, Json);
        foreach (var target in targets)
        {
            try
            {
                using var http = httpClientFactory.CreateClient(HttpClientName);
                http.Timeout = TimeSpan.FromSeconds(15);
                using var request = new HttpRequestMessage(HttpMethod.Post, target)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Atlas-Event", payload.Event);
                if (!string.IsNullOrWhiteSpace(options.Secret))
                {
                    request.Headers.Add("X-Atlas-Signature", "sha256=" + Sign(body, options.Secret));
                }

                using var response = await http.SendAsync(request, cancellationToken);
                logger.LogInformation("Webhook {Target} answered {Status} for run #{Run} of {Assessment}.", target, (int)response.StatusCode, payload.RunNumber, payload.AssessmentId);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Webhook {Target} failed for run #{Run} of {Assessment}.", target, payload.RunNumber, payload.AssessmentId);
            }
        }
    }

    public static string Sign(string body, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    public static RunCompletedPayload BuildPayload(string assessmentName, Guid assessmentId, RunSummary current, RunSummary? previous, string? publicBaseUrl, int? targetScore = null, DateTimeOffset? targetDate = null)
    {
        var targetStatus = targetScore is null ? null : Atlas.Domain.Assessments.Targets.Evaluate(current.HealthScore, targetScore, targetDate, DateTimeOffset.UtcNow).ToString();
        var delta = current.HealthScore is { } s && previous?.HealthScore is { } p ? s - p : (int?)null;
        return new RunCompletedPayload(
            "run.completed", assessmentId, assessmentName, current.Number, current.Status, current.HealthScore, previous?.HealthScore, delta,
            current.OpenFindings, current.FindingsNew, current.FindingsResolved, current.FindingsRegressed,
            string.IsNullOrWhiteSpace(publicBaseUrl) ? null : $"{publicBaseUrl.TrimEnd('/')}/assessments/{assessmentId}",
            current.FinishedAtUtc ?? DateTimeOffset.UtcNow,
            targetScore, targetDate, targetStatus);
    }
}
