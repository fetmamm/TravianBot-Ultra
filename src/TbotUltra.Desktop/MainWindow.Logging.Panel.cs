using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private ComboBox LogCategoryFilterComboBox => LogsPanelControl.CategoryFilter;
    private StackPanel LogFilterPanel => LogsPanelControl.FilterPanel;
    private CheckBox LogCleanModeToggle => LogsPanelControl.CleanModeToggle;
    private TabControl TerminalAlarmTabControl => LogsPanelControl.LogTabControl;
    private ListBox TerminalListBox => LogsPanelControl.TerminalList;
    private TabItem AlarmTabItem => LogsPanelControl.AlarmTab;
    private ListBox AlarmListBox => LogsPanelControl.AlarmList;
    private TabItem StatisticsTabItem => LogsPanelControl.StatisticsTab;
    private TextBlock BrowserStatisticsAccountTextBlock => LogsPanelControl.StatisticsAccount;
    private TextBlock BrowserStatisticsPeriodTextBlock => LogsPanelControl.StatisticsPeriod;
    private DataGrid BrowserStatisticsSummaryDataGrid => LogsPanelControl.StatisticsSummary;
    private DataGrid BrowserStatisticsDestinationDataGrid => LogsPanelControl.StatisticsDestinations;
    private Button ClearBrowserStatisticsSessionButton => LogsPanelControl.ClearStatisticsSessionButton;
    private Button ClearBrowserStatisticsLifetimeButton => LogsPanelControl.ClearStatisticsLifetimeButton;
    private TextBlock CopyFeedbackTextBlock => LogsPanelControl.CopyFeedback;
    private Button PopoutLogsButton => LogsPanelControl.PopoutButton;
    private Button CopyCurrentTabButton => LogsPanelControl.CopyButton;
    private Button AcknowledgeAlarmButton => LogsPanelControl.AcknowledgeButton;
    private Button ClearCurrentLogButton => LogsPanelControl.ClearButton;

    internal void OnLogCategoryFilterChanged(object sender, SelectionChangedEventArgs e)
        => LogCategoryFilterComboBox_SelectionChanged(sender, e);

    internal void OnLogCleanModeChanged(object sender, RoutedEventArgs e)
        => LogCleanModeToggle_Changed(sender, e);

    internal void OnPopoutLogsClicked(object sender, RoutedEventArgs e)
        => PopoutLogsButton_Click(sender, e);

    internal void OnCopyCurrentLogTabClicked(object sender, RoutedEventArgs e)
        => CopyCurrentTabButton_Click(sender, e);

    internal void OnAcknowledgeAlarmsClicked(object sender, RoutedEventArgs e)
        => AcknowledgeAlarmButton_Click(sender, e);

    internal void OnClearCurrentLogClicked(object sender, RoutedEventArgs e)
        => ClearCurrentLogButton_Click(sender, e);

    internal void OnClearBrowserStatisticsSessionClicked(object sender, RoutedEventArgs e)
        => ClearBrowserStatisticsSessionButton_Click(sender, e);

    internal void OnClearBrowserStatisticsLifetimeClicked(object sender, RoutedEventArgs e)
        => ClearBrowserStatisticsLifetimeButton_Click(sender, e);
}
