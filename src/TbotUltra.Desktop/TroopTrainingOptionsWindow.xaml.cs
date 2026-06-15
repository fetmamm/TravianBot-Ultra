using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop;

/// <summary>
/// Per-village troop-training settings popup. The caller seeds a <see cref="TroopTrainingViewModel"/> from
/// the village's saved override (or the global config when it has none) and passes it in; on Save the
/// window serialises the rows back into a <see cref="TroopTrainingPayload"/> via <see cref="Result"/> for
/// the caller to persist + re-queue. Mirrors <see cref="SmithyUpgradeOptionsWindow"/>.
/// </summary>
public partial class TroopTrainingOptionsWindow : Window
{
    private readonly TroopTrainingViewModel _viewModel;
    private readonly string _villageName;

    public TroopTrainingPayload? Result { get; private set; }

    // True when the user chose "Sync to all villages": the caller applies Result to every village.
    public bool SyncRequested { get; private set; }

    public TroopTrainingOptionsWindow(TroopTrainingViewModel viewModel, string villageName)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        _villageName = string.IsNullOrWhiteSpace(villageName) ? "this village" : villageName.Trim();
        SubtitleTextBlock.Text = $"Settings for village: {_villageName}";
    }

    // Re-uses the view model's existing config writer + the payload parser so the popup stays in lock-step
    // with how troop-training settings are serialised everywhere else.
    private TroopTrainingPayload? BuildPayload()
    {
        var config = new JsonObject();
        _viewModel.WriteToConfig(config);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config)
        {
            dict[pair.Key] = NodeToString(pair.Value);
        }

        return TroopTrainingPayload.TryFromDictionary(dict, out var payload) ? payload : null;
    }

    private static string NodeToString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b))
            {
                return b ? "true" : "false";
            }

            if (value.TryGetValue<long>(out var l))
            {
                return l.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var d))
            {
                return d.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<string>(out var s))
            {
                return s ?? string.Empty;
            }
        }

        return node.ToString();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Result = BuildPayload();
        DialogResult = true;
        Close();
    }

    private void SyncToAllButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = AppDialog.Show(
            this,
            $"Copy these troop-training settings from '{_villageName}' to ALL villages?\n\n"
            + "This overwrites every village's troop-training settings.",
            "Sync to all villages",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        Result = BuildPayload();
        SyncRequested = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
