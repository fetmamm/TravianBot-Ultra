using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.ViewModels;

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

    private void HeroRaidingOpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HeroRaidingCandidateItem candidate }
            && Window.GetWindow(this) is MainWindow host)
        {
            _ = host.OnHeroRaidingOpenInBrowserClicked(candidate.X, candidate.Y);
        }
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
