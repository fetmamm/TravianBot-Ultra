using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

/// <summary>
/// Troops tab panel. Inherits its DataContext (a
/// <see cref="ViewModels.TroopTrainingViewModel"/>) from the host
/// TabItem. Click handlers route through a Host accessor back to
/// MainWindow's internal Core methods that drive _botService and the
/// queue, so the panel itself stays free of service references.
/// </summary>
public partial class TroopsPanel : UserControl
{
    private MainWindow? _hostCache;

    public TroopsPanel()
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

    private void UpgradeOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnTroopsUpgradeOptionsClicked();
    }

    private void BuildTroopsNowButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnTroopsBuildNowClicked();
    }

    private async void RefreshTroopQueuesButton_Click(object sender, RoutedEventArgs e)
    {
        if (Host is not { } host)
        {
            return;
        }

        RefreshTroopQueuesButton.IsEnabled = false;
        try
        {
            await host.RefreshTroopQueuesCoreAsync();
        }
        finally
        {
            RefreshTroopQueuesButton.IsEnabled = true;
        }
    }

    private async void CheckCelebrationButton_Click(object sender, RoutedEventArgs e)
    {
        if (Host is not { } host)
        {
            return;
        }

        CheckCelebrationButton.IsEnabled = false;
        try
        {
            await host.OnCheckCelebrationClickedAsync();
        }
        finally
        {
            CheckCelebrationButton.IsEnabled = true;
        }
    }
}
