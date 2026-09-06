using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Ai.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atlas.Scanner.Ai;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the AI Estate scanner. <paramref name="options"/> comes from Atlas:AiEstate (null = defaults);
    /// <paramref name="hmacKeyBase64"/> is the installation's Atlas:Secrets:HmacKeyBase64, shared with the secrets scanner.
    /// </summary>
    public static IServiceCollection AddAiEstateScanner(this IServiceCollection services, AiEstateOptions? options, string? hmacKeyBase64)
    {
        var effective = options ?? new AiEstateOptions();
        effective.HmacKeyBase64 ??= hmacKeyBase64;
        services.AddSingleton(effective);
        services.AddSingleton(sp => LoadCatalog(effective, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Atlas.Scanner.Ai.Catalog")));
        services.AddSingleton<IScanner, AiEstateScanner>();
        return services;
    }

    /// <summary>Custom catalog file wins wholesale; anything wrong with it falls back to the builtin with a warning, never a crash.</summary>
    internal static SignatureCatalog LoadCatalog(AiEstateOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.CatalogPath))
        {
            try
            {
                var custom = SignatureCatalog.LoadFile(options.CatalogPath);
                logger.LogInformation("AI signature catalog loaded from {Path}: version {Version}, hash {Hash}.", options.CatalogPath, custom.Version, custom.Hash[..12]);
                return custom;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            {
                logger.LogWarning(ex, "AI signature catalog at {Path} could not be loaded; using the builtin catalog.", options.CatalogPath);
            }
        }

        var builtin = SignatureCatalog.LoadBuiltin();
        logger.LogInformation("AI signature catalog: builtin version {Version}, hash {Hash}.", builtin.Version, builtin.Hash[..12]);
        return builtin;
    }
}
