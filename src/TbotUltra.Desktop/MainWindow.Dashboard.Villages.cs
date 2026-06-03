using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

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

        var items = BuildMergedVillageSelectionItems(villages);
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

    private void UpdateDashboardVillageList(IReadOnlyList<Village> villages)
    {
        ApplyDashboardVillageListItems(BuildMergedVillageSelectionItems(villages));
    }

    private void RefreshVillagePickerFromVillages(IReadOnlyList<Village> villages, string? preferredVillageName)
    {
        ApplyVillagePickerItems(BuildMergedVillageSelectionItems(villages), preferredVillageName, null);
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
            .ThenByDescending(item => item.Population ?? -1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private List<VillageSelectionItem> BuildMergedVillageSelectionItems(IReadOnlyList<Village> villages)
    {
        var existingVillageData = BuildExistingVillageSelectionLookup();

        return villages
            .Where(village => !string.IsNullOrWhiteSpace(village.Name))
            .Select(village =>
            {
                existingVillageData.TryGetValue(village.Name!, out var existing);
                return BuildVillageSelectionItem(
                    village.Name!,
                    village.Url,
                    village.IsCapital,
                    village.CoordX,
                    village.CoordY,
                    village.Population,
                    village.CropFields,
                    existing,
                    preferExistingPopulation: true);
            })
            .ToList();
    }

    private List<VillageSelectionItem> BuildMergedVillageSelectionItems(IReadOnlyList<UiSyncVillagePayload> villages)
    {
        var existingVillageData = BuildExistingVillageSelectionLookup();

        return villages
            .Where(village => !string.IsNullOrWhiteSpace(village.Name))
            .Select(village =>
            {
                var name = village.Name!;
                existingVillageData.TryGetValue(name, out var existing);
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
    }

    private Dictionary<string, VillageSelectionItem> BuildExistingVillageSelectionLookup()
    {
        return Enumerable.Empty<VillageSelectionItem>()
            .Concat(VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem> ?? [])
            .Concat(DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem> ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
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

    private void SelectVillageInPicker(string? activeVillageName)
    {
        if (string.IsNullOrWhiteSpace(activeVillageName))
        {
            return;
        }

        if (VillageComboBox.ItemsSource is not IEnumerable<VillageSelectionItem> villages)
        {
            return;
        }

        var selected = villages.FirstOrDefault(v =>
            string.Equals(v.Name, activeVillageName, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return;
        }

        _suppressVillageSelectionChange = true;
        try
        {
            VillageComboBox.SelectedItem = selected;
        }
        finally
        {
            _suppressVillageSelectionChange = false;
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
        if (BlockIfSessionSleeping("Village switch"))
        {
            return;
        }

        _ = SwitchToSelectedVillageAndRefreshAsync(selected);
    }

    private async Task SwitchToSelectedVillageAndRefreshAsync(VillageSelectionItem selectedVillage)
    {
        if (selectedVillage is null)
        {
            return;
        }

        if (BlockIfSessionSleeping("Village switch"))
        {
            return;
        }

        var switchKey = $"{selectedVillage.Name}|{selectedVillage.Url}";
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(_lastVillageSwitchRefreshKey, switchKey, StringComparison.OrdinalIgnoreCase)
            && (now - _lastVillageSwitchRefreshAt).TotalSeconds < 2)
        {
            return;
        }

        _lastVillageSwitchRefreshKey = switchKey;
        _lastVillageSwitchRefreshAt = now;

        if (IsExecutionActiveForVillageChange())
        {
            await StopAndClearForVillageChangeAsync(selectedVillage.Name);
        }

        if (_uiBusy || _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted))
        {
            AppendLog($"Village switch to '{selectedVillage.Name}' skipped because bot is still stopping.");
            return;
        }

        if (!_isLoggedIn || !_browserSessionLikelyOpen)
        {
            return;
        }

        var operationToken = _loopController.StartVillageSwitch("village-switch");
        var operationId = BeginOperation("SwitchVillage");
        var operationSw = Stopwatch.StartNew();
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog($"[{operationId}] INFO switch village to '{selectedVillage.Name}'");

            var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: false, forceCurrentVillage: false);

            ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
            _lastBuildingStatus = status;
            PopulateBuildingsTab(status);

            BuildingsInfoTextBlock.Text = _buildingsViewModel.DescribeLoadedSlots($"selected village '{selectedVillage.Name}'");

            TribeInfoTextBlock.Text = $"Tribe: {status.Tribe}";
            VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
            SyncDashboardVillageUiFromVillages(status.Villages, status.ActiveVillage, selectedVillage.Name);
            await RefreshResourceSnapshotForUiAsync(options, operationToken);

            CompleteOperation(operationId, operationSw, $"Village switched to '{selectedVillage.Name}' and UI refreshed.");
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

    private bool IsExecutionActiveForVillageChange()
    {
        return _uiBusy || _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted);
    }

    private async Task StopAndClearForVillageChangeAsync(string? villageName)
    {
        var label = string.IsNullOrWhiteSpace(villageName) ? "-" : villageName;
        AppendLog($"Village changed to '{label}' while bot is running. Stopping active work and clearing queue.");

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

            await Task.Delay(120);
        }

        try
        {
            _botService.ClearQueue();
            RefreshQueueUi();
            AppendLog($"Queue cleared due to village change to '{label}'.");
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear queue after village change: {ex.Message}");
        }
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

        return BotOptionsPayloadApplier.Apply(source, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TargetVillageName] = selectedName ?? string.Empty,
            [BotOptionPayloadKeys.TargetVillageUrl] = selectedUrl ?? string.Empty,
        });
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
