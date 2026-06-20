using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
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
    private readonly Action<VillageSettingsRow>? _onGroupsChanged;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onTroopSettingsRequested;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onTownHallSettingsRequested;
    private readonly Action<IReadOnlyList<VillageSettingsRow>>? _onHeroResourceSettingsRequested;
    private readonly Action? _onSaved;

    public VillageSettingsWindow(
        IReadOnlyList<VillageSettingsRow> rows,
        Action<VillageSettingsRow>? onEnabledChanged = null,
        Action<VillageSettingsRow>? onNpcTradeChanged = null,
        Action<VillageSettingsRow>? onHeroResourcesChanged = null,
        Action<VillageSettingsRow>? onGroupsChanged = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onTroopSettingsRequested = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onTownHallSettingsRequested = null,
        Action<IReadOnlyList<VillageSettingsRow>>? onHeroResourceSettingsRequested = null,
        Action? onSaved = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _rows = rows;
        _onEnabledChanged = onEnabledChanged;
        _onNpcTradeChanged = onNpcTradeChanged;
        _onHeroResourcesChanged = onHeroResourcesChanged;
        _onGroupsChanged = onGroupsChanged;
        _onTroopSettingsRequested = onTroopSettingsRequested;
        _onTownHallSettingsRequested = onTownHallSettingsRequested;
        _onHeroResourceSettingsRequested = onHeroResourceSettingsRequested;
        _onSaved = onSaved;
        BuildGroupColumns(rows);
        VillageSettingsDataGrid.ItemsSource = rows;
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

            VillageSettingsDataGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = BuildColumnHeader(toggle.Title, tooltip),
                Width = DataGridLength.Auto,
                CellTemplate = BuildGroupCellTemplate(toggle.GroupKey, $"GroupToggles[{i}].IsEnabled"),
            });

            if (string.Equals(toggle.GroupKey, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase))
            {
                VillageSettingsDataGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = BuildColumnHeader(
                        "Hero res.",
                        "Selects which villages may use hero inventory resources. The Auto settings master toggle still applies."),
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
        NpcTradeColumn.DisplayIndex = 3 + groupsBeforeNpc.Count();
    }

    // Builds a column header: the title plus the reusable "i" tooltip icon (InfoTooltipIconStyle) so the
    // user can hover to read what the column does.
    private static FrameworkElement BuildColumnHeader(string title, string tooltip)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });

        var icon = new ContentControl
        {
            Margin = new Thickness(5, 0, 0, 0),
            ToolTip = tooltip,
        };
        if (Application.Current?.TryFindResource("InfoTooltipIconStyle") is Style iconStyle)
        {
            icon.Style = iconStyle;
        }

        panel.Children.Add(icon);
        return panel;
    }

    private static DataTemplate BuildToggleCellTemplate(string bindingPath)
    {
        var xaml =
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
            + "<CheckBox Style=\"{DynamicResource ToggleSwitchBlueStyle}\" HorizontalAlignment=\"Center\" "
            + "VerticalAlignment=\"Center\" Margin=\"6,2\" "
            + $"IsChecked=\"{{Binding {bindingPath}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}}\" />"
            + "</DataTemplate>";
        return (DataTemplate)XamlReader.Parse(xaml);
    }

    private DataTemplate BuildGroupCellTemplate(string groupKey, string bindingPath)
    {
        if (string.Equals(groupKey, QueueGroupCatalog.GetKey(QueueGroup.TroopTraining), StringComparison.OrdinalIgnoreCase))
        {
            return BuildToggleWithGearCellTemplate(bindingPath, "Open troop settings", TroopSettingsButton_Click);
        }

        if (string.Equals(groupKey, QueueGroupCatalog.GetKey(QueueGroup.TownHallCelebration), StringComparison.OrdinalIgnoreCase))
        {
            return BuildToggleWithGearCellTemplate(bindingPath, "Open Town Hall settings", TownHallSettingsButton_Click);
        }

        return BuildToggleCellTemplate(bindingPath);
    }

    private static DataTemplate BuildToggleWithGearCellTemplate(
        string bindingPath,
        string tooltip,
        RoutedEventHandler clickHandler)
    {
        var template = new DataTemplate();
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        panel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var toggle = new FrameworkElementFactory(typeof(CheckBox));
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitchBlueStyle");
        toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        toggle.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 2, 2, 2));
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(bindingPath)
        {
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
        });
        panel.AppendChild(toggle);

        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(FrameworkElement.WidthProperty, 24d);
        button.SetValue(FrameworkElement.HeightProperty, 24d);
        button.SetValue(Control.PaddingProperty, new Thickness(0));
        button.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 6, 0));
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

    private void TownHallSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _onTownHallSettingsRequested?.Invoke(_rows);
    }

    private void HeroResourceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _onHeroResourceSettingsRequested?.Invoke(_rows);
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
