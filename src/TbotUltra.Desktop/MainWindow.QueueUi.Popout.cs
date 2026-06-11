using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void QueuePopoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queuePopupWindow is not null)
        {
            _queuePopupWindow.Activate();
            return;
        }

        var activeGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            BorderBrush = new SolidColorBrush(ThemeColors.Get("BorderBrush")),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
            ItemsSource = QueueDataGrid.ItemsSource,
        };
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding("GroupName"), Width = new DataGridLength(1.15, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Village", Binding = new Binding("VillageName"), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Task", Binding = new Binding("DisplayName"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Retries", Binding = new Binding("RetriesText"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Cost (W|C|I|Cr)", Binding = new Binding("CostText"), Width = new DataGridLength(1.6, DataGridLengthUnitType.Star) });

        var historyGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            BorderBrush = new SolidColorBrush(ThemeColors.Get("BorderBrush")),
            BorderThickness = new Thickness(1),
            ItemsSource = QueueHistoryDataGrid.ItemsSource,
        };
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding("GroupName"), Width = new DataGridLength(1.15, DataGridLengthUnitType.Star) });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Village", Binding = new Binding("VillageName"), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Completed task", Binding = new Binding("DisplayName"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Created", Binding = new Binding("CreatedAtServer"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });

        var closeButton = new Button
        {
            Content = "Close",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(activeGrid);
        Grid.SetRow(historyGrid, 1);
        root.Children.Add(historyGrid);
        Grid.SetRow(closeButton, 2);
        root.Children.Add(closeButton);

        _queuePopupWindow = new Window
        {
            Title = "Queue",
            Width = 700,
            Height = 400,
            MinWidth = 580,
            MinHeight = 320,
            Content = root,
            Background = ThemeColors.Brush("AppBackgroundBrush"),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left + Width + 10,
            Top = Top + 30,
        };
        closeButton.Click += (_, _) => _queuePopupWindow?.Close();
        _queuePopupWindow.Closed += (_, _) => _queuePopupWindow = null;
        _queuePopupWindow.Show();
    }
}
