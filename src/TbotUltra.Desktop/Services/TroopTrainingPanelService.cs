using System.Text.Json.Nodes;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

/// <summary>Owns Troop Training panel persistence and Worker reads behind the Desktop facade.</summary>
public sealed class TroopTrainingPanelService(ITroopTrainingPanelClient client, BotConfigStore configStore, string projectRoot)
{
    public Task<VillageStatus> ReadBuildingsAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => client.ReadBuildingsAsync(options, log, cancellationToken);

    public Task<IReadOnlyList<TroopTrainingQueueStatus>> ReadQueuesAsync(
        BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken)
        => client.ReadQueuesAsync(options, log, buildings, cancellationToken);

    public Task<SmithyUpgradeStatus> ReadSmithyStatusAsync(
        BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken)
        => client.ReadSmithyStatusAsync(options, log, buildings, cancellationToken);

    public Task<BreweryCelebrationStatus> ReadBreweryStatusAsync(
        BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken)
        => client.ReadBreweryStatusAsync(options, log, buildings, cancellationToken);

    public TroopTrainingPayload? LoadVillageSettings(string account, string villageKey)
        => TroopTrainingSettingsStore.Load(projectRoot, account, villageKey);

    public void SaveVillageSettings(string account, string villageKey, TroopTrainingPayload settings)
        => TroopTrainingSettingsStore.Save(projectRoot, account, villageKey, settings);

    public void SaveVillageSettings(string account, IReadOnlyCollection<string> villageKeys, TroopTrainingPayload settings)
        => TroopTrainingSettingsStore.SaveForVillages(projectRoot, account, villageKeys, settings);

    public void SaveGlobalSettings(TroopTrainingViewModel viewModel)
    {
        var config = configStore.Load();
        var draft = SettingsConfigurationProjection.ApplyTroopTrainingPanel(
            config,
            viewModel.BuildGlobalSettings());
        configStore.Save(draft);
    }
}
