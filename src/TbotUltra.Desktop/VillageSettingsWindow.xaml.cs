using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop;

// Central per-village settings window. Lists the villages with their population and coordinates plus
// per-village toggle columns. The leftmost "Auto" toggle is wired to VillageSettingsStore (persisted
// per account); the remaining columns (Hero res, NPC, Build troops, Upgrade troops, Farming) are not
// wired to automation yet.
public partial class VillageSettingsWindow : Window
{
    private readonly IReadOnlyList<VillageSettingsRow> _rows;
    private readonly Action<VillageSettingsRow>? _onEnabledChanged;
    private readonly Action<VillageSettingsRow>? _onNpcTradeChanged;

    public VillageSettingsWindow(
        IReadOnlyList<VillageSettingsRow> rows,
        Action<VillageSettingsRow>? onEnabledChanged = null,
        Action<VillageSettingsRow>? onNpcTradeChanged = null)
    {
        InitializeComponent();
        _rows = rows;
        _onEnabledChanged = onEnabledChanged;
        _onNpcTradeChanged = onNpcTradeChanged;
        VillageSettingsDataGrid.ItemsSource = rows;

        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        Closed += (_, _) =>
        {
            foreach (var row in _rows)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }
        };
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VillageSettingsRow row)
        {
            return;
        }

        if (e.PropertyName == nameof(VillageSettingsRow.IsEnabledForAutomation))
        {
            _onEnabledChanged?.Invoke(row);
        }
        else if (e.PropertyName == nameof(VillageSettingsRow.NpcTrade))
        {
            _onNpcTradeChanged?.Invoke(row);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
