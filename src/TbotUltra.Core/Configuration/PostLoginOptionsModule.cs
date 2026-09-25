using Microsoft.Extensions.Configuration;
using static TbotUltra.Core.Configuration.PayloadValueReader;

namespace TbotUltra.Core.Configuration;

internal sealed record PostLoginOptions(
    bool AnalyzeFarmlists,
    bool AnalyzeHero,
    bool AnalyzeHeroInventory,
    bool ReadTroopTrainingQueue,
    bool AnalyzeBrewery,
    bool AnalyzeNewVillages,
    bool AnalyzeNewAccount,
    bool AutomaticallyCheckLanguage)
{
    public bool DetailedBrowserLoggingEnabled { get; init; }
    public bool TurnOffVideoSound { get; init; }
}

internal static class PostLoginOptionsModule
{
    internal static IReadOnlyList<string> AccountScopedKeys { get; } =
    [
        BotOptionPayloadKeys.TurnOffVideoSound,
        BotOptionPayloadKeys.PostLoginAnalyzeFarmlists,
        BotOptionPayloadKeys.PostLoginAnalyzeHero,
        BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory,
        BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue,
        BotOptionPayloadKeys.PostLoginAnalyzeBrewery,
        BotOptionPayloadKeys.PostLoginAnalyzeNewVillages,
        BotOptionPayloadKeys.PostLoginAnalyzeNewAccount,
        BotOptionPayloadKeys.PostLoginQuickReloginEnabled,
        BotOptionPayloadKeys.PostLoginLastFullLoginAt,
    ];

    internal static PostLoginOptions FromConfiguration(IConfiguration configuration)
        => new(
            configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeFarmlists, false),
            configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeHero, false),
            configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeHeroInventory, false),
            configuration.GetValue(BotOptionPayloadKeys.PostLoginReadTroopTrainingQueue, false),
            configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeBrewery, false),
            configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeNewVillages, true),
            configuration.GetValue(BotOptionPayloadKeys.PostLoginAnalyzeNewAccount, true),
            configuration.GetValue(BotOptionPayloadKeys.AutomaticallyCheckLanguage, true))
        {
            DetailedBrowserLoggingEnabled = configuration.GetValue(BotOptionPayloadKeys.DetailedBrowserLoggingEnabled, false),
            TurnOffVideoSound = configuration.GetValue(BotOptionPayloadKeys.TurnOffVideoSound, true),
        };

    internal static PostLoginOptions Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var result = new PostLoginOptions(
            source.PostLoginAnalyzeFarmlists,
            source.PostLoginAnalyzeHero,
            source.PostLoginAnalyzeHeroInventory,
            source.PostLoginReadTroopTrainingQueue,
            source.PostLoginAnalyzeBrewery,
            source.PostLoginAnalyzeNewVillages,
            source.PostLoginAnalyzeNewAccount,
            source.AutomaticallyCheckLanguage)
        {
            DetailedBrowserLoggingEnabled = source.DetailedBrowserLoggingEnabled,
            TurnOffVideoSound = source.TurnOffVideoSound,
        };

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
            else if (TryReadBool(key, value, BotOptionPayloadKeys.PostLoginAnalyzeNewAccount, out var account))
                result = result with { AnalyzeNewAccount = account };
            else if (TryReadBool(key, value, BotOptionPayloadKeys.AutomaticallyCheckLanguage, out var language))
                result = result with { AutomaticallyCheckLanguage = language };
        }

        return result;
    }

    internal static BotOptions ApplyTo(this PostLoginOptions values, BotOptions source)
        => source with
        {
            PostLoginAnalyzeFarmlists = values.AnalyzeFarmlists,
            PostLoginAnalyzeHero = values.AnalyzeHero,
            PostLoginAnalyzeHeroInventory = values.AnalyzeHeroInventory,
            PostLoginReadTroopTrainingQueue = values.ReadTroopTrainingQueue,
            PostLoginAnalyzeBrewery = values.AnalyzeBrewery,
            PostLoginAnalyzeNewVillages = values.AnalyzeNewVillages,
            PostLoginAnalyzeNewAccount = values.AnalyzeNewAccount,
            AutomaticallyCheckLanguage = values.AutomaticallyCheckLanguage,
            DetailedBrowserLoggingEnabled = values.DetailedBrowserLoggingEnabled,
            TurnOffVideoSound = values.TurnOffVideoSound,
        };

}
