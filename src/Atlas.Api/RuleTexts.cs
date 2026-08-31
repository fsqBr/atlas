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
            if (map is null)
            {
                return null;
            }

            // Same fallback as FindingLocalizer: exact match first, then language-prefix, case-insensitive.
            var exact = map.FirstOrDefault(kv => string.Equals(kv.Key, language, StringComparison.OrdinalIgnoreCase)).Value;
            if (exact is not null)
            {
                return exact;
            }

            var prefix = language.Split('-')[0];
            return map.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Value;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
