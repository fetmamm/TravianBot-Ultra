using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Views;

// Central per-village settings panel. Lists the villages with their population plus per-village toggles.
// "Auto" turns the village on/off (green); "NPC" gates NPC trade; the per-group columns mirror the
// dashboard automation-loop cards per village (all blue). Bound-row changes are written immediately to
// VillageSettingsStore through the supplied callbacks.
public partial class VillageSettingsPanel : UserControl, IDisposable
{
    private readonly IReadOnlyList<VillageSettingsRow> _rows;
    private readonly IReadOnlyList<VillageSettingsRow> _gridRows;
    private readonly Action<VillageSettingsRow>? _onEnabledChanged;
    private readonly Action<VillageSettingsRow>? _onNpcTradeChanged;
    private readonly Action<VillageSettingsRow>? _onAttackScanChanged;
    private readonly Action<VillageSettingsRow>? _onTroopEvadeChanged;
    private readonly Action<VillageSettingsRow>? _onHeroResourcesChanged;
    private readonly Action<VillageSettingsRow>? _onConstructFasterChanged;
    private readonly Action<VillageSettingsRow>? _onRomanConstructionPriorityChanged;
    private readonly Action<VillageSettingsRow>? _onGroupsChanged;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onTroopSettingsRequested;
    private readonly Action<VillageSettingsRow>? _onSmithyUpgradeSettingsRequested;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onTownHallSettingsRequested;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onHeroResourceSettingsRequested;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onConstructFasterSettingsRequested;
    private readonly Action? _onSaved;
    private readonly Func<CancellationToken, Task<VillageOverviewProjection>>? _overviewProjectionProvider;
    private readonly Func<long>? _overviewSourceVersionProvider;
    private readonly LatestWinsProjectionCoordinator<VillageOverviewProjection> _overviewCoordinator = new();
    private readonly DispatcherTimer? _overviewRefreshTimer;
    private VillageOverviewProjection? _overviewProjection;
    private long _appliedOverviewSourceVersion = -1;
    private long _requestedOverviewSourceVersion = -1;
    private bool _overviewDisposed;
    private readonly ObservableCollection<UpcomingTaskRow> _upcomingTaskRows = [];
    private readonly ObservableCollection<VillageOverviewRow> _overviewVillageRows = [];
    private readonly Dictionary<VillageGroupToggle, VillageSettingsRow> _toggleOwners = [];
    private bool _isApplyingBulkChange;
    private bool _bulkChangeOccurred;

    internal VillageSettingsPanel(
        IReadOnlyList<VillageSettingsRow> rows,
        string section = "Settings",
        Action<VillageSettingsRow>? onEnabledChanged = null,
        Action<VillageSettingsRow>? onNpcTradeChanged = null,
        Action<VillageSettingsRow>? onAttackScanChanged = null,
        Action<VillageSettingsRow>? onTroopEvadeChanged = null,
        Action<VillageSettingsRow>? onHeroResourcesChanged = null,
        Action<VillageSettingsRow>? onConstructFasterChanged = null,
        Action<VillageSettingsRow>? onRomanConstructionPriorityChanged = null,
        Action<VillageSettingsRow>? onGroupsChanged = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onTroopSettingsRequested = null,
        Action<VillageSettingsRow>? onSmithyUpgradeSettingsRequested = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onTownHallSettingsRequested = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onHeroResourceSettingsRequested = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onConstructFasterSettingsRequested = null,
        Action? onSaved = null,
        Func<CancellationToken, Task<VillageOverviewProjection>>? overviewProjectionProvider = null,
        Func<long>? overviewSourceVersionProvider = null)
    {
        InitializeComponent();
        VillageSettingsTabControl.SelectedItem = string.Equals(section, "Overview", StringComparison.OrdinalIgnoreCase)
            ? OverviewTabItem
            : SettingsTabItem;
        VillageSettingsTabControl.Template = (ControlTemplate)FindResource("ContentOnlyTabControlTemplate");
        var showingOverview = ReferenceEquals(VillageSettingsTabControl.SelectedItem, OverviewTabItem);
        SectionTitleTextBlock.Text = showingOverview ? "Village overview" : "Village settings";
        VillageSettingsInfoIcon.Visibility = showingOverview ? Visibility.Collapsed : Visibility.Visible;
        _rows = rows;
        _gridRows = rows.Count == 0 ? rows : [CreateCheckAllRow(rows), .. rows];
        _onEnabledChanged = onEnabledChanged;
        _onNpcTradeChanged = onNpcTradeChanged;
        _onAttackScanChanged = onAttackScanChanged;
        _onTroopEvadeChanged = onTroopEvadeChanged;
        _onHeroResourcesChanged = onHeroResourcesChanged;
        _onConstructFasterChanged = onConstructFasterChanged;
        _onRomanConstructionPriorityChanged = onRomanConstructionPriorityChanged;
        _onGroupsChanged = onGroupsChanged;
        _onTroopSettingsRequested = onTroopSettingsRequested;
        _onSmithyUpgradeSettingsRequested = onSmithyUpgradeSettingsRequested;
        _onTownHallSettingsRequested = onTownHallSettingsRequested;
        _onHeroResourceSettingsRequested = onHeroResourceSettingsRequested;
        _onConstructFasterSettingsRequested = onConstructFasterSettingsRequested;
        _onSaved = onSaved;
        _overviewProjectionProvider = overviewProjectionProvider;
        _overviewSourceVersionProvider = overviewSourceVersionProvider;
        BuildGroupColumns(rows);
        BuildProtectionColumns();
        BuildOverviewColumns();
        ApplyTribeColumnVisibility(rows);
        VillageSettingsDataGrid.ItemsSource = _gridRows;
        UpcomingTasksDataGrid.ItemsSource = _upcomingTaskRows;
        VillageOverviewDataGrid.ItemsSource = _overviewVillageRows;
        SubscribeToSettingsChanges();

        if (_overviewProjectionProvider is not null)
        {
            OverviewUpdatedTextBlock.Text = "Loading overview...";
            _overviewRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _overviewRefreshTimer.Tick += async (_, _) => await RefreshOverviewAsync();
            Loaded += async (_, _) =>
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                await RefreshOverviewAsync(force: true);
                if (!_overviewDisposed)
                {
                    _overviewRefreshTimer.Start();
                }
            };
            Unloaded += (_, _) =>
            {
                _overviewRefreshTimer.Stop();
            };
        }
    }

    public void Dispose()
    {
        if (_overviewDisposed)
        {
            return;
        }

        _overviewDisposed = true;
        _overviewRefreshTimer?.Stop();
        _overviewCoordinator.Dispose();
        foreach (var row in _rows)
        {
            row.PropertyChanged -= SettingsRow_PropertyChanged;
            foreach (var toggle in row.GroupToggles)
            {
                toggle.PropertyChanged -= GroupToggle_PropertyChanged;
            }
        }

        _toggleOwners.Clear();
    }

    private void SubscribeToSettingsChanges()
    {
        foreach (var row in _rows)
        {
            row.PropertyChanged += SettingsRow_PropertyChanged;
            foreach (var toggle in row.GroupToggles)
            {
                _toggleOwners[toggle] = row;
                toggle.PropertyChanged += GroupToggle_PropertyChanged;
            }
        }
    }

    private void SettingsRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VillageSettingsRow row)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(VillageSettingsRow.IsEnabledForAutomation):
                _onEnabledChanged?.Invoke(row);
                break;
            case nameof(VillageSettingsRow.NpcTrade):
                _onNpcTradeChanged?.Invoke(row);
                break;
            case nameof(VillageSettingsRow.AttackScanEnabled):
                _onAttackScanChanged?.Invoke(row);
                break;
            case nameof(VillageSettingsRow.TroopEvadeEnabled):
                _onTroopEvadeChanged?.Invoke(row);
                break;
            case nameof(VillageSettingsRow.HeroResourcesEnabled):
                _onHeroResourcesChanged?.Invoke(row);
                break;
            case nameof(VillageSettingsRow.ConstructFasterEnabled):
                _onConstructFasterChanged?.Invoke(row);
                break;
            case nameof(VillageSettingsRow.RomanConstructionPriority):
                _onRomanConstructionPriorityChanged?.Invoke(row);
                break;
            default:
                return;
        }

        NotifySettingsChanged();
    }

    private void GroupToggle_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VillageGroupToggle.IsEnabled)
            || sender is not VillageGroupToggle toggle
            || !_toggleOwners.TryGetValue(toggle, out var row))
        {
            return;
        }

        _onGroupsChanged?.Invoke(row);
        NotifySettingsChanged();
    }

    private void NotifySettingsChanged()
    {
        if (_isApplyingBulkChange)
        {
            _bulkChangeOccurred = true;
            return;
        }

        _onSaved?.Invoke();
    }

    private static VillageSettingsRow CreateCheckAllRow(IReadOnlyList<VillageSettingsRow> rows) => new()
    {
        IsCheckAllRow = true,
        GroupToggles = rows[0].GroupToggles.Select(toggle => new VillageGroupToggle
        {
            GroupKey = toggle.GroupKey,
            Title = toggle.Title,
            Description = toggle.Description,
        }).ToList(),
    };

    private async Task RefreshOverviewAsync(bool force = false)
    {
        if (_overviewProjectionProvider is null)
        {
            return;
        }

        try
        {
            var sourceVersion = _overviewSourceVersionProvider?.Invoke() ?? 0;
            if (force || _overviewProjection is null || sourceVersion != _appliedOverviewSourceVersion)
            {
                if (!force && sourceVersion == _requestedOverviewSourceVersion)
                {
                    if (_overviewProjection is not null)
                    {
                        ApplyOverviewSnapshot(_overviewProjection.Render(DateTimeOffset.UtcNow));
                    }

                    return;
                }

                _requestedOverviewSourceVersion = sourceVersion;
                await _overviewCoordinator.RequestAsync(
                    _overviewProjectionProvider,
                    projection =>
                    {
                        _overviewProjection = projection;
                        _appliedOverviewSourceVersion = sourceVersion;
                        ApplyOverviewSnapshot(projection.Render(DateTimeOffset.UtcNow));
                    });
                return;
            }

            ApplyOverviewSnapshot(_overviewProjection.Render(DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _requestedOverviewSourceVersion = -1;
            OverviewUpdatedTextBlock.Text = $"Overview unavailable: {ex.Message}";
        }
    }

    private void ApplyOverviewSnapshot(VillageOverviewSnapshot snapshot)
    {
        OverviewRunningTaskTextBlock.Text = $"Running: {snapshot.RunningTask}";
        OverviewUpdatedTextBlock.Text = $"Updated {snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss}";
        var upcoming = snapshot.UpcomingTasks.ToList();
        if (upcoming.Count < 5)
        {
            upcoming.Add(new UpcomingTaskRow("-", "No more schedulable tasks", "-", "-", "-", "-"));
        }

        ReplaceRows(_upcomingTaskRows, upcoming);
        ReplaceRows(_overviewVillageRows, snapshot.Villages);
    }

    private static void ReplaceRows<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        var sharedCount = Math.Min(target.Count, source.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(target[index], source[index]))
            {
                target[index] = source[index];
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (var index = sharedCount; index < source.Count; index++)
        {
            target.Add(source[index]);
        }
    }

    // Adds one blue toggle-switch column per automation group (header = group title + a tooltip icon
    // describing the group). Columns bind to GroupToggles[i].IsEnabled — every row has the same group order,
    // so a positional binding lines each column up with its group across all rows.
    private void BuildGroupColumns(IReadOnlyList<VillageSettingsRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var template = rows[0].GroupToggles;
        var demolishKey = QueueGroupCatalog.GetKey(QueueGroup.Demolish);
        for (var i = 0; i < template.Count; i++)
        {
            var groupIndex = i;
            var toggle = template[i];
            if (string.Equals(toggle.GroupKey, demolishKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tooltip = string.IsNullOrWhiteSpace(toggle.Description)
                ? $"Uncheck to stop \"{toggle.Title}\" running in this village."
                : $"{toggle.Description} Uncheck to stop it running in this village.";
            var headerTitle = ShortColumnTitle(toggle.Title);
            if (!string.Equals(headerTitle, toggle.Title, StringComparison.Ordinal))
            {
                tooltip = $"{toggle.Title}. {tooltip}";
            }

            VillageSettingsDataGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = BuildColumnHeader(headerTitle, tooltip),
                Width = DataGridLength.Auto,
                CellTemplate = BuildGroupCellTemplate(
                    toggle.GroupKey,
                    $"GroupToggles[{i}].IsEnabled",
                    (_, _) => ToggleAllGroup(groupIndex)),
            });

            if (string.Equals(toggle.GroupKey, QueueGroupCatalog.GetKey(QueueGroup.Construction), StringComparison.OrdinalIgnoreCase))
            {
                if (rows.Any(row => string.Equals(row.TribeText, "Romans", StringComparison.OrdinalIgnoreCase)))
                {
                    VillageSettingsDataGrid.Columns.Add(new DataGridTemplateColumn
                    {
                        Header = BuildColumnHeader(
                            "Roman priority",
                            "Auto follows the earliest runnable construction category. Resources targets two resource fields and one building; Buildings targets two buildings and one resource field."),
                        Width = DataGridLength.Auto,
                        CellTemplate = BuildRomanConstructionPriorityCellTemplate(),
                    });
                }

                VillageSettingsDataGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = BuildColumnHeader(
                        "25% construct.",
                        "Construct 25% faster. Enables Official Travian construct-faster bonus videos for this village."),
                    Width = DataGridLength.Auto,
                    CellTemplate = BuildToggleWithGearCellTemplate(
                        nameof(VillageSettingsRow.ConstructFasterEnabled),
                        "Open Construct 25% faster settings",
                        ConstructFasterSettingsButton_Click,
                        (_, _) => ToggleAllRows(
                            row => row.ConstructFasterEnabled,
                            (row, isEnabled) => row.ConstructFasterEnabled = isEnabled),
                        "ToggleSwitchPurpleStyle"),
                });
            }

            if (string.Equals(toggle.GroupKey, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase))
            {
                VillageSettingsDataGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = BuildColumnHeader(
                        "Hero res.",
                        "Selects which villages may use hero inventory resources."),
                    Width = DataGridLength.Auto,
                    CellTemplate = BuildToggleWithGearCellTemplate(
                        nameof(VillageSettingsRow.HeroResourcesEnabled),
                        "Open Hero resource settings",
                        HeroResourceSettingsButton_Click,
                        (_, _) => ToggleAllRows(
                            row => row.HeroResourcesEnabled,
                            (row, isEnabled) => row.HeroResourcesEnabled = isEnabled)),
                });
            }
        }

        var resourceTransferKey = QueueGroupCatalog.GetKey(QueueGroup.ResourceTransfer);
        var reinforcementsKey = QueueGroupCatalog.GetKey(QueueGroup.Reinforcements);
        var groupsBeforeNpc = template
            .Where(toggle => !string.Equals(toggle.GroupKey, demolishKey, StringComparison.OrdinalIgnoreCase))
            .TakeWhile(toggle =>
                !string.Equals(toggle.GroupKey, resourceTransferKey, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(toggle.GroupKey, reinforcementsKey, StringComparison.OrdinalIgnoreCase));
        var constructFasterColumnBeforeNpc = template.Any(toggle =>
            string.Equals(toggle.GroupKey, QueueGroupCatalog.GetKey(QueueGroup.Construction), StringComparison.OrdinalIgnoreCase))
            ? 1
            : 0;
        var romanPriorityColumnBeforeNpc = constructFasterColumnBeforeNpc == 1
            && rows.Any(row => string.Equals(row.TribeText, "Romans", StringComparison.OrdinalIgnoreCase))
                ? 1
                : 0;
        NpcTradeColumn.DisplayIndex = 3
            + groupsBeforeNpc.Count()
            + constructFasterColumnBeforeNpc
            + romanPriorityColumnBeforeNpc;
    }

    private void BuildProtectionColumns()
    {
        var townHallEntry = VillageSettingsDataGrid.Columns
            .Select((column, index) => (Column: column, Index: index))
            .FirstOrDefault(entry => string.Equals(
                (entry.Column.Header as TextBlock)?.Text,
                "Town Hall",
                StringComparison.OrdinalIgnoreCase));
        var insertIndex = townHallEntry.Column is null
            ? VillageSettingsDataGrid.Columns.Count
            : townHallEntry.Index + 1;

        VillageSettingsDataGrid.Columns.Insert(insertIndex, new DataGridTemplateColumn
        {
            Header = BuildColumnHeader(
                "Attack scan",
                "Monitor this village for incoming attacks. Disabling it also prevents troop evasion from running there."),
            Width = DataGridLength.Auto,
            CellTemplate = BuildToggleCellTemplate(
                nameof(VillageSettingsRow.AttackScanEnabled),
                (_, _) => ToggleAllRows(
                    row => row.AttackScanEnabled,
                    (row, isEnabled) => row.AttackScanEnabled = isEnabled)),
        });
        VillageSettingsDataGrid.Columns.Insert(insertIndex + 1, new DataGridTemplateColumn
        {
            Header = BuildColumnHeader(
                "Troop evade",
                "Enable troop evasion for this village when its complete evasion settings are valid."),
            Width = DataGridLength.Auto,
            CellTemplate = BuildToggleCellTemplate(
                nameof(VillageSettingsRow.TroopEvadeEnabled),
                (_, _) => ToggleAllRows(
                    row => row.TroopEvadeEnabled,
                    (row, isEnabled) => row.TroopEvadeEnabled = isEnabled)),
        });
    }

    // Builds the per-village overview columns in code so every status cell shares one color-coded template
    // (OverviewStatusText: Ready green / Waiting amber / Disabled muted). Columns auto-size to their content
    // so idle "Disabled" columns stay narrow, capped by a per-column max so a long active line wraps instead
    // of stretching the whole grid.
    // On a normal server every village has the account's tribe, so a Tribe column would just repeat
    // the same word on every row. Only special servers (one tribe per village) make it worth the
    // width, and those are exactly the accounts where the villages report more than one tribe.
    private void ApplyTribeColumnVisibility(IReadOnlyList<VillageSettingsRow> rows)
    {
        var distinctTribes = rows
            .Select(row => row.TribeText)
            .Where(tribe => !string.IsNullOrWhiteSpace(tribe))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        TribeColumn.Visibility = distinctTribes > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildOverviewColumns()
    {
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Village", 170, nameof(VillageOverviewRow.Village), colorize: false));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Next task", 240, nameof(VillageOverviewRow.NextTask)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn(
            "Construction queue",
            190,
            nameof(VillageOverviewRow.ConstructionQueue),
            highlightTrailingParenthetical: true));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Construction", 220, nameof(VillageOverviewRow.Construction)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Smithy", 220, nameof(VillageOverviewRow.Smithy)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Build troops", 180, nameof(VillageOverviewRow.BuildTroops)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Farming", 220, nameof(VillageOverviewRow.Farming)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Hero", 170, nameof(VillageOverviewRow.Hero)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Town Hall", 170, nameof(VillageOverviewRow.TownHall)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Brewery", 170, nameof(VillageOverviewRow.Brewery)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Resource transfer", 190, nameof(VillageOverviewRow.ResourceTransfer)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Reinforcements", 190, nameof(VillageOverviewRow.Reinforcements)));
    }

    // A single overview cell: a wrapping TextBlock whose status text is color-coded per line via the
    // OverviewStatusText attached property. Fixed widths avoid a full column re-measure on every countdown
    // tick. The Village name column binds plain Text (colorize: false) so a village name is never mistaken
    // for a status keyword.
    private static DataGridTemplateColumn BuildOverviewColumn(
        string header,
        double maxWidth,
        string bindingPath,
        bool colorize = true,
        bool highlightTrailingParenthetical = false)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        var binding = new System.Windows.Data.Binding(bindingPath);
        text.SetBinding(colorize ? OverviewStatusText.TextProperty : TextBlock.TextProperty, binding);
        if (highlightTrailingParenthetical)
        {
            text.SetValue(OverviewStatusText.HighlightTrailingParentheticalProperty, true);
        }

        return new DataGridTemplateColumn
        {
            Header = header,
            Width = new DataGridLength(maxWidth),
            MinWidth = 60,
            CellTemplate = new DataTemplate { VisualTree = text },
        };
    }

    private static FrameworkElement BuildColumnHeader(string title, string tooltip)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
        };
        ToolTipService.SetInitialShowDelay(header, 100);
        return header;
    }

    private void CheckAllAutomationButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleAllRows(
            row => row.IsEnabledForAutomation,
            (row, isEnabled) => row.IsEnabledForAutomation = isEnabled);
    }

    private void CheckAllNpcTradeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleAllRows(
            row => row.NpcTrade,
            (row, isEnabled) => row.NpcTrade = isEnabled);
    }

    private void ToggleAllGroup(int groupIndex)
    {
        ToggleAllRows(
            row => groupIndex < row.GroupToggles.Count && row.GroupToggles[groupIndex].IsEnabled,
            (row, isEnabled) =>
            {
                if (groupIndex < row.GroupToggles.Count)
                {
                    row.GroupToggles[groupIndex].IsEnabled = isEnabled;
                }
            },
            row => groupIndex < row.GroupToggles.Count && row.GroupToggles[groupIndex].CanToggle);
    }

    private void ToggleAllRows(
        Func<VillageSettingsRow, bool> isChecked,
        Action<VillageSettingsRow, bool> setChecked,
        Func<VillageSettingsRow, bool>? canToggle = null)
    {
        var eligibleRows = (canToggle is null ? _rows : _rows.Where(canToggle)).ToList();
        if (eligibleRows.Count == 0)
        {
            return;
        }

        var checkAll = eligibleRows.Any(row => !isChecked(row));
        _isApplyingBulkChange = true;
        try
        {
            foreach (var row in eligibleRows)
            {
                setChecked(row, checkAll);
            }
        }
        finally
        {
            _isApplyingBulkChange = false;
            if (_bulkChangeOccurred)
            {
                _bulkChangeOccurred = false;
                _onSaved?.Invoke();
            }
        }
    }

    // Display-only short titles so every column fits without horizontal scrolling. The full name is
    // prepended to the header tooltip when shortened.
    private static string ShortColumnTitle(string title) => title switch
    {
        "Hero adv." => "Adventure",
        "Construction" => "Construct.",
        "Upgrade Troops" => "Smithy",
        "Build Troops" => "Build troops",
        "Reinforcements" => "Reinf.",
        "Resource Transfer" => "Res. transfer",
        _ => title,
    };

    private static DataTemplate BuildToggleCellTemplate(string bindingPath, RoutedEventHandler checkAllClick)
    {
        var canToggleBindingPath = ResolveCanToggleBindingPath(bindingPath);
        var template = new DataTemplate();
        var grid = new FrameworkElementFactory(typeof(Grid));

        var toggle = new FrameworkElementFactory(typeof(CheckBox)) { Name = "Toggle" };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitchBlueStyle");
        toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        toggle.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
        toggle.SetBinding(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(canToggleBindingPath));
        toggle.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(VillageSettingsRow.ToggleVisibility)));
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(bindingPath)
        {
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
        });
        grid.AppendChild(toggle);

        var checkAll = BuildCheckAllButton(checkAllClick, "CheckAll");
        grid.AppendChild(checkAll);
        template.VisualTree = grid;
        template.Seal();
        return template;
    }

    private static DataTemplate BuildRomanConstructionPriorityCellTemplate()
    {
        var template = new DataTemplate();
        var comboBox = new FrameworkElementFactory(typeof(ComboBox));
        comboBox.SetValue(FrameworkElement.MinWidthProperty, 92d);
        comboBox.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 3, 0));
        comboBox.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(
            nameof(VillageSettingsRow.RomanConstructionPriorityVisibility)));
        comboBox.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(
            nameof(VillageSettingsRow.RomanConstructionPriorityOptions)));
        comboBox.SetBinding(Selector.SelectedItemProperty, new System.Windows.Data.Binding(
            nameof(VillageSettingsRow.RomanConstructionPriority))
        {
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
        });
        template.VisualTree = comboBox;
        template.Seal();
        return template;
    }

    private static string ResolveCanToggleBindingPath(string bindingPath)
    {
        const string enabledSuffix = ".IsEnabled";
        return bindingPath.EndsWith(enabledSuffix, StringComparison.Ordinal)
            ? bindingPath[..^enabledSuffix.Length] + ".CanToggle"
            : "CanToggle";
    }

    private DataTemplate BuildGroupCellTemplate(string groupKey, string bindingPath, RoutedEventHandler checkAllClick)
    {
        if (string.Equals(groupKey, QueueGroupCatalog.GetKey(QueueGroup.TroopTraining), StringComparison.OrdinalIgnoreCase))
        {
            return BuildToggleWithGearCellTemplate(bindingPath, "Open troop settings", TroopSettingsButton_Click, checkAllClick);
        }

        if (string.Equals(groupKey, QueueGroupCatalog.GetKey(QueueGroup.Troops), StringComparison.OrdinalIgnoreCase))
        {
            return BuildToggleWithGearCellTemplate(bindingPath, "Open Upgrade options", SmithyUpgradeSettingsButton_Click, checkAllClick);
        }

        if (string.Equals(groupKey, QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration), StringComparison.OrdinalIgnoreCase))
        {
            return BuildToggleWithGearCellTemplate(bindingPath, "Open Bot Settings > Celebrations", TownHallSettingsButton_Click, checkAllClick);
        }

        return BuildToggleCellTemplate(bindingPath, checkAllClick);
    }

    private static DataTemplate BuildToggleWithGearCellTemplate(
        string bindingPath,
        string tooltip,
        RoutedEventHandler clickHandler,
        RoutedEventHandler checkAllClick,
        string toggleStyleKey = "ToggleSwitchBlueStyle")
    {
        var template = new DataTemplate();
        var grid = new FrameworkElementFactory(typeof(Grid));
        var panel = new FrameworkElementFactory(typeof(StackPanel)) { Name = "RowControls" };
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        panel.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(VillageSettingsRow.ToggleVisibility)));
        panel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var toggle = new FrameworkElementFactory(typeof(CheckBox));
        toggle.SetResourceReference(FrameworkElement.StyleProperty, toggleStyleKey);
        toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        toggle.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 0, 0));
        toggle.SetBinding(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(ResolveCanToggleBindingPath(bindingPath)));
        toggle.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(VillageSettingsRow.ToggleVisibility)));
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(bindingPath)
        {
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
        });
        panel.AppendChild(toggle);

        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(FrameworkElement.WidthProperty, 22d);
        button.SetValue(FrameworkElement.HeightProperty, 22d);
        button.SetValue(Control.PaddingProperty, new Thickness(0));
        button.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
        button.SetValue(Control.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        button.SetValue(ContentControl.ContentProperty, "\uE713");
        button.SetValue(FrameworkElement.ToolTipProperty, tooltip);
        button.AddHandler(ButtonBase.ClickEvent, clickHandler);
        panel.AppendChild(button);

        grid.AppendChild(panel);
        grid.AppendChild(BuildCheckAllButton(checkAllClick, "CheckAll"));
        template.VisualTree = grid;
        template.Seal();
        return template;
    }

    private static FrameworkElementFactory BuildCheckAllButton(RoutedEventHandler clickHandler, string name)
    {
        var button = new FrameworkElementFactory(typeof(Button)) { Name = name };
        button.SetValue(ContentControl.ContentProperty, "Check all");
        button.SetResourceReference(FrameworkElement.StyleProperty, "CheckAllButtonStyle");
        button.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        button.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        button.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(VillageSettingsRow.CheckAllVisibility)));
        button.SetValue(FrameworkElement.ToolTipProperty, "Checks every available village in this column. If all are checked, clears them.");
        button.AddHandler(ButtonBase.ClickEvent, clickHandler);
        return button;
    }

    private void TroopSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _onTroopSettingsRequested?.Invoke(_rows);
    }

    private void SmithyUpgradeSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // The overview is per-row: open the settings for the village on the CLICKED gear's row (its
        // DataContext), not whichever village the bot/UI is currently on.
        if ((sender as FrameworkElement)?.DataContext is VillageSettingsRow row)
        {
            _onSmithyUpgradeSettingsRequested?.Invoke(row);
        }
    }

    private void TownHallSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _onTownHallSettingsRequested?.Invoke(_rows);
    }

    private void HeroResourceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _onHeroResourceSettingsRequested?.Invoke(_rows);
    }

    private void ConstructFasterSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _onConstructFasterSettingsRequested?.Invoke(_rows);
    }

}
