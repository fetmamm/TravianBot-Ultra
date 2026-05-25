using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

/// <summary>
/// Messages / Reports panel. Inherits its DataContext (an
/// <see cref="ViewModels.InboxViewModel"/>) from the host TabItem and
/// routes button clicks back to MainWindow host methods that still own
/// the service-bound logic.
/// </summary>
public partial class InboxPanel : UserControl
{
    private MainWindow? _hostCache;

    public InboxPanel()
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

    private void MarkMessagesReadButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnInboxMarkMessagesReadClicked();
    }

    private void MarkReportsReadButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnInboxMarkReportsReadClicked();
    }

    private void AutoReadCheckBox_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnInboxAutoReadChanged();
    }

    internal void SetActionsEnabled(bool enabled)
    {
        MarkMessagesReadButton.IsEnabled = enabled;
        MarkReportsReadButton.IsEnabled = enabled;
    }
}
