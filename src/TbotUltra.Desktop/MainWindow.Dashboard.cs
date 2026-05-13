using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void SidebarNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        try
        {
            var targetTab = button.Tag?.ToString() switch
            {
                "dashboard" => DashboardTabItem,
                "resources" => ResourcesTabItem,
                "buildings" => BuildingsTabItem,
                "hero" => HeroTabItem,
                "farming" => FarmingTabItem,
                "troops" => TroopsTabItem,
                "npc_trade" => NpcTradeTabItem,
                "queue" => QueueTabItem,
                "logs" => LogsTabItem,
                "inbox" => InboxTabItem,
                _ => DashboardTabItem,
            };

            if (targetTab is not null)
            {
                MainTabControl.SelectedItem = targetTab;
                if (ReferenceEquals(targetTab, FarmingTabItem))
                {
                    RefreshFarmListsItemsControl();
                    SyncFarmingControlsEnabledState();
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Sidebar navigation failed: {ex.Message}");
            MainTabControl.SelectedItem = DashboardTabItem;
        }

        UpdateSidebarSelection(button);
    }

    private void DashboardFunctionListButton_Click(object sender, RoutedEventArgs e)
    {
        var currentByGroup = _automationLoopTasks
            .ToDictionary(item => item.TaskName, item => item, StringComparer.OrdinalIgnoreCase);
        var orderedGroupKeys = QueueGroupCatalog.AllGroups
            .Select(QueueGroupCatalog.GetKey)
            .ToList();
        var selectableGroupKeys = orderedGroupKeys
            .Where(groupKey =>
                !string.Equals(groupKey, QueueGroupCatalog.GetKey(QueueGroup.BreweryCelebration), StringComparison.OrdinalIgnoreCase)
                || IsTeutonsTribe(ResolveStoredTroopTrainingTribe()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var options = orderedGroupKeys
            .Where(selectableGroupKeys.Contains)
            .Select(groupKey =>
            {
                QueueGroupCatalog.TryParse(groupKey, out var group);
                return new DashboardFunctionOption
                {
                    Key = groupKey,
                    Label = currentByGroup.TryGetValue(groupKey, out var current)
                        ? current.Title
                        : QueueGroupCatalog.GetTitle(group),
                    IsVisible = currentByGroup.TryGetValue(groupKey, out var selected) && selected.IsVisible,
                };
            })
            .ToList();

        var dialog = new DashboardFunctionListWindow(options)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var selectedGroupNames = dialog.SelectedVisibility
            .Where(item => item.Value)
            .Select(item => item.Key)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _suppressAutomationLoopConfigWrite = true;
        try
        {
            _automationLoopTasks.Clear();
            foreach (var groupKey in orderedGroupKeys)
            {
                if (!QueueGroupCatalog.TryParse(groupKey, out var group))
                {
                    continue;
                }

                var isSelectable = selectableGroupKeys.Contains(groupKey);
                var isVisible = isSelectable && selectedGroupNames.Contains(groupKey);

                if (currentByGroup.TryGetValue(groupKey, out var existing))
                {
                    _automationLoopTasks.Add(new LoopTaskOption
                    {
                        TaskName = existing.TaskName,
                        Title = existing.Title,
                        Description = existing.Description,
                        IsEnabled = existing.IsEnabled && isVisible,
                        IsVisible = isVisible,
                        StateText = existing.StateText,
                        DetailText = existing.DetailText,
                        QueuedCount = existing.QueuedCount,
                        RemainingSeconds = existing.RemainingSeconds,
                    });
                    continue;
                }

                _automationLoopTasks.Add(new LoopTaskOption
                {
                    TaskName = groupKey,
                    Title = QueueGroupCatalog.GetTitle(group),
                    Description = QueueGroupCatalog.GetDescription(group),
                    IsEnabled = false,
                    IsVisible = isVisible,
                    StateText = "Idle",
                    DetailText = "No queued task.",
                });
            }
        }
        finally
        {
            _suppressAutomationLoopConfigWrite = false;
        }

        UpdateAutomationLoopOrders();
        UpdateAutomationLoopSummaryText();
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void UpdateSidebarSelection(Button selectedButton)
    {
        var navButtons = new[]
        {
            DashboardNavButton,
            ResourcesNavButton,
            BuildingsNavButton,
            HeroNavButton,
            FarmingNavButton,
            TroopsNavButton,
            NpcTradeNavButton,
            QueueNavButton,
            LogsNavButton,
            InboxNavButton,
        };

        foreach (var nav in navButtons)
        {
            nav.BorderThickness = new Thickness(1);
            nav.BorderBrush = new SolidColorBrush(Color.FromRgb(243, 244, 246));
        }

        _activeSidebarButton = selectedButton;
        selectedButton.BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42));
    }

    private void UpdateDashboardVillageList(IReadOnlyList<Village> villages)
    {
        var items = BuildMergedVillageSelectionItems(villages)
            .OrderByDescending(v => v.IsCapital)
            .ThenByDescending(v => v.Population ?? -1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        DashboardVillageList.ItemsSource = items;
    }

    private void RefreshVillagePickerFromVillages(IReadOnlyList<Village> villages, string? preferredVillageName)
    {
        var currentSelectedName = string.IsNullOrWhiteSpace(preferredVillageName)
            ? GetSelectedVillageName()
            : preferredVillageName;

        var items = BuildMergedVillageSelectionItems(villages);

        if (items.Count == 0)
        {
            items.Add(new VillageSelectionItem { Name = "-", Url = string.Empty });
        }

        _suppressVillageSelectionChange = true;
        try
        {
            VillageComboBox.ItemsSource = items;
            var selected = items.FirstOrDefault(v =>
                string.Equals(v.Name, currentSelectedName, StringComparison.OrdinalIgnoreCase))
                ?? items[0];
            VillageComboBox.SelectedItem = selected;
        }
        finally
        {
            _suppressVillageSelectionChange = false;
        }
    }

    private List<VillageSelectionItem> BuildMergedVillageSelectionItems(IReadOnlyList<Village> villages)
    {
        var existingVillageData = Enumerable.Empty<VillageSelectionItem>()
            .Concat(VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem> ?? [])
            .Concat(DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem> ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return villages
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v =>
            {
                existingVillageData.TryGetValue(v.Name!, out var existing);
                return new VillageSelectionItem
                {
                    Name = v.Name!,
                    Url = string.IsNullOrWhiteSpace(v.Url) ? existing?.Url ?? string.Empty : v.Url,
                    IsCapital = v.IsCapital ?? existing?.IsCapital ?? false,
                    CoordX = v.CoordX ?? existing?.CoordX,
                    CoordY = v.CoordY ?? existing?.CoordY,
                    Population = v.Population ?? existing?.Population,
                    CropFields = v.CropFields ?? existing?.CropFields,
                };
            })
            .ToList();
    }
}
