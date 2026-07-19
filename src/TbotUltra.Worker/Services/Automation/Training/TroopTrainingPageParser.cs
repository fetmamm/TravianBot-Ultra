using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal sealed record TroopUnitBuildInfo(
    bool Found,
    bool CanTrain,
    string TroopType,
    int WoodCost,
    int ClayCost,
    int IronCost,
    int CropCost);

/// <summary>
/// Pure parsers for the JSON payloads produced by the troop-training page scripts.
/// Extracted from <see cref="TravianClient"/> so the interpretation of the browser
/// output is fixture-testable without Playwright; the scripts and click order stay
/// in the client.
/// </summary>
internal static class TroopTrainingPageParser
{
    internal static TroopUnitBuildInfo ParseTroopUnitBuildInfo(string? rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson ?? "{}");
            var root = doc.RootElement;
            return new TroopUnitBuildInfo(
                root.TryGetProperty("found", out var found) && found.GetBoolean(),
                root.TryGetProperty("canTrain", out var canTrain) && canTrain.GetBoolean(),
                root.TryGetProperty("troopType", out var troopType) ? troopType.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("woodCost", out var woodCost) ? woodCost.GetInt32() : 0,
                root.TryGetProperty("clayCost", out var clayCost) ? clayCost.GetInt32() : 0,
                root.TryGetProperty("ironCost", out var ironCost) ? ironCost.GetInt32() : 0,
                root.TryGetProperty("cropCost", out var cropCost) ? cropCost.GetInt32() : 0);
        }
        catch
        {
            return new TroopUnitBuildInfo(false, false, string.Empty, 0, 0, 0, 0);
        }
    }

    internal static IReadOnlyList<BuildQueueItem> ParseTroopTrainingQueue(string? rawJson)
    {
        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? []
            : JsonSerializer.Deserialize<List<QueueRowJs>>(rawJson) ?? [];

        return raw
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => new BuildQueueItem(item.Text!, item.TimeLeft))
            .ToList();
    }

    private sealed class QueueRowJs
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("timeLeft")]
        public string? TimeLeft { get; init; }
    }
}
