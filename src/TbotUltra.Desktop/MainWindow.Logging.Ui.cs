using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services.Logging;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed record LogListAnchor(object? Item, bool FollowLiveTop);

    private void InitializeLogFilterControls()
    {
        LogCategoryFilterComboBox.ItemsSource = LogClassifier.FilterOptions.Select(option => option.Label).ToList();
        LogCategoryFilterComboBox.SelectedIndex = 0;
        LogCleanModeToggle.IsChecked = _terminalViewModel.CleanMode;
    }

    private bool TerminalEntryFilter(object item)
    {
        if (item is not TerminalEntryRow row)
        {
            return true;
        }

        return _terminalViewModel.ShouldShow(row);
    }

    private bool AlarmEntryFilter(object item)
    {
        if (item is not AlarmEntryRow row)
        {
            return true;
        }

        return !_terminalViewModel.CleanMode || !IsCleanModeHiddenAlarmMessage(row.Text);
    }

    private IEnumerable<TerminalEntryRow> VisibleTerminalEntries()
        => _terminalEntries.Where(TerminalEntryFilter);

    private void LogCategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = LogCategoryFilterComboBox.SelectedIndex;
        _terminalViewModel.FilterCategory = index >= 0 && index < LogClassifier.FilterOptions.Length
            ? LogClassifier.FilterOptions[index].Category
            : LogCategory.All;
        _terminalView?.Refresh();
        UpdateStatusFromVisibleLog();
        UpdateTerminalAlarmUi();
    }

    private void LogCleanModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        _terminalViewModel.CleanMode = LogCleanModeToggle.IsChecked == true;
        _terminalView?.Refresh();
        _alarmView?.Refresh();
        UpdateStatusFromVisibleLog();
        if (TerminalAlarmTabControl is not null)
        {
            UpdateTerminalAlarmUi();
        }
    }

    private void AcknowledgeAlarmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_alarmsViewModel.UnacknowledgedCount == 0)
        {
            return;
        }

        AcknowledgeAllAlarmEntries();
        StatusTextBlock.Text = "Alerts acknowledged.";
        UpdateTerminalAlarmUi();
    }

    private void ClearCurrentLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalAlarmTabControl.SelectedIndex == 2)
        {
            return;
        }

        var alarmsSelected = TerminalAlarmTabControl.SelectedIndex == 1;
        if (alarmsSelected)
        {
            _alarmsViewModel.Clear();
        }
        else
        {
            _terminalEntries.Clear();
        }

        UpdateStatusFromVisibleLog("Ready.", "Ready.");
        UpdateTerminalAlarmUi();
    }

    private void CopyCurrentTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalAlarmTabControl.SelectedIndex == 2)
        {
            Clipboard.SetText(BuildBrowserStatisticsReport());
            StatusTextBlock.Text = "Browser statistics copied to clipboard.";
            CopyFeedbackTextBlock.Text = "Copied";
            CopyFeedbackTextBlock.Visibility = Visibility.Visible;
            _copyFeedbackTimer.Stop();
            _copyFeedbackTimer.Start();
            return;
        }

        var alertsTabSelected = TerminalAlarmTabControl.SelectedIndex == 1;
        var list = alertsTabSelected ? AlarmListBox : TerminalListBox;
        var selectedLines = alertsTabSelected
            ? list.SelectedItems.Cast<AlarmEntryRow>().Select(item => item.Text).ToList()
            : list.SelectedItems.Cast<TerminalEntryRow>().Select(item => item.Text).ToList();
        var source = alertsTabSelected
            ? _alarmEntries.Select(item => item.Text).ToList()
            : VisibleTerminalEntries().Select(item => item.Text).ToList();
        var linesToCopy = selectedLines.Count > 0 ? selectedLines : source;
        if (linesToCopy.Count == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, linesToCopy));
        StatusTextBlock.Text = alertsTabSelected
            ? "Alerts copied to clipboard."
            : "Terminal log copied to clipboard.";

        CopyFeedbackTextBlock.Text = "Copied";
        CopyFeedbackTextBlock.Visibility = Visibility.Visible;
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void UpdateTerminalAlarmUi()
    {
        var hasAlarms = _alarmsViewModel.UnacknowledgedCount > 0;
        var hasAlarmEntries = _alarmEntries.Count > 0;
        var alarmTabSelected = TerminalAlarmTabControl.SelectedIndex == 1;
        var statisticsTabSelected = TerminalAlarmTabControl.SelectedIndex == 2;
        LogFilterPanel.Visibility = statisticsTabSelected ? Visibility.Collapsed : Visibility.Visible;
        var activeList = alarmTabSelected ? AlarmListBox : TerminalListBox;
        var hasSelection = !statisticsTabSelected && activeList.SelectedItems.Count > 0;
        AcknowledgeAlarmButton.IsEnabled = hasAlarms;
        AcknowledgeAlarmButton.Visibility = statisticsTabSelected ? Visibility.Collapsed : Visibility.Visible;
        SetAcknowledgeAlarmButtonHighlight(hasAlarms && alarmTabSelected);
        CopyCurrentTabButton.IsEnabled = statisticsTabSelected || (alarmTabSelected ? hasAlarmEntries : _terminalEntries.Count > 0);
        CopyCurrentTabButton.ToolTip = statisticsTabSelected ? "Copy browser statistics" : alarmTabSelected ? "Copy alerts" : "Copy terminal";
        ClearCurrentLogButton.Visibility = statisticsTabSelected ? Visibility.Collapsed : Visibility.Visible;
        ClearCurrentLogButton.IsEnabled = alarmTabSelected ? hasAlarmEntries : _terminalEntries.Count > 0;
        if (statisticsTabSelected)
        {
            RefreshBrowserStatisticsUi();
        }

        if (hasAlarms)
        {
            LogsNavButton.Background = new SolidColorBrush(ThemeColors.Get("DangerBrush"));
            LogsNavButton.Foreground = Brushes.White;
            LogsNavButton.ToolTip = $"Logs ({_alarmsViewModel.UnacknowledgedCount} alarms)";
            AlarmTabItem.Foreground = Brushes.White;
            AlarmTabItem.FontWeight = alarmTabSelected ? FontWeights.SemiBold : FontWeights.Normal;
            AlarmTabItem.Template = (ControlTemplate)AlarmTabItem.Resources["ActiveAlarmTabTemplate"];
            AlarmTabItem.ToolTip = $"Alarms ({_alarmsViewModel.UnacknowledgedCount})";
        }
        else
        {
            LogsNavButton.Background = new SolidColorBrush(ThemeColors.Get("AppBackgroundBrush"));
            LogsNavButton.Foreground = new SolidColorBrush(ThemeColors.Get("TextPrimaryBrush"));
            LogsNavButton.ToolTip = "Logs";
            AlarmTabItem.ClearValue(Control.ForegroundProperty);
            AlarmTabItem.ClearValue(Control.FontWeightProperty);
            AlarmTabItem.ClearValue(Control.TemplateProperty);
            AlarmTabItem.ClearValue(FrameworkElement.ToolTipProperty);
        }

        if (hasSelection)
        {
            CopyCurrentTabButton.Content = "Copy selected";
        }
        else
        {
            CopyCurrentTabButton.Content = "Copy";
        }
    }

    private void SetAcknowledgeAlarmButtonHighlight(bool highlighted)
    {
        if (highlighted)
        {
            AcknowledgeAlarmButton.Background = new SolidColorBrush(ThemeColors.Get("DangerBrush"));
            AcknowledgeAlarmButton.Foreground = Brushes.White;
            AcknowledgeAlarmButton.BorderBrush = new SolidColorBrush(ThemeColors.Get("DangerStrongBrush"));
            return;
        }

        AcknowledgeAlarmButton.ClearValue(Control.BackgroundProperty);
        AcknowledgeAlarmButton.ClearValue(Control.ForegroundProperty);
        AcknowledgeAlarmButton.ClearValue(Control.BorderBrushProperty);
    }

    private void AcknowledgeAllAlarmEntries()
    {
        _alarmsViewModel.AcknowledgeAll();
        AlarmListBox.Items.Refresh();
        _logsPopupAlarmList?.Items.Refresh();
    }

    private void AcknowledgeLanguageAlarmEntries()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(AcknowledgeLanguageAlarmEntries);
            return;
        }

        FlushPendingLogsToUi();

        if (!_alarmsViewModel.AcknowledgeWhere(entry => IsLanguageAlarmMessage(entry.Text)))
        {
            return;
        }

        AlarmListBox.Items.Refresh();
        _logsPopupAlarmList?.Items.Refresh();
        UpdateTerminalAlarmUi();
        AppendLog("[language] language alarm acknowledged after automatic language update.");
    }

    private static bool IsLanguageAlarmMessage(string message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("[language]", StringComparison.OrdinalIgnoreCase)
            && message.Contains("ALARM:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("Travian language", StringComparison.OrdinalIgnoreCase);
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (IsDescendantOf(source, CopyCurrentTabButton)
            || IsDescendantOf(source, PopoutLogsButton)
            || IsDescendantOf(source, AcknowledgeAlarmButton)
            || IsDescendantOf(source, ClearCurrentLogButton))
        {
            return;
        }

        if (!IsDescendantOf(source, TerminalListBox))
        {
            TerminalListBox.UnselectAll();
        }

        if (!IsDescendantOf(source, AlarmListBox))
        {
            AlarmListBox.UnselectAll();
        }
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void LogListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        var item = GetListBoxItemAt(list, e.GetPosition(list));
        if (item is null)
        {
            return;
        }

        var index = list.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0 || index >= list.Items.Count)
        {
            return;
        }

        _logDragSelecting = true;
        _logDragSourceList = list;
        _logDragAnchorIndex = index;
        SelectListBoxRange(list, index, index);
        list.Focus();
        list.CaptureMouse();
    }

    private void LogListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_logDragSelecting || _logDragSourceList is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!ReferenceEquals(sender, _logDragSourceList))
        {
            return;
        }

        var mousePosition = e.GetPosition(_logDragSourceList);
        var item = GetListBoxItemAt(_logDragSourceList, mousePosition);
        int index;
        if (item is not null)
        {
            index = _logDragSourceList.ItemContainerGenerator.IndexFromContainer(item);
        }
        else if (_logDragSourceList.Items.Count > 0 && mousePosition.Y < 0)
        {
            index = 0;
        }
        else if (_logDragSourceList.Items.Count > 0 && mousePosition.Y > _logDragSourceList.ActualHeight)
        {
            index = _logDragSourceList.Items.Count - 1;
        }
        else
        {
            return;
        }

        if (index < 0 || index >= _logDragSourceList.Items.Count || _logDragAnchorIndex < 0)
        {
            return;
        }

        SelectListBoxRange(_logDragSourceList, _logDragAnchorIndex, index);
    }

    private void LogListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_logDragSelecting)
        {
            return;
        }

        _logDragSelecting = false;
        _logDragAnchorIndex = -1;
        if (_logDragSourceList is not null && _logDragSourceList.IsMouseCaptured)
        {
            _logDragSourceList.ReleaseMouseCapture();
        }

        _logDragSourceList = null;
        UpdateTerminalAlarmUi();
    }

    private static void SelectListBoxRange(ListBox list, int startIndex, int endIndex)
    {
        if (list.Items.Count == 0)
        {
            return;
        }

        var start = Math.Clamp(Math.Min(startIndex, endIndex), 0, list.Items.Count - 1);
        var end = Math.Clamp(Math.Max(startIndex, endIndex), 0, list.Items.Count - 1);
        list.SelectedItems.Clear();
        for (var i = start; i <= end; i++)
        {
            list.SelectedItems.Add(list.Items[i]);
        }

        list.ScrollIntoView(list.Items[end]);
    }

    private static ListBoxItem? GetListBoxItemAt(ListBox list, Point point)
    {
        var hit = list.InputHitTest(point) as DependencyObject;
        var direct = FindAncestor<ListBoxItem>(hit);
        if (direct is not null)
        {
            return direct;
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)
            {
                continue;
            }

            var topLeft = item.TranslatePoint(new Point(0, 0), list);
            var bounds = new Rect(topLeft, new Size(item.ActualWidth, item.ActualHeight));
            if (bounds.Contains(point))
            {
                return item;
            }
        }

        return null;
    }

    private static LogListAnchor CaptureLogListAnchor(ListBox? list)
    {
        if (list is null || list.Items.Count == 0)
        {
            return new LogListAnchor(null, true);
        }

        var scrollViewer = FindDescendant<ScrollViewer>(list);
        if (scrollViewer is null)
        {
            return new LogListAnchor(null, true);
        }

        if (scrollViewer.VerticalOffset <= 0.5)
        {
            return new LogListAnchor(null, true);
        }

        var anchorIndex = Math.Clamp((int)Math.Floor(scrollViewer.VerticalOffset), 0, list.Items.Count - 1);
        return new LogListAnchor(list.Items[anchorIndex], false);
    }

    private static void RestoreLogListAnchor(ListBox? list, LogListAnchor anchor)
    {
        if (list is null || list.Items.Count == 0)
        {
            return;
        }

        if (anchor.FollowLiveTop)
        {
            var scrollViewer = FindDescendant<ScrollViewer>(list);
            scrollViewer?.ScrollToTop();
            return;
        }

        if (anchor.Item is not null)
        {
            list.ScrollIntoView(anchor.Item);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void PopoutLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_logsPopupWindow is not null)
        {
            _logsPopupWindow.Activate();
            return;
        }

        var popupTab = new TabControl();
        var popupLogList = new ListBox
        {
            Background = new SolidColorBrush(ThemeColors.Get("TerminalBgBrush")),
            Foreground = new SolidColorBrush(ThemeColors.Get("TerminalFgBrush")),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            SelectionMode = SelectionMode.Extended,
            ItemsSource = _terminalView,
        };
        popupLogList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        popupLogList.ItemTemplate = new DataTemplate
        {
            VisualTree = new FrameworkElementFactory(typeof(TextBlock)),
        };
        popupLogList.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding(nameof(TerminalEntryRow.Text)));
        popupLogList.ItemTemplate.VisualTree.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var popupAlarmList = new ListBox
        {
            Background = new SolidColorBrush(ThemeColors.Get("AlarmBgBrush")),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            SelectionMode = SelectionMode.Extended,
            ItemsSource = _alarmView,
        };
        _logsPopupLogList = popupLogList;
        _logsPopupAlarmList = popupAlarmList;
        popupAlarmList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        popupAlarmList.ItemTemplate = new DataTemplate
        {
            VisualTree = new FrameworkElementFactory(typeof(TextBlock)),
        };
        popupAlarmList.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding(nameof(AlarmEntryRow.Text)));
        popupAlarmList.ItemTemplate.VisualTree.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var popupAlarmStyle = new Style(typeof(TextBlock));
        popupAlarmStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(ThemeColors.Get("AlarmTextBrush"))));
        popupAlarmStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(nameof(AlarmEntryRow.IsAcknowledged)),
            Value = true,
            Setters =
            {
                new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(ThemeColors.Get("TerminalFgBrush"))),
            }
        });
        popupAlarmList.ItemTemplate.VisualTree.SetValue(TextBlock.StyleProperty, popupAlarmStyle);

        var popupStatisticsTextBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Text = BuildBrowserStatisticsReport(),
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _logsPopupStatisticsTextBox = popupStatisticsTextBox;
        var popupClearSessionStatisticsButton = new Button
        {
            Content = "Clear session",
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 4, 10, 4),
        };
        popupClearSessionStatisticsButton.Click += ClearBrowserStatisticsSessionButton_Click;
        var popupClearLifetimeStatisticsButton = new Button
        {
            Content = "Clear lifetime",
            Padding = new Thickness(10, 4, 10, 4),
        };
        popupClearLifetimeStatisticsButton.Click += ClearBrowserStatisticsLifetimeButton_Click;
        var popupStatisticsHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 8),
        };
        popupStatisticsHeader.Children.Add(popupClearSessionStatisticsButton);
        popupStatisticsHeader.Children.Add(popupClearLifetimeStatisticsButton);
        var popupStatisticsRoot = new Grid();
        popupStatisticsRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        popupStatisticsRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        popupStatisticsRoot.Children.Add(popupStatisticsHeader);
        Grid.SetRow(popupStatisticsTextBox, 1);
        popupStatisticsRoot.Children.Add(popupStatisticsTextBox);

        popupTab.Items.Add(new TabItem { Header = "Log", Content = popupLogList });
        popupTab.Items.Add(new TabItem { Header = "Alarms", Content = popupAlarmList });
        popupTab.Items.Add(new TabItem { Header = "Statistics", Content = popupStatisticsRoot });

        var clearButton = new Button { Content = "Clear", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        clearButton.Click += (_, _) =>
        {
            if (popupTab.SelectedIndex == 2)
            {
                return;
            }

            if (popupTab.SelectedIndex == 1)
            {
                _alarmEntries.Clear();
            }
            else
            {
                _terminalEntries.Clear();
            }

            UpdateTerminalAlarmUi();
        };

        var acknowledgeButton = new Button { Content = "Acknowledge alarms", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        acknowledgeButton.Click += (_, _) =>
        {
            AcknowledgeAllAlarmEntries();
            UpdateTerminalAlarmUi();
        };

        var copyButton = new Button { Content = "Copy", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        copyButton.Click += (_, _) =>
        {
            if (popupTab.SelectedIndex == 2)
            {
                Clipboard.SetText(BuildBrowserStatisticsReport());
                return;
            }

            var selected = popupTab.SelectedIndex == 1
                ? popupAlarmList.SelectedItems.Cast<AlarmEntryRow>().Select(item => item.Text).ToList()
                : popupLogList.SelectedItems.Cast<TerminalEntryRow>().Select(item => item.Text).ToList();
            var lines = selected.Count > 0
                ? selected
                : (popupTab.SelectedIndex == 1 ? _alarmEntries.Select(item => item.Text).ToList() : VisibleTerminalEntries().Select(item => item.Text).ToList());
            if (lines.Count == 0)
            {
                return;
            }

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        };

        var closeButton = new Button { Content = "Close", Padding = new Thickness(10, 4, 10, 4), Height = 30 };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        footer.Children.Add(acknowledgeButton);
        footer.Children.Add(copyButton);
        footer.Children.Add(clearButton);
        footer.Children.Add(closeButton);
        popupTab.SelectionChanged += (_, _) =>
        {
            var statisticsSelected = popupTab.SelectedIndex == 2;
            acknowledgeButton.Visibility = popupTab.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            clearButton.Visibility = statisticsSelected ? Visibility.Collapsed : Visibility.Visible;
            if (statisticsSelected)
            {
                popupStatisticsTextBox.Text = BuildBrowserStatisticsReport();
            }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(popupTab);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        root.PreviewMouseDown += (_, args) =>
        {
            if (args.OriginalSource is not DependencyObject src)
            {
                return;
            }

            if (!IsDescendantOf(src, popupLogList))
            {
                popupLogList.UnselectAll();
            }

            if (!IsDescendantOf(src, popupAlarmList))
            {
                popupAlarmList.UnselectAll();
            }
        };

        _logsPopupWindow = new Window
        {
            Title = "Logs",
            Width = 700,
            Height = 400,
            MinWidth = 580,
            MinHeight = 320,
            Content = root,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left + Width + 10,
            Top = Top + 30,
        };

        ThemeChrome.EnableEarlyDarkTitleBar(_logsPopupWindow);
        _logsPopupWindow.Closed += (_, _) =>
        {
            _logsPopupWindow = null;
            _logsPopupLogList = null;
            _logsPopupAlarmList = null;
            _logsPopupStatisticsTextBox = null;
        };
        closeButton.Click += (_, _) => _logsPopupWindow?.Close();
        _logsPopupWindow.Show();
    }
}
