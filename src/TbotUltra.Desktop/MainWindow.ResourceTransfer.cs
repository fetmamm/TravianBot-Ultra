using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void ApplyResourceTransferVillageItems(IReadOnlyList<VillageSelectionItem> villages)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyResourceTransferVillageItems(villages));
            return;
        }

        _suppressResourceTransferConfigWrite = true;
        try
        {
            var options = LoadBotOptions();
            var targetName = options.ResourceTransferTargetVillageName;
            var selectedSources = options.ResourceTransferSourceVillageNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previousItems = _resourceTransferVillages
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var existing in _resourceTransferVillages)
            {
                existing.PropertyChanged -= ResourceTransferVillage_PropertyChanged;
            }

            _resourceTransferVillages.Clear();
            foreach (var village in villages.Where(village => !string.IsNullOrWhiteSpace(village.Name) && village.Name != "-"))
            {
                var item = new ResourceTransferVillageItem
                {
                    Name = village.Name,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    IsSource = selectedSources.Contains(village.Name),
                };
                if (previousItems.TryGetValue(village.Name, out var previous))
                {
                    item.ApplyResourceStatusFrom(previous);
                }
                item.PropertyChanged += ResourceTransferVillage_PropertyChanged;
                _resourceTransferVillages.Add(item);
            }

            var selectedTarget = ResolveResourceTransferTargetSelection(targetName);
            if (selectedTarget is not null)
            {
                ResourceTransferTargetVillageComboBox.SelectedItem = selectedTarget;
            }

            UpdateResourceTransferTargetSourceState(persist: false);
        }
        finally
        {
            _suppressResourceTransferConfigWrite = false;
        }

        PersistResourceTransferSettings();
        UpdateResourceTransferStatus();
    }

    private void ApplyResourceTransferConfigToUi(BotOptions options)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyResourceTransferConfigToUi(options));
            return;
        }

        _suppressResourceTransferConfigWrite = true;
        try
        {
            SelectComboValue(ResourceTransferSourceThresholdComboBox, options.ResourceTransferSourceThresholdPercent);
            SelectComboValue(ResourceTransferSourceKeepComboBox, options.ResourceTransferSourceKeepPercent);
            SelectComboValue(ResourceTransferTargetFillComboBox, options.ResourceTransferTargetFillPercent);
            ResourceTransferWoodCheckBox.IsChecked = options.ResourceTransferSendWood;
            ResourceTransferClayCheckBox.IsChecked = options.ResourceTransferSendClay;
            ResourceTransferIronCheckBox.IsChecked = options.ResourceTransferSendIron;
            ResourceTransferCropCheckBox.IsChecked = options.ResourceTransferSendCrop;

            var target = ResolveResourceTransferTargetSelection(options.ResourceTransferTargetVillageName);
            if (target is not null)
            {
                ResourceTransferTargetVillageComboBox.SelectedItem = target;
            }

            UpdateResourceTransferTargetSourceState(persist: false);

            var selectedSources = options.ResourceTransferSourceVillageNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var village in _resourceTransferVillages)
            {
                village.IsSource = selectedSources.Contains(village.Name);
            }

            UpdateResourceTransferTargetSourceState(persist: false);
        }
        finally
        {
            _suppressResourceTransferConfigWrite = false;
        }

        UpdateResourceTransferStatus();
    }

    private static void SelectComboValue(ComboBox comboBox, int value)
    {
        comboBox.SelectedValue = value.ToString();
        if (comboBox.SelectedItem is null && comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static int ReadComboPercent(ComboBox comboBox, int fallback)
    {
        if (comboBox.SelectedValue is string raw && int.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        if (comboBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && int.TryParse(tag, out parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private ResourceTransferVillageItem? ResolveResourceTransferTargetSelection(string? configuredTargetName)
    {
        if (!string.IsNullOrWhiteSpace(configuredTargetName))
        {
            var configuredTarget = _resourceTransferVillages.FirstOrDefault(item =>
                string.Equals(item.Name, configuredTargetName, StringComparison.OrdinalIgnoreCase));
            if (configuredTarget is not null)
            {
                return configuredTarget;
            }
        }

        if (VillageComboBox.ItemsSource is IEnumerable<VillageSelectionItem> villages)
        {
            var capital = villages.FirstOrDefault(village => village.IsCapital);
            if (capital is not null)
            {
                return _resourceTransferVillages.FirstOrDefault(item =>
                    string.Equals(item.Name, capital.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        return _resourceTransferVillages.FirstOrDefault();
    }

    private Dictionary<string, string> BuildResourceTransferPayload()
    {
        var target = ResourceTransferTargetVillageComboBox.SelectedItem as ResourceTransferVillageItem;
        var sourceNames = _resourceTransferVillages
            .Where(item => item.IsSource && !item.IsTarget)
            .Select(item => item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ResourceTransferPayload(
            Enabled: true,
            TargetVillageName: target?.Name ?? string.Empty,
            SourceVillageNames: sourceNames,
            SourceThresholdPercent: ReadComboPercent(ResourceTransferSourceThresholdComboBox, 50),
            SourceKeepPercent: ReadComboPercent(ResourceTransferSourceKeepComboBox, 5),
            TargetFillPercent: ReadComboPercent(ResourceTransferTargetFillComboBox, 90),
            SendWood: ResourceTransferWoodCheckBox.IsChecked == true,
            SendClay: ResourceTransferClayCheckBox.IsChecked == true,
            SendIron: ResourceTransferIronCheckBox.IsChecked == true,
            SendCrop: ResourceTransferCropCheckBox.IsChecked == true).ToDictionary();
    }

    private void PersistResourceTransferSettings()
    {
        if (_suppressResourceTransferConfigWrite || _botConfigStore is null)
        {
            return;
        }

        var payload = BuildResourceTransferPayload();
        var config = _botConfigStore.Load();
        config[BotOptionPayloadKeys.ResourceTransferTargetVillageName] = payload[BotOptionPayloadKeys.ResourceTransferTargetVillageName];
        config[BotOptionPayloadKeys.ResourceTransferSourceVillageNames] = new JsonArray(
            payload[BotOptionPayloadKeys.ResourceTransferSourceVillageNames]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => JsonValue.Create(name)!)
                .ToArray());
        config[BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent] = ReadComboPercent(ResourceTransferSourceThresholdComboBox, 50);
        config[BotOptionPayloadKeys.ResourceTransferSourceKeepPercent] = ReadComboPercent(ResourceTransferSourceKeepComboBox, 5);
        config[BotOptionPayloadKeys.ResourceTransferTargetFillPercent] = ReadComboPercent(ResourceTransferTargetFillComboBox, 90);
        config[BotOptionPayloadKeys.ResourceTransferSendWood] = ResourceTransferWoodCheckBox.IsChecked == true;
        config[BotOptionPayloadKeys.ResourceTransferSendClay] = ResourceTransferClayCheckBox.IsChecked == true;
        config[BotOptionPayloadKeys.ResourceTransferSendIron] = ResourceTransferIronCheckBox.IsChecked == true;
        config[BotOptionPayloadKeys.ResourceTransferSendCrop] = ResourceTransferCropCheckBox.IsChecked == true;
        _botConfigStore.Save(config);
    }

    private void ResourceTransferSetting_Changed(object sender, RoutedEventArgs e)
    {
        PersistResourceTransferSettings();
        UpdateResourceTransferStatus();
    }

    private void ResourceTransferSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ResourceTransferTargetVillageComboBox))
        {
            UpdateResourceTransferTargetSourceState(persist: true);
            return;
        }

        PersistResourceTransferSettings();
        UpdateResourceTransferStatus();
    }

    private void ResourceTransferVillage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ResourceTransferVillageItem.IsSource), StringComparison.Ordinal))
        {
            PersistResourceTransferSettings();
            UpdateResourceTransferStatus();
        }
    }

    private void UpdateResourceTransferTargetSourceState(bool persist)
    {
        var target = ResourceTransferTargetVillageComboBox.SelectedItem as ResourceTransferVillageItem;
        var targetName = target?.Name ?? string.Empty;
        var wasSuppressed = _suppressResourceTransferConfigWrite;
        _suppressResourceTransferConfigWrite = true;
        try
        {
            foreach (var village in _resourceTransferVillages)
            {
                village.IsTarget = !string.IsNullOrWhiteSpace(targetName)
                    && string.Equals(village.Name, targetName, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _suppressResourceTransferConfigWrite = wasSuppressed;
        }

        if (persist)
        {
            PersistResourceTransferSettings();
        }

        UpdateResourceTransferStatus();
    }

    private void UpdateResourceTransferStatus()
    {
        var canRun = CanRunResourceTransfer(out var reason);
        ResourceTransferStatusTextBlock.Text = canRun ? "Ready." : reason;
        ResourceTransferStatusTextBlock.Foreground = canRun ? Brushes.SeaGreen : Brushes.DarkOrange;
        ResourceTransferQueueNowButton.IsEnabled = canRun && !_uiBusy && !_resourceTransferScanRunning;
        ResourceTransferScanVillagesButton.IsEnabled = !IsSessionSleeping && !_uiBusy && !_resourceTransferScanRunning && _resourceTransferVillages.Count > 0;

        if (!canRun && _resourceTransferVillages.Count > 0)
        {
            DisableResourceTransferLoopGroup(reason);
        }
    }

    private void DisableResourceTransferLoopGroup(string reason)
    {
        var groupKey = QueueGroupCatalog.GetKey(QueueGroup.ResourceTransfer);
        var item = _automationLoopTasks.FirstOrDefault(option =>
            string.Equals(option.TaskName, groupKey, StringComparison.OrdinalIgnoreCase));
        if (item is null || !item.IsEnabled)
        {
            return;
        }

        item.IsEnabled = false;
        item.StateText = "Disabled";
        item.DetailText = reason;
        item.RemainingSeconds = null;
        item.IsBlocked = true;
        item.BlockedText = "Setup needed";
        UpdateAutomationLoopSummaryText();
        PersistAutomationLoopTasksToConfig();
    }

    private void QueueResourceTransferNowButton_Click(object sender, RoutedEventArgs e)
    {
        PersistResourceTransferSettings();
        if (!CanRunResourceTransfer(out var reason))
        {
            AppendLog($"Resource transfer not queued: {reason}");
            UpdateAutomationLoopRunningIndicators();
            return;
        }

        var payload = BuildResourceTransferPayload();
        _botService.Enqueue("send_resources_between_villages", payload, priority: 5, maxRetries: 0);
        RefreshQueueUi();
        TriggerQueueAutoRunFromEnqueue();
        AppendLog("Resource transfer queued.");
    }

    private async void ResourceTransferScanVillagesButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(ResourceTransferScanVillagesButtonClickAsync);

    private async Task ResourceTransferScanVillagesButtonClickAsync()
    {
        if (BlockIfSessionSleeping("Resource transfer village scan"))
        {
            return;
        }

        if (_resourceTransferScanRunning || _uiBusy)
        {
            return;
        }

        var selectedVillageName = GetSelectedVillageName();
        var selectedVillageUrl = GetSelectedVillageUrl();
        var operationId = BeginOperation("Scan Resource Villages");
        var operationSw = Stopwatch.StartNew();
        _resourceTransferScanRunning = true;
        var operationToken = _loopController.StartOperation("operation");
        ToggleUiBusy(true);
        UpdateResourceTransferStatus();
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog("Resource transfer village scan started.");
            var statuses = await _botService.ReadAllVillageResourceStatusesAsync(
                options,
                AppendLog,
                selectedVillageName,
                selectedVillageUrl,
                operationToken);
            ApplyResourceTransferVillageResourceStatuses(statuses);
            CompleteOperation(operationId, operationSw, $"Resource transfer village scan completed: {statuses.Count} village(s).");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Resource transfer village scan paused.";
            AppendLog("Resource transfer village scan paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            _resourceTransferScanRunning = false;
            ToggleUiBusy(false);
            UpdateResourceTransferStatus();
            DisposeOperationCts();
        }
    }

    private void ApplyResourceTransferVillageResourceStatuses(IEnumerable<VillageStatus> statuses)
    {
        foreach (var status in statuses)
        {
            ApplyResourceTransferVillageResourceStatus(status);
        }
    }

    private void ApplyResourceTransferVillageResourceStatus(VillageStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyResourceTransferVillageResourceStatus(status));
            return;
        }

        var item = _resourceTransferVillages.FirstOrDefault(village =>
            string.Equals(village.Name, status.ActiveVillage, StringComparison.OrdinalIgnoreCase));
        item?.ApplyResourceStatus(status);
    }

    private void TickResourceTransferVillageForecasts()
    {
        foreach (var village in _resourceTransferVillages)
        {
            village.TickResourceForecasts();
        }
    }

    private bool CanRunResourceTransfer(out string reason)
    {
        reason = string.Empty;
        if (_resourceTransferVillages.Count < 2)
        {
            reason = "Requires at least 2 villages.";
            return false;
        }

        if (ResourceTransferTargetVillageComboBox.SelectedItem is not ResourceTransferVillageItem target
            || string.IsNullOrWhiteSpace(target.Name))
        {
            reason = "Select a target village.";
            return false;
        }

        var sourceCount = _resourceTransferVillages.Count(item =>
            item.IsSource
            && !string.Equals(item.Name, target.Name, StringComparison.OrdinalIgnoreCase));
        if (sourceCount <= 0)
        {
            reason = "Select at least one source village.";
            return false;
        }

        if (ResourceTransferWoodCheckBox.IsChecked != true
            && ResourceTransferClayCheckBox.IsChecked != true
            && ResourceTransferIronCheckBox.IsChecked != true
            && ResourceTransferCropCheckBox.IsChecked != true)
        {
            reason = "Select at least one resource.";
            return false;
        }

        return true;
    }

    private bool CanRunResourceTransfer(BotOptions options, out string reason)
    {
        reason = string.Empty;
        var villageCount = _resourceTransferVillages.Count;
        if (villageCount < 2)
        {
            reason = "Requires at least 2 villages.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.ResourceTransferTargetVillageName))
        {
            reason = "Select a target village.";
            return false;
        }

        var sourceCount = options.ResourceTransferSourceVillageNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(name => !string.Equals(name, options.ResourceTransferTargetVillageName, StringComparison.OrdinalIgnoreCase));
        if (sourceCount <= 0)
        {
            reason = "Select at least one source village.";
            return false;
        }

        if (!options.ResourceTransferSendWood
            && !options.ResourceTransferSendClay
            && !options.ResourceTransferSendIron
            && !options.ResourceTransferSendCrop)
        {
            reason = "Select at least one resource.";
            return false;
        }

        return true;
    }
}
