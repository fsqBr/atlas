using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Domain.Assessments;

namespace Atlas.Application.Findings;

/// <summary>
/// CycloneDX 1.5 JSON from the component list the license scanner publishes with
/// the "license.inventory" finding. purl per ecosystem, SPDX expression when known.
/// </summary>
public static class SbomBuilder
{
    public const string InventoryRuleId = "license.inventory";

    public static string? Build(Assessment assessment, string? componentsJson, string toolVersion, DateTimeOffset generatedAt)
    {
        if (string.IsNullOrWhiteSpace(componentsJson))
        {
            return null;
        }

        JsonArray? components;
        try
        {
            components = JsonNode.Parse(componentsJson) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }

        if (components is null)
        {
            return null;
        }

        var bom = new JsonObject
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.5",
            ["serialNumber"] = "urn:uuid:" + Guid.NewGuid(),
            ["version"] = 1,
            ["metadata"] = new JsonObject
            {
                ["timestamp"] = generatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["tools"] = new JsonArray(new JsonObject { ["vendor"] = "Atlas", ["name"] = "Atlas", ["version"] = toolVersion }),
                ["component"] = new JsonObject { ["type"] = "application", ["name"] = assessment.Name, ["bom-ref"] = "assessment:" + assessment.Id },
            },
            ["components"] = new JsonArray(components.Select(c =>
            {
                var ecosystem = c?["Ecosystem"]?.GetValue<string>() ?? c?["ecosystem"]?.GetValue<string>() ?? "generic";
                var id = c?["Id"]?.GetValue<string>() ?? c?["id"]?.GetValue<string>() ?? "?";
                var version = c?["Version"]?.GetValue<string>() ?? c?["version"]?.GetValue<string>() ?? "";
                var license = c?["License"]?.GetValue<string>() ?? c?["license"]?.GetValue<string>();
                // purl spec: '@' in a scoped npm name must be percent-encoded (pkg:npm/%40scope/name@1.0.0).
                var purlName = ecosystem == "npm" && id.StartsWith('@') ? "%40" + id[1..] : id;
                var purl = $"pkg:{(ecosystem == "npm" ? "npm" : ecosystem)}/{purlName}@{version}";
                var component = new JsonObject
                {
                    ["type"] = "library",
                    ["bom-ref"] = purl,
                    ["name"] = id,
                    ["version"] = version,
                    ["purl"] = purl,
                };
                if (!string.IsNullOrWhiteSpace(license))
                {
                    // Free text ("SEE LICENSE IN LICENSE.txt") is not an SPDX expression; validating SBOM
                    // consumers reject it as one, so it belongs under license.name.
                    var looksSpdx = !license.Contains(' ')
                        || license.Split(' ').All(t => t is "OR" or "AND" or "WITH" || (!t.Contains(' ') && t.Trim('(', ')').Length > 0 && t.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.' or '+' or '(' or ')')));
                    component["licenses"] = looksSpdx
                        ? new JsonArray(new JsonObject { ["expression"] = license })
                        : new JsonArray(new JsonObject { ["license"] = new JsonObject { ["name"] = license } });
                }

                return (JsonNode)component;
            }).ToArray()),
        };

        return bom.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
