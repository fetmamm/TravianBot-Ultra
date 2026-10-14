using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

/// <summary>Owns Hero-panel persistence, queue insertion, and Worker calls behind the Desktop facade.</summary>
public sealed class HeroPanelService(IHeroPanelClient client, BotConfigStore configStore)
{
    public void PersistPriority(HeroViewModel viewModel)
    {
        var config = configStore.Load();
        config[BotOptionPayloadKeys.HeroStatPriority] = viewModel.BuildPriorityPayload();
        config[BotOptionPayloadKeys.HeroStatMaximums] = viewModel.BuildMaximumsPayload();
        configStore.Save(config);
    }

    public void PersistSettings(HeroViewModel viewModel)
    {
        var config = configStore.Load();
        config[BotOptionPayloadKeys.HeroMinHpForAdventure] = viewModel.MinHpForAdventure;
        config[BotOptionPayloadKeys.HeroAutoRevive] = viewModel.AutoRevive;
        config[BotOptionPayloadKeys.HeroAutoAssignPoints] = viewModel.AutoAssignPoints;
        config[BotOptionPayloadKeys.HeroAutoUseOintments] = viewModel.AutoUseOintments;
        config[BotOptionPayloadKeys.HeroOintmentTargetHpPercent] = viewModel.OintmentTargetHpPercent;
        config[BotOptionPayloadKeys.HeroStatPriority] = viewModel.BuildPriorityPayload();
        config[BotOptionPayloadKeys.HeroStatMaximums] = viewModel.BuildMaximumsPayload();
        config[BotOptionPayloadKeys.HeroAdventurePickOrder] = viewModel.AdventurePickOrder;
        config.Remove("hero_hide_mode_enabled");
        config.Remove("hero_hide_mode");
        config[BotOptionPayloadKeys.HeroContinuousAdventures] = viewModel.ContinuousAdventures;
        config[BotOptionPayloadKeys.IncreaseAdventuresToHard] = viewModel.IncreaseAdventuresToHard;
        config[BotOptionPayloadKeys.ReduceAdventureTime] = viewModel.ReduceAdventureTime;
        config[BotOptionPayloadKeys.HeroAdventureVideoChancePercent] = viewModel.AdventureVideoChancePercent;
        config[BotOptionPayloadKeys.HeroRaidingEnabled] = viewModel.HeroRaidingEnabled;
        config[BotOptionPayloadKeys.HeroRaidingMinimumHpPercent] = viewModel.HeroRaidingMinimumHpPercent;
        config[BotOptionPayloadKeys.HeroRaidingMaximumDistance] = viewModel.HeroRaidingMaximumDistance;
        config[BotOptionPayloadKeys.HeroRaidingObservationMinutes] = viewModel.HeroRaidingObservationMinutes;
        config[BotOptionPayloadKeys.HeroRaidingSelectedTypes] = viewModel.HeroRaidingSelectedTypes;
        config[BotOptionPayloadKeys.HeroRaidingPriority] = viewModel.HeroRaidingPriority;
        configStore.Save(config);
    }

    public IReadOnlyList<Dictionary<string, string>> CreateAdventurePayloads(HeroViewModel viewModel, int availableAdventures)
    {
        var payload = new HeroPayload(
            MinHpForAdventure: viewModel.MinHpForAdventure,
            AutoRevive: viewModel.AutoRevive,
            AutoAssignPoints: viewModel.AutoAssignPoints,
            AutoUseOintments: viewModel.AutoUseOintments,
            OintmentTargetHpPercent: viewModel.OintmentTargetHpPercent,
            StatPriority: viewModel.BuildPriorityPayload(),
            StatMaximums: viewModel.BuildMaximumsPayload(),
            AdventurePickOrder: viewModel.AdventurePickOrder).ToDictionary();
        var copies = viewModel.ContinuousAdventures && availableAdventures > 1
            ? Math.Min(availableAdventures, 20)
            : 1;
        return Enumerable.Range(0, copies)
            .Select(_ => new Dictionary<string, string>(payload, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public Task<HeroAttributeSnapshot> ReadAttributesAsync(
        BotOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
        => client.ReadAttributesAsync(options, log, cancellationToken);

    public Task<int?> ReadAdventureCountAsync(
        BotOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
        => client.ReadAdventureCountAsync(options, log, cancellationToken);

    public Task<int?> ReadHpAsync(
        BotOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
        => client.ReadHpAsync(options, log, cancellationToken);

    public Task<HeroInventoryResources> ReadInventoryAsync(
        BotOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
        => client.ReadInventoryAsync(options, log, cancellationToken);
}
