using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
                "reinforcements" => ReinforcementsTabItem,
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
                else if (ReferenceEquals(targetTab, DashboardTabItem))
                {
                    UpdateAutomationLoopRunningIndicators();
                    RefreshVillageActivityIndicatorsOnDashboard();
                }
                else if (ReferenceEquals(targetTab, ResourcesTabItem))
                {
                    _resourcesViewModel.TickLiveForecasts();
                }
                else if (ReferenceEquals(targetTab, ReinforcementsTabItem))
                {
                    UpdateReinforcementStatus();
                }
                else if (ReferenceEquals(targetTab, NpcTradeTabItem))
                {
                    TickResourceTransferVillageForecasts();
                }
                else if (ReferenceEquals(targetTab, QueueTabItem))
                {
                    UpdateBuildQueueStatusText();
                    RefreshTravianBuildQueueUi();
                    RefreshTravianSmithyQueueUi();
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
            ReinforcementsNavButton,
            NpcTradeNavButton,
            QueueNavButton,
            LogsNavButton,
            InboxNavButton,
        };

        foreach (var nav in navButtons)
        {
            nav.BorderThickness = new Thickness(1);
            nav.BorderBrush = new SolidColorBrush(ThemeColors.Get("AppBackgroundBrush"));
        }

        _activeSidebarButton = selectedButton;
        selectedButton.BorderBrush = new SolidColorBrush(ThemeColors.Get("InkBrush"));
    }
}
