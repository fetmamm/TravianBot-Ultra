using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TbotUltra.Desktop.Models;

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

    private void LoadBuildingsButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnLoadBuildingsClicked();
    }

    private void UpgradeAllBuildingsToMaxButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnUpgradeAllBuildingsToMaxClicked();
    }

    private static bool IsPinnedBuildingTopSlot(int slotId)
    {
        return slotId == 26 || slotId == 39 || slotId == 40;
    }

    private void BuildingTopSlotsView_Filter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is BuildingSlotRow row && IsPinnedBuildingTopSlot(row.SlotId);
    }

    private void BuildingRemainingSlotsView_Filter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is BuildingSlotRow row && !IsPinnedBuildingTopSlot(row.SlotId);
    }

    private void BuildingSlotCircleButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.BuildingSlotCircleButton_Click(sender, e);
    }
}
