using System;
using System.Linq;
using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
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
            .Select(groupKey =>
            {
                QueueGroupCatalog.TryParse(groupKey, out var group);
                var isSelectable = selectableGroupKeys.Contains(groupKey);
                return new DashboardFunctionOption
                {
                    Key = groupKey,
                    Label = currentByGroup.TryGetValue(groupKey, out var current)
                        ? current.Title
                        : QueueGroupCatalog.GetTitle(group),
                    IsVisible = isSelectable
                        && currentByGroup.TryGetValue(groupKey, out var selected)
                        && selected.IsVisible,
                    IsSelectable = isSelectable,
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
        RefreshAutomationLoopDashboardUi();
        PersistAutomationLoopTasksToConfig();
    }
}
