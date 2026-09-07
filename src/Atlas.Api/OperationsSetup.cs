using System.Security.Claims;
using System.Threading.RateLimiting;
using Atlas.Application.Assessments;
using Atlas.Application.Audit;
using Atlas.Application.Tenants;
using Atlas.Domain.Audit;
using Atlas.Domain.Jobs;
using Atlas.Domain.Tenants;
using Microsoft.AspNetCore.RateLimiting;
using Prometheus;

namespace Atlas.Api;

public sealed class OperationsOptions
{
    public const string SectionName = "Atlas:Operations";

    /// <summary>Prometheus scrape endpoint (/metrics) and HTTP request metrics.</summary>
    public bool MetricsEnabled { get; set; } = true;

    /// <summary>Requests per minute per client IP on /api before 429.</summary>
    public int RateLimitPerMinute { get; set; } = 600;

    /// <summary>Persist every state-changing /api call to atlas.audit_entries.</summary>
    public bool AuditEnabled { get; set; } = true;

    /// <summary>Emit JSON console logs (one object per line) instead of the human-readable formatter.</summary>
    public bool JsonLogs { get; set; }
}

/// <summary>
/// Production plumbing (observability, §33 security): Prometheus
/// metrics with queue depth and scan health gauges, a per-IP rate limiter on the
/// API, structured logs, and an append-only audit trail of every mutating call.
/// </summary>
public static class OperationsSetup
{
    private static readonly Gauge QueueDepth = Metrics.CreateGauge("atlas_scan_jobs", "Scan jobs by state.", "state");
    private static readonly Gauge Assessments = Metrics.CreateGauge("atlas_assessments", "Assessments known to this instance.");
    private static readonly Gauge HealthAverage = Metrics.CreateGauge("atlas_health_score_average", "Average latest health score across assessed assessments.");
    private static readonly Gauge OpenFindings = Metrics.CreateGauge("atlas_open_findings", "Open findings across the estate by severity.", "severity");
    private static readonly Counter AuditWrites = Metrics.CreateCounter("atlas_audit_entries_total", "Audit entries written.");

    public static OperationsOptions AddAtlasOperations(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(OperationsOptions.SectionName).Get<OperationsOptions>() ?? new OperationsOptions();
        builder.Services.AddSingleton(options);

        if (options.JsonLogs)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(json =>
            {
                json.IncludeScopes = true;
                json.UseUtcTimestamp = true;
                json.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(10, options.RateLimitPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        return options;
    }

    public static void UseAtlasOperations(this WebApplication app, OperationsOptions options)
    {
        app.UseRateLimiter();

        if (options.MetricsEnabled)
        {
            app.UseHttpMetrics();
            var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
            Metrics.DefaultRegistry.AddBeforeCollectCallback(async ct =>
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    scope.ServiceProvider.GetRequiredService<HttpTenantContext>().UseSystemScope();
                    var queue = scope.ServiceProvider.GetRequiredService<IScanJobQueue>();
                    var jobs = await queue.ListRecentAsync(500, null, ct);
                    foreach (var state in Enum.GetValues<ScanJobState>())
                    {
                        QueueDepth.WithLabels(state.ToString()).Set(jobs.Count(j => j.State == state));
                    }

                    var portfolio = await scope.ServiceProvider.GetRequiredService<Atlas.Application.Portfolio.PortfolioBuilder>().BuildAsync(null, ct);
                    Assessments.Set(portfolio.Assessments);
                    HealthAverage.Set(portfolio.AverageScore ?? double.NaN);
                    foreach (var (severity, count) in portfolio.OpenBySeverity)
                    {
                        OpenFindings.WithLabels(severity.ToString()).Set(count);
                    }
                }
                catch
                {
                    // Metrics must never take the API down; stale gauges are acceptable.
                }
            });
            app.MapMetrics().AllowAnonymous();
        }

        if (options.AuditEnabled)
        {
            app.Use(async (context, next) =>
            {
                await next(context);

                if (!IsAuditable(context))
                {
                    return;
                }

                try
                {
                    var repository = context.RequestServices.GetRequiredService<IAuditRepository>();
                    var unitOfWork = context.RequestServices.GetRequiredService<IUnitOfWork>();
                    repository.Add(new AuditEntry(
                        context.RequestServices.GetRequiredService<Atlas.Application.Tenants.ITenantContext>().Require(),
                        ActorOf(context.User),
                        context.Request.Method,
                        context.Request.Path.Value ?? "/",
                        context.Response.StatusCode,
                        AssessmentIdOf(context.Request.Path.Value),
                        context.Request.RouteValues.TryGetValue("name", out var name) ? name?.ToString() : null,
                        context.Connection.RemoteIpAddress?.ToString()));
                    await unitOfWork.SaveChangesAsync(CancellationToken.None);
                    AuditWrites.Inc();
                }
                catch (Exception ex)
                {
                    context.RequestServices.GetRequiredService<ILogger<OperationsOptions>>().LogWarning(ex, "Audit write failed for {Method} {Path}.", context.Request.Method, context.Request.Path);
                }
            });
        }
    }

    /// <summary>Mutating /api calls, excluding the audit endpoint itself and metrics.</summary>
    internal static bool IsAuditable(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/api/audit")
        && HttpMethods.IsPost(context.Request.Method) is var isPost
        && (isPost || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method));

    internal static string ActorOf(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true
            ? user.FindFirst("preferred_username")?.Value ?? user.FindFirst("email")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value ?? user.FindFirst("sub")?.Value ?? "authenticated"
            : "anonymous";

    internal static Guid? AssessmentIdOf(string? path)
    {
        if (path is null)
        {
            return null;
        }

        const string prefix = "/api/assessments/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segment = path[prefix.Length..].Split('/')[0];
        return Guid.TryParse(segment, out var id) ? id : null;
    }
}
