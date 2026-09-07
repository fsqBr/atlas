namespace Atlas.Governance.Domain;

/// <summary>What a budget applies to. The key is the team name, provider id, model pattern, actor label or tool id.</summary>
public enum BudgetScope
{
    Tenant = 0,
    Team = 1,
    Provider = 2,
    Model = 3,
    Actor = 4,
    Tool = 5,
}

/// <summary>
/// A monthly estimated-spend budget for one scope. Thresholds are fixed at 50 / 80 / 100 % so alerts mean the same
/// thing everywhere; each crossing is alerted once per calendar month. Amounts are USD estimates from the price
/// catalog — a budget never claims to be the invoice.
/// </summary>
public sealed class Budget
{
    public static readonly int[] Thresholds = [50, 80, 100];

    private Budget()
    {
    }

    public Budget(Guid id, Guid tenantId, BudgetScope scope, string? scopeKey, decimal monthlyAmount, string? name, string updatedBy)
    {
        Id = id;
        TenantId = tenantId;
        Scope = scope;
        ScopeKey = scope == BudgetScope.Tenant ? null : Normalize(scopeKey, scope);
        Set(monthlyAmount, name, updatedBy, enabled: true);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public BudgetScope Scope { get; private set; }

    public string? ScopeKey { get; private set; }

    public decimal MonthlyAmount { get; private set; }

    public string? Name { get; private set; }

    public bool Enabled { get; private set; }

    public string UpdatedBy { get; private set; } = null!;

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public string Label => Name ?? (Scope == BudgetScope.Tenant ? "Whole tenant" : $"{Scope.ToString().ToLowerInvariant()}: {ScopeKey}");

    public void Set(decimal monthlyAmount, string? name, string updatedBy, bool enabled)
    {
        if (monthlyAmount <= 0 || monthlyAmount > 100_000_000m)
        {
            throw new ArgumentException("A budget is a positive monthly USD amount.");
        }

        MonthlyAmount = decimal.Round(monthlyAmount, 2);
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 120)];
        Enabled = enabled;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "web" : updatedBy.Trim()[..Math.Min(updatedBy.Trim().Length, 100)];
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string Normalize(string? key, BudgetScope scope)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException($"A {scope.ToString().ToLowerInvariant()} budget needs the {scope.ToString().ToLowerInvariant()} it applies to.");
        }

        var k = key.Trim();
        return k.Length <= 200 ? k : k[..200];
    }
}

/// <summary>One alert that was raised: a budget threshold crossed or an anomalous day. Deduplicated by <see cref="Key"/>.</summary>
public sealed class BudgetAlert
{
    private BudgetAlert()
    {
    }

    public BudgetAlert(Guid id, Guid tenantId, Guid? budgetId, string kind, string key, string message, decimal amount, decimal? percent)
    {
        Id = id;
        TenantId = tenantId;
        BudgetId = budgetId;
        Kind = kind;
        Key = key;
        Message = message.Length <= 500 ? message : message[..500];
        Amount = amount;
        Percent = percent;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? BudgetId { get; private set; }

    /// <summary>threshold | anomaly.</summary>
    public string Kind { get; private set; } = null!;

    /// <summary>threshold:{budgetId}:{yyyy-MM}:{percent} or anomaly:{scope}:{yyyy-MM-dd}.</summary>
    public string Key { get; private set; } = null!;

    public string Message { get; private set; } = null!;

    public decimal Amount { get; private set; }

    public decimal? Percent { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public bool Delivered { get; private set; }

    public string? DeliveryError { get; private set; }

    public void MarkDelivered(string? error)
    {
        Delivered = error is null;
        DeliveryError = error is null ? null : (error.Length <= 300 ? error : error[..300]);
    }
}
