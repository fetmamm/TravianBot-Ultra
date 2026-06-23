using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void SyncDashboardVillageUiFromVillages(
        IReadOnlyList<Village> villages,
        string? activeVillageName,
        string? preferredVillageName = null)
    {
        var selectedVillageName = string.IsNullOrWhiteSpace(preferredVillageName)
            ? GetSelectedVillageName()
            : preferredVillageName;

        var items = BuildMergedVillageSelectionItems(villages, activeVillageName);
        SyncDashboardVillageUi(items, selectedVillageName, activeVillageName);
    }

    private void SyncDashboardVillageUiFromPayloadVillages(
        IReadOnlyList<UiSyncVillagePayload> villages,
        string? activeVillageName)
    {
        var items = BuildMergedVillageSelectionItems(villages);
        SyncDashboardVillageUi(items, GetSelectedVillageName(), activeVillageName);
    }

    private void SyncDashboardVillageUi(
        IReadOnlyList<VillageSelectionItem> items,
        string? preferredVillageName,
        string? fallbackVillageName)
    {
        var ensuredItems = EnsureVillageSelectionItems(items);
        ApplyVillagePickerItems(ensuredItems, preferredVillageName, fallbackVillageName);
        ApplyDashboardVillageListItems(ensuredItems);
        ApplyResourceTransferVillageItems(ensuredItems);
        ApplyReinforcementVillageItems(ensuredItems);
    }

    private void ApplyVillagePickerItems(
        IReadOnlyList<VillageSelectionItem> items,
        string? preferredVillageName,
        string? fallbackVillageName)
    {
        // Don't blank out a populated picker with an empty/placeholder update (e.g. a transient
        // status refresh delivered while the page was navigating). Keeps the village visible.
        if (!HasRealVillages(items)
            && VillageComboBox.ItemsSource is IEnumerable<VillageSelectionItem> currentPicker
            && HasRealVillages(currentPicker.ToList()))
        {
            return;
        }

        var ensuredItems = EnsureVillageSelectionItems(items);

        _suppressVillageSelectionChange = true;
        try
        {
            VillageComboBox.ItemsSource = ensuredItems;
            var selected = ensuredItems.FirstOrDefault(item =>
                string.Equals(item.Name, preferredVillageName, StringComparison.OrdinalIgnoreCase))
                ?? ensuredItems.FirstOrDefault(item =>
                    string.Equals(item.Name, fallbackVillageName, StringComparison.OrdinalIgnoreCase))
                ?? ensuredItems[0];
            VillageComboBox.SelectedItem = selected;
        }
        finally
        {
            _suppressVillageSelectionChange = false;
        }

        // Mirror the picker into the Queue tab's village dropdown so both stay in sync.
        SyncQueueVillagePicker(VillageComboBox.SelectedItem as VillageSelectionItem);
        ApplyHeroResourceTransferConfigToUi();
    }

    private void ApplyDashboardVillageListItems(IReadOnlyList<VillageSelectionItem> items)
    {
        // Don't blank out a populated list with an empty/placeholder update (e.g. a transient
        // status refresh delivered while the page was navigating). Keeps population visible.
        if (!HasRealVillages(items)
            && DashboardVillageList.ItemsSource is IEnumerable<VillageSelectionItem> currentList
            && HasRealVillages(currentList.ToList()))
        {
            return;
        }

        DashboardVillageList.ItemsSource = EnsureVillageSelectionItems(items)
            .OrderByDescending(item => item.IsCapital)
            .ToList();

        // The list was rebuilt with fresh items; re-apply the active-village border.
        ApplyActiveVillageHighlight();
    }

    // A list is "real" when it contains at least one named village that isn't the "-" placeholder.
    private static bool HasRealVillages(IReadOnlyList<VillageSelectionItem> items)
    {
        return items.Any(item =>
            !string.IsNullOrWhiteSpace(item.Name)
            && !string.Equals(item.Name, "-", StringComparison.Ordinal));
    }

    private static IReadOnlyList<VillageSelectionItem> EnsureVillageSelectionItems(IReadOnlyList<VillageSelectionItem> items)
    {
        return items.Count > 0
            ? items
            : new[]
            {
                new VillageSelectionItem { Name = "-", Url = string.Empty },
            };
    }

    // Villages are identified by their coordinates (stable and unique per village: a village never moves
    // and keeps its coordinates across renames). This also collapses a village that the page has reported
    // under more than one newdid into a single identity, so its per-village settings never split in two.
    // Falls back to the newdid (from the switch URL), then the name, when no coordinate is available.
    private static readonly Regex NewdidRegex =
        new(@"[?&]newdid=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string GetVillageKey(string? url, int? coordX, int? coordY, string? name)
    {
        var match = string.IsNullOrWhiteSpace(url) ? Match.Empty : NewdidRegex.Match(url);
        var newdid = match.Success ? match.Groups[1].Value : null;
        return VillageKey.FromComponents(coordX, coordY, newdid, name);
    }

    private static string GetVillageKey(VillageSelectionItem item)
        => GetVillageKey(item.Url, item.CoordX, item.CoordY, item.Name);

    // A queue item's target village is carried in its payload (set by ApplySelectedVillageToPayload at
    // enqueue time). Returns the stable village key, or null when the item is not tied to a village
    // (legacy/global tasks) so rotation treats those as the default group.
    private string? GetQueueItemVillageKey(QueueItem item)
    {
        var name = GetQueueItemVillageName(item);
        var url = GetQueueItemPayloadValue(item, BotOptionPayloadKeys.TargetVillageUrl);
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Queue payloads carry no coordinates, so key by the village NAME, then resolve it through the
        // settings store to the canonical coordinate key. This keeps queue gating AND the per-village queue
        // rotation pointer (seeded with the coordinate key on Switch village) using one consistent identity.
        // Fall back to the newdid url only when there is no name.
        var rawKey = string.IsNullOrWhiteSpace(name)
            ? GetVillageKey(url, null, null, null)
            : GetVillageKey(null, null, null, name);
        return _villageSettingsStore.ResolveCanonicalKey(rawKey);
    }

    private static string? GetQueueItemVillageName(QueueItem item)
        => GetQueueItemPayloadValue(item, BotOptionPayloadKeys.TargetVillageName);

    // Whether a queue item's target village is enabled for automation. Items without a village pass the
    // village gate here; their group is still gated separately. Unknown villages default to allowed so a
    // not-yet-discovered legacy queue item is not silently blocked.
    private bool IsQueueItemVillageEnabled(QueueItem item)
    {
        var key = GetQueueItemVillageKey(item);
        return key is null || _villageSettingsStore.IsEnabledByKey(key, defaultIfUnknown: true);
    }

    private static string? GetQueueItemPayloadValue(QueueItem item, string key)
    {
        if (item.Payload is null || !item.Payload.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private string? _lastDisplayedVillageSignature;

    // Applies a village list read during the periodic page refresh to the dropdown and the Dashboard
    // list. Goal: every refresh can pick up renamed or newly founded villages. Two safety rules:
    //   1. If the current page had no readable village info, do nothing (never blank what is shown).
    //   2. Only re-apply when the village set actually changed, to avoid per-tick churn/flicker.
    private void TryUpdateDashboardVillagesFromStatus(VillageStatus status)
    {
        var villages = status.Villages;
        if (villages is null || !villages.Any(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            return;
        }

        var signature = BuildVillageSignature(villages);
        if (string.Equals(signature, _lastDisplayedVillageSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastDisplayedVillageSignature = signature;
        VillagesInfoTextBlock.Text = $"Villages: {villages.Count}";
        SyncDashboardVillageUiFromVillages(villages, status.ActiveVillage);
    }

    private static string BuildVillageSignature(IReadOnlyList<Village> villages)
    {
        return string.Join(";", villages
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => $"{GetVillageKey(v.Url, v.CoordX, v.CoordY, v.Name)}|{v.Name}|{v.CoordX}|{v.CoordY}|{v.IsCapital}|{v.Population}"));
    }

    private void ReconcileConfirmedVillageList(IReadOnlyList<Village> villages, string source)
    {
        var confirmed = villages
            .Where(village => !string.IsNullOrWhiteSpace(village.Name))
            .Select(village => new VillageSettingsStore.VillageKeyInfo(
                GetVillageKey(village.Url, village.CoordX, village.CoordY, village.Name),
                village.Name!,
                village.CoordX,
                village.CoordY,
                village.IsCapital ?? false))
            .ToList();
        if (confirmed.Count == 0)
        {
            return;
        }

        var disabledVillages = _villageSettingsStore.DisableVillagesMissingFromConfirmedList(confirmed);
        var liveKeys = BuildConfirmedLiveVillageKeys(villages);
        var pausedQueueItems = ConfirmedVillageQueueReconciler.PausePendingItemsForMissingVillages(
            _botService.GetQueueItemsForDisplay(),
            liveKeys,
            GetQueueItemVillageKey,
            id => _botService.PauseQueueItem(id));

        if (disabledVillages.Count > 0)
        {
            RefreshVillageEnabledStateOnDashboard();
        }

        if (pausedQueueItems > 0)
        {
            RequestQueueUiRefresh();
        }

        if (disabledVillages.Count > 0 || pausedQueueItems > 0)
        {
            AppendLog(
                $"[village-reconcile] source={source} confirmedVillages={confirmed.Count} " +
                $"disabledVillages={disabledVillages.Count} " +
                $"pausedQueueItems={pausedQueueItems}" +
                (disabledVillages.Count == 0 ? string.Empty : $" names='{string.Join(", ", disabledVillages)}'"));
        }
    }

    private static HashSet<string> BuildConfirmedLiveVillageKeys(IReadOnlyList<Village> villages)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var village in villages.Where(village => !string.IsNullOrWhiteSpace(village.Name)))
        {
            keys.Add(GetVillageKey(village.Url, village.CoordX, village.CoordY, village.Name));
            keys.Add(GetVillageKey(null, null, null, village.Name));
            if (!string.IsNullOrWhiteSpace(village.Url))
            {
                keys.Add(GetVillageKey(village.Url, null, null, null));
            }
        }

        return keys;
    }

    private List<VillageSelectionItem> BuildMergedVillageSelectionItems(
        IReadOnlyList<Village> villages,
        string? activeVillageName = null)
    {
        var existingVillageData = BuildExistingVillageSelectionLookup();

        var items = villages
            .Where(village => !string.IsNullOrWhiteSpace(village.Name))
            .Select(village =>
            {
                existingVillageData.TryGetValue(
                    GetVillageKey(village.Url, village.CoordX, village.CoordY, village.Name),
                    out var existing);
                // The active village's population is read live from the current page each refresh and
                // is the true value, so let it overwrite any cached value. Other villages carry a
                // frozen/stale population in status reads, so keep the currently displayed value.
                var isActiveVillage = !string.IsNullOrWhiteSpace(activeVillageName)
                    && string.Equals(village.Name, activeVillageName, StringComparison.OrdinalIgnoreCase);
                return BuildVillageSelectionItem(
                    village.Name!,
                    village.Url,
                    village.IsCapital,
                    village.CoordX,
                    village.CoordY,
                    village.Population,
                    village.CropFields,
                    existing,
                    preferExistingPopulation: !isActiveVillage);
            })
            .ToList();

        ApplyVillageEnabledState(items);
        ApplyVillageActivityIndicators(items);
        return items;
    }

    private List<VillageSelectionItem> BuildMergedVillageSelectionItems(IReadOnlyList<UiSyncVillagePayload> villages)
    {
        var existingVillageData = BuildExistingVillageSelectionLookup();

        var items = villages
            .Where(village => !string.IsNullOrWhiteSpace(village.Name))
            .Select(village =>
            {
                var name = village.Name!;
                existingVillageData.TryGetValue(
                    GetVillageKey(village.Url, village.CoordX, village.CoordY, name),
                    out var existing);
                return BuildVillageSelectionItem(
                    name,
                    village.Url,
                    village.IsCapital,
                    village.CoordX,
                    village.CoordY,
                    village.Population,
                    village.CropFields,
                    existing);
            })
            .ToList();

        ApplyVillageEnabledState(items);
        ApplyVillageActivityIndicators(items);
        return items;
    }

    // Persists newly discovered villages (only village enabled, later villages disabled) and applies the
    // stored enabled choice onto each item so the Dashboard toggle reflects the saved per-account state
    // across refreshes, restarts and renames.
    private void ApplyVillageEnabledState(List<VillageSelectionItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var keyInfos = items
            .Select(BuildVillageKeyInfo)
            .ToList();

        _villageSettingsStore.Merge(keyInfos);

        for (var i = 0; i < items.Count; i++)
        {
            items[i].IsEnabledForAutomation = _villageSettingsStore.GetEnabled(keyInfos[i]);
        }
    }

    private static VillageSettingsStore.VillageKeyInfo BuildVillageKeyInfo(VillageSelectionItem item)
    {
        return new VillageSettingsStore.VillageKeyInfo(
            GetVillageKey(item),
            item.Name,
            item.CoordX,
            item.CoordY,
            item.IsCapital);
    }

    // All known villages as key infos, deduplicated by canonical key. Used to apply a setting to every
    // village (e.g. "Sync to all villages" for Smithy upgrades).
    private List<VillageSettingsStore.VillageKeyInfo> GetAllVillageKeyInfos()
    {
        var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? Enumerable.Empty<VillageSelectionItem>();

        return source
            .Where(v => !string.IsNullOrWhiteSpace(v.Name) && !string.Equals(v.Name, "-", StringComparison.Ordinal))
            .Select(BuildVillageKeyInfo)
            .GroupBy(info => info.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    // Persists a village's automation toggle from the Village settings window. SetEnabled no-ops when the
    // stored value already matches, so seeding the rows never causes redundant writes.
    private void PersistVillageEnabledFromSettingsRow(VillageSettingsRow row)
    {
        if (row?.KeyInfo is null)
        {
            return;
        }

        _villageSettingsStore.SetEnabled(row.KeyInfo, row.IsEnabledForAutomation);
        // Repaint the dashboard enabled indicator (green/grey dot) right away.
        RefreshVillageEnabledStateOnDashboard();
    }

    // Effective NPC trade flag for a village: the account-wide master (Auto settings NPC toggle, stored as
    // config NpcTradeEnabled) AND the per-village choice. So NPC runs only when both are on.
    private bool IsNpcTradeEnabledForVillageKey(string? villageKey)
    {
        bool master;
        try
        {
            master = LoadBotOptions().NpcTradeEnabled;
        }
        catch
        {
            master = false;
        }

        return master && _villageSettingsStore.IsNpcTradeEnabledByKey(villageKey, defaultIfUnknown: false);
    }

    // Persists a village's per-village NPC trade choice from the Village settings window.
    private void PersistVillageNpcTradeFromSettingsRow(VillageSettingsRow row)
    {
        if (row?.KeyInfo is null)
        {
            return;
        }

        _villageSettingsStore.SetNpcTrade(row.KeyInfo, row.NpcTrade);
    }

    // Re-applies the persisted enabled state onto the current dashboard village items so the green/grey
    // enabled dot updates immediately after a toggle in the Village settings window.
    private void RefreshVillageEnabledStateOnDashboard()
    {
        if (DashboardVillageList.ItemsSource is not IEnumerable<VillageSelectionItem> items)
        {
            return;
        }

        foreach (var item in items)
        {
            item.IsEnabledForAutomation = _villageSettingsStore.IsEnabledByKey(GetVillageKey(item), defaultIfUnknown: false);
        }
    }

    private Dictionary<string, VillageSelectionItem> BuildExistingVillageSelectionLookup()
    {
        return Enumerable.Empty<VillageSelectionItem>()
            .Concat(VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem> ?? [])
            .Concat(DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem> ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(GetVillageKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static VillageSelectionItem BuildVillageSelectionItem(
        string name,
        string? url,
        bool? isCapital,
        int? coordX,
        int? coordY,
        int? population,
        int? cropFields,
        VillageSelectionItem? existing,
        bool preferExistingPopulation = false)
    {
        // Population is driven by the live [ui-sync] path (incremental upgrades + real spieler reads).
        // Status/resource-refresh updates carry a frozen/stale population, so for those we keep the
        // currently displayed value and only seed it when nothing is shown yet — never overwrite.
        var resolvedPopulation = preferExistingPopulation
            ? existing?.Population ?? population
            : population ?? existing?.Population;

        return new VillageSelectionItem
        {
            Name = name,
            Url = string.IsNullOrWhiteSpace(url) ? existing?.Url ?? string.Empty : url,
            IsCapital = isCapital ?? existing?.IsCapital ?? false,
            CoordX = coordX ?? existing?.CoordX,
            CoordY = coordY ?? existing?.CoordY,
            Population = resolvedPopulation,
            CropFields = cropFields ?? existing?.CropFields,
        };
    }

    // Opens the central per-village settings window, seeded with the currently known villages
    // (name/pop/coords). The first and only village starts with Auto on; later villages start off.
    // Construction is the only automation group enabled by default.
    private void VillageSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? Enumerable.Empty<VillageSelectionItem>();

        var popupGroupOrder = new[]
        {
            QueueGroup.Hero,
            QueueGroup.Construction,
            QueueGroup.Troops,
            QueueGroup.TroopTraining,
            QueueGroup.Farming,
            QueueGroup.BreweryCelebration,
            QueueGroup.TownHallCelebration,
            QueueGroup.ResourceTransfer,
            QueueGroup.Reinforcements,
        }
            .Select((group, index) => (Key: QueueGroupCatalog.GetKey(group), Index: index))
            .ToDictionary(entry => entry.Key, entry => entry.Index, StringComparer.OrdinalIgnoreCase);

        // Keep the popup stable even when the dashboard cards have been reordered by the user.
        var groupCards = _automationLoopTasks
            .Where(card => card.IsVisible)
            .Select(card => (
                Key: card.TaskName,
                Title: string.Equals(
                    card.TaskName,
                    QueueGroupCatalog.GetKey(QueueGroup.Hero),
                    StringComparison.OrdinalIgnoreCase)
                        ? "Hero adv."
                        : string.Equals(
                    card.TaskName,
                    QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration),
                    StringComparison.OrdinalIgnoreCase)
                        ? "Brewery"
                        : string.Equals(
                            card.TaskName,
                            QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration),
                            StringComparison.OrdinalIgnoreCase)
                                ? "Town Hall"
                        : card.Title,
                Description: string.Equals(
                    card.TaskName,
                    QueueGroupCatalog.GetKey(QueueGroup.Hero),
                    StringComparison.OrdinalIgnoreCase)
                        ? "Hero adventures."
                        : card.Description))
            .OrderBy(card => popupGroupOrder.TryGetValue(card.Key, out var index) ? index : int.MaxValue)
            .ToList();

        var rows = source
            .Where(v => !string.IsNullOrWhiteSpace(v.Name) && !string.Equals(v.Name, "-", StringComparison.Ordinal))
            .Select(v =>
            {
                var keyInfo = BuildVillageKeyInfo(v);
                var enabledGroups = _villageSettingsStore.GetEnabledGroups(keyInfo)
                    ?? VillageSettingsStore.DefaultEnabledGroups;
                var toggles = groupCards
                    .Select(card => new VillageGroupToggle
                    {
                        GroupKey = card.Key,
                        Title = card.Title,
                        Description = card.Description,
                        IsEnabled = enabledGroups.Contains(card.Key, StringComparer.OrdinalIgnoreCase),
                    })
                    .ToList();

                return new VillageSettingsRow
                {
                    Name = v.Name,
                    PopText = v.PopText,
                    KeyInfo = keyInfo,
                    IsEnabledForAutomation = _villageSettingsStore.GetEnabled(keyInfo),
                    NpcTrade = _villageSettingsStore.GetNpcTrade(keyInfo),
                    HeroResourcesEnabled = _villageSettingsStore.GetHeroResourcesEnabled(keyInfo),
                    GroupToggles = toggles,
                };
            })
            .ToList();

        var window = new VillageSettingsWindow(
            rows,
            PersistVillageEnabledFromSettingsRow,
            PersistVillageNpcTradeFromSettingsRow,
            PersistVillageHeroResourcesFromSettingsRow,
            PersistVillageGroupsFromSettingsRow,
            OpenTroopSettingsFromVillageSettings,
            OpenTownHallSettingsFromVillageSettings,
            OpenHeroResourceSettingsFromVillageSettings,
            OnVillageSettingsSaved)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private void PersistVillageHeroResourcesFromSettingsRow(VillageSettingsRow row)
    {
        if (row?.KeyInfo is null)
        {
            return;
        }

        _villageSettingsStore.SetHeroResourcesEnabled(row.KeyInfo, row.HeroResourcesEnabled);
    }

    // Persists a village's per-village automation-group set from the Village settings window, then keeps the
    // dashboard cards in sync when the changed village is the one currently selected.
    private void PersistVillageGroupsFromSettingsRow(VillageSettingsRow row)
    {
        if (row?.KeyInfo is null)
        {
            return;
        }

        var previouslyEnabled = _villageSettingsStore.GetEnabledGroups(row.KeyInfo)
            ?? VillageSettingsStore.DefaultEnabledGroups;
        var enabled = row.GroupToggles
            .Where(toggle => toggle.IsEnabled)
            .Select(toggle => toggle.GroupKey)
            .ToList();
        _villageSettingsStore.SetEnabledGroups(row.KeyInfo, enabled);

        var constructionKey = QueueGroupCatalog.GetKey(QueueGroup.Construction);
        if (!previouslyEnabled.Contains(constructionKey, StringComparer.OrdinalIgnoreCase)
            && enabled.Contains(constructionKey, StringComparer.OrdinalIgnoreCase))
        {
            ResetConstructionBuildQueueTimerForManualRefresh();
            ResetDeferredConstructionWaitsNow("construction group enabled in village settings");
        }

        if (string.Equals(GetSelectedVillageKey(), row.KeyInfo.Key, StringComparison.OrdinalIgnoreCase))
        {
            ApplyAutomationLoopGroupsForSelectedVillage();
        }
    }

    private void OnVillageSettingsSaved()
    {
        RefreshVillageEnabledStateOnDashboard();
        RefreshAutomationLoopDashboardUi();
        ApplyHeroResourceTransferConfigToUi();

        if (IsContinuousLoopRunning())
        {
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
            AppendLog("Village settings saved. Continuous loop will refresh the queue now.");
        }
    }

    private void VillageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVillageSelectionChange)
        {
            return;
        }

        if (VillageComboBox.SelectedItem is not VillageSelectionItem selected)
        {
            return;
        }

        StatusTextBlock.Text = $"Selected village: {selected.Name}";

        // Selecting a village is a view/queue-context change only — it must NOT navigate the browser or
        // touch the running bot. Show this village's cached buildings/resources and filter the queue to
        // it. Use the "Switch village" button to actually move the bot to this village.
        ShowSelectedVillageFromCache(selected);
        ApplyHeroResourceTransferConfigToUi();
    }


    private bool IsExecutionActiveForVillageChange()
    {
        return _uiBusy || _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted);
    }

    // Switching the viewed village while the bot is running stops the active run, but no longer clears
    // the queue: with one queue per account and each task tagged for its own village, other villages'
    // queued work must survive a village switch. Press Start to resume; rotation drains per village.
    private async Task StopForVillageChangeAsync(string? villageName)
    {
        var label = string.IsNullOrWhiteSpace(villageName) ? "-" : villageName;
        AppendLog($"Village changed to '{label}' while bot is running. Stopping active work (queue kept).");

        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();
        _loopController.CancelVillageSwitch();

        var stopDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < stopDeadline)
        {
            if (!_uiBusy && !_autoQueueRunning && (_loopTask is null || _loopTask.IsCompleted))
            {
                break;
            }

            await Task.Delay(Random.Shared.Next(150, 350)); // Random wait
        }

        RefreshQueueUi();
    }

    private string? GetSelectedVillageName()
    {
        var selection = GetSelectedVillageSelectionSnapshot();
        return selection.Name;
    }

    private string? GetSelectedVillageUrl()
    {
        var selection = GetSelectedVillageSelectionSnapshot();
        return selection.Url;
    }

    private BotOptions ApplySelectedVillageToOptions(BotOptions source)
    {
        var selection = GetSelectedVillageSelectionSnapshot();
        var selectedName = selection.Name;
        var selectedUrl = selection.Url;
        if (string.IsNullOrWhiteSpace(selectedName) && string.IsNullOrWhiteSpace(selectedUrl))
        {
            return source;
        }

        var options = BotOptionsPayloadApplier.Apply(source, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TargetVillageName] = selectedName ?? string.Empty,
            [BotOptionPayloadKeys.TargetVillageUrl] = selectedUrl ?? string.Empty,
        });

        var villageKey = GetVillageKey(selectedUrl, null, null, selectedName);
        return ApplyHeroResourceSettingsForVillage(options, villageKey, selectedName);
    }

    private (string? Name, string? Url) GetSelectedVillageSelectionSnapshot()
    {
        if (Dispatcher.CheckAccess())
        {
            return ReadSelectedVillageSelectionCore();
        }

        return Dispatcher.Invoke(ReadSelectedVillageSelectionCore);
    }

    private (string? Name, string? Url) ReadSelectedVillageSelectionCore()
    {
        if (VillageComboBox.SelectedItem is not VillageSelectionItem selected)
        {
            return (null, null);
        }

        string? name = null;
        if (!string.IsNullOrWhiteSpace(selected.Name))
        {
            var trimmed = selected.Name.Trim();
            if (!string.Equals(trimmed, "-", StringComparison.Ordinal)
                && !string.Equals(trimmed, "Unknown village", StringComparison.OrdinalIgnoreCase))
            {
                name = trimmed;
            }
        }

        var url = string.IsNullOrWhiteSpace(selected.Url) ? null : selected.Url.Trim();
        return (name, url);
    }
}
