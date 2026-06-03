using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void ApplyReinforcementVillageItems(IReadOnlyList<VillageSelectionItem> villages)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyReinforcementVillageItems(villages));
            return;
        }

        _suppressReinforcementConfigWrite = true;
        try
        {
            var options = LoadBotOptions();
            var targetName = options.ReinforcementsTargetVillageName;
            var selectedSources = options.ReinforcementsSourceVillageNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in _reinforcementVillages)
            {
                existing.PropertyChanged -= ReinforcementVillage_PropertyChanged;
            }

            _reinforcementVillages.Clear();
            foreach (var village in villages.Where(village => !string.IsNullOrWhiteSpace(village.Name) && village.Name != "-"))
            {
                var item = new ReinforcementVillageItem
                {
                    Name = village.Name,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    IsSource = selectedSources.Contains(village.Name),
                };
                item.PropertyChanged += ReinforcementVillage_PropertyChanged;
                _reinforcementVillages.Add(item);
            }

            var selectedTarget = ResolveReinforcementTargetSelection(targetName);
            if (selectedTarget is not null)
            {
                ReinforcementTargetVillageComboBox.SelectedItem = selectedTarget;
            }

            UpdateReinforcementTargetSourceState(persist: false);
        }
        finally
        {
            _suppressReinforcementConfigWrite = false;
        }

        PersistReinforcementSettings();
        UpdateReinforcementVillageTroopSummaries();
        UpdateReinforcementStatus();
    }

    private void ApplyReinforcementConfigToUi(BotOptions options)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyReinforcementConfigToUi(options));
            return;
        }

        _suppressReinforcementConfigWrite = true;
        try
        {
            _configuredReinforcementTroopRules = NormalizeReinforcementRules(options.ReinforcementsTroopRules);
            var knownTribe = ResolveKnownReinforcementTribe();
            if (!string.IsNullOrWhiteSpace(knownTribe))
            {
                RefreshReinforcementTroopRules(knownTribe, _configuredReinforcementTroopRules);
            }
            else
            {
                ClearReinforcementTroopRules();
            }

            var target = ResolveReinforcementTargetSelection(options.ReinforcementsTargetVillageName);
            if (target is not null)
            {
                ReinforcementTargetVillageComboBox.SelectedItem = target;
            }

            var selectedSources = options.ReinforcementsSourceVillageNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var village in _reinforcementVillages)
            {
                village.IsSource = selectedSources.Contains(village.Name);
            }

            UpdateReinforcementTargetSourceState(persist: false);
            UpdateReinforcementVillageTroopSummaries();
        }
        finally
        {
            _suppressReinforcementConfigWrite = false;
        }

        UpdateReinforcementStatus();
    }

    private void RefreshReinforcementTroopRules(string? tribe, IReadOnlyList<ReinforcementTroopRule>? configuredRules = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RefreshReinforcementTroopRules(tribe, configuredRules));
            return;
        }

        if (!_isLoggedIn || !IsKnownReinforcementTribe(tribe))
        {
            ClearReinforcementTroopRules();
            return;
        }

        var configuredSource = configuredRules ?? (_reinforcementTroopRules.Count > 0
            ? _reinforcementTroopRules.Select(rule => rule.ToRule()).ToList()
            : _configuredReinforcementTroopRules);
        var configured = configuredSource
            .Where(rule => !string.IsNullOrWhiteSpace(rule.TroopType))
            .GroupBy(rule => rule.TroopType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Normalize(), StringComparer.OrdinalIgnoreCase);

        foreach (var rule in _reinforcementTroopRules)
        {
            rule.PropertyChanged -= ReinforcementTroopRule_PropertyChanged;
        }

        _reinforcementTroopRules.Clear();
        foreach (var troopType in TroopCatalog.ResolveTroopTypesForTribe(tribe))
        {
            configured.TryGetValue(troopType, out var configuredRule);
            var item = new ReinforcementTroopRuleItem
            {
                SourceVillageName = configuredRule?.SourceVillageName ?? string.Empty,
                TroopType = troopType,
                IsEnabled = configuredRule?.IsEnabled ?? false,
                AmountMode = configuredRule?.AmountMode ?? "fixed",
                Amount = configuredRule?.Amount ?? 1,
            };
            item.PropertyChanged += ReinforcementTroopRule_PropertyChanged;
            _reinforcementTroopRules.Add(item);
        }

        UpdateReinforcementTroopSummary();
    }

    private void ClearReinforcementTroopRules()
    {
        foreach (var rule in _reinforcementTroopRules)
        {
            rule.PropertyChanged -= ReinforcementTroopRule_PropertyChanged;
        }

        _reinforcementTroopRules.Clear();
        UpdateReinforcementTroopSummary();
    }

    private ReinforcementVillageItem? ResolveReinforcementTargetSelection(string? configuredTargetName)
    {
        if (!string.IsNullOrWhiteSpace(configuredTargetName))
        {
            var configuredTarget = _reinforcementVillages.FirstOrDefault(item =>
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
                return _reinforcementVillages.FirstOrDefault(item =>
                    string.Equals(item.Name, capital.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        return _reinforcementVillages.FirstOrDefault();
    }

    private Dictionary<string, string> BuildReinforcementPayload()
    {
        var target = ReinforcementTargetVillageComboBox.SelectedItem as ReinforcementVillageItem;
        var sourceNames = _reinforcementVillages
            .Where(item => item.IsSource && !item.IsTarget)
            .Select(item => item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rules = BuildReinforcementRulesForRun();

        return new ReinforcementsPayload(
            Enabled: true,
            TargetVillageName: target?.Name ?? string.Empty,
            SourceVillageNames: sourceNames,
            TroopRules: rules).ToDictionary();
    }

    private void PersistReinforcementSettings()
    {
        if (_suppressReinforcementConfigWrite || _botConfigStore is null)
        {
            return;
        }

        var payload = BuildReinforcementPayload();
        var config = _botConfigStore.Load();
        var rules = BuildReinforcementRulesForSave();
        config[BotOptionPayloadKeys.ReinforcementsTargetVillageName] = payload[BotOptionPayloadKeys.ReinforcementsTargetVillageName];
        config[BotOptionPayloadKeys.ReinforcementsSourceVillageNames] = new JsonArray(
            payload[BotOptionPayloadKeys.ReinforcementsSourceVillageNames]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => JsonValue.Create(name)!)
                .ToArray());
        config[BotOptionPayloadKeys.ReinforcementsTroopRules] = new JsonArray(
            rules
                .Select(rule => new JsonObject
                {
                    ["accountName"] = rule.AccountName,
                    ["sourceVillageName"] = rule.SourceVillageName,
                    ["troopType"] = rule.TroopType,
                    ["amountMode"] = rule.AmountMode,
                    ["amount"] = rule.Amount,
                    ["isEnabled"] = rule.IsEnabled,
                })
                .Cast<JsonNode>()
                .ToArray());
        _configuredReinforcementTroopRules = NormalizeReinforcementRules(rules);
        _botConfigStore.Save(config);
        UpdateReinforcementVillageTroopSummaries();
    }

    private void ReinforcementSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ReinforcementTargetVillageComboBox))
        {
            UpdateReinforcementTargetSourceState(persist: true);
            return;
        }

        PersistReinforcementSettings();
        UpdateReinforcementStatus();
    }

    private void ReinforcementVillage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ReinforcementVillageItem.IsSource), StringComparison.Ordinal))
        {
            PersistReinforcementSettings();
            UpdateReinforcementStatus();
        }
    }

    private void ReinforcementTroopRule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        PersistReinforcementSettings();
        UpdateReinforcementStatus();
        UpdateReinforcementTroopSummary();
    }

    private void ChooseReinforcementVillageTroopsButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ReinforcementVillageItem village)
        {
            return;
        }

        var knownTribe = ResolveKnownReinforcementTribe();
        if (string.IsNullOrWhiteSpace(knownTribe))
        {
            AppDialog.Show(this, "Login first so troop types can be detected.", "Choose troops", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var troopRules = CreateReinforcementTroopRuleItems(knownTribe, village.Name);

        var dialog = new ReinforcementTroopSelectionWindow(troopRules, village.DisplayName)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true)
        {
            SaveReinforcementRulesForVillage(village.Name, troopRules.Select(rule => rule.ToRule()));
            if (dialog.SyncSettingsRequested)
            {
                SyncReinforcementSettingsToOtherVillages(village.Name, troopRules.Select(rule => rule.ToRule()));
            }

            PersistReinforcementSettings();
        }

        UpdateReinforcementStatus();
        UpdateReinforcementTroopSummary();
    }

    private void MarkAllReinforcementTroopsButton_Click(object sender, RoutedEventArgs e)
    {
        var knownTribe = ResolveKnownReinforcementTribe();
        if (string.IsNullOrWhiteSpace(knownTribe))
        {
            AppDialog.Show(this, "Login first so troop types can be detected.", "Mark all", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var villages = _reinforcementVillages
            .Where(village => !string.IsNullOrWhiteSpace(village.Name) && village.Name != "-")
            .ToList();
        if (villages.Count == 0)
        {
            AppDialog.Show(this, "Refresh villages before marking troops.", "Mark all", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = AppDialog.Show(
            this,
            $"Mark every troop type for {villages.Count} village(s) and set mode to All?",
            "Mark all",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var accountName = ResolveActiveReinforcementAccountName();
        var troopTypes = TroopCatalog.ResolveTroopTypesForTribe(knownTribe).ToList();
        foreach (var village in villages)
        {
            if (village.CanSelectAsSource)
            {
                village.IsSource = true;
            }

            var rules = troopTypes.Select(troopType => new ReinforcementTroopRule
            {
                AccountName = accountName,
                SourceVillageName = village.Name,
                TroopType = troopType,
                AmountMode = "all_available",
                Amount = 1,
                IsEnabled = true,
            });
            SaveReinforcementRulesForVillage(village.Name, rules);
        }

        PersistReinforcementSettings();
        UpdateReinforcementStatus();
        UpdateReinforcementTroopSummary();
        AppendLog($"Reinforcements: marked all troop types as All for {villages.Count} village(s).");
    }

    private void UpdateReinforcementTargetSourceState(bool persist)
    {
        var target = ReinforcementTargetVillageComboBox.SelectedItem as ReinforcementVillageItem;
        var targetName = target?.Name ?? string.Empty;
        var wasSuppressed = _suppressReinforcementConfigWrite;
        _suppressReinforcementConfigWrite = true;
        try
        {
            foreach (var village in _reinforcementVillages)
            {
                village.IsTarget = !string.IsNullOrWhiteSpace(targetName)
                    && string.Equals(village.Name, targetName, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _suppressReinforcementConfigWrite = wasSuppressed;
        }

        if (persist)
        {
            PersistReinforcementSettings();
        }

        UpdateReinforcementStatus();
    }

    private void UpdateReinforcementStatus()
    {
        if (ReinforcementStatusTextBlock is null)
        {
            return;
        }

        var canRun = CanRunReinforcements(out var reason);
        ReinforcementStatusTextBlock.Text = canRun ? "Ready." : reason;
        ReinforcementStatusTextBlock.Foreground = canRun ? Brushes.SeaGreen : Brushes.DarkOrange;
        ReinforcementQueueNowButton.IsEnabled = canRun && !_uiBusy;
        ReinforcementRefreshVillagesButton.IsEnabled = !_uiBusy && !IsSessionSleeping;
        ReinforcementMarkAllTroopsButton.IsEnabled = !_uiBusy && !string.IsNullOrWhiteSpace(ResolveKnownReinforcementTribe());
        UpdateReinforcementVillageTroopSummaries();
        UpdateReinforcementTroopSummary();

        if (!canRun && _reinforcementVillages.Count > 0)
        {
            DisableReinforcementLoopGroup(reason);
        }
    }

    private void DisableReinforcementLoopGroup(string reason)
    {
        var groupKey = QueueGroupCatalog.GetKey(QueueGroup.Reinforcements);
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

    private void QueueReinforcementsNowButton_Click(object sender, RoutedEventArgs e)
    {
        PersistReinforcementSettings();
        if (!CanRunReinforcements(out var reason))
        {
            AppendLog($"Reinforcements not queued: {reason}");
            UpdateAutomationLoopRunningIndicators();
            return;
        }

        var payload = BuildReinforcementPayload();
        _botService.Enqueue("send_reinforcements_between_villages", payload, priority: 5, maxRetries: 0);
        RefreshQueueUi();
        TriggerQueueAutoRunFromEnqueue();
        AppendLog("Reinforcements queued.");
    }

    private async void ReinforcementRefreshVillagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockIfSessionSleeping("Refresh reinforcement villages"))
        {
            return;
        }

        if (_uiBusy)
        {
            return;
        }

        var operationId = BeginOperation("Refresh Reinforcement Villages");
        var operationSw = System.Diagnostics.Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        ToggleUiBusy(true);
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog("Reinforcement village refresh started.");
            var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: false, forceCurrentVillage: false);
            SyncDashboardVillageUiFromVillages(status.Villages, status.ActiveVillage);
            CompleteOperation(operationId, operationSw, $"Reinforcement village refresh completed: {status.Villages.Count} village(s).");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Reinforcement village refresh paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
            UpdateReinforcementStatus();
            DisposeOperationCts();
        }
    }

    private bool CanRunReinforcements(out string reason)
    {
        reason = string.Empty;
        if (_reinforcementVillages.Count < 2)
        {
            reason = "Requires at least 2 villages.";
            return false;
        }

        if (ReinforcementTargetVillageComboBox.SelectedItem is not ReinforcementVillageItem target
            || string.IsNullOrWhiteSpace(target.Name))
        {
            reason = "Select a target village.";
            return false;
        }

        var sourceNames = _reinforcementVillages
            .Where(item => item.IsSource && !string.Equals(item.Name, target.Name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceNames.Count <= 0)
        {
            reason = "Select at least one source village.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ResolveKnownReinforcementTribe()))
        {
            reason = "Login to load troop types.";
            return false;
        }

        if (sourceNames.Any(sourceName => !HasEnabledReinforcementRule(GetConfiguredReinforcementRulesForVillage(sourceName))))
        {
            reason = "Choose troops for each source village.";
            return false;
        }

        return true;
    }

    private bool CanRunReinforcements(BotOptions options, out string reason)
    {
        reason = string.Empty;
        if (_reinforcementVillages.Count < 2)
        {
            reason = "Requires at least 2 villages.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.ReinforcementsTargetVillageName))
        {
            reason = "Select a target village.";
            return false;
        }

        var sourceCount = options.ReinforcementsSourceVillageNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(name => !string.Equals(name, options.ReinforcementsTargetVillageName, StringComparison.OrdinalIgnoreCase));
        if (sourceCount <= 0)
        {
            reason = "Select at least one source village.";
            return false;
        }

        var missingTroops = options.ReinforcementsSourceVillageNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !string.Equals(name, options.ReinforcementsTargetVillageName, StringComparison.OrdinalIgnoreCase))
            .Any(sourceName => !HasEnabledReinforcementRule(GetConfiguredReinforcementRulesForVillage(sourceName, options.ReinforcementsTroopRules)));
        if (missingTroops)
        {
            reason = "Choose troops for each source village.";
            return false;
        }

        return true;
    }

    private List<ReinforcementTroopRule> BuildReinforcementRulesForSave()
    {
        return NormalizeReinforcementRules(_configuredReinforcementTroopRules);
    }

    private List<ReinforcementTroopRule> BuildReinforcementRulesForRun()
    {
        var accountName = ResolveActiveReinforcementAccountName();
        var rules = NormalizeReinforcementRules(_configuredReinforcementTroopRules);
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return rules;
        }

        var accountRules = rules
            .Where(rule => string.Equals(rule.AccountName, accountName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return accountRules;
    }

    private ObservableCollection<ReinforcementTroopRuleItem> CreateReinforcementTroopRuleItems(string tribe, string sourceVillageName)
    {
        var configured = GetConfiguredReinforcementRulesForVillage(sourceVillageName)
            .Where(rule => !string.IsNullOrWhiteSpace(rule.TroopType))
            .GroupBy(rule => rule.TroopType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Normalize(), StringComparer.OrdinalIgnoreCase);

        var items = new ObservableCollection<ReinforcementTroopRuleItem>();
        foreach (var troopType in TroopCatalog.ResolveTroopTypesForTribe(tribe))
        {
            configured.TryGetValue(troopType, out var configuredRule);
            items.Add(new ReinforcementTroopRuleItem
            {
                AccountName = ResolveActiveReinforcementAccountName(),
                SourceVillageName = sourceVillageName,
                TroopType = troopType,
                IsEnabled = configuredRule?.IsEnabled ?? false,
                AmountMode = configuredRule?.AmountMode ?? "fixed",
                Amount = configuredRule?.Amount ?? 1,
            });
        }

        return items;
    }

    private void SaveReinforcementRulesForVillage(string sourceVillageName, IEnumerable<ReinforcementTroopRule> rules)
    {
        var accountName = ResolveActiveReinforcementAccountName();
        var updated = _configuredReinforcementTroopRules
            .Where(rule =>
                !string.Equals(rule.AccountName, accountName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(rule.SourceVillageName, sourceVillageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        updated.AddRange(rules.Select(rule => rule with { AccountName = accountName, SourceVillageName = sourceVillageName }));
        _configuredReinforcementTroopRules = NormalizeReinforcementRules(updated);
        UpdateReinforcementVillageTroopSummaries();
    }

    private void SyncReinforcementSettingsToOtherVillages(string sourceVillageName, IEnumerable<ReinforcementTroopRule> rules)
    {
        var targetVillages = _reinforcementVillages
            .Where(village => !string.Equals(village.Name, sourceVillageName, StringComparison.OrdinalIgnoreCase))
            .Where(village => !string.IsNullOrWhiteSpace(village.Name))
            .ToList();
        if (targetVillages.Count == 0)
        {
            AppDialog.Show(this, "There are no other villages to sync.", "Sync settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = AppDialog.Show(
            this,
            $"Sync these troop settings to {targetVillages.Count} other village(s)? Existing troop settings for those villages will be replaced.",
            "Sync settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var sourceRules = rules.Select(rule => rule.Normalize()).ToList();
        foreach (var village in targetVillages)
        {
            SaveReinforcementRulesForVillage(village.Name, sourceRules.Select(rule => rule with { SourceVillageName = village.Name }));
        }
    }

    private IReadOnlyList<ReinforcementTroopRule> GetConfiguredReinforcementRulesForVillage(
        string sourceVillageName,
        IEnumerable<ReinforcementTroopRule>? rules = null)
    {
        var normalizedRules = NormalizeReinforcementRules(rules ?? _configuredReinforcementTroopRules);
        var accountName = ResolveActiveReinforcementAccountName();
        var accountRules = string.IsNullOrWhiteSpace(accountName)
            ? normalizedRules.Where(rule => string.IsNullOrWhiteSpace(rule.AccountName)).ToList()
            : normalizedRules.Where(rule => string.Equals(rule.AccountName, accountName, StringComparison.OrdinalIgnoreCase)).ToList();

        var sourceRules = accountRules
            .Where(rule => string.Equals(rule.SourceVillageName, sourceVillageName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sourceRules.Count > 0)
        {
            return sourceRules;
        }

        return accountRules
            .Where(rule => string.IsNullOrWhiteSpace(rule.SourceVillageName))
            .ToList();
    }

    private static List<ReinforcementTroopRule> NormalizeReinforcementRules(IEnumerable<ReinforcementTroopRule>? rules)
    {
        return (rules ?? [])
            .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.TroopType))
            .Select(rule => rule.Normalize())
            .GroupBy(rule => $"{rule.AccountName}\u001f{rule.SourceVillageName}\u001f{rule.TroopType}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private string ResolveActiveReinforcementAccountName()
    {
        try
        {
            return _accountStore.ActiveAccountName().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private string? ResolveKnownReinforcementTribe()
    {
        if (!_isLoggedIn)
        {
            return null;
        }

        var raw = TribeInfoTextBlock?.Text?.Replace("Tribe:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return IsKnownReinforcementTribe(raw) ? raw : null;
    }

    private static bool IsKnownReinforcementTribe(string? tribe)
    {
        if (string.IsNullOrWhiteSpace(tribe))
        {
            return false;
        }

        var value = tribe.Trim();
        return !string.Equals(value, "-", StringComparison.Ordinal)
            && !string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateReinforcementTroopSummary()
    {
        if (ReinforcementTroopsSummaryTextBlock is null || ReinforcementTroopsDetailTextBlock is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ResolveKnownReinforcementTribe()))
        {
            ReinforcementTroopsSummaryTextBlock.Text = "Login required.";
            ReinforcementTroopsDetailTextBlock.Text = "Login to load troop types, then choose what to send.";
            return;
        }

        var sourceVillages = _reinforcementVillages
            .Where(item => item.IsSource && !item.IsTarget)
            .ToList();
        var configuredSources = sourceVillages
            .Count(village => HasEnabledReinforcementRule(GetConfiguredReinforcementRulesForVillage(village.Name)));
        if (configuredSources == 0)
        {
            ReinforcementTroopsSummaryTextBlock.Text = "No troops selected.";
            ReinforcementTroopsDetailTextBlock.Text = "Use the Troops button on each source village.";
            return;
        }

        ReinforcementTroopsSummaryTextBlock.Text = $"{configuredSources} of {sourceVillages.Count} source village(s) configured.";
        ReinforcementTroopsDetailTextBlock.Text = string.Join(", ", sourceVillages.Select(village =>
            $"{village.Name}: {FormatReinforcementRuleSummary(GetConfiguredReinforcementRulesForVillage(village.Name))}"));
    }

    private void UpdateReinforcementVillageTroopSummaries()
    {
        foreach (var village in _reinforcementVillages)
        {
            if (!village.IsSource || village.IsTarget)
            {
                village.TroopSummaryText = string.Empty;
                continue;
            }

            village.TroopSummaryText = FormatReinforcementRuleSummary(GetConfiguredReinforcementRulesForVillage(village.Name));
        }
    }

    private static string FormatReinforcementRuleSummary(IEnumerable<ReinforcementTroopRule> rules)
    {
        var enabledRules = rules
            .Where(rule => rule.IsEnabled && !string.IsNullOrWhiteSpace(rule.TroopType))
            .Select(rule => rule.Normalize())
            .ToList();
        if (enabledRules.Count == 0)
        {
            return "No troops";
        }

        return string.Join(", ", enabledRules.Select(rule =>
            rule.UsesAllAvailable
                ? $"{rule.TroopType}: all"
                : rule.PercentAvailable is { } percent
                    ? $"{rule.TroopType}: {percent}%"
                    : $"{rule.TroopType}: {rule.Amount}"));
    }

    private static bool HasEnabledReinforcementRule(IEnumerable<ReinforcementTroopRule> rules)
    {
        return rules.Any(rule =>
            rule.IsEnabled
            && !string.IsNullOrWhiteSpace(rule.TroopType)
            && (rule.UsesAllAvailable || rule.UsesPercentAvailable || rule.Amount > 0));
    }
}
