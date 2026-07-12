using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void TownHallSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        OpenTownHallSettingsWindow(null);
    }

    private void OpenTownHallSettingsFromVillageSettings(IReadOnlyList<VillageSettingsRow> villageSettingsRows)
    {
        OpenTownHallSettingsWindow(villageSettingsRows);
    }

    private void OpenTownHallSettingsWindow(IReadOnlyList<VillageSettingsRow>? villageSettingsRows)
    {
        var account = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(account))
        {
            AppendLog("Town Hall settings: no active account.");
            return;
        }

        var villages = GetAllVillageKeyInfos();
        if (villages.Count == 0)
        {
            AppendLog("Town Hall settings: no villages loaded.");
            return;
        }

        var options = LoadBotOptions();
        var globalMode = TownHallCelebrationDefaults.NormalizeMode(options.TownHallCelebrationMode);
        var townHallGroupKey = QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration);
        var rows = villages
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

        var window = new TownHallOverviewWindow(
            rows,
            options.TownHallCelebrationCount,
            options.TownHallCelebrationRestartDelayMinMinutes,
            options.TownHallCelebrationRestartDelayMaxMinutes)
        {
            Owner = this,
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        SaveTownHallQueueSettings(account, window.Queue);

        foreach (var result in window.Results)
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
            }
        }

        var removed = RemoveTownHallQueueItemsForVillage(null);
        RefreshAutomationLoopDashboardUi();
        AppendLog($"Saved Town Hall settings for {window.Results.Count} village(s). Cleared {removed} queued Town Hall task(s).");
    }

    // Persists the account-wide celebration-queue settings (one vs two celebrations + restart delay) from
    // the popup's bottom box. Stored account-scoped in bot.json, mirroring TownHallCelebrationMode.
    private void SaveTownHallQueueSettings(string account, TownHallQueueSettings queue)
    {
        try
        {
            var config = _botConfigStore.LoadForAccount(account);
            config[BotOptionPayloadKeys.TownHallCelebrationCount] = TownHallCelebrationDefaults.NormalizeCount(queue.Count);
            config[BotOptionPayloadKeys.TownHallCelebrationRestartDelayMinMinutes] = queue.ResolvedDelayMinMinutes;
            config[BotOptionPayloadKeys.TownHallCelebrationRestartDelayMaxMinutes] = queue.ResolvedDelayMaxMinutes;
            _botConfigStore.SaveForAccount(account, config);
            AppendLog($"[town-hall] queue settings saved: count={queue.Count}, restart delay {queue.ResolvedDelayMinMinutes:0.##}-{queue.ResolvedDelayMaxMinutes:0.##} min.");
        }
        catch (Exception ex)
        {
            AppendLog($"[town-hall] could not save queue settings: {ex.Message}");
        }
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
        AppendLog($"[town-hall] remembered celebration timer for '{NormalizeVillageName(GetQueueItemVillageName(item)) ?? villageKey}' until {FormatQueueServerTime(endsAtUtc)}.");
    }
}
