using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Reporting;

public static class DependencyInjection
{
    public static IServiceCollection AddAtlasReporting(this IServiceCollection services, ReportOptions options)
    {
        services.AddSingleton(options);
        services.AddScoped<ExecutiveReportBuilder>();
        services.AddScoped<ReportNarrativeService>();

        if (!string.IsNullOrWhiteSpace(options.PdfServiceUrl))
        {
            services.AddHttpClient<GotenbergPdfRenderer>(http => http.Timeout = TimeSpan.FromSeconds(120));
            services.AddSingleton<IPdfRenderer>(sp => sp.GetRequiredService<GotenbergPdfRenderer>());
        }
        else
        {
            services.AddSingleton<IPdfRenderer>(new ChromiumPdfRenderer(options));
        }

        return services;
    }
}
