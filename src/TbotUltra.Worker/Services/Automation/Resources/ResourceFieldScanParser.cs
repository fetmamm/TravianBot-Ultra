using System.Text.Json;
using System.Text.Json.Serialization;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Deserializes the two preserved resource-field scan result shapes without browser state.
/// The DOM strategies remain separate so compatibility fallback removal can be evaluated later.
/// </summary>
internal static class ResourceFieldScanParser
{
    internal static List<ResourceFieldJs> ParseOfficialMap(string? json) =>
        Parse(json);

    internal static List<ResourceFieldJs> ParseCompatibilityFallback(string? json) =>
        Parse(json);

    private static List<ResourceFieldJs> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<ResourceFieldJs>>(json) ?? [];
    }
}

internal sealed class ResourceFieldJs
{
    [JsonPropertyName("slotId")]
    public int? SlotId { get; init; }

    [JsonPropertyName("fieldType")]
    public string? FieldType { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("level")]
    public int? Level { get; init; }

    [JsonPropertyName("href")]
    public string? Href { get; init; }
}
