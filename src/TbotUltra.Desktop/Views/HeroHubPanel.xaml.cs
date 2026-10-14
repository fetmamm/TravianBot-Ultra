using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class HeroHubPanel : UserControl
{
    public HeroHubPanel() => InitializeComponent();

    public void ShowRaidingTab() => HeroTabControl.SelectedItem = HeroRaidingTabItem;

    private void HeroRaidingScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow host)
            _ = host.OnHeroRaidingScanClicked();
    }

    private void HeroTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, HeroTabControl)
            && ReferenceEquals(HeroTabControl.SelectedItem, HeroInventoryTabItem))
        {
            HeroInventoryPanel.RefreshHeroResourceSettings();
        }
    }
}
