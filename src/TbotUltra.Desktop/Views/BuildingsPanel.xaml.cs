using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop.Views;

/// <summary>
/// Visible Buildings tab content. Service-bound and queue-bound logic stays on
/// MainWindow; this panel only routes the visible interactions back to the host.
/// Hidden compatibility controls remain on MainWindow.
/// </summary>
public partial class BuildingsPanel : UserControl
{
    private MainWindow? _hostCache;

    public BuildingsPanel()
    {
        InitializeComponent();
    }

    private MainWindow? Host
    {
        get
        {
            if (_hostCache is not null)
            {
                return _hostCache;
            }

            _hostCache = Window.GetWindow(this) as MainWindow;
            return _hostCache;
        }
    }

    private async void LoadBuildingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Host is { } host)
        {
            await host.GuardUiAsync(host.OnLoadBuildingsClicked);
        }
    }

    private void UpgradeAllBuildingsToMaxButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnUpgradeAllBuildingsToMaxClicked();
    }

    private void BuildingTemplatesButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnBuildingTemplatesClicked();
    }

    private void ShowBuildingSlotsButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnShowBuildingSlotsClicked();
    }

    private void BuildingTopSlotsView_Filter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is BuildingSlotRow row && BuildingsViewModel.IsPinnedBuildingTopSlot(row.SlotId);
    }

    private void BuildingRemainingSlotsView_Filter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is BuildingSlotRow row && !BuildingsViewModel.IsPinnedBuildingTopSlot(row.SlotId);
    }

    private void BuildingSlotCircleButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.BuildingSlotCircleButton_Click(sender, e);
    }

}
