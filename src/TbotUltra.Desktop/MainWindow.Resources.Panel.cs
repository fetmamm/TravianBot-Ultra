using System.Windows;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private System.Windows.Controls.Button LoadResourcesButton => ResourcesPanelControl.RefreshButton;
    private System.Windows.Controls.StackPanel CroplandColumnPanel => ResourcesPanelControl.CroplandColumn;
    private System.Windows.Controls.ItemsControl CroplandItemsControl => ResourcesPanelControl.CroplandItems;

    internal void OnLoadResourcesClicked(object sender, RoutedEventArgs e) => LoadResourcesButton_Click(sender, e);
    internal void OnUpgradeAllResourcesClicked(object sender, RoutedEventArgs e) => UpgradeAllResourcesButton_Click(sender, e);
    internal void OnUpgradeAllResourcesToMaxClicked(object sender, RoutedEventArgs e) => UpgradeAllResourcesToMaxButton_Click(sender, e);
    internal void OnResourceBuildStrategyChanged(object sender, RoutedEventArgs e) => ResourceBuildStrategyRadio_Click(sender, e);
    internal void OnResourceLevelBadgeClicked(object sender, RoutedEventArgs e) => ResourceLevelBadge_Click(sender, e);
}
