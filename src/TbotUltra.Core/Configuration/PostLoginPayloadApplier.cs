namespace TbotUltra.Core.Configuration;

internal sealed record PostLoginPayloadValues(
    bool AnalyzeFarmlists,
    bool AnalyzeHero,
    bool AnalyzeHeroInventory,
    bool ReadTroopTrainingQueue,
    bool AnalyzeBrewery,
    bool AnalyzeNewVillages,
    bool AutomaticallyCheckLanguage);

internal static class PostLoginPayloadApplier
{
    internal static PostLoginPayloadValues Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = new PostLoginPayloadValues(
            source.PostLoginAnalyzeFarmlists,
            source.PostLoginAnalyzeHero,
            source.PostLoginAnalyzeHeroInventory,
            source.PostLoginReadTroopTrainingQueue,
            source.PostLoginAnalyzeBrewery,
            source.PostLoginAnalyzeNewVillages,
            source.AutomaticallyCheckLanguage);

        if (payload is null)
            return result;

        foreach (var pair in payload)
        {
            var key = pair.Key.Trim();
            var value = pair.Value.Trim();
            if (key.Length == 0 || value.Length == 0)
                continue;

            if (TryReadBool(key, value, BotOptionPayloadKeys.PostLoginAnalyzeFarmlists, out var farmlists))
                result = result with { AnalyzeFarmlists = farmlists };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.PostLoginAnalyzeHero, out var hero))
                result = result with { AnalyzeHero = hero };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory, out var inventory))
                result = result with { AnalyzeHeroInventory = inventory };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue, out var troopQueue))
                result = result with { ReadTroopTrainingQueue = troopQueue };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.PostLoginAnalyzeBrewery, out var brewery))
                result = result with { AnalyzeBrewery = brewery };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.PostLoginAnalyzeNewVillages, out var villages))
                result = result with { AnalyzeNewVillages = villages };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.AutomaticallyCheckLanguage, out var language))
                result = result with { AutomaticallyCheckLanguage = language };
        }

        return result;
    }

    private static bool TryReadBool(string key, string value, string expected, out bool parsed)
    {
        parsed = false;
        return key.Equals(expected, StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out parsed);
    }
}
