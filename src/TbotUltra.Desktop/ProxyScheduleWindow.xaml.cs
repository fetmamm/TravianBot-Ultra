using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

public partial class ProxyScheduleWindow : Window
{
    private const string AllDays = "All days";
    private const string Weekdays = "Weekdays";
    private const string Weekends = "Weekends";

    private readonly AccountProxyPlanStore _planStore;
    private readonly ProxyLibraryStore _libraryStore;
    private readonly string _accountName;
    private readonly string _serverUrl;
    private readonly bool _neverUseOwnIp;
    private readonly bool _sessionPacingEnabled;
    private readonly IReadOnlyCollection<int> _allowedHours;
    private readonly int _sleepMinMinutes;
    private readonly IReadOnlyList<string> _accountNames;
    private readonly List<ProxyLibraryEntry> _library;
    private readonly ObservableCollection<ProxyTimelineRow> _rows = [];
    private CancellationTokenSource? _validationCts;
    private bool _buildingTimeline;
    private bool _validatedSinceChange;

    public AccountProxyPlan ResultPlan { get; private set; }

    public ProxyScheduleWindow(
        AccountProxyPlanStore planStore,
        ProxyLibraryStore libraryStore,
        AccountProxyPlan plan,
        IReadOnlyCollection<ProxyLibraryEntry> library,
        string accountName,
        string serverUrl,
        bool neverUseOwnIp,
        bool sessionPacingEnabled,
        IReadOnlyCollection<int> allowedHours,
        int sleepMinMinutes,
        IEnumerable<string> accountNames)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _planStore = planStore;
        _libraryStore = libraryStore;
        _accountName = accountName;
        _serverUrl = serverUrl;
        _neverUseOwnIp = neverUseOwnIp;
        _sessionPacingEnabled = sessionPacingEnabled;
        _allowedHours = allowedHours.Where(hour => hour is >= 0 and <= 23).Distinct().Order().ToArray();
        _sleepMinMinutes = sleepMinMinutes;
        _accountNames = accountNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _library = library.OrderBy(proxy => proxy.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        ResultPlan = plan.Clone();
        VariationTextBox.Text = Math.Clamp(plan.VariationPercent, 0, 49).ToString(CultureInfo.InvariantCulture);

        foreach (var assignment in plan.Assignments)
        {
            _rows.Add(ProxyTimelineRow.FromAssignment(assignment));
        }

        BuildTimeline();
    }

    private void BuildTimeline()
    {
        _buildingTimeline = true;
        try
        {
            TimelineGrid.Children.Clear();
            TimelineGrid.ColumnDefinitions.Clear();
            TimelineGrid.RowDefinitions.Clear();
            TimelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            TimelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            for (var hour = 0; hour < 24; hour++)
            {
                TimelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            }
            TimelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
            TimelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            TimelineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            TimelineGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddHeader("Proxy", 0);
            AddHeader("Days", 1);
            for (var hour = 0; hour < 24; hour++)
            {
                AddHeader(hour.ToString("00", CultureInfo.InvariantCulture), hour + 2, HorizontalAlignment.Center);
            }
            AddHeader("Hours", 26);
            AddHeader("Status", 27);

            TimelineGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var allowedLabel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 8, 8, 8),
                VerticalAlignment = VerticalAlignment.Center,
            };
            allowedLabel.Children.Add(new TextBlock
            {
                Text = "Allowed hours",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            allowedLabel.Children.Add(new ContentControl
            {
                Style = FindResource("InfoTooltipIconStyle") as Style,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Read-only allowed hours from Settings > Bot behaviour. Blue means the program may run during that hour.",
            });
            Grid.SetRow(allowedLabel, 1);
            Grid.SetColumn(allowedLabel, 0);
            Grid.SetColumnSpan(allowedLabel, 2);
            TimelineGrid.Children.Add(allowedLabel);
            for (var hour = 0; hour < 24; hour++)
            {
                var allowed = _allowedHours.Contains(hour);
                var square = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = FindResource(allowed ? "InfoBrush" : "ControlBackgroundBrush") as Brush,
                    BorderBrush = FindResource(allowed ? "InfoBrush" : "BorderBrush") as Brush,
                    BorderThickness = new Thickness(1),
                    ToolTip = allowed ? "Program may run during this hour" : "Program is blocked during this hour",
                };
                Grid.SetRow(square, 1);
                Grid.SetColumn(square, hour + 2);
                TimelineGrid.Children.Add(square);
            }

            for (var index = 0; index < _rows.Count; index++)
            {
                AddProxyRow(_rows[index], index + 2);
            }
        }
        finally
        {
            _buildingTimeline = false;
        }
    }

    private void AddHeader(string text, int column, HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindResource("SlateTextBrush") as Brush,
            Margin = new Thickness(4, 2, 4, 6),
            HorizontalAlignment = alignment,
        };
        Grid.SetRow(block, 0);
        Grid.SetColumn(block, column);
        TimelineGrid.Children.Add(block);
    }

    private void AddProxyRow(ProxyTimelineRow row, int gridRow)
    {
        TimelineGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var proxyCombo = new ComboBox
        {
            ItemsSource = _library,
            DisplayMemberPath = nameof(ProxyLibraryEntry.DisplayName),
            SelectedValuePath = nameof(ProxyLibraryEntry.Id),
            SelectedValue = row.ProxyId,
            Tag = row,
            Margin = new Thickness(2, 4, 6, 4),
            MinWidth = 165,
        };
        proxyCombo.SelectionChanged += ProxyComboBox_SelectionChanged;
        Grid.SetRow(proxyCombo, gridRow);
        Grid.SetColumn(proxyCombo, 0);
        TimelineGrid.Children.Add(proxyCombo);

        var daysCombo = new ComboBox
        {
            ItemsSource = new[] { AllDays, Weekdays, Weekends },
            SelectedItem = row.DayGroup,
            Tag = row,
            Margin = new Thickness(2, 4, 6, 4),
        };
        daysCombo.SelectionChanged += DaysComboBox_SelectionChanged;
        Grid.SetRow(daysCombo, gridRow);
        Grid.SetColumn(daysCombo, 1);
        TimelineGrid.Children.Add(daysCombo);

        for (var hour = 0; hour < 24; hour++)
        {
            var checkBox = CreateHourCheckBox(row.Hours[hour]);
            checkBox.Tag = new ProxyHourTag(row, hour);
            checkBox.Checked += ProxyHourCheckBox_Changed;
            checkBox.Unchecked += ProxyHourCheckBox_Changed;
            Grid.SetRow(checkBox, gridRow);
            Grid.SetColumn(checkBox, hour + 2);
            TimelineGrid.Children.Add(checkBox);
        }

        var toggleAllButton = new Button
        {
            Content = row.Hours.All(selected => selected) ? "Uncheck all" : "Check all",
            Tag = row,
            Width = 60,
            Height = 22,
            Margin = new Thickness(2),
            Padding = new Thickness(2, 0, 2, 0),
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggleAllButton.Click += ToggleAllHoursButton_Click;
        Grid.SetRow(toggleAllButton, gridRow);
        Grid.SetColumn(toggleAllButton, 26);
        TimelineGrid.Children.Add(toggleAllButton);

        var proxy = FindProxy(row.ProxyId);
        var status = proxy?.StatusText ?? "Missing";
        var statusText = new TextBlock
        {
            Text = status,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 7, 4, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = status switch
            {
                "Working" => FindResource("SuccessBrush") as Brush,
                "Failed" or "Missing" => FindResource("DangerTextBrush") as Brush,
                _ => FindResource("SlateTextBrush") as Brush,
            },
            ToolTip = proxy?.LatencyMs is > 0 ? $"{proxy.LatencyMs} ms" : null,
        };
        Grid.SetRow(statusText, gridRow);
        Grid.SetColumn(statusText, 27);
        TimelineGrid.Children.Add(statusText);

        var removeButton = new Button
        {
            Content = "Remove",
            Tag = row,
            Margin = new Thickness(4),
            Padding = new Thickness(5, 2, 5, 2),
        };
        removeButton.Click += RemoveProxyButton_Click;
        Grid.SetRow(removeButton, gridRow);
        Grid.SetColumn(removeButton, 28);
        TimelineGrid.Children.Add(removeButton);
    }

    private static CheckBox CreateHourCheckBox(bool isChecked) => new()
    {
        IsChecked = isChecked,
        Width = 16,
        Height = 16,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(1, 7, 1, 7),
    };

    private void ToggleAllHoursButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProxyTimelineRow row })
        {
            return;
        }

        var selectAll = !row.Hours.All(selected => selected);
        for (var hour = 0; hour < 24; hour++)
        {
            row.Hours[hour] = selectAll;
        }

        BuildTimeline();
        MarkChanged(selectAll ? "All hours checked. Validate again before saving." : "All hours unchecked. Validate again before saving.");
    }

    private void AddProxyButton_Click(object sender, RoutedEventArgs e)
    {
        var unused = _library.FirstOrDefault(proxy => _rows.All(row => !string.Equals(row.ProxyId, proxy.Id, StringComparison.OrdinalIgnoreCase)));
        if (unused is null)
        {
            ValidationTextBlock.Text = _library.Count == 0
                ? "Add proxies to the proxy list first."
                : "All saved proxies are already in the schedule.";
            return;
        }

        var row = new ProxyTimelineRow(unused.Id, AllDays);
        if (_rows.Count == 0)
        {
            foreach (var hour in _allowedHours)
            {
                row.Hours[hour] = true;
            }
        }

        _rows.Add(row);
        BuildTimeline();
        MarkChanged("Proxy added. Check the hours it should use, then validate the setup.");
    }

    private void RemoveProxyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProxyTimelineRow row })
        {
            _rows.Remove(row);
            BuildTimeline();
            MarkChanged("Proxy removed. Validate again before saving.");
        }
    }

    private void ProxyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_buildingTimeline || sender is not ComboBox { Tag: ProxyTimelineRow row, SelectedValue: string proxyId })
        {
            return;
        }

        if (_rows.Any(other => !ReferenceEquals(other, row) && string.Equals(other.ProxyId, proxyId, StringComparison.OrdinalIgnoreCase)))
        {
            MarkChanged("That proxy already has a schedule row. Choose another proxy.");
            BuildTimeline();
            return;
        }

        row.ProxyId = proxyId;
        BuildTimeline();
        MarkChanged("Proxy changed. Validate again before saving.");
    }

    private void DaysComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_buildingTimeline || sender is not ComboBox { Tag: ProxyTimelineRow row, SelectedItem: string dayGroup })
        {
            return;
        }

        row.DayGroup = dayGroup;
        MarkChanged("Day selection changed. Validate again before saving.");
    }

    private void ProxyHourCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_buildingTimeline || sender is not CheckBox { Tag: ProxyHourTag tag } checkBox)
        {
            return;
        }

        tag.Row.Hours[tag.Hour] = checkBox.IsChecked == true;
        MarkChanged("Hours changed. Validate again before saving.");
    }

    private async void ValidateButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(
            async () => { await ValidateAsync(showHealthyResult: true); },
            message => System.Diagnostics.Debug.WriteLine(message));

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(SaveAsync, message => System.Diagnostics.Debug.WriteLine(message));

    private async Task SaveAsync()
    {
        if (!_validatedSinceChange)
        {
            var choice = AppDialog.ShowCustom(
                this,
                "This setup has not passed the proxy health check since its last change.\n\nValidate and save tests every selected proxy against the Travian server. Save without check skips the network test but still blocks unsafe schedules.",
                "Save proxy schedule",
                [("Validate and save", MessageBoxResult.Yes), ("Save without check", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
                MessageBoxImage.Question,
                MessageBoxResult.Yes,
                MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Cancel)
            {
                return;
            }

            if (choice == MessageBoxResult.Yes && !await ValidateAsync(showHealthyResult: true))
            {
                return;
            }

            if (choice == MessageBoxResult.No)
            {
                AccountProxyPlan uncheckedPlan;
                try
                {
                    uncheckedPlan = BuildPlan();
                }
                catch (Exception ex)
                {
                    ValidationTextBlock.Text = $"Error: {ex.Message}";
                    return;
                }

                var structural = AccountProxyPlanValidator.Validate(
                    uncheckedPlan,
                    _library,
                    _accountName,
                    _neverUseOwnIp,
                    _sessionPacingEnabled,
                    _allowedHours,
                    _sleepMinMinutes,
                    requireHealth: false);
                ShowValidation(structural, showHealthyResult: false);
                if (!structural.IsValid)
                {
                    return;
                }
            }
        }

        ResultPlan = BuildPlan();
        _planStore.SaveActive(_accountName, ResultPlan);
        _planStore.DeleteDraft(_accountName);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _validationCts?.Cancel();
        Close();
    }

    private void ProxyListButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProxyLibraryWindow(_libraryStore, _accountNames, activeAccountName: _accountName)
        {
            Owner = this,
        };
        _ = dialog.ShowDialog();

        _library.Clear();
        _library.AddRange(_libraryStore.Load().OrderBy(proxy => proxy.DisplayName, StringComparer.OrdinalIgnoreCase));
        BuildTimeline();
        MarkChanged("Proxy list updated. Validate again before saving.");
    }

    private void VariationTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ValidationTextBlock is not null)
        {
            MarkChanged("Variation changed. Validate again before saving.");
        }
    }

    private void MarkChanged(string message)
    {
        _validatedSinceChange = false;
        if (ValidationTextBlock is not null)
        {
            ValidationTextBlock.Text = message;
        }
    }

    private async Task<bool> ValidateAsync(bool showHealthyResult)
    {
        AccountProxyPlan plan;
        try
        {
            plan = BuildPlan();
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"Error: {ex.Message}";
            return false;
        }

        ValidateButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var selected = _rows.Select(row => FindProxy(row.ProxyId)).Where(proxy => proxy is not null).Cast<ProxyLibraryEntry>().ToList();
            var tester = new ProxyListTester();
            for (var index = 0; index < selected.Count; index++)
            {
                var proxy = selected[index];
                ValidationTextBlock.Text = $"Testing proxy {index + 1} of {selected.Count}: {proxy.DisplayName}…";
                var probe = await tester.TestServerAgainstTargetAsync(proxy.Server, _serverUrl, _validationCts.Token);
                proxy.IsWorking = probe.Success;
                proxy.LatencyMs = probe.LatencyMs > 0 ? probe.LatencyMs : proxy.LatencyMs;
                proxy.LastFailureUtc = probe.Success ? null : probe.LatencyMs <= 0 ? DateTime.UtcNow : proxy.LastFailureUtc;
            }

            _libraryStore.Save(_library);
            BuildTimeline();
            var result = AccountProxyPlanValidator.Validate(
                plan,
                _library,
                _accountName,
                _neverUseOwnIp,
                _sessionPacingEnabled,
                _allowedHours,
                _sleepMinMinutes,
                requireHealth: true);
            ShowValidation(result, showHealthyResult);
            _validatedSinceChange = result.IsValid;
            return result.IsValid;
        }
        catch (OperationCanceledException)
        {
            ValidationTextBlock.Text = "Proxy validation was cancelled or timed out.";
            return false;
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"Proxy validation failed: {ex.Message}";
            return false;
        }
        finally
        {
            ValidateButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private AccountProxyPlan BuildPlan()
    {
        if (!int.TryParse(VariationTextBox.Text.Trim(), out var variation) || variation is < 0 or > 49)
        {
            throw new InvalidOperationException("Schedule variation must be a whole number between 0 and 49.");
        }

        if (_rows.Select(row => row.ProxyId).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
        {
            throw new InvalidOperationException("Proxy rotation requires at least two different proxies.");
        }

        return AccountProxyPlanNormalizer.Normalize(new AccountProxyPlan
        {
            Enabled = _rows.Count > 0,
            VariationPercent = variation,
            Assignments = _rows.Select(row => new AccountProxyAssignment
            {
                ProxyId = row.ProxyId,
                TimeBlocks = row.BuildTimeBlocks(),
            }).ToList(),
        });
    }

    private void ShowValidation(ProxyPlanValidationResult result, bool showHealthyResult)
    {
        ValidationTextBlock.Inlines.Clear();
        if (result.Issues.Count == 0 && showHealthyResult)
        {
            ValidationTextBlock.Inlines.Add(new Run("Setup is valid. All selected proxies passed the stability and Travian tests.")
            {
                Foreground = FindResource("SuccessTextBrush") as Brush,
            });
            return;
        }

        for (var index = 0; index < result.Issues.Count; index++)
        {
            var issue = result.Issues[index];
            if (index > 0)
            {
                ValidationTextBlock.Inlines.Add(new LineBreak());
            }

            ValidationTextBlock.Inlines.Add(new Run(
                $"{(issue.Severity == ProxyPlanIssueSeverity.Error ? "ERROR" : "WARNING")}: {issue.Message}")
            {
                Foreground = FindResource("DangerTextBrush") as Brush,
            });
        }
    }

    private ProxyLibraryEntry? FindProxy(string proxyId)
        => _library.FirstOrDefault(proxy => string.Equals(proxy.Id, proxyId, StringComparison.OrdinalIgnoreCase));

    private sealed record ProxyHourTag(ProxyTimelineRow Row, int Hour);
}

public sealed class ProxyTimelineRow
{
    public string ProxyId { get; set; }
    public string DayGroup { get; set; }
    public bool[] Hours { get; } = new bool[24];

    public ProxyTimelineRow(string proxyId, string dayGroup)
    {
        ProxyId = proxyId;
        DayGroup = dayGroup;
    }

    public static ProxyTimelineRow FromAssignment(AccountProxyAssignment assignment)
    {
        var allDays = assignment.TimeBlocks.SelectMany(block => block.Days).Distinct().ToHashSet();
        var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        var weekends = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday };
        var group = allDays.SetEquals(weekdays)
            ? "Weekdays"
            : allDays.SetEquals(weekends)
                ? "Weekends"
                : "All days";
        var row = new ProxyTimelineRow(assignment.ProxyId, group);
        var relevantDays = DaysFor(group);
        for (var hour = 0; hour < 24; hour++)
        {
            row.Hours[hour] = relevantDays.Any(day => assignment.TimeBlocks.Any(block => AccountProxyPlanValidator.Covers(block, day, hour)));
        }

        return row;
    }

    public List<ProxyTimeBlock> BuildTimeBlocks()
    {
        var days = DaysFor(DayGroup);
        if (Hours.All(selected => selected))
        {
            return [new ProxyTimeBlock { Days = days, FullDay = true }];
        }

        var blocks = new List<ProxyTimeBlock>();
        for (var hour = 0; hour < 24; hour++)
        {
            var previous = (hour + 23) % 24;
            if (!Hours[hour] || Hours[previous])
            {
                continue;
            }

            var end = (hour + 1) % 24;
            while (Hours[end] && end != hour)
            {
                end = (end + 1) % 24;
            }

            blocks.Add(new ProxyTimeBlock
            {
                Days = days.ToList(),
                StartHour = hour,
                EndHour = end,
            });
        }

        return blocks;
    }

    private static List<DayOfWeek> DaysFor(string group) => group switch
    {
        "Weekdays" => [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
        "Weekends" => [DayOfWeek.Saturday, DayOfWeek.Sunday],
        _ => Enum.GetValues<DayOfWeek>().ToList(),
    };
}
