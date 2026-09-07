using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.JavaScript;

public static class DependencyInjection
{
    public static IServiceCollection AddJavaScriptScanner(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, JavaScriptScanner>();
        return services;
    }
}
