using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Language.Sql;

public static class DependencyInjection
{
    /// <summary>Registers the SQL candidate source as itself; hosts compose it with the C# one.</summary>
    public static IServiceCollection AddSqlLanguage(this IServiceCollection services)
    {
        services.AddSingleton<SqlBusinessRuleCandidateSource>();
        return services;
    }
}
