using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

/// <summary>Owns account-scoped Farming panel settings persistence.</summary>
public sealed class FarmingPanelService(IFarmingPanelClient client, BotConfigStore configStore)
{
    public Task<bool> ReadAndPersistGoldClubStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => client.ReadAndPersistGoldClubStatusAsync(options, log, cancellationToken);

    public Task<IReadOnlyList<FarmListOverview>> ReadOverviewAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => client.ReadOverviewAsync(options, log, cancellationToken);

    public Task<FarmAddBatchResult> AddFarmsAsync(
        BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount,
        IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops, Action<string> log,
        IProgress<FarmAddProgress>? progress, CancellationToken cancellationToken)
        => client.AddFarmsAsync(options, farmListName, troopType, troopCount, requestedCount,
            coordinates, useDefaultTroops, log, progress, cancellationToken);

    public Task<FarmListCreateBatchResult> CreateListsAsync(
        BotOptions options, FarmListCreateRequest request, Action<string> log,
        IProgress<FarmListCreateProgress>? progress, CancellationToken cancellationToken)
        => client.CreateListsAsync(options, request, log, progress, cancellationToken);

    public Task<int?> SendOneAsync(BotOptions options, string farmListName, Action<string> log, CancellationToken cancellationToken)
        => client.SendOneAsync(options, farmListName, log, cancellationToken);

    public Task<int> SendSelectedAsync(
        BotOptions options, IReadOnlyCollection<string> names, IReadOnlyCollection<string> ids,
        Action<string> log, CancellationToken cancellationToken)
        => client.SendSelectedAsync(options, names, ids, log, cancellationToken);

    public Task<int> SendAllAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken)
        => client.SendAllAsync(options, log, cancellationToken);

    public FarmingSettingsSaveResult SaveSettings(FarmingPanelSettings settings)
    {
        var config = configStore.Load();
        var redDestination = settings.SelectedRedDestination;
        var yellowDestination = settings.SelectedYellowDestination;
        var redMoveEnabled = settings.DeactivateRedLosses && settings.MoveRedLosses && redDestination is not null;
        var yellowMoveEnabled = settings.DeactivateYellowLosses && settings.MoveYellowLosses && yellowDestination is not null;

        var sendMode = FarmingDefaults.NormalizeSendMode(settings.SendMode);
        config[BotOptionPayloadKeys.ContinuousFarmSendMode] = sendMode;
        config[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes] = settings.DispatchDelayMinMinutes;
        config[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes] = settings.DispatchDelayMaxMinutes;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateRedLosses] = settings.DeactivateRedLosses;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateYellowLosses] = settings.DeactivateYellowLosses;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateRedOasisLosses] = settings.DeactivateRedOasisLosses;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateYellowOasisLosses] = settings.DeactivateYellowOasisLosses;
        config[BotOptionPayloadKeys.ContinuousFarmMoveRedLosses] = redMoveEnabled;
        config[BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses] = yellowMoveEnabled;
        SaveDestination(config, true, redDestination);
        SaveDestination(config, false, yellowDestination);

        // Keep legacy aggregate keys coherent for older queued payloads while all new behavior reads the split keys.
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateLosses] = settings.DeactivateRedLosses || settings.DeactivateYellowLosses;
        config[BotOptionPayloadKeys.ContinuousFarmDeactivateOasisLosses] = settings.DeactivateRedOasisLosses || settings.DeactivateYellowOasisLosses;
        config[BotOptionPayloadKeys.ContinuousFarmMoveLosses] = redMoveEnabled || yellowMoveEnabled;
        configStore.Save(config);

        return new FarmingSettingsSaveResult(
            sendMode,
            settings.DispatchDelayMinMinutes,
            settings.DispatchDelayMaxMinutes,
            redMoveEnabled,
            yellowMoveEnabled,
            redDestination?.Name,
            yellowDestination?.Name);
    }

    public void SaveDestinationBaseName(bool isRed, string name)
    {
        var config = configStore.Load();
        config[isRed
            ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName
            : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName] = name;
        configStore.Save(config);
    }

    private static void SaveDestination(System.Text.Json.Nodes.JsonObject config, bool isRed, FarmLossDestinationOption? destination)
    {
        var idKey = isRed ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId;
        var nameKey = isRed ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListName;
        var baseNameKey = isRed ? BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName : BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationBaseName;
        var existingId = config[idKey]?.GetValue<string>()
            ?? config[BotOptionPayloadKeys.ContinuousFarmLossDestinationListId]?.GetValue<string>()
            ?? string.Empty;
        var priorBaseName = config[baseNameKey]?.GetValue<string>()
            ?? config[BotOptionPayloadKeys.ContinuousFarmLossDestinationBaseName]?.GetValue<string>();
        var changedByUser = destination is not null
            && !string.Equals(existingId, destination.ListId, StringComparison.OrdinalIgnoreCase);

        config[idKey] = destination?.ListId ?? string.Empty;
        config[nameKey] = destination?.Name ?? string.Empty;
        config[baseNameKey] = changedByUser || string.IsNullOrWhiteSpace(priorBaseName)
            ? destination?.Name ?? string.Empty
            : priorBaseName;
    }
}

public sealed record FarmingPanelSettings(
    string SendMode,
    int DispatchDelayMinMinutes,
    int DispatchDelayMaxMinutes,
    bool DeactivateRedLosses,
    bool DeactivateYellowLosses,
    bool DeactivateRedOasisLosses,
    bool DeactivateYellowOasisLosses,
    bool MoveRedLosses,
    bool MoveYellowLosses,
    FarmLossDestinationOption? SelectedRedDestination,
    FarmLossDestinationOption? SelectedYellowDestination);

public sealed record FarmingSettingsSaveResult(
    string SendMode,
    int DelayMinMinutes,
    int DelayMaxMinutes,
    bool MoveRedLossesEnabled,
    bool MoveYellowLossesEnabled,
    string? RedDestinationName,
    string? YellowDestinationName);
