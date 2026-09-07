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

/// <summary>Tenant settings: cost model, demo estate, notification channels.</summary>
internal static class SettingsEndpoints
{
    public static void Map(WebApplication app)
    {
        // The tenant's cost-model market parameters: currency and hourly rate are market facts, not FX conversions.
        app.MapGet("/api/settings/cost", async (ITenantCostProfileRepository profiles, Atlas.Domain.Modernization.CostParameters defaults, Atlas.Application.Tenants.ITenantContext tenant, CancellationToken ct) =>
        {
            var profile = await profiles.GetForTenantAsync(tenant.Require(), ct);
            return Results.Ok(profile is null
                ? new CostProfileResponse(defaults.Currency, defaults.HourlyRate, defaults.TeamSize, IsDefault: true, null, null)
                : new CostProfileResponse(profile.Currency, profile.HourlyRate, profile.TeamSize ?? defaults.TeamSize, IsDefault: false, profile.UpdatedBy, profile.UpdatedAtUtc,
                    profile.WindowsHostingPerLegacyAppYear, profile.ExtendedSupportPerLegacyAppYear, profile.SqlServerSavingsPerYear));
        }).RequireRateLimiting("api");

        app.MapPut("/api/settings/cost", async (CostProfileRequest request, ITenantCostProfileRepository profiles, Atlas.Domain.Modernization.CostParameters defaults, Atlas.Application.Tenants.ITenantContext tenant, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            try
            {
                var author = string.IsNullOrWhiteSpace(request.Author) ? tenant.SubjectName ?? tenant.Subject ?? "unknown" : request.Author;
                var profile = await profiles.GetForTenantAsync(tenant.Require(), ct);
                if (profile is null)
                {
                    profile = new Atlas.Domain.Modernization.TenantCostProfile(tenant.Require(), request.Currency, request.HourlyRate, request.TeamSize, author,
                        request.WindowsHostingPerLegacyAppYear, request.ExtendedSupportPerLegacyAppYear, request.SqlServerSavingsPerYear);
                    profiles.Add(profile);
                }
                else
                {
                    profile.Update(request.Currency, request.HourlyRate, request.TeamSize, author,
                        request.WindowsHostingPerLegacyAppYear, request.ExtendedSupportPerLegacyAppYear, request.SqlServerSavingsPerYear);
                }

                await unitOfWork.SaveChangesAsync(ct);
                return Results.Ok(new CostProfileResponse(profile.Currency, profile.HourlyRate, profile.TeamSize ?? defaults.TeamSize, IsDefault: false, profile.UpdatedBy, profile.UpdatedAtUtc,
                    profile.WindowsHostingPerLegacyAppYear, profile.ExtendedSupportPerLegacyAppYear, profile.SqlServerSavingsPerYear));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireRateLimiting("api");

        app.MapDelete("/api/settings/cost", async (ITenantCostProfileRepository profiles, Atlas.Application.Tenants.ITenantContext tenant, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            var profile = await profiles.GetForTenantAsync(tenant.Require(), ct);
            if (profile is not null)
            {
                profiles.Remove(profile);
                await unitOfWork.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        }).RequireRateLimiting("api");

        // One-click demo estate: five fictional assessments so dashboards and reports come alive instantly.
        app.MapPost("/api/demo", async (Atlas.Application.Assessments.DemoSeeder seeder, CancellationToken ct) =>
            Results.Ok(new { created = await seeder.SeedAsync(ct) })).RequireRateLimiting("api");

        app.MapDelete("/api/demo", async (Atlas.Application.Assessments.DemoSeeder seeder, CancellationToken ct) =>
            Results.Ok(new { removed = await seeder.RemoveAsync(ct) })).RequireRateLimiting("api");

        // The tenant's own notification channels; overrides the deployment-wide Atlas:Notifications.
        app.MapGet("/api/settings/notifications", async (ITenantNotificationSettingsRepository settingsRepo, Atlas.Application.Tenants.ITenantContext tenant, CancellationToken ct) =>
        {
            var row = await settingsRepo.GetForTenantAsync(tenant.Require(), ct);
            return Results.Ok(row is null
                ? new NotificationSettingsResponse(null, false, null, null, null, 13, IsDefault: true, null, null)
                : new NotificationSettingsResponse(row.WebhookUrl, row.Secret is not null, row.SlackWebhookUrl, row.TeamsWebhookUrl, row.DigestDayOfWeek, row.DigestHourUtc, IsDefault: false, row.UpdatedBy, row.UpdatedAtUtc));
        }).RequireRateLimiting("api");

        app.MapPut("/api/settings/notifications", async (NotificationSettingsRequest request, ITenantNotificationSettingsRepository settingsRepo, Atlas.Application.Tenants.ITenantContext tenant, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            try
            {
                var author = string.IsNullOrWhiteSpace(request.Author) ? tenant.SubjectName ?? tenant.Subject ?? "unknown" : request.Author;
                var row = await settingsRepo.GetForTenantAsync(tenant.Require(), ct);
                if (row is null)
                {
                    row = new Atlas.Domain.Tenants.TenantNotificationSettings(tenant.Require(), author);
                    settingsRepo.Add(row);
                }

                // The secret is write-only: the UI never sees it back. null keeps what is stored; empty or
                // whitespace clears; anything else replaces.
                var effectiveSecret = request.Secret is null ? row.Secret : (request.Secret.Trim().Length == 0 ? null : request.Secret);
                row.Update(request.WebhookUrl, effectiveSecret, request.SlackWebhookUrl, request.TeamsWebhookUrl, request.DigestDayOfWeek, request.DigestHourUtc, author);
                if (row.IsEmpty)
                {
                    settingsRepo.Remove(row);
                    await unitOfWork.SaveChangesAsync(ct);
                    return Results.Ok(new NotificationSettingsResponse(null, false, null, null, null, 13, IsDefault: true, null, null));
                }

                await unitOfWork.SaveChangesAsync(ct);
                return Results.Ok(new NotificationSettingsResponse(row.WebhookUrl, row.Secret is not null, row.SlackWebhookUrl, row.TeamsWebhookUrl, row.DigestDayOfWeek, row.DigestHourUtc, IsDefault: false, row.UpdatedBy, row.UpdatedAtUtc));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireRateLimiting("api");
    }
}
