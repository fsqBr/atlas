using Atlas.Application.Tenants;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Application;

/// <summary>Where alerts go: the tenant's notification channels (generic webhook, Slack, Teams).</summary>
/// <summary>Explicit channels (a team's); null falls back to the tenant's notification settings.</summary>
public sealed record AlertChannels(string? WebhookUrl, string? SlackWebhookUrl, string? TeamsWebhookUrl);

public interface IAlertSink
{
    /// <summary>Returns null on success, otherwise the delivery error (already truncated for storage).</summary>
    Task<string?> SendAsync(Guid tenantId, string title, string body, AlertChannels? channels, CancellationToken cancellationToken);
}

public sealed class NullAlertSink : IAlertSink
{
    public Task<string?> SendAsync(Guid tenantId, string title, string body, AlertChannels? channels, CancellationToken cancellationToken) => Task.FromResult<string?>("no notification channel configured");
}

public sealed record BudgetStatus(Budget Budget, decimal SpentMonthToDate, decimal Percent, decimal ProjectedMonth, decimal ProjectedPercent, string State, int DaysElapsed, int DaysInMonth);

public sealed record TeamSpend(string Team, int Members, int ActiveMembers, long Tokens, decimal EstimatedCost, decimal MonthToDateCost, decimal ProjectedMonthCost);

/// <summary>
/// Budgets: CRUD, status (spent, projected, state) and the alerting pass that runs after every ingest. Thresholds
/// alert once per month per budget; anomalies alert once per day per scope. Amounts are catalog estimates.
/// </summary>
public sealed class BudgetService(
    IBudgetRepository budgetsRepo,
    ITeamRepository teams,
    IUsageFactRepository facts,
    IAlertSink sink,
    IGovernanceUnitOfWork unitOfWork,
    ITenantContext tenant,
    ILogger<BudgetService> logger)
{
    public const decimal AnomalyMultiplier = 3m;
    public const decimal AnomalyMinimumUsd = 5m;

    public Task<IReadOnlyList<Budget>> ListAsync(CancellationToken ct) => budgetsRepo.ListAsync(ct);

    public async Task<Budget> UpsertAsync(Guid? id, BudgetScope scope, string? scopeKey, decimal monthlyAmount, string? name, bool enabled, string? updatedBy, CancellationToken ct)
    {
        var all = await budgetsRepo.ListAsync(ct);
        var existing = id is null ? null : all.FirstOrDefault(b => b.Id == id);
        if (existing is null)
        {
            if (all.Count >= 200)
            {
                throw new ArgumentException("At most 200 budgets per tenant.");
            }

            existing = new Budget(Guid.NewGuid(), tenant.Require(), scope, scopeKey, monthlyAmount, name, updatedBy ?? "web");
            if (!enabled)
            {
                existing.Set(monthlyAmount, name, updatedBy ?? "web", enabled: false);
            }

            budgetsRepo.Add(existing);
        }
        else
        {
            existing.Set(monthlyAmount, name, updatedBy ?? "web", enabled);
        }

        await unitOfWork.SaveChangesAsync(ct);
        await EvaluateAsync(ct); // a budget created already over its line alerts right away, not at the next report
        return existing;
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        var all = await budgetsRepo.ListAsync(ct);
        var row = all.FirstOrDefault(b => b.Id == id);
        if (row is null)
        {
            return false;
        }

        budgetsRepo.Remove(row);
        await unitOfWork.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<BudgetStatus>> StatusAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var rows = await facts.ListAsync(monthStart, today, ct);
        var all = await budgetsRepo.ListAsync(ct);
        var teamList = await teams.ListAsync(ct);
        return all.Select(b => Status(b, rows, teamList, today)).OrderByDescending(s => s.Percent).ToList();
    }

    public Task<IReadOnlyList<BudgetAlert>> RecentAlertsAsync(int days, CancellationToken ct) => budgetsRepo.ListAlertsAsync(DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90)), ct);

    /// <summary>Spend per team this window and month to date; actors outside every team roll up as "(unassigned)".</summary>
    public static IReadOnlyList<TeamSpend> ByTeam(IReadOnlyList<Team> teamList, IReadOnlyList<UsageFact> window, IReadOnlyList<UsageFact> month, DateOnly today, IReadOnlySet<string>? activeActors = null)
    {
        string TeamOf(string actor) => teamList.FirstOrDefault(t => t.Matches(actor))?.Name ?? "(unassigned)";
        var daysElapsed = Math.Max(1, today.Day);
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var names = teamList.Select(t => t.Name).Append("(unassigned)").Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new List<TeamSpend>();
        foreach (var name in names)
        {
            var w = window.Where(f => string.Equals(TeamOf(f.Actor), name, StringComparison.OrdinalIgnoreCase)).ToList();
            var m = month.Where(f => string.Equals(TeamOf(f.Actor), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (w.Count == 0 && m.Count == 0 && name == "(unassigned)")
            {
                continue;
            }

            var members = w.Concat(m).Select(f => f.Actor).Distinct(StringComparer.Ordinal).ToList();
            var mtd = m.Sum(f => f.EstimatedCost ?? 0m);
            result.Add(new TeamSpend(name, members.Count, activeActors is null ? 0 : members.Count(activeActors.Contains),
                w.Sum(f => f.TotalTokens), decimal.Round(w.Sum(f => f.EstimatedCost ?? 0m), 2), decimal.Round(mtd, 2), decimal.Round(mtd / daysElapsed * daysInMonth, 2)));
        }

        return result.OrderByDescending(t => t.EstimatedCost).ToList();
    }

    /// <summary>Runs after an ingest: raises threshold and anomaly alerts that were not raised yet, delivers them, stores them. Returns the number raised.</summary>
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var all = await budgetsRepo.ListAsync(ct);
        var rows = await facts.ListAsync(today.AddDays(-8) < monthStart ? today.AddDays(-8) : monthStart, today, ct);
        var teamList = await teams.ListAsync(ct);
        var existingKeys = await budgetsRepo.AlertKeysAsync(monthStart, ct);
        var raised = new List<BudgetAlert>();
        var tenantId = tenant.Require();
        var month = $"{today:yyyy-MM}";

        foreach (var b in all.Where(b => b.Enabled))
        {
            var status = Status(b, rows.Where(r => r.Period >= monthStart).ToList(), teamList, today);
            foreach (var threshold in Budget.Thresholds)
            {
                var key = $"threshold:{b.Id}:{month}:{threshold}";
                if (status.Percent >= threshold && !existingKeys.Contains(key))
                {
                    var message = threshold >= 100
                        ? $"Budget exceeded — {b.Label}: {status.SpentMonthToDate:N2} USD of {b.MonthlyAmount:N2} ({status.Percent:0}%) with {status.DaysInMonth - status.DaysElapsed} day(s) left."
                        : $"Budget at {threshold}% — {b.Label}: {status.SpentMonthToDate:N2} USD of {b.MonthlyAmount:N2}; projected {status.ProjectedMonth:N2} ({status.ProjectedPercent:0}%) by month end.";
                    raised.Add(new BudgetAlert(Guid.NewGuid(), tenantId, b.Id, "threshold", key, message, status.SpentMonthToDate, status.Percent));
                }
            }
        }

        // Anomaly: today's estimated spend far above the median of the previous seven days (tenant-wide).
        var byDay = rows.GroupBy(r => r.Period).ToDictionary(g => g.Key, g => g.Sum(r => r.EstimatedCost ?? 0m));
        var previous = Enumerable.Range(1, 7).Select(i => byDay.GetValueOrDefault(today.AddDays(-i))).Where(v => v > 0).OrderBy(v => v).ToList();
        var todayCost = byDay.GetValueOrDefault(today);
        if (previous.Count >= 4 && todayCost >= AnomalyMinimumUsd)
        {
            var median = previous[previous.Count / 2];
            var key = $"anomaly:tenant:{today:yyyy-MM-dd}";
            if (median > 0 && todayCost > median * AnomalyMultiplier && !existingKeys.Contains(key))
            {
                raised.Add(new BudgetAlert(Guid.NewGuid(), tenantId, null, "anomaly", key,
                    $"Unusual AI spend today: {todayCost:N2} USD so far, {todayCost / median:0.#}× the median of the last 7 days ({median:N2} USD/day).", todayCost, decimal.Round(todayCost / median * 100m, 0)));
            }
        }

        foreach (var alert in raised)
        {
            // A team budget goes to the team's own channels when it has any; everything else to the tenant's.
            AlertChannels? channels = null;
            var budget = alert.BudgetId is null ? null : all.FirstOrDefault(b => b.Id == alert.BudgetId);
            if (budget is { Scope: BudgetScope.Team } && teamList.FirstOrDefault(t => string.Equals(t.Name, budget.ScopeKey, StringComparison.OrdinalIgnoreCase)) is { HasChannels: true } team)
            {
                channels = new AlertChannels(team.WebhookUrl, team.SlackWebhookUrl, team.TeamsWebhookUrl);
            }

            var error = await sink.SendAsync(tenantId, alert.Kind == "anomaly" ? "Atlas: unusual AI spend" : "Atlas: AI budget", alert.Message, channels, ct);
            alert.MarkDelivered(error);
            budgetsRepo.AddAlert(alert);
            logger.LogWarning("AI budget alert ({Kind}): {Message} [delivery: {Delivery}]", alert.Kind, alert.Message, error ?? "ok");
        }

        if (raised.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return raised.Count;
    }

    private static BudgetStatus Status(Budget b, IReadOnlyList<UsageFact> monthRows, IReadOnlyList<Team> teamList, DateOnly today)
    {
        IEnumerable<UsageFact> scoped = b.Scope switch
        {
            BudgetScope.Tenant => monthRows,
            BudgetScope.Team => teamList.FirstOrDefault(t => string.Equals(t.Name, b.ScopeKey, StringComparison.OrdinalIgnoreCase)) is { } team ? monthRows.Where(r => team.Matches(r.Actor)) : [],
            BudgetScope.Provider => monthRows.Where(r => string.Equals(r.Provider ?? ModelProviders.Guess(r.Model), b.ScopeKey, StringComparison.OrdinalIgnoreCase)),
            BudgetScope.Model => monthRows.Where(r => r.Model.StartsWith(b.ScopeKey!, StringComparison.OrdinalIgnoreCase)),
            BudgetScope.Actor => monthRows.Where(r => string.Equals(r.Actor, b.ScopeKey, StringComparison.OrdinalIgnoreCase)),
            BudgetScope.Tool => monthRows.Where(r => string.Equals(r.Tool, b.ScopeKey, StringComparison.OrdinalIgnoreCase)),
            _ => [],
        };
        var spent = decimal.Round(scoped.Sum(r => r.EstimatedCost ?? 0m), 2);
        var daysElapsed = Math.Max(1, today.Day);
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var projected = decimal.Round(spent / daysElapsed * daysInMonth, 2);
        var percent = decimal.Round(spent / b.MonthlyAmount * 100m, 1);
        var projectedPercent = decimal.Round(projected / b.MonthlyAmount * 100m, 1);
        var state = percent >= 100 ? "over" : percent >= 80 || projectedPercent >= 100 ? "warn" : "ok";
        return new BudgetStatus(b, spent, percent, projected, projectedPercent, state, daysElapsed, daysInMonth);
    }
}

/// <summary>Teams: CRUD plus the actor labels seen in reports, so the UI can offer them as members.</summary>
public sealed class TeamService(ITeamRepository teams, IUsageFactRepository facts, IGovernanceUnitOfWork unitOfWork, ITenantContext tenant)
{
    public Task<IReadOnlyList<Team>> ListAsync(CancellationToken ct) => teams.ListAsync(ct);

    public async Task<Team> UpsertAsync(Guid? id, string name, IEnumerable<string> members, string? updatedBy, CancellationToken ct, AlertChannels? channels = null)
    {
        var all = await teams.ListAsync(ct);
        var existing = id is null ? null : all.FirstOrDefault(t => t.Id == id);
        if (existing is null && all.Any(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A team named '{name.Trim()}' already exists.");
        }

        if (existing is null)
        {
            if (all.Count >= 200)
            {
                throw new ArgumentException("At most 200 teams per tenant.");
            }

            existing = new Team(Guid.NewGuid(), tenant.Require(), name, members, updatedBy ?? "web");
            teams.Add(existing);
        }
        else
        {
            existing.Rename(name);
            existing.Set(members, updatedBy ?? "web");
        }

        if (channels is not null)
        {
            existing.SetChannels(channels.WebhookUrl, channels.SlackWebhookUrl, channels.TeamsWebhookUrl);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        var row = (await teams.ListAsync(ct)).FirstOrDefault(t => t.Id == id);
        if (row is null)
        {
            return false;
        }

        teams.Remove(row);
        await unitOfWork.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Every actor label seen in the last 90 days, with the team it currently resolves to.</summary>
    public async Task<IReadOnlyList<(string Actor, string? Team, DateOnly LastSeen)>> KnownActorsAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await facts.ListAsync(today.AddDays(-90), today, ct);
        var all = await teams.ListAsync(ct);
        return rows.GroupBy(r => r.Actor, StringComparer.Ordinal)
            .Select(g => (g.Key, all.FirstOrDefault(t => t.Matches(g.Key))?.Name, g.Max(r => r.Period)))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
