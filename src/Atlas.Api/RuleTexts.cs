using System.Text.Json;
using Atlas.Domain.Rules;

namespace Atlas.Api;

/// <summary>Reads a rule's localized texts out of its LocalizationsJson (display-time, like findings).</summary>
public static class RuleTexts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RuleLocalization? Localize(RuleDefinition rule, string language)
    {
        if (rule.LocalizationsJson is null or "" or "{}")
        {
            return null;
        }

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, RuleLocalization>>(rule.LocalizationsJson, Json);
            return map is not null && map.TryGetValue(language, out var loc) ? loc : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
