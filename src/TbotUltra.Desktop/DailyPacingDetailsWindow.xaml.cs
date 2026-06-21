using System.Collections.Generic;
using System.Windows;

namespace TbotUltra.Desktop;

public sealed record DailyPacingDayRow(
    string Date,
    string Online,
    string Limit,
    string Usage);

public sealed record DailyPacingTaskRow(
    string Task,
    int Runs,
    string LastRun,
    string PeakHour);

public partial class DailyPacingDetailsWindow : Window
{
    public DailyPacingDetailsWindow(
        string onlineToday,
        string timeLeft,
        string dailyLimit,
        string weekTotal,
        IReadOnlyList<DailyPacingDayRow> weekRows,
        IReadOnlyList<DailyPacingTaskRow> taskRows)
    {
        InitializeComponent();
        OnlineTodayTextBlock.Text = onlineToday;
        TimeLeftTextBlock.Text = timeLeft;
        DailyLimitTextBlock.Text = dailyLimit;
        WeekTotalTextBlock.Text = weekTotal;
        WeekDataGrid.ItemsSource = weekRows;
        TaskDataGrid.ItemsSource = taskRows;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
