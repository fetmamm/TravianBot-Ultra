using System.Text.Json;
using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal static partial class CityCapabilityParser
{
    internal static CityCapability Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return CityCapability.Unknown;
        }

        var match = FeatureFlagsRegex().Match(html);
        if (!match.Success)
        {
            return CityCapability.Unknown;
        }

        try
        {
            using var document = JsonDocument.Parse(match.Groups["json"].Value);
            if (!document.RootElement.TryGetProperty("cities", out var cities)
                || cities.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return CityCapability.Unknown;
            }

            return cities.GetBoolean() ? CityCapability.Enabled : CityCapability.Disabled;
        }
        catch (JsonException)
        {
            return CityCapability.Unknown;
        }
    }

    [GeneratedRegex(@"\bT4_feature_flags\s*=\s*(?<json>\{[^;]+\})\s*;", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FeatureFlagsRegex();
}
