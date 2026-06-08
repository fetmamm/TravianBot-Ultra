using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

// Separation of the "selected/viewed village" (the dropdown — a view/queue-target context) from the
// "active working village" (the one open in the browser, where the bot is actually working). Selecting
// a village in the dropdown no longer navigates the browser; it only repaints the Buildings/resources
// tabs from a per-village cache and re-targets/filters the queue. The browser is only moved by the
// explicit "Switch village" button (or by automation rotating between villages as it runs).
public partial class MainWindow
{
    // Last-read status per village NAME, so the dropdown can show a village's buildings/resources for
    // queueing without navigating to it. Keyed by name (the identity reads + dropdown agree on) so a
    // url/newdid difference between reads can never split or collide a village's cache entry.
    private readonly Dictionary<string, VillageStatus> _villageStatusCacheByName = new(StringComparer.OrdinalIgnoreCase);

    private static string? NormalizeVillageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return string.Equals(trimmed, "Unknown village", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "-", StringComparison.Ordinal)
            ? null
            : trimmed;
    }

    // The hero's home village name, captured from the hero attributes read at login (one home village at
    // a time). Drives the green/yellow/dark Hero icon in the Dashboard village list.
    private string? _heroHomeVillageName;

    // Hero icon state for the home village: away (yellow), reviving (orange), dead (red, overrides). Dark =
    // not the hero village, green = hero home and alive.
    private bool _heroIsAway;
    private bool _heroIsDead;
    private bool _heroIsReviving;

    // Records the hero home village + away/dead/reviving state and repaints the Dashboard hero indicators. A
    // null name keeps the last-known home village (e.g. when the hero is on an adventure and the page no
    // longer names a village) so the flags can still color the right row. No-op when nothing changed.
    private void SetHeroState(string? name, bool isAway, bool isDead, bool isReviving = false)
    {
        var normalized = NormalizeVillageName(name) ?? _heroHomeVillageName;
        if (string.Equals(_heroHomeVillageName, normalized, StringComparison.OrdinalIgnoreCase)
            && _heroIsAway == isAway
            && _heroIsDead == isDead
            && _heroIsReviving == isReviving)
        {
            return;
        }

        var homeChanged = !string.Equals(_heroHomeVillageName, normalized, StringComparison.OrdinalIgnoreCase);
        _heroHomeVillageName = normalized;
        _heroIsAway = isAway;
        _heroIsDead = isDead;
        _heroIsReviving = isReviving;
        AppendLog($"[hero] home village='{_heroHomeVillageName ?? "(unknown)"}' away={isAway} dead={isDead} reviving={isReviving} — updating dashboard hero icons.");

        // Remember the last-read home village across restarts (per account). Only persist a real name.
        if (homeChanged && _heroHomeVillageName is not null)
        {
            try
            {
                _villageSettingsStore.SetHeroHomeVillageName(_heroHomeVillageName);
            }
            catch (Exception ex)
            {
                AppendLog($"[hero] could not persist home village: {ex.Message}");
            }
        }

        RefreshVillageActivityIndicatorsOnDashboard();
    }

    // Seeds the remembered hero home village (from a previous session) so the dashboard icon shows on the
    // right village immediately at login, before the first hero read. Assumes home/alive until a read updates.
    private void LoadHeroHomeVillageForActiveAccount()
    {
        try
        {
            var saved = _villageSettingsStore.GetHeroHomeVillageName();
            if (!string.IsNullOrWhiteSpace(saved) && _heroHomeVillageName is null)
            {
                _heroHomeVillageName = NormalizeVillageName(saved);
                RefreshVillageActivityIndicatorsOnDashboard();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[hero] could not load remembered home village: {ex.Message}");
        }
    }

    private bool ResolveIsRomansTribe()
    {
        foreach (var status in _villageStatusCacheByName.Values)
        {
            if (!string.IsNullOrWhiteSpace(status.Tribe)
                && status.Tribe.Contains("Roman", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Fills the Buildings/Troops/Hero overview indicators on each village item from the per-village status
    // cache. Non-active villages show their last-scanned state (the bot reads one village at a time).
    private void ApplyVillageActivityIndicators(IReadOnlyList<VillageSelectionItem> items)
    {
        if (items is null || items.Count == 0)
        {
            return;
        }

        var buildingSlotCount = ResolveIsRomansTribe() ? 3 : 2;
        var heroHome = NormalizeVillageName(_heroHomeVillageName);
        // Per-village "build queue is occupied" signal from the queue: a construction item deferred with
        // reason queue_full means the server build queue is full → something IS building there. This lights
        // the Buildings slots for villages the bot isn't currently scanning (their cached status has no
        // live build count).
        var queueFullByVillage = BuildQueueFullConstructionCountByVillage();
        var queuedVillages = BuildVillagesWithConstructionQueue();
        foreach (var item in items)
        {
            var name = NormalizeVillageName(item.Name);
            VillageStatus? status = null;
            if (name is not null)
            {
                _villageStatusCacheByName.TryGetValue(name, out status);
            }

            var queueActive = name is not null && queueFullByVillage.TryGetValue(name, out var count) ? count : 0;
            item.BuildingSlots = BuildBuildingActivitySlots(status, buildingSlotCount, queueActive);
            item.TroopSlots = BuildTroopActivitySlots(status);
            item.SmithySlots = BuildSmithyActivitySlots();
            item.HasQueue = name is not null && queuedVillages.Contains(name);

            var isHeroVillage = name is not null
                && heroHome is not null
                && string.Equals(name, heroHome, StringComparison.OrdinalIgnoreCase);
            item.IsHeroHome = isHeroVillage;
            // Priority: dead (red) > reviving (orange) > away (yellow) > home (green).
            item.IsHeroDead = isHeroVillage && _heroIsDead;
            item.IsHeroReviving = isHeroVillage && _heroIsReviving && !_heroIsDead;
            item.IsHeroAway = isHeroVillage && _heroIsAway && !_heroIsDead && !_heroIsReviving;
        }
    }

    // Village names that currently have at least one pending construction item queued. Drives the green
    // (has queue) vs muted (empty) queue icon so the user sees which villages need more queued.
    private HashSet<string> BuildVillagesWithConstructionQueue()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in _botService.GetQueueItemsForDisplay())
            {
                if (item.Status != QueueStatus.Pending || !IsConstructionQueueTask(item.TaskName))
                {
                    continue;
                }

                var villageName = NormalizeVillageName(GetQueueItemVillageName(item));
                if (villageName is not null)
                {
                    set.Add(villageName);
                }
            }
        }
        catch
        {
            // Non-fatal for an overview indicator.
        }

        return set;
    }

    // Two idle smithy upgrade slots (two simultaneous upgrades). UI placeholder — not yet wired to live data.
    private static IReadOnlyList<VillageActivitySlot> BuildSmithyActivitySlots()
    {
        return new[]
        {
            new VillageActivitySlot { IsActive = false, Label = "", Tooltip = "Smithy upgrade slot (not connected yet)" },
            new VillageActivitySlot { IsActive = false, Label = "", Tooltip = "Smithy upgrade slot (not connected yet)" },
        };
    }

    private void RefreshVillageActivityIndicatorsOnDashboard()
    {
        if (DashboardVillageList.ItemsSource is IEnumerable<VillageSelectionItem> items)
        {
            ApplyVillageActivityIndicators(items.ToList());
        }
    }

    // Counts, per village name, construction queue items deferred because the build queue was full. Those
    // imply the server build queue is occupied, so the Buildings slots can light up even for a village the
    // bot isn't actively scanning.
    private Dictionary<string, int> BuildQueueFullConstructionCountByVillage()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in _botService.GetQueueItemsForDisplay())
            {
                if (item.Status != QueueStatus.Pending || !IsConstructionQueueTask(item.TaskName))
                {
                    continue;
                }

                if (item.Payload is null
                    || !item.Payload.TryGetValue(BotOptionPayloadKeys.UpgradeDeferReason, out var reason)
                    || !string.Equals(reason, BotOptionPayloadKeys.UpgradeDeferReasonQueueFull, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var villageName = NormalizeVillageName(GetQueueItemVillageName(item));
                if (villageName is null)
                {
                    continue;
                }

                map[villageName] = (map.TryGetValue(villageName, out var current) ? current : 0) + 1;
            }
        }
        catch
        {
            // Queue read failures are non-fatal for an overview indicator.
        }

        return map;
    }

    private static IReadOnlyList<VillageActivitySlot> BuildBuildingActivitySlots(VillageStatus? status, int slotCount, int queueActiveCount = 0)
    {
        var active = Math.Clamp(Math.Max(status?.ActiveBuildCount ?? 0, queueActiveCount), 0, slotCount);
        var slots = new List<VillageActivitySlot>(slotCount);
        for (var i = 0; i < slotCount; i++)
        {
            var isActive = i < active;
            slots.Add(new VillageActivitySlot
            {
                IsActive = isActive,
                Label = "", // hammer glyph set in XAML; Label unused for build slots — represents a construction slot.
                Tooltip = isActive ? "Construction in progress" : "Build slot free",
            });
        }

        return slots;
    }

    private static IReadOnlyList<VillageActivitySlot> BuildTroopActivitySlots(VillageStatus? status)
    {
        var defs = new (TroopTrainingBuildingType Type, string Letter, string Label)[]
        {
            (TroopTrainingBuildingType.Barracks, "B", "Barracks"),
            (TroopTrainingBuildingType.Stable, "S", "Stable"),
            (TroopTrainingBuildingType.Workshop, "W", "Workshop"),
        };

        var queues = status?.TroopTrainingQueues;
        var slots = new List<VillageActivitySlot>(defs.Length);
        foreach (var (type, letter, label) in defs)
        {
            var queue = queues?.FirstOrDefault(q => q.BuildingType == type);
            var isActive = queue is { RemainingSeconds: > 0 };
            string tooltip;
            if (queue is null || !queue.Exists)
            {
                tooltip = $"{label}: not built";
            }
            else
            {
                tooltip = isActive ? $"{label}: training ({queue.RemainingText})" : $"{label}: idle";
            }

            slots.Add(new VillageActivitySlot { IsActive = isActive, Label = letter, Tooltip = tooltip });
        }

        return slots;
    }

    // The village the bot is currently working in (browser's village). Shown with a colored border on
    // the Dashboard village list. Null until the first village is read.
    private string? _activeWorkingVillageKey;
    private string? _activeWorkingVillageName;

    // A village switch requested via the Switch village button while automation is running. The browser
    // is owned by the running loop, so the UI can't navigate directly; the loop performs the switch at
    // its next iteration boundary (between tasks) where touching the browser is safe.
    private readonly object _pendingSwitchVillageLock = new();
    private string? _pendingSwitchVillageName;
    private string? _pendingSwitchVillageUrl;

    // Performs a pending Switch-village request from inside a running loop (between tasks). Navigates the
    // browser to the requested village, reads it, and refreshes the whole UI (buildings/resources/storage
    // + green border + dropdown). No-op when nothing is pending.
    private async Task HonorPendingVillageSwitchAsync(BotOptions options, CancellationToken token)
    {
        string? name;
        string? url;
        lock (_pendingSwitchVillageLock)
        {
            name = _pendingSwitchVillageName;
            url = _pendingSwitchVillageUrl;
            _pendingSwitchVillageName = null;
            _pendingSwitchVillageUrl = null;
        }

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            await _botService.NavigateToVillageResourceFieldsAsync(options, AppendLog, name, url, token);
            AppendLog($"Switch village: browser moved to '{name}'.");
            await ApplyCurrentVillageToUiAsync(options, token);
            MarkContinuousBrowserActivity();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"Switch village navigation failed: {ex.Message}");
        }
    }

    // Reads the village the browser is CURRENTLY on (reliable full read with retry) and applies it to the
    // whole dashboard: dropdown selection, green active-village border, buildings, resources, tribe and
    // village count. Used after login (so the UI shows the village Travian actually landed in) and after a
    // Switch village during a run. The dropdown is set to the active village BEFORE painting so the
    // selected==active gates let the buildings/resources repaint through.
    private async Task ApplyCurrentVillageToUiAsync(BotOptions options, CancellationToken token)
    {
        var status = await ReadVillageStatusWithRetryAsync(options, token, resourceOnly: false, forceCurrentVillage: true);
        await Dispatcher.InvokeAsync(() =>
        {
            SyncDashboardVillageUiFromVillages(status.Villages, status.ActiveVillage, status.ActiveVillage);
            SetActiveWorkingVillageFromStatus(status);
            CacheVillageStatus(status);
            ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
            _resourcesViewModel.ApplyStorageForecasts(status);
            _lastBuildingStatus = status;
            PopulateBuildingsTab(status);
            BuildingsInfoTextBlock.Text = _buildingsViewModel.DescribeLoadedSlots($"active village '{status.ActiveVillage}'");
            SetTribeText(status.Tribe);
            VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
            ApplyAutomationLoopGroupsForSelectedVillage();
            ApplyConstructionTimerFromStatus(status);
        });
    }

    // Filters queue rows to the selected village (matched by NAME, robust to url/key differences).
    // Village-less (global) rows are always shown; when no village is selected, nothing is filtered.
    private List<QueueItemRow> FilterQueueRowsForSelectedVillage(IReadOnlyList<QueueItemRow> rows)
    {
        var selectedName = NormalizeVillageName(GetSelectedVillageName());
        if (selectedName is null)
        {
            return rows.ToList();
        }

        return rows
            .Where(row => string.IsNullOrWhiteSpace(row.VillageName)
                || string.Equals(row.VillageName, "-", StringComparison.Ordinal)
                || string.Equals(row.VillageName.Trim(), selectedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // The Queue tab just shows which village's queue is displayed; the village is chosen with the
    // existing dropdown on the Dashboard. Keep the label in sync with that selection.
    private void SyncQueueVillagePicker(VillageSelectionItem? selected)
    {
        if (QueueSelectedVillageTextBlock is null)
        {
            return;
        }

        var name = selected?.Name;
        QueueSelectedVillageTextBlock.Text = string.IsNullOrWhiteSpace(name) || string.Equals(name, "-", StringComparison.Ordinal)
            ? "Selected village: -"
            : $"Selected village: {name}";
    }

    private void SwitchVillageButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        if (VillageComboBox.SelectedItem is not VillageSelectionItem selected)
        {
            AppendLog("Switch village: no village selected.");
            return;
        }

        _backgroundTasks.Track(SwitchToActiveVillageAsync(selected));
    }

    // The account's configured auto-loop groups, used as the default for villages that have no
    // per-village override yet. Captured when the automation loop is loaded.
    private List<string> _defaultEnabledGroupKeys = new();

    private VillageSettingsStore.VillageKeyInfo? GetSelectedVillageKeyInfoOrNull()
    {
        if (VillageComboBox.SelectedItem is not VillageSelectionItem selected
            || string.IsNullOrWhiteSpace(selected.Name)
            || string.Equals(selected.Name, "-", StringComparison.Ordinal))
        {
            return null;
        }

        return BuildVillageKeyInfo(selected);
    }

    // Whether a queue item belongs to the selected village or is a village-less/global task (shown for
    // every village). Used to make the auto-loop group cards (timer/queued count/state) per village.
    private bool IsQueueItemForSelectedVillageOrGlobal(QueueItem item)
    {
        var selectedName = NormalizeVillageName(GetSelectedVillageName());
        if (selectedName is null)
        {
            return true;
        }

        var villageName = NormalizeVillageName(GetQueueItemVillageName(item));
        return villageName is null
            || string.Equals(villageName, selectedName, StringComparison.OrdinalIgnoreCase);
    }

    // Updates the auto-loop construction timer (build-queue remaining) to reflect a specific village's
    // status, then refreshes the group indicators. Used on village switch/view so the Construction
    // group's timer shows the selected village (timers differ per village). Pass null to clear (unknown).
    private void ApplyConstructionTimerFromStatus(VillageStatus? status)
    {
        _buildQueueActiveCount = status?.ActiveBuildCount ?? 0;
        _buildQueueRemainingSeconds = status?.BuildQueueRemainingSeconds ?? -1;
        UpdateAutomationLoopRunningIndicators();
    }

    // Loads the selected village's per-village auto-loop group toggles into the Dashboard list (or the
    // global default when the village has no override). Does not persist.
    private void ApplyAutomationLoopGroupsForSelectedVillage()
    {
        var info = GetSelectedVillageKeyInfoOrNull();
        if (info is null)
        {
            return;
        }

        var groups = _villageSettingsStore.GetEnabledGroups(info.Key) ?? _defaultEnabledGroupKeys;

        _suppressAutomationLoopConfigWrite = true;
        try
        {
            foreach (var option in _automationLoopTasks)
            {
                option.IsEnabled = groups.Contains(option.TaskName, StringComparer.OrdinalIgnoreCase);
            }

            RefreshAutomationLoopDashboardUi();
        }
        finally
        {
            _suppressAutomationLoopConfigWrite = false;
        }
    }

    // Persists the current Dashboard group toggles as the selected village's per-village override.
    private void SaveAutomationLoopGroupsForSelectedVillage()
    {
        var info = GetSelectedVillageKeyInfoOrNull();
        if (info is null)
        {
            return;
        }

        var enabled = _automationLoopTasks
            .Where(item => item.IsEnabled)
            .Select(item => item.TaskName)
            .ToList();
        _villageSettingsStore.SetEnabledGroups(info, enabled);
    }

    private string? GetSelectedVillageKey()
    {
        var (name, url) = GetSelectedVillageSelectionSnapshot();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return GetVillageKey(url, null, null, name);
    }

    // Caches the latest read for a village (keyed by name). Merges so a resource-only read (no buildings)
    // keeps the buildings/resource-fields from a prior fuller read — the dropdown can then still show a
    // village's buildings even after a lightweight refresh updated only its resources.
    private void CacheVillageStatus(VillageStatus status, string? villageNameOverride = null)
    {
        var name = NormalizeVillageName(villageNameOverride) ?? NormalizeVillageName(status.ActiveVillage);
        if (name is null)
        {
            return;
        }

        // A "full" read brings buildings (or resource fields); a lightweight resource refresh does not.
        // Persist only on full reads so the durable structure is saved without thrashing the file every
        // 16s; lighter refreshes still update memory.
        var isFullRead = status.Buildings is { Count: > 0 } || status.ResourceFields is { Count: > 0 };

        if (_villageStatusCacheByName.TryGetValue(name, out var existing))
        {
            if ((status.Buildings is null || status.Buildings.Count == 0) && existing.Buildings is { Count: > 0 })
            {
                status = status with { Buildings = existing.Buildings };
            }

            if ((status.ResourceFields is null || status.ResourceFields.Count == 0) && existing.ResourceFields is { Count: > 0 })
            {
                status = status with { ResourceFields = existing.ResourceFields };
            }
        }

        _villageStatusCacheByName[name] = status;

        if (isFullRead)
        {
            _villageCacheStore.Save(_villageStatusCacheByName);
        }

        // Repaint the Dashboard village-list overview (Buildings/Troops slots) from the refreshed cache.
        if (Dispatcher.CheckAccess())
        {
            RefreshVillageActivityIndicatorsOnDashboard();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(RefreshVillageActivityIndicatorsOnDashboard);
        }
    }

    // Loads the per-village buildings/resource-field cache persisted for the active account so a village
    // scanned in a previous session is remembered immediately (dropdown shows its buildings without a
    // fresh scan). Live refreshes then update both the UI and the saved file.
    private void LoadVillageCacheForActiveAccount()
    {
        AppendLog("[LoadVillageCacheForActiveAccount] Started");       
        try
        {
            var loaded = _villageCacheStore.Load();
            _villageStatusCacheByName.Clear();
            foreach (var pair in loaded)
            {
                var name = NormalizeVillageName(pair.Key);
                if (name is not null && pair.Value is not null)
                {
                    _villageStatusCacheByName[name] = pair.Value;
                }
            }

            if (_villageStatusCacheByName.Count > 0)
            {
                AppendLog($"Loaded cached buildings/fields for {_villageStatusCacheByName.Count} village(s).");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load village cache: {ex.Message}");
        }
        AppendLog("[LoadVillageCacheForActiveAccount] Completed");
    }

    // True when a freshly read status belongs to the village the user currently has selected (or the
    // village is indeterminate — then we don't suppress, to avoid blanking the UI). Name-based.
    private bool IsStatusForSelectedVillage(VillageStatus status)
    {
        var statusName = NormalizeVillageName(status.ActiveVillage);
        var selectedName = NormalizeVillageName(GetSelectedVillageName());
        if (statusName is null || selectedName is null)
        {
            return true;
        }

        return string.Equals(statusName, selectedName, StringComparison.OrdinalIgnoreCase);
    }

    private void SetActiveWorkingVillage(string? villageKey, string? villageName)
    {
        // The village NAME is the primary identity (it's what reads/dropdown agree on). Ignore empty or
        // "Unknown village" so a bad/transient read never clears a good marker.
        var name = string.IsNullOrWhiteSpace(villageName) ? null : villageName.Trim();
        if (string.Equals(name, "Unknown village", StringComparison.OrdinalIgnoreCase))
        {
            name = null;
        }

        // Key is best-effort (used as a secondary match); resolve from the list by name when not given.
        var resolvedKey = !string.IsNullOrWhiteSpace(villageKey)
            ? villageKey
            : ResolveVillageKeyByName(name);

        if (name is null && string.IsNullOrWhiteSpace(resolvedKey))
        {
            return;
        }

        if (name is not null)
        {
            _activeWorkingVillageName = name;
        }

        if (!string.IsNullOrWhiteSpace(resolvedKey))
        {
            _activeWorkingVillageKey = resolvedKey;
        }

        ApplyActiveVillageHighlight();
        UpdateSwitchVillageButtonHighlight();
    }

    private void SetActiveWorkingVillageFromStatus(VillageStatus status)
    {
        // Name is the identity; SetActiveWorkingVillage resolves the key from the village list by name.
        SetActiveWorkingVillage(null, status.ActiveVillage);
    }

    private string? ResolveVillageKeyByName(string? villageName)
    {
        if (string.IsNullOrWhiteSpace(villageName))
        {
            return null;
        }

        var items = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
            ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>);
        var match = items?.FirstOrDefault(item =>
            string.Equals(item.Name?.Trim(), villageName.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null ? null : GetVillageKey(match);
    }

    // Called when a queue item begins executing so the "active village" border follows the bot as it
    // rotates between villages. Village-less (global) tasks leave the indicator unchanged.
    private void MarkActiveWorkingVillageFromQueueItem(QueueItem item)
    {
        var key = GetQueueItemVillageKey(item);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var name = GetQueueItemVillageName(item);
        if (Dispatcher.CheckAccess())
        {
            SetActiveWorkingVillage(key, name);
        }
        else
        {
            _ = Dispatcher.BeginInvoke(() => SetActiveWorkingVillage(key, name));
        }
    }

    private void ApplyActiveVillageHighlight()
    {
        if (DashboardVillageList.ItemsSource is not IEnumerable<VillageSelectionItem> items)
        {
            return;
        }

        foreach (var item in items)
        {
            // Name is the primary match (robust to key/url differences between reads and the list);
            // key is a secondary match.
            var nameMatch = !string.IsNullOrWhiteSpace(_activeWorkingVillageName)
                && string.Equals(item.Name?.Trim(), _activeWorkingVillageName.Trim(), StringComparison.OrdinalIgnoreCase);
            var keyMatch = !string.IsNullOrWhiteSpace(_activeWorkingVillageKey)
                && string.Equals(GetVillageKey(item), _activeWorkingVillageKey, StringComparison.OrdinalIgnoreCase);
            item.IsActiveWorkingVillage = nameMatch || keyMatch;
        }
    }

    // The Switch village button lights up green (same style as Start bot) when the selected/viewed
    // village differs from the village the bot is actually working in — a clear cue that pressing it
    // will move the bot to the selected village.
    private void UpdateSwitchVillageButtonHighlight()
    {
        if (SwitchVillageButton is null)
        {
            return;
        }

        // Compare by NAME so key/url differences between the dropdown and the read status don't make the
        // button look permanently "switchable" when the selected and active village are actually the same.
        var selectedName = GetSelectedVillageName();
        var differs = !string.IsNullOrWhiteSpace(selectedName)
            && !string.IsNullOrWhiteSpace(_activeWorkingVillageName)
            && !string.Equals(selectedName.Trim(), _activeWorkingVillageName.Trim(), StringComparison.OrdinalIgnoreCase);

        if (differs)
        {
            // Soft/tinted green, matching the Start bot button (not a solid green fill).
            SwitchVillageButton.Background = new System.Windows.Media.SolidColorBrush(ThemeColors.Get("SuccessBgBrush"));
            SwitchVillageButton.BorderBrush = new System.Windows.Media.SolidColorBrush(ThemeColors.Get("SuccessBorderBrush"));
            SwitchVillageButton.Foreground = new System.Windows.Media.SolidColorBrush(ThemeColors.Get("SuccessTextBrush"));
        }
        else
        {
            SwitchVillageButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            SwitchVillageButton.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            SwitchVillageButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
    }

    // Dropdown selection = pure view/queue context. Repaints Buildings + resources from the per-village
    // cache (no navigation), keeps the queue filtered to this village, and re-targets new queue items.
    private void ShowSelectedVillageFromCache(VillageSelectionItem selected)
    {
        if (selected is null || string.IsNullOrWhiteSpace(selected.Name)
            || string.Equals(selected.Name, "-", StringComparison.Ordinal))
        {
            return;
        }

        // The optimistic "just queued" per-slot echoes are keyed by slot only and belong to the village
        // they were queued from. Drop them when the viewed village changes so they don't paint the same
        // slot numbers as pending in another village (the queue, filtered per village, remains the truth).
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();

        var name = NormalizeVillageName(selected.Name);
        if (name is not null && _villageStatusCacheByName.TryGetValue(name, out var cached))
        {
            ApplyResourceRowsAndVillageStatus(cached, includeQueuedTargets: true);
            // Storage bars must follow the selected village too (they were staying on the previous one).
            _resourcesViewModel.ApplyStorageForecasts(cached);
            _lastBuildingStatus = cached;
            PopulateBuildingsTab(cached);
            BuildingsInfoTextBlock.Text = _buildingsViewModel.DescribeLoadedSlots($"selected village '{selected.Name}'");
        }
        else
        {
            // No data for this village yet — clear the detail tabs so we never show ANOTHER village's
            // buildings/resources (the "same status for both villages" bug). Load it with Switch village.
            ClearVillageDetailUiForUncachedSelection(selected.Name);
        }

        // Keep the queue view in sync with the selected village.
        SyncQueueVillagePicker(selected);
        RefreshQueueUi();
        // Show this village's auto-loop group toggles + construction timer.
        ApplyAutomationLoopGroupsForSelectedVillage();
        ApplyConstructionTimerFromStatus(
            name is not null && _villageStatusCacheByName.TryGetValue(name, out var timerStatus) ? timerStatus : null);
        // Light up Switch village when the selected village differs from the one the bot works in.
        UpdateSwitchVillageButtonHighlight();
    }

    // Clears the buildings/resources detail for a selected village that has no cached read yet, so stale
    // data from a different village is never shown for it.
    private void ClearVillageDetailUiForUncachedSelection(string villageName)
    {
        _lastBuildingStatus = null;
        _buildingRows.Clear();
        SetResourceRows([]);
        // Empty the storage bars (show "-") rather than leaving another village's values up.
        _resourcesViewModel.ResetStorageForecasts();
        BuildingsInfoTextBlock.Text = $"No data for '{villageName}' yet. Press \"Switch village\" to load it.";
    }

    // The explicit "Switch village" control: makes the bot work in the selected village. When idle it
    // navigates the browser there and reads fresh status; when automation is running it seeds the
    // rotation so the loop prioritizes this village next (the loop owns the browser, so we don't
    // navigate from the UI). Either way the active-village indicator updates.
    private async Task SwitchToActiveVillageAsync(VillageSelectionItem selected)
    {
        if (selected is null || string.IsNullOrWhiteSpace(selected.Name)
            || string.Equals(selected.Name, "-", StringComparison.Ordinal))
        {
            return;
        }

        if (BlockIfSessionSleeping("Switch village"))
        {
            return;
        }

        var key = GetVillageKey(selected);

        // Prioritize this village in both runners so automation works there next.
        _autoQueueRotationVillageKey = key;
        _continuousConstructionRotationVillageKey = key;
        SetActiveWorkingVillage(key, selected.Name);

        if (IsExecutionActiveForVillageChange())
        {
            // The running loop owns the browser — hand it a pending switch it performs between tasks, so
            // the browser actually moves to this village (not just prioritized in the queue rotation).
            lock (_pendingSwitchVillageLock)
            {
                _pendingSwitchVillageName = selected.Name;
                _pendingSwitchVillageUrl = selected.Url;
            }

            // Wake the continuous loop if it is idle-waiting so the switch happens as soon as possible
            // (it is still honored only between tasks, never mid-task, to avoid breaking a running action).
            System.Threading.Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
            AppendLog($"Switch village: moving the bot to '{selected.Name}' as soon as the current action finishes.");
            RefreshQueueUi();
            return;
        }

        if (!_isLoggedIn || !_browserSessionLikelyOpen)
        {
            AppendLog($"Switch village to '{selected.Name}' set; will apply on next login/run.");
            return;
        }

        var operationToken = _loopController.StartVillageSwitch("switch-village");
        var operationId = BeginOperation("SwitchVillage");
        var operationSw = Stopwatch.StartNew();
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog($"[{operationId}] INFO switch village to '{selected.Name}'");

            var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: false, forceCurrentVillage: false);

            ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
            _resourcesViewModel.ApplyStorageForecasts(status);
            _lastBuildingStatus = status;
            PopulateBuildingsTab(status);
            CacheVillageStatus(status, selected.Name);

            BuildingsInfoTextBlock.Text = _buildingsViewModel.DescribeLoadedSlots($"active village '{selected.Name}'");
            SetTribeText(status.Tribe);
            VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
            SyncDashboardVillageUiFromVillages(status.Villages, status.ActiveVillage, selected.Name);
            SetActiveWorkingVillage(key, selected.Name);
            ApplyAutomationLoopGroupsForSelectedVillage();
            ApplyConstructionTimerFromStatus(status);
            await RefreshResourceSnapshotForUiAsync(options, operationToken);

            CompleteOperation(operationId, operationSw, $"Switched to '{selected.Name}' and UI refreshed.");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{operationId}] INFO canceled.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }
}
