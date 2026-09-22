using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void OpenQueuePopout()
    {
        if (_queuePopupWindow is not null)
        {
            _queuePopupWindow.Activate();
            return;
        }

        EnsureQueueHistoryProjection();
        ApplyCachedQueueRowsForSelectedVillage();

        var activeGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            CanUserResizeColumns = true,
            BorderBrush = new SolidColorBrush(ThemeColors.Get("BorderBrush")),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
            ItemsSource = QueueDataGrid.ItemsSource,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
        };
        VirtualizingPanel.SetVirtualizationMode(activeGrid, VirtualizationMode.Recycling);
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding("GroupName"), Width = new DataGridLength(1.15, DataGridLengthUnitType.Star), MinWidth = 90 });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Village", Binding = new Binding("VillageName"), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star), MinWidth = 100 });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Task", Binding = new Binding("DisplayName"), Width = new DataGridLength(3, DataGridLengthUnitType.Star), MinWidth = 330 });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 85 });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding("BuildTimeText"), Width = new DataGridLength(1.1, DataGridLengthUnitType.Star), MinWidth = 90 });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Cost (W | C | I | Cr)", Binding = new Binding("CostText"), Width = new DataGridLength(2.55, DataGridLengthUnitType.Star), MinWidth = 247.5 });

        var historyGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            CanUserResizeColumns = true,
            BorderBrush = new SolidColorBrush(ThemeColors.Get("BorderBrush")),
            BorderThickness = new Thickness(1),
            ItemsSource = QueueHistoryDataGrid.ItemsSource,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
        };
        VirtualizingPanel.SetVirtualizationMode(historyGrid, VirtualizationMode.Recycling);
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding("GroupName"), Width = new DataGridLength(1.15, DataGridLengthUnitType.Star), MinWidth = 90 });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Village", Binding = new Binding("VillageName"), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star), MinWidth = 100 });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Completed task", Binding = new Binding("DisplayName"), Width = new DataGridLength(3, DataGridLengthUnitType.Star), MinWidth = 330 });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 85 });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Created", Binding = new Binding("CreatedAtServer"), Width = new DataGridLength(2, DataGridLengthUnitType.Star), MinWidth = 150 });

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
        ThemeChrome.EnableEarlyDarkTitleBar(_queuePopupWindow);
        closeButton.Click += (_, _) => _queuePopupWindow?.Close();
        _queuePopupWindow.Closed += (_, _) => _queuePopupWindow = null;
        _queuePopupWindow.Show();
    }
}
