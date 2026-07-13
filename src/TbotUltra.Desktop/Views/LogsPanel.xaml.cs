using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class LogsPanel : UserControl
{
    private MainWindow? _host;

    public LogsPanel()
    {
        InitializeComponent();
    }

    private MainWindow? Host => _host ??= Window.GetWindow(this) as MainWindow;

    internal ComboBox CategoryFilter => LogCategoryFilterComboBox;
    internal CheckBox CleanModeToggle => LogCleanModeToggle;
    internal TabControl LogTabControl => TerminalAlarmTabControl;
    internal ListBox TerminalList => TerminalListBox;
    internal TabItem AlarmTab => AlarmTabItem;
    internal ListBox AlarmList => AlarmListBox;
    internal TextBlock CopyFeedback => CopyFeedbackTextBlock;
    internal Button PopoutButton => PopoutLogsButton;
    internal Button CopyButton => CopyCurrentTabButton;
    internal Button AcknowledgeButton => AcknowledgeAlarmButton;
    internal Button ClearButton => ClearCurrentLogButton;

    private void LogCategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => Host?.OnLogCategoryFilterChanged(sender, e);

    private void LogCleanModeToggle_Changed(object sender, RoutedEventArgs e)
        => Host?.OnLogCleanModeChanged(sender, e);

    private void PopoutLogsButton_Click(object sender, RoutedEventArgs e)
        => Host?.OnPopoutLogsClicked(sender, e);

    private void CopyCurrentTabButton_Click(object sender, RoutedEventArgs e)
        => Host?.OnCopyCurrentLogTabClicked(sender, e);

    private void AcknowledgeAlarmButton_Click(object sender, RoutedEventArgs e)
        => Host?.OnAcknowledgeAlarmsClicked(sender, e);

    private void ClearCurrentLogButton_Click(object sender, RoutedEventArgs e)
        => Host?.OnClearCurrentLogClicked(sender, e);
}
