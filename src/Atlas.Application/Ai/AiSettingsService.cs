using System.Diagnostics;
using System.Text;
using Atlas.Application.Assessments;
using Atlas.Application.Credentials;
using Atlas.Domain.Ai;
using Atlas.Application.Tenants;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Ai;

public sealed record AiSettingsSummary(
    bool Configured,
    bool SecretStoreConfigured,
    string Provider,
    string Model,
    string? BaseUrl,
    bool HasKey,
    bool RequiresKey,
    bool Enabled,
    bool Usable,
    int MaxSnippetsPerAnalysis,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage,
    IReadOnlyList<AiProviderInfo> Providers,
    LocalAiStatus? LocalOllama = null);

public sealed record AiProviderInfo(string Id, string DefaultModel, string? DefaultBaseUrl, bool RequiresKey);

public sealed record AiTestResult(bool Succeeded, string Message, string Model, long ElapsedMs, long InputTokens, long OutputTokens);

/// <summary>
/// Admin-facing configuration of the AI provider: stores the key as an
/// AES-GCM envelope (never returned), tests the connection with a one-word
/// prompt, and hands the worker a ready client.
/// </summary>
public sealed class AiSettingsService(
    IAiSettingsRepository repository,
    ISecretCipher cipher,
    IChatClientFactory clients,
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ILogger<AiSettingsService> logger,
    ILocalAiProbe? localProbe = null)
{
    public static readonly IReadOnlyList<AiProviderInfo> Providers = Enum.GetValues<AiProvider>()
        .Select(p => new AiProviderInfo(p.ToString(), AiProviderSettings.DefaultModel(p), AiProviderSettings.DefaultBaseUrl(p), AiProviderSettings.RequiresKeyFor(p)))
        .ToList();

    public async Task<AiSettingsSummary> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(tenant.Require(), cancellationToken);
        return await WithLocalAsync(ToSummary(settings), cancellationToken);
    }

    private async Task<AiSettingsSummary> WithLocalAsync(AiSettingsSummary summary, CancellationToken cancellationToken) =>
        localProbe is null ? summary : summary with { LocalOllama = await localProbe.ProbeAsync(cancellationToken) };

    public async Task<AiSettingsSummary> UpsertAsync(
        string provider, string? model, string? baseUrl, string? apiKey, bool enabled, int? maxSnippets, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AiProvider>(provider, ignoreCase: true, out var parsed))
        {
            throw new ArgumentException($"Unknown provider '{provider}'. Use one of: {string.Join(", ", Enum.GetNames<AiProvider>())}.", nameof(provider));
        }

        byte[]? envelope = null;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (!cipher.IsConfigured)
            {
                throw new SecretStoreNotConfiguredException();
            }

            envelope = cipher.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()));
        }

        var settings = await repository.GetAsync(tenant.Require(), cancellationToken);
        if (settings is null)
        {
            settings = new AiProviderSettings(Guid.NewGuid(), tenant.Require());
            repository.Add(settings);
        }

        settings.Configure(parsed, model, baseUrl ?? AiProviderSettings.DefaultBaseUrl(parsed), envelope, enabled, maxSnippets);
        if (enabled && settings.RequiresKey && !settings.HasKey)
        {
            throw new ArgumentException($"{parsed} needs an API key before it can be enabled.", nameof(apiKey));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("AI provider configured: {Provider}/{Model}, enabled={Enabled}, key={HasKey}.", parsed, settings.Model, enabled, settings.HasKey);
        return await WithLocalAsync(ToSummary(settings), cancellationToken);
    }

    public async Task<AiSettingsSummary> ClearKeyAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(tenant.Require(), cancellationToken);
        if (settings is not null)
        {
            settings.ClearKey();
            if (settings.RequiresKey)
            {
                settings.Configure(settings.Provider, settings.Model, settings.BaseUrl, null, enabled: false, settings.MaxSnippetsPerAnalysis);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await WithLocalAsync(ToSummary(settings), cancellationToken);
    }

    /// <summary>Sends a one-word prompt; records the outcome on the settings so the UI can show "last tested".</summary>
    public async Task<AiTestResult> TestAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(tenant.Require(), cancellationToken)
            ?? throw new InvalidOperationException("AI provider is not configured yet.");
        if (settings.RequiresKey && !settings.HasKey)
        {
            throw new InvalidOperationException($"{settings.Provider} needs an API key.");
        }

        var client = clients.Create(settings, Decrypt(settings));
        var watch = Stopwatch.StartNew();
        AiTestResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var reply = await client.CompleteAsync(new ChatRequest("You are a connectivity check.", "Reply with the single word OK.", MaxTokens: 16, Temperature: 0), timeout.Token);
            watch.Stop();
            var ok = reply.Text.Contains("OK", StringComparison.OrdinalIgnoreCase);
            result = new AiTestResult(ok, ok ? $"Connected to {client.Provider} ({reply.Model}) in {watch.ElapsedMilliseconds} ms." : $"Unexpected reply: {Trim(reply.Text)}", reply.Model, watch.ElapsedMilliseconds, reply.InputTokens, reply.OutputTokens);
        }
        catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            result = new AiTestResult(false, ex is TaskCanceledException or OperationCanceledException ? "Timed out after 60 s." : ex.Message, settings.Model, watch.ElapsedMilliseconds, 0, 0);
        }

        settings.RecordTest(result.Succeeded, result.Message);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>For the worker: null when AI is disabled or unusable — callers must not send anything then.</summary>
    public async Task<(AiProviderSettings Settings, IChatClient Client)?> ResolveClientAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(tenant.Require(), cancellationToken);
        if (settings is null || !settings.IsUsable)
        {
            return null;
        }

        return (settings, clients.Create(settings, Decrypt(settings)));
    }

    private string? Decrypt(AiProviderSettings settings) =>
        settings.KeyEnvelope is { Length: > 0 } envelope ? Encoding.UTF8.GetString(cipher.Unprotect(envelope)) : null;

    private AiSettingsSummary ToSummary(AiProviderSettings? s) => s is null
        ? new AiSettingsSummary(false, cipher.IsConfigured, AiProvider.Anthropic.ToString(), AiProviderSettings.DefaultModel(AiProvider.Anthropic), AiProviderSettings.DefaultBaseUrl(AiProvider.Anthropic), false, true, false, false, 40, null, null, null, null, Providers)
        : new AiSettingsSummary(true, cipher.IsConfigured, s.Provider.ToString(), s.Model, s.BaseUrl, s.HasKey, s.RequiresKey, s.Enabled, s.IsUsable, s.MaxSnippetsPerAnalysis, s.UpdatedAtUtc, s.LastTestedAtUtc, s.LastTestSucceeded, s.LastTestMessage, Providers);

    private static string Trim(string text) => text.Length > 80 ? text[..80] + "…" : text;
}
