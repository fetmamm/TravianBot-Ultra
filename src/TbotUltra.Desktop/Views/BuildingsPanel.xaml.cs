using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop.Views;

/// <summary>
/// Visible Buildings tab content. Service-bound and queue-bound logic stays on
/// MainWindow; this panel only routes the visible interactions back to the host.
/// Hidden compatibility controls remain on MainWindow.
/// </summary>
public partial class BuildingsPanel : UserControl
{
    private MainWindow? _hostCache;
    // Guards the setting handlers while we seed the UI, so loading does not immediately write back.
    private bool _suppressHumanizeWrite;

    public BuildingsPanel()
    {
        InitializeComponent();
        Loaded += BuildingsPanel_Loaded;
        // Re-seed whenever the tab becomes visible too: at first Loaded the host window / config may
        // not be ready yet, which would otherwise leave the checkbox showing its XAML default.
        IsVisibleChanged += BuildingsPanel_IsVisibleChanged;
    }

    private MainWindow? Host
    {
        get
        {
            if (_hostCache is not null)
            {
                return _hostCache;
            }

            _hostCache = Window.GetWindow(this) as MainWindow;
            return _hostCache;
        }
    }

    private async void LoadBuildingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Host is { } host)
        {
            await host.GuardUiAsync(host.OnLoadBuildingsClicked);
        }
    }

    private void UpgradeAllBuildingsToMaxButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnUpgradeAllBuildingsToMaxClicked();
    }

    private void BuildingTemplatesButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.OnBuildingTemplatesClicked();
    }

    private void BuildingTopSlotsView_Filter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is BuildingSlotRow row && BuildingsViewModel.IsPinnedBuildingTopSlot(row.SlotId);
    }

    private void BuildingRemainingSlotsView_Filter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is BuildingSlotRow row && !BuildingsViewModel.IsPinnedBuildingTopSlot(row.SlotId);
    }

    private void BuildingSlotCircleButton_Click(object sender, RoutedEventArgs e)
    {
        Host?.BuildingSlotCircleButton_Click(sender, e);
    }

    private void BuildingsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        LoadConstructionHumanizeSettings();
    }

    private void BuildingsPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            LoadConstructionHumanizeSettings();
        }
    }

    private void LoadConstructionHumanizeSettings()
    {
        if (ConstructionHumanizeCheckBox is null || Host is not { } host)
        {
            return;
        }

        _suppressHumanizeWrite = true;
        try
        {
            var settings = host.GetConstructionHumanizeSettings();
            ConstructionHumanizeCheckBox.IsChecked = settings.Enabled;
            ConstructionHumanizeQueuePercentMinTextBox.Text = FormatHumanizeNumber(settings.QueuePercentMin);
            ConstructionHumanizeQueuePercentMaxTextBox.Text = FormatHumanizeNumber(settings.QueuePercentMax);
            ConstructionHumanizeMaxDelayTextBox.Text = FormatHumanizeNumber(settings.MaxDelayMinutes);
            ConstructionHumanizeNoPlusMinTextBox.Text = FormatHumanizeNumber(settings.NoPlusMinMinutes);
            ConstructionHumanizeNoPlusMaxTextBox.Text = FormatHumanizeNumber(settings.NoPlusMaxMinutes);
        }
        catch
        {
            // Never let seeding throw out of a Loaded/visibility handler; the XAML defaults stand in.
        }
        finally
        {
            _suppressHumanizeWrite = false;
        }
    }

    private void ConstructionHumanizeSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressHumanizeWrite || Host is not { } host)
        {
            return;
        }

        var settings = new ConstructionHumanizeSettings(
            ConstructionHumanizeCheckBox.IsChecked == true,
            ParseHumanizeNumber(ConstructionHumanizeQueuePercentMinTextBox, PacingDefaults.ConstructionHumanizeQueuePercentMin),
            ParseHumanizeNumber(ConstructionHumanizeQueuePercentMaxTextBox, PacingDefaults.ConstructionHumanizeQueuePercentMax),
            ParseHumanizeNumber(ConstructionHumanizeMaxDelayTextBox, PacingDefaults.ConstructionHumanizeMaxDelayMinutes),
            ParseHumanizeNumber(ConstructionHumanizeNoPlusMinTextBox, PacingDefaults.ConstructionHumanizeNoPlusMinMinutes),
            ParseHumanizeNumber(ConstructionHumanizeNoPlusMaxTextBox, PacingDefaults.ConstructionHumanizeNoPlusMaxMinutes));

        host.SaveConstructionHumanizeSettings(settings);

        // Re-seed from the persisted (clamped/ordered) values so the UI shows exactly what was saved.
        LoadConstructionHumanizeSettings();
    }

    private static string FormatHumanizeNumber(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static double ParseHumanizeNumber(TextBox textBox, double fallback)
    {
        var text = textBox.Text?.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        // Be forgiving of a locale that uses a comma decimal separator.
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var localized)
            ? localized
            : fallback;
    }
}
