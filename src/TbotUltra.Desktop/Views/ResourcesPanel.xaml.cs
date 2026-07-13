using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class ResourcesPanel : UserControl
{
    private MainWindow? _host;

    public ResourcesPanel() => InitializeComponent();

    private MainWindow? Host => _host ??= Window.GetWindow(this) as MainWindow;

    internal Button RefreshButton => LoadResourcesButton;
    internal StackPanel CroplandColumn => CroplandColumnPanel;
    internal ItemsControl CroplandItems => CroplandItemsControl;

    private void LoadResourcesButton_Click(object sender, RoutedEventArgs e) => Host?.OnLoadResourcesClicked(sender, e);
    private void UpgradeAllResourcesButton_Click(object sender, RoutedEventArgs e) => Host?.OnUpgradeAllResourcesClicked(sender, e);
    private void UpgradeAllResourcesToMaxButton_Click(object sender, RoutedEventArgs e) => Host?.OnUpgradeAllResourcesToMaxClicked(sender, e);
    private void ResourceBuildStrategyRadio_Click(object sender, RoutedEventArgs e) => Host?.OnResourceBuildStrategyChanged(sender, e);
    private void ResourceLevelBadge_Click(object sender, RoutedEventArgs e) => Host?.OnResourceLevelBadgeClicked(sender, e);
}
