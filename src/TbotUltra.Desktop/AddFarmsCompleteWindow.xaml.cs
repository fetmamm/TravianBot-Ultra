using System;
using System.Globalization;
using System.Windows;

namespace TbotUltra.Desktop;

/// <summary>
/// Modern completion dialog shown after an "Add farms" run: the run summary as stat tiles, plus — when
/// dead villages were found — an inline "remove them from the Travco list?" question (Keep / Remove them)
/// so the user still answers it in a single popup. Replaces the plain Yes/No message box.
/// </summary>
public partial class AddFarmsCompleteWindow : Window
{
    /// <summary>True only when invalid coordinates were found AND the user chose to remove them.</summary>
    public bool RemoveInvalidCoordinates { get; private set; }

    public AddFarmsCompleteWindow(
        Window? owner,
        int added,
        int duplicates,
        int occupiedSkipped,
        int excludedPlayers,
        int excludedAlliances,
        int identityUnavailable,
        int failed,
        TimeSpan elapsed,
        int invalidCount,
        string? sourceListName)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        AddedValueText.Text = added.ToString(CultureInfo.InvariantCulture);
        DuplicatesValueText.Text = duplicates.ToString(CultureInfo.InvariantCulture);
        OccupiedValueText.Text = occupiedSkipped.ToString(CultureInfo.InvariantCulture);
        ExcludedPlayersValueText.Text = excludedPlayers.ToString(CultureInfo.InvariantCulture);
        ExcludedAlliancesValueText.Text = excludedAlliances.ToString(CultureInfo.InvariantCulture);
        IdentityUnavailableValueText.Text = identityUnavailable.ToString(CultureInfo.InvariantCulture);
        FailedValueText.Text = failed.ToString(CultureInfo.InvariantCulture);
        ElapsedValueText.Text =
            $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        SubtitleText.Text = added == 1
            ? "1 farm added to your lists."
            : $"{added} farms added to your lists.";

        // Highlight the Failed tile in red only when something actually failed, so a clean run stays neutral.
        if (failed > 0)
        {
            FailedValueText.SetResourceReference(ForegroundProperty, "DangerTextBrush");
            FailedTile.SetResourceReference(BorderBrushProperty, "DangerBorderBrush");
        }

        if (invalidCount > 0)
        {
            var listLabel = string.IsNullOrWhiteSpace(sourceListName) ? "the Travco list" : $"Travco list '{sourceListName}'";
            InvalidText.Text = invalidCount == 1
                ? $"1 invalid village coordinate was found. Remove it from {listLabel}?"
                : $"{invalidCount} invalid village coordinates were found. Remove them from {listLabel}?";
            InvalidPanel.Visibility = Visibility.Visible;
            RemoveButtons.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void KeepButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveInvalidCoordinates = false;
        DialogResult = true;
        Close();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        RemoveInvalidCoordinates = true;
        DialogResult = true;
        Close();
    }
}
