using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Atlas.Governance.Application;

namespace Atlas.Governance.Infrastructure.Providers;

/// <summary>Shared plumbing for the read-only provider clients: one named HttpClient, bounded responses, provider errors that never echo the key.</summary>
internal static class ProviderHttp
{
    public const string HttpClientName = "ai-cost";
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, HttpRequestMessage request, string provider, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Atlas-AI-Estate/0.50 (+https://github.com/fsqBr/atlas)");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new CostProviderException($"{provider}: the provider rejected the credential (HTTP {(int)response.StatusCode}). Use an organization admin/billing key with read access.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new CostProviderException($"{provider}: endpoint not found (HTTP 404) — check the scope (organization) and that the account has access to this API.");
        }

        if ((int)response.StatusCode == 429)
        {
            throw new CostProviderException($"{provider}: rate limited (HTTP 429); retry the sync later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CostProviderException($"{provider}: HTTP {(int)response.StatusCode} from the provider.");
        }

        if (response.Content.Headers.ContentLength is { } length && length > MaxResponseBytes)
        {
            throw new CostProviderException($"{provider}: response too large ({length} bytes).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            limited.Write(buffer, 0, read);
            if (limited.Length > MaxResponseBytes)
            {
                throw new CostProviderException($"{provider}: response too large.");
            }
        }

        limited.Position = 0;
        try
        {
            return await JsonDocument.ParseAsync(limited, default, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new CostProviderException($"{provider}: the provider returned a body that is not JSON.", ex);
        }
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static decimal? Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null,
            _ => null,
        };
    }

    public static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static DateOnly? DateFromUnix(JsonElement e, string name)
    {
        var n = Num(e, name);
        return n is null ? null : DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds((long)n.Value).UtcDateTime);
    }

    public static DateOnly? DateFromIso(JsonElement e, string name)
    {
        var s = Str(e, name);
        return s is not null && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? DateOnly.FromDateTime(d.UtcDateTime)
            : null;
    }

    public static DateTimeOffset? TimeFromIso(JsonElement e, string name)
    {
        var s = Str(e, name);
        return s is not null && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }
}
