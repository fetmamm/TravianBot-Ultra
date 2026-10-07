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

        var featureFlags = match.Groups["json"].Value;
        try
        {
            using var document = JsonDocument.Parse(featureFlags);
            if (!document.RootElement.TryGetProperty("cities", out var cities)
                || cities.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return CityCapability.Unknown;
            }

            return cities.GetBoolean() ? CityCapability.Enabled : CityCapability.Disabled;
        }
        catch (JsonException)
        {
            var cities = CitiesFlagRegex().Match(featureFlags);
            return !cities.Success
                ? CityCapability.Unknown
                : bool.Parse(cities.Groups["value"].Value)
                    ? CityCapability.Enabled
                    : CityCapability.Disabled;
        }
    }

    [GeneratedRegex(@"\bT4_feature_flags\s*=\s*(?<json>\{[^;]+\})", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FeatureFlagsRegex();

    [GeneratedRegex(@"[\""']?cities[\""']?\s*:\s*(?<value>true|false)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CitiesFlagRegex();
}
