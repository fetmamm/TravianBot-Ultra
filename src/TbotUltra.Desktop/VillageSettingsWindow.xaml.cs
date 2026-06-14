using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using TbotUltra.Desktop.Models;

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
    private readonly Action<VillageSettingsRow>? _onGroupsChanged;

    public VillageSettingsWindow(
        IReadOnlyList<VillageSettingsRow> rows,
        Action<VillageSettingsRow>? onEnabledChanged = null,
        Action<VillageSettingsRow>? onNpcTradeChanged = null,
        Action<VillageSettingsRow>? onGroupsChanged = null)
    {
        InitializeComponent();
        _rows = rows;
        _onEnabledChanged = onEnabledChanged;
        _onNpcTradeChanged = onNpcTradeChanged;
        _onGroupsChanged = onGroupsChanged;
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
                CellTemplate = BuildToggleCellTemplate($"GroupToggles[{i}].IsEnabled"),
            });
        }
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

    // Persists every row's current state (Auto / NPC / groups) via the callbacks, then closes. The persist
    // callbacks no-op when a value is unchanged, so writing all rows is cheap.
    private void SaveAndCloseButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            _onEnabledChanged?.Invoke(row);
            _onNpcTradeChanged?.Invoke(row);
            _onGroupsChanged?.Invoke(row);
        }

        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Discard: nothing was written, and the rows are rebuilt from the store next time the window opens.
        Close();
    }
}
