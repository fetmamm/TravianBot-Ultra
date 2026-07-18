using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

// Central per-village settings window. Lists the villages with their population plus per-village toggles.
// "Auto" turns the village on/off (green); "NPC" gates NPC trade; the per-group columns mirror the
// dashboard automation-loop cards per village (all blue). Changes are buffered in the bound rows and only
// written to VillageSettingsStore (via the callbacks) when the user clicks "Save & close"; "Close" discards.
public partial class VillageSettingsWindow : Window
{
    private readonly IReadOnlyList<VillageSettingsRow> _rows;
    private readonly Action<VillageSettingsRow>? _onEnabledChanged;
    private readonly Action<VillageSettingsRow>? _onNpcTradeChanged;
    private readonly Action<VillageSettingsRow>? _onHeroResourcesChanged;
    private readonly Action<VillageSettingsRow>? _onConstructFasterChanged;
    private readonly Action<VillageSettingsRow>? _onGroupsChanged;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onTroopSettingsRequested;
    private readonly Action<VillageSettingsRow>? _onSmithyUpgradeSettingsRequested;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onTownHallSettingsRequested;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onHeroResourceSettingsRequested;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onConstructFasterSettingsRequested;
    private readonly Action? _onSaved;
    private readonly Func<VillageOverviewSnapshot>? _overviewSnapshotProvider;
    private readonly DispatcherTimer? _overviewRefreshTimer;
    private readonly ObservableCollection<UpcomingTaskRow> _upcomingTaskRows = [];
    private readonly ObservableCollection<VillageOverviewRow> _overviewVillageRows = [];

    public VillageSettingsWindow(
        IReadOnlyList<VillageSettingsRow> rows,
        Action<VillageSettingsRow>? onEnabledChanged = null,
        Action<VillageSettingsRow>? onNpcTradeChanged = null,
        Action<VillageSettingsRow>? onHeroResourcesChanged = null,
        Action<VillageSettingsRow>? onConstructFasterChanged = null,
        Action<VillageSettingsRow>? onGroupsChanged = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onTroopSettingsRequested = null,
        Action<VillageSettingsRow>? onSmithyUpgradeSettingsRequested = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onTownHallSettingsRequested = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onHeroResourceSettingsRequested = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onConstructFasterSettingsRequested = null,
        Action? onSaved = null,
        Func<VillageOverviewSnapshot>? overviewSnapshotProvider = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _rows = rows;
        _onEnabledChanged = onEnabledChanged;
        _onNpcTradeChanged = onNpcTradeChanged;
        _onHeroResourcesChanged = onHeroResourcesChanged;
        _onConstructFasterChanged = onConstructFasterChanged;
        _onGroupsChanged = onGroupsChanged;
        _onTroopSettingsRequested = onTroopSettingsRequested;
        _onSmithyUpgradeSettingsRequested = onSmithyUpgradeSettingsRequested;
        _onTownHallSettingsRequested = onTownHallSettingsRequested;
        _onHeroResourceSettingsRequested = onHeroResourceSettingsRequested;
        _onConstructFasterSettingsRequested = onConstructFasterSettingsRequested;
        _onSaved = onSaved;
        _overviewSnapshotProvider = overviewSnapshotProvider;
        BuildGroupColumns(rows);
        BuildOverviewColumns();
        VillageSettingsDataGrid.ItemsSource = rows;
        UpcomingTasksDataGrid.ItemsSource = _upcomingTaskRows;
        VillageOverviewDataGrid.ItemsSource = _overviewVillageRows;

        if (_overviewSnapshotProvider is not null)
        {
            RefreshOverview();
            _overviewRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _overviewRefreshTimer.Tick += (_, _) => RefreshOverview();
            _overviewRefreshTimer.Start();
            Closed += (_, _) => _overviewRefreshTimer.Stop();
        }
    }

    private void RefreshOverview()
    {
        if (_overviewSnapshotProvider is null)
        {
            return;
        }

        try
        {
            var snapshot = _overviewSnapshotProvider();
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
        catch (Exception ex)
        {
            OverviewUpdatedTextBlock.Text = $"Overview unavailable: {ex.Message}";
        }
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
        for (var i = 0; i < template.Count; i++)
        {
            var toggle = template[i];
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
                CellTemplate = BuildGroupCellTemplate(toggle.GroupKey, $"GroupToggles[{i}].IsEnabled"),
            });

            if (string.Equals(toggle.GroupKey, QueueGroupCatalog.GetKey(QueueGroup.Construction), StringComparison.OrdinalIgnoreCase))
            {
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
                        HeroResourceSettingsButton_Click),
                });
            }
        }

        var resourceTransferKey = QueueGroupCatalog.GetKey(QueueGroup.ResourceTransfer);
        var reinforcementsKey = QueueGroupCatalog.GetKey(QueueGroup.Reinforcements);
        var groupsBeforeNpc = template.TakeWhile(toggle =>
            !string.Equals(toggle.GroupKey, resourceTransferKey, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(toggle.GroupKey, reinforcementsKey, StringComparison.OrdinalIgnoreCase));
        var constructFasterColumnBeforeNpc = template.Any(toggle =>
            string.Equals(toggle.GroupKey, QueueGroupCatalog.GetKey(QueueGroup.Construction), StringComparison.OrdinalIgnoreCase))
            ? 1
            : 0;
        NpcTradeColumn.DisplayIndex = 3 + groupsBeforeNpc.Count() + constructFasterColumnBeforeNpc;
    }

    // Builds the per-village overview columns in code so every status cell shares one color-coded template
    // (OverviewStatusText: Ready green / Waiting amber / Disabled muted). Columns auto-size to their content
    // so idle "Disabled" columns stay narrow, capped by a per-column max so a long active line wraps instead
    // of stretching the whole grid.
    private void BuildOverviewColumns()
    {
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Village", 170, nameof(VillageOverviewRow.Village), colorize: false));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Next task", 240, nameof(VillageOverviewRow.NextTask)));
        VillageOverviewDataGrid.Columns.Add(BuildOverviewColumn("Construction queue", 130, nameof(VillageOverviewRow.ConstructionQueue)));
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
    // OverviewStatusText attached property. The column auto-sizes to content up to maxWidth (then the text
    // wraps). The Village name column binds plain Text (colorize: false) so a village name is never mistaken
    // for a status keyword.
    private static DataGridTemplateColumn BuildOverviewColumn(string header, double maxWidth, string bindingPath, bool colorize = true)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        var binding = new System.Windows.Data.Binding(bindingPath);
        text.SetBinding(colorize ? OverviewStatusText.TextProperty : TextBlock.TextProperty, binding);

        return new DataGridTemplateColumn
        {
            Header = header,
            Width = DataGridLength.Auto,
            MinWidth = 60,
            MaxWidth = maxWidth,
            CellTemplate = new DataTemplate { VisualTree = text },
        };
    }

    // Builds a compact column header: small title text with the explanation as tooltip directly on the
    // text (no separate "i" icon — with 10+ columns the icons alone cost ~200px of width).
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

    private static DataTemplate BuildToggleCellTemplate(string bindingPath)
    {
        var canToggleBindingPath = ResolveCanToggleBindingPath(bindingPath);
        var xaml =
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
            + "<CheckBox Style=\"{DynamicResource ToggleSwitchBlueStyle}\" HorizontalAlignment=\"Center\" "
            + "VerticalAlignment=\"Center\" Margin=\"2,0\" "
            + $"IsEnabled=\"{{Binding {canToggleBindingPath}}}\" "
            + $"IsChecked=\"{{Binding {bindingPath}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}}\" />"
            + "</DataTemplate>";
        return (DataTemplate)XamlReader.Parse(xaml);
    }

    private static string ResolveCanToggleBindingPath(string bindingPath)
    {
        const string enabledSuffix = ".IsEnabled";
        return bindingPath.EndsWith(enabledSuffix, StringComparison.Ordinal)
            ? bindingPath[..^enabledSuffix.Length] + ".CanToggle"
            : "CanToggle";
    }

    private DataTemplate BuildGroupCellTemplate(string groupKey, string bindingPath)
    {
        if (string.Equals(groupKey, QueueGroupCatalog.GetKey(QueueGroup.TroopTraining), StringComparison.OrdinalIgnoreCase))
        {
            return BuildToggleWithGearCellTemplate(bindingPath, "Open troop settings", TroopSettingsButton_Click);
        }

        if (string.Equals(groupKey, QueueGroupCatalog.GetKey(QueueGroup.Troops), StringComparison.OrdinalIgnoreCase))
        {
            return BuildToggleWithGearCellTemplate(bindingPath, "Open Upgrade options", SmithyUpgradeSettingsButton_Click);
        }

        if (string.Equals(groupKey, QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration), StringComparison.OrdinalIgnoreCase))
        {
            return BuildToggleWithGearCellTemplate(bindingPath, "Open Bot Settings > Celebrations", TownHallSettingsButton_Click);
        }

        return BuildToggleCellTemplate(bindingPath);
    }

    private static DataTemplate BuildToggleWithGearCellTemplate(
        string bindingPath,
        string tooltip,
        RoutedEventHandler clickHandler,
        string toggleStyleKey = "ToggleSwitchBlueStyle")
    {
        var template = new DataTemplate();
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        panel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var toggle = new FrameworkElementFactory(typeof(CheckBox));
        toggle.SetResourceReference(FrameworkElement.StyleProperty, toggleStyleKey);
        toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        toggle.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 0, 0));
        toggle.SetBinding(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(ResolveCanToggleBindingPath(bindingPath)));
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

        template.VisualTree = panel;
        return template;
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

    // Persists every row's current state via the callbacks, then closes. The persist callbacks no-op when
    // a value is unchanged, so writing all rows is cheap.
    private void SaveAndCloseButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            _onEnabledChanged?.Invoke(row);
            _onNpcTradeChanged?.Invoke(row);
            _onHeroResourcesChanged?.Invoke(row);
            _onConstructFasterChanged?.Invoke(row);
            _onGroupsChanged?.Invoke(row);
        }

        _onSaved?.Invoke();
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Discard: nothing was written, and the rows are rebuilt from the store next time the window opens.
        Close();
    }
}
