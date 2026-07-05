using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private BotOptions ApplyConstructFasterSettingsForVillage(
        BotOptions options,
        string? villageKey,
        string? villageName)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ApplyConstructFasterSettingsToPayload(payload, options, villageKey, villageName);
        return BotOptionsPayloadApplier.Apply(options, payload);
    }

    private void ApplyConstructFasterSettingsToPayload(Dictionary<string, string> payload, string? villageKey, string? villageName = null)
    {
        ApplyConstructFasterSettingsToPayload(payload, LoadBotOptions(), villageKey, villageName);
    }

    private void ApplyConstructFasterSettingsToPayload(
        Dictionary<string, string> payload,
        BotOptions options,
        string? villageKey,
        string? villageName)
    {
        var villageEnabled = !string.IsNullOrWhiteSpace(villageKey)
            && _villageSettingsStore.IsConstructFasterEnabledByKey(villageKey, defaultIfUnknown: false);
        if (!villageEnabled && !string.IsNullOrWhiteSpace(villageName))
        {
            villageEnabled = _villageSettingsStore.IsConstructFasterEnabledByKey($"name:{villageName.Trim()}", defaultIfUnknown: false);
        }

        payload[BotOptionPayloadKeys.ConstructFasterEnabled] = villageEnabled ? "true" : "false";
        payload[BotOptionPayloadKeys.ConstructFasterMinBuildMinutes] =
            Math.Max(0, options.ConstructFasterMinBuildMinutes).ToString(CultureInfo.InvariantCulture);
        payload[BotOptionPayloadKeys.ConstructFasterRandomEnabled] =
            options.ConstructFasterRandomEnabled ? "true" : "false";
        payload[BotOptionPayloadKeys.ConstructFasterRandomChancePercent] =
            Math.Clamp(options.ConstructFasterRandomChancePercent, 0, 100).ToString(CultureInfo.InvariantCulture);
    }

    private void RefreshConstructFasterPayloadForExecution(QueueItem item)
    {
        if (!IsConstructionQueueTask(item.TaskName))
        {
            return;
        }

        var payload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase);
        var beforeEnabled = payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterEnabled);
        var beforeMin = payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterMinBuildMinutes);
        var beforeRandom = payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterRandomEnabled);
        var beforeChance = payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterRandomChancePercent);

        ApplyConstructFasterSettingsToPayload(
            payload,
            GetQueueItemVillageKey(item),
            GetQueueItemVillageName(item));

        var changed = !string.Equals(beforeEnabled, payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterEnabled), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(beforeMin, payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterMinBuildMinutes), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(beforeRandom, payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterRandomEnabled), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(beforeChance, payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterRandomChancePercent), StringComparison.OrdinalIgnoreCase);
        if (!changed)
        {
            return;
        }

        var persisted = _botService.UpdateDeferredQueueItem(item.Id, payload);
        item.Payload = payload;
        AppendLog(
            $"[construct-faster] refreshed queue payload before execution: id={item.Id} " +
            $"village='{GetQueueItemVillageName(item) ?? "-"}' " +
            $"enabled={payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterEnabled, "false")} " +
            $"min={payload.GetValueOrDefault(BotOptionPayloadKeys.ConstructFasterMinBuildMinutes, "0")}m" +
            (persisted ? string.Empty : " (not persisted; using in-memory value)"));
    }

    private void OpenConstructFasterSettingsFromVillageSettings(IReadOnlyList<VillageSettingsRow> villageSettingsRows)
    {
        OpenConstructFasterSettingsWindow(villageSettingsRows);
    }

    private void OpenConstructFasterSettingsWindow(IReadOnlyList<VillageSettingsRow>? villageSettingsRows = null)
    {
        if (string.IsNullOrWhiteSpace(_accountStore.ActiveAccountName()))
        {
            AppendLog("Construct-faster settings: no active account.");
            return;
        }

        var villages = GetAllVillageKeyInfos();
        if (villages.Count == 0)
        {
            AppendLog("Construct-faster settings: no villages loaded.");
            return;
        }

        var options = LoadBotOptions();
        var rows = villages
            .Select(village => new ConstructFasterSettingsRow(
                village,
                FindVillageSettingsRow(villageSettingsRows, village)?.ConstructFasterEnabled
                    ?? _villageSettingsStore.GetConstructFaster(village)))
            .ToList();

        var window = new ConstructFasterSettingsWindow(
            rows,
            options.ConstructFasterMinBuildMinutes,
            options.ConstructFasterRandomEnabled,
            options.ConstructFasterRandomChancePercent)
        {
            Owner = this,
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        foreach (var result in window.Results)
        {
            _villageSettingsStore.SetConstructFaster(result.Village, result.IsEnabled);
            UpdateVillageSettingsConstructFasterRow(villageSettingsRows, result.Village, result.IsEnabled);
        }

        if (_botConfigStore is not null)
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.ConstructFasterEnabled] = _villageSettingsStore.HasAnyConstructFasterEnabled();
            config[BotOptionPayloadKeys.ConstructFasterMinBuildMinutes] = window.MinimumBuildMinutes;
            config[BotOptionPayloadKeys.ConstructFasterRandomEnabled] = window.RandomEnabled;
            config[BotOptionPayloadKeys.ConstructFasterRandomChancePercent] = window.RandomChancePercent;
            _botConfigStore.Save(config);
        }

        ApplyConstructFasterConfigToUi(LoadBotOptions());

        if (IsContinuousLoopRunning())
        {
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
        }

        AppendLog($"Saved construct-faster settings for {window.Results.Count} village(s).");
    }

    private void SaveConstructFasterMasterFlag()
    {
        if (_botConfigStore is null)
        {
            return;
        }

        var config = _botConfigStore.Load();
        config[BotOptionPayloadKeys.ConstructFasterEnabled] = _villageSettingsStore.HasAnyConstructFasterEnabled();
        _botConfigStore.Save(config);
    }

    private static void UpdateVillageSettingsConstructFasterRow(
        IReadOnlyList<VillageSettingsRow>? villageSettingsRows,
        VillageSettingsStore.VillageKeyInfo village,
        bool enabled)
    {
        var row = FindVillageSettingsRow(villageSettingsRows, village);
        if (row is not null)
        {
            row.ConstructFasterEnabled = enabled;
        }
    }
}
