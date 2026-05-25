using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async void AnalyzeNatarsProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Analyze natars profile is unavailable while Gold Club farming is disabled.");
            return;
        }

        var operationId = BeginOperation("Analyze Natars Profile");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingFunctionRunning(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var natarFarmCount = await _botService.EnsureNatarFarmCacheAndReturnToFarmListAsync(options, AppendLog, true, operationToken);
            var analyzed = natarFarmCount > 0 || _natarsProfileAnalyzed;
            SetNatarsProfileAnalyzed(analyzed);
            if (natarFarmCount > 0)
            {
                CompleteOperation(operationId, operationSw, $"Natars analyzed. Farms found: {natarFarmCount}.");
            }
            else if (analyzed)
            {
                CompleteOperation(operationId, operationSw, "No new Natar farms found. Existing cached analysis kept.");
            }
            else
            {
                CompleteOperation(operationId, operationSw, "No matching Natar farms found.");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Analyze natars profile paused.");
        }
        catch (Exception ex)
        {
            RefreshNatarsProfileAnalyzedFromCache();
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            SetFarmingFunctionRunning(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void ShowNatarsListButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_natarsProfileAnalyzed)
        {
            return;
        }

        var snapshot = TryLoadActiveNatarFarmSnapshot();
        var missingVillageNames = snapshot is not null
            && snapshot.Coordinates.Count > 0
            && snapshot.Coordinates.All(item => string.IsNullOrWhiteSpace(item.VillageName));
        if (missingVillageNames)
        {
            try
            {
                var options = ApplySelectedVillageToOptions(LoadBotOptions());
                await EnsureChromiumInstalledAsync();
                await _botService.EnsureNatarFarmCacheAndReturnToFarmListAsync(options, AppendLog, true, CancellationToken.None);
                snapshot = TryLoadActiveNatarFarmSnapshot();
            }
            catch (Exception ex)
            {
                AppendLog($"Could not refresh Natar villages before showing list: {ex.Message}");
            }
        }

        if (snapshot is null || snapshot.Coordinates.Count <= 0)
        {
            SetNatarsProfileAnalyzed(false);
            AppDialog.Show(this, "No analyzed Natars list is available for the active account.", "Natars list", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rows = snapshot.Coordinates
            .Select((item, index) => new NatarListRow
            {
                Index = index + 1,
                VillageName = string.IsNullOrWhiteSpace(item.VillageName) ? "-" : item.VillageName,
                X = item.X,
                Y = item.Y,
                IsChecked = item.Enabled,
            })
            .ToList();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            CanUserResizeRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            Margin = new Thickness(0, 0, 0, 10),
            ItemsSource = rows,
        };

        var useCheckBoxFactory = new FrameworkElementFactory(typeof(CheckBox));
        useCheckBoxFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        useCheckBoxFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        useCheckBoxFactory.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(NatarListRow.IsChecked))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Use",
            CellTemplate = new DataTemplate { VisualTree = useCheckBoxFactory },
            Width = new DataGridLength(60),
        });
        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(NatarListRow.Index)), Width = new DataGridLength(70) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Village", Binding = new Binding(nameof(NatarListRow.VillageName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "X", Binding = new Binding(nameof(NatarListRow.X)), Width = new DataGridLength(90) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Y", Binding = new Binding(nameof(NatarListRow.Y)), Width = new DataGridLength(90) });

        var summaryText = new TextBlock
        {
            Text = BuildNatarsListSummaryText(rows, snapshot.SelectionMode),
            Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var markAllButton = new Button
        {
            Content = "Mark all",
            Width = 90,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var unmarkAllButton = new Button
        {
            Content = "Unmark all",
            Width = 90,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var markSelectedButton = new Button
        {
            Content = "Mark selected",
            Width = 110,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var unmarkSelectedButton = new Button
        {
            Content = "Unmark selected",
            Width = 120,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var closeButton = new Button
        {
            Content = "Close",
            Width = 90,
        };
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                markAllButton,
                unmarkAllButton,
                markSelectedButton,
                unmarkSelectedButton,
                closeButton,
            },
        };

        var popup = new Window
        {
            Title = "Natars list",
            Owner = this,
            Width = 660,
            Height = 620,
            MinWidth = 560,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Margin = new Thickness(12),
                Children =
                {
                    buttonRow,
                    summaryText,
                    grid,
                },
            },
        };

        DockPanel.SetDock(buttonRow, Dock.Bottom);
        DockPanel.SetDock(summaryText, Dock.Top);

        void SaveSelection()
        {
            var enabledKeys = rows
                .Where(row => row.IsChecked)
                .Select(row => NatarFarmCacheStore.BuildCoordinateKey(row.X, row.Y))
                .ToHashSet(StringComparer.Ordinal);
            _natarFarmCacheStore.SaveSelection(snapshot.AccountName, snapshot.ServerUrl, snapshot.SelectionMode, enabledKeys);
            summaryText.Text = BuildNatarsListSummaryText(rows, snapshot.SelectionMode);
        }

        var suppressSelectionSave = false;

        void ApplyCheckedStateToRows(IEnumerable<NatarListRow> targetRows, bool isChecked)
        {
            var selectedRows = targetRows.Distinct().ToList();
            if (selectedRows.Count <= 0)
            {
                return;
            }

            suppressSelectionSave = true;
            foreach (var row in selectedRows)
            {
                row.IsChecked = isChecked;
            }

            suppressSelectionSave = false;
            SaveSelection();
        }

        static bool IsInsideCheckBox(DependencyObject? source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is CheckBox)
                {
                    return true;
                }

                current = GetVisualParent(current);
            }

            return false;
        }

        static DependencyObject? GetVisualParent(DependencyObject source)
        {
            try
            {
                var parent = VisualTreeHelper.GetParent(source);
                if (parent is not null)
                {
                    return parent;
                }
            }
            catch (InvalidOperationException)
            {
            }

            return source is FrameworkElement element ? element.Parent : null;
        }

        var isDraggingSelection = false;
        var dragAnchorIndex = -1;
        var selectionAnchorIndex = -1;

        void SelectRange(int anchorIndex, int currentIndex)
        {
            if (anchorIndex < 0 || currentIndex < 0)
            {
                return;
            }

            var start = Math.Min(anchorIndex, currentIndex);
            var end = Math.Max(anchorIndex, currentIndex);
            grid.SelectedItems.Clear();
            for (var i = start; i <= end; i++)
            {
                grid.SelectedItems.Add(rows[i]);
            }

            grid.SelectedItem = rows[currentIndex];
            grid.ScrollIntoView(rows[currentIndex]);
        }

        int GetFallbackSelectionAnchorIndex(int clickedIndex)
        {
            if (selectionAnchorIndex >= 0)
            {
                return selectionAnchorIndex;
            }

            if (grid.SelectedItems.Count > 0 && grid.SelectedItems[0] is NatarListRow selectedRow)
            {
                var selectedIndex = rows.IndexOf(selectedRow);
                if (selectedIndex >= 0)
                {
                    return selectedIndex;
                }
            }

            return clickedIndex;
        }

        void StopDragSelection()
        {
            isDraggingSelection = false;
            dragAnchorIndex = -1;
        }

        void RowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
        {
            if (IsInsideCheckBox(args.OriginalSource as DependencyObject))
            {
                return;
            }

            if (sender is not DataGridRow row)
            {
                return;
            }

            var clickedIndex = row.GetIndex();
            if (clickedIndex < 0)
            {
                return;
            }

            var modifiers = Keyboard.Modifiers;
            if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                StopDragSelection();
                SelectRange(GetFallbackSelectionAnchorIndex(clickedIndex), clickedIndex);
                args.Handled = true;
                return;
            }

            if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                StopDragSelection();
                selectionAnchorIndex = clickedIndex;
                return;
            }

            selectionAnchorIndex = clickedIndex;
            dragAnchorIndex = clickedIndex;
            isDraggingSelection = true;
            grid.Focus();
            SelectRange(dragAnchorIndex, dragAnchorIndex);
            args.Handled = true;
        }

        void RowMouseEnter(object sender, MouseEventArgs args)
        {
            if (!isDraggingSelection || args.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            if (sender is not DataGridRow row)
            {
                return;
            }

            var currentIndex = row.GetIndex();
            if (currentIndex >= 0)
            {
                SelectRange(dragAnchorIndex, currentIndex);
                args.Handled = true;
            }
        }

        grid.PreviewMouseLeftButtonUp += (_, _) =>
        {
            StopDragSelection();
        };
        grid.MouseLeave += (_, _) => StopDragSelection();

        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new EventSetter(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(RowPreviewMouseLeftButtonDown)));
        rowStyle.Setters.Add(new EventSetter(UIElement.MouseEnterEvent, new MouseEventHandler(RowMouseEnter)));
        grid.RowStyle = rowStyle;

        foreach (var row in rows)
        {
            row.PropertyChanged += (_, args) =>
            {
                if (string.Equals(args.PropertyName, nameof(NatarListRow.IsChecked), StringComparison.Ordinal))
                {
                    if (!suppressSelectionSave)
                    {
                        SaveSelection();
                    }
                }
            };
        }

        markAllButton.Click += (_, _) =>
        {
            ApplyCheckedStateToRows(rows, true);
        };
        unmarkAllButton.Click += (_, _) =>
        {
            ApplyCheckedStateToRows(rows, false);
        };
        markSelectedButton.Click += (_, _) =>
        {
            ApplyCheckedStateToRows(grid.SelectedItems.OfType<NatarListRow>(), true);
        };
        unmarkSelectedButton.Click += (_, _) =>
        {
            ApplyCheckedStateToRows(grid.SelectedItems.OfType<NatarListRow>(), false);
        };
        closeButton.Click += (_, _) => popup.Close();
        popup.ShowDialog();
    }

    private static string BuildNatarsListSummaryText(IReadOnlyCollection<NatarListRow> rows, string selectionMode)
    {
        var modeText = string.Equals(selectionMode, "all_villages", StringComparison.OrdinalIgnoreCase)
            ? "All villages"
            : "Farm villages";
        return $"Entries: {rows.Count:N0} | Checked: {rows.Count(row => row.IsChecked):N0} | Mode: {modeText}";
    }

    private string ResolveCurrentTribeForFarming()
    {
        var tribeFromUi = TribeInfoTextBlock.Text?.Replace("Tribe:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (!string.IsNullOrWhiteSpace(tribeFromUi) && !string.Equals(tribeFromUi, "-", StringComparison.OrdinalIgnoreCase))
        {
            return tribeFromUi;
        }

        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (!string.IsNullOrWhiteSpace(accountName)
                && _accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                && analysis is not null
                && !string.IsNullOrWhiteSpace(analysis.Tribe))
            {
                return analysis.Tribe;
            }
        }
        catch
        {
            // Ignore lookup errors and use fallback tribe.
        }

        return "Unknown";
    }

    private void SetNatarsProfileAnalyzed(bool analyzed)
    {
        _natarsProfileAnalyzed = analyzed;
        if (NatarsProfileAnalyzedIndicator is not null)
        {
            NatarsProfileAnalyzedIndicator.Fill = analyzed
                ? new SolidColorBrush(Color.FromRgb(22, 163, 74))
                : new SolidColorBrush(Color.FromRgb(156, 163, 175));
        }

        SetEnabled(ShowNatarsListButton, !_farmingOperationBusy && _farmingFeaturesAvailable && analyzed);
    }
}
