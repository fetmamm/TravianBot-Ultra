using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private bool TryHandleTownHallUnavailableExecution(QueueItem item, Exception exception, string logPrefix)
    {
        if (!string.Equals(item.TaskName, "run_town_hall_celebration", StringComparison.OrdinalIgnoreCase)
            || !exception.Message.Contains("town_hall_unavailable=missing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _botService.MarkQueueItemSucceeded(item.Id);
        var villageKey = GetQueueItemVillageKey(item);
        var villageName = GetQueueItemVillageName(item);
        if (string.IsNullOrWhiteSpace(villageKey))
        {
            AppendLog($"{logPrefix} SKIP task={item.TaskName} | Town Hall missing, but the village identity was unavailable; the task was removed without changing another village's setting.");
            return true;
        }

        void Apply()
        {
            var village = new VillageSettingsStore.VillageKeyInfo(
                villageKey,
                villageName ?? villageKey,
                null,
                null,
                false);
            PersistAutomationGroupEnabledForVillage(
                village,
                enabled: false,
                QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration));
            TownHallCelebrationStateStore.Clear(_projectRoot, _accountStore.ActiveAccountName(), villageKey);
            InvalidateVillageOverviewTownHallCache();
            RefreshAutomationLoopDashboardUi();
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.Invoke(Apply);
        }

        AppendLog($"{logPrefix} DISABLED task={item.TaskName} | Town Hall is not built in '{villageName ?? villageKey}'. Town Hall celebrations were turned off for this village.");
        return true;
    }

    private void OpenTownHallSettingsFromVillageSettings(IReadOnlyList<VillageSettingsRow> villageSettingsRows)
    {
        OpenSettingsWindow(SettingsCategory.Celebrations, villageSettingsRows);
    }

    private IReadOnlyList<TownHallOverviewRow> BuildTownHallOverviewRows(
        IReadOnlyList<VillageSettingsRow>? villageSettingsRows)
    {
        var account = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(account))
        {
            return [];
        }

        var villages = GetAllVillageKeyInfos();
        var options = LoadBotOptions();
        var globalMode = TownHallCelebrationDefaults.NormalizeMode(options.TownHallCelebrationMode);
        var townHallGroupKey = QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration);
        return villages
            .Select(village =>
            {
                var savedMode = TownHallSettingsStore.LoadMode(_projectRoot, account, village.Key);
                return new TownHallOverviewRow(
                    village.Key,
                    village.Name,
                    ResolveTownHallEnabledForVillage(village, villageSettingsRows, townHallGroupKey),
                    savedMode ?? globalMode);
            })
            .ToList();
    }

    private void PersistTownHallSettings(
        IReadOnlyList<TownHallOverviewResult> results,
        IReadOnlyList<VillageSettingsRow>? villageSettingsRows)
    {
        var account = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        var villages = GetAllVillageKeyInfos();
        var townHallGroupKey = QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration);
        foreach (var result in results)
        {
            TownHallSettingsStore.SaveMode(_projectRoot, account, result.VillageKey, result.Mode);
            var village = villages.FirstOrDefault(v => string.Equals(v.Key, result.VillageKey, StringComparison.OrdinalIgnoreCase))
                ?? new VillageSettingsStore.VillageKeyInfo(result.VillageKey, result.VillageName, null, null, false);
            PersistAutomationGroupEnabledForVillage(village, result.IsTownHallEnabled, townHallGroupKey);
            UpdateVillageSettingsGroupRow(
                villageSettingsRows,
                result.VillageKey,
                result.VillageName,
                townHallGroupKey,
                result.IsTownHallEnabled);

            if (!result.IsTownHallEnabled)
            {
                TownHallCelebrationStateStore.Clear(_projectRoot, account, result.VillageKey);
                InvalidateVillageOverviewTownHallCache();
            }
        }

        var removed = RemoveTownHallQueueItemsForVillage(null);
        RefreshAutomationLoopDashboardUi();
        AppendLog($"Saved Town Hall settings for {results.Count} village(s). Cleared {removed} queued Town Hall task(s).");
    }

    private bool ResolveTownHallEnabledForVillage(
        VillageSettingsStore.VillageKeyInfo village,
        IReadOnlyList<VillageSettingsRow>? villageSettingsRows,
        string townHallGroupKey)
    {
        var row = FindVillageSettingsRow(villageSettingsRows, village);
        var rowToggle = row?.GroupToggles.FirstOrDefault(toggle =>
            string.Equals(toggle.GroupKey, townHallGroupKey, StringComparison.OrdinalIgnoreCase));
        if (rowToggle is not null)
        {
            return rowToggle.IsEnabled;
        }

        var groups = _villageSettingsStore.GetEnabledGroups(village)
            ?? VillageSettingsStore.DefaultEnabledGroups;
        return groups.Contains(townHallGroupKey, StringComparer.OrdinalIgnoreCase);
    }

    private void PersistAutomationGroupEnabledForVillage(
        VillageSettingsStore.VillageKeyInfo village,
        bool enabled,
        string groupKey)
    {
        var groups = (_villageSettingsStore.GetEnabledGroups(village)
            ?? VillageSettingsStore.DefaultEnabledGroups)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (enabled)
        {
            if (!groups.Contains(groupKey, StringComparer.OrdinalIgnoreCase))
            {
                groups.Add(groupKey);
            }
        }
        else
        {
            groups.RemoveAll(group => string.Equals(group, groupKey, StringComparison.OrdinalIgnoreCase));
        }

        _villageSettingsStore.SetEnabledGroups(village, groups);

        if (string.Equals(GetSelectedVillageKey(), village.Key, StringComparison.OrdinalIgnoreCase))
        {
            ApplyAutomationLoopGroupsForSelectedVillage();
        }
        else
        {
            RefreshAutomationLoopDashboardUi();
        }
    }

    private static void UpdateVillageSettingsGroupRow(
        IReadOnlyList<VillageSettingsRow>? villageSettingsRows,
        string villageKey,
        string villageName,
        string groupKey,
        bool enabled)
    {
        var row = villageSettingsRows?.FirstOrDefault(candidate =>
            candidate.KeyInfo is not null
            && (string.Equals(candidate.KeyInfo.Key, villageKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.KeyInfo.Name, villageName, StringComparison.OrdinalIgnoreCase)));
        var toggle = row?.GroupToggles.FirstOrDefault(item =>
            string.Equals(item.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase));
        if (toggle is not null)
        {
            toggle.IsEnabled = enabled;
        }
    }

    private void ApplyTownHallCelebrationDeferSignal(QueueItem item, string? message, TimeSpan queueWaitDelay)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var seconds = (int)Math.Ceiling(queueWaitDelay.TotalSeconds);
        if (seconds <= 0)
        {
            return;
        }

        var lower = message.ToLowerInvariant();
        if (!lower.Contains("town hall celebration running")
            && !lower.Contains("town hall celebration started"))
        {
            return;
        }

        var villageKey = GetQueueItemVillageKey(item);
        if (string.IsNullOrWhiteSpace(villageKey))
        {
            AppendLog("[town-hall] could not persist celebration timer: queue item has no village key.");
            return;
        }

        item.Payload.TryGetValue(BotOptionPayloadKeys.TownHallCelebrationMode, out var rawMode);
        var mode = TownHallCelebrationDefaults.NormalizeMode(rawMode);
        var endsAtUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
        TownHallCelebrationStateStore.Save(_projectRoot, _accountStore.ActiveAccountName(), villageKey, mode, endsAtUtc);
        InvalidateVillageOverviewTownHallCache();
        AppendLog($"[town-hall] remembered celebration timer for '{NormalizeVillageName(GetQueueItemVillageName(item)) ?? villageKey}' until {FormatQueueServerTime(endsAtUtc)}.");
    }
}
