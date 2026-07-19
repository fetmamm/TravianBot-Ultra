using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Views;
using Xunit;

namespace TbotUltra.Desktop.Tests;

/// <summary>
/// Constructs the standalone windows and asserts the parts that are decided at load time. These cover
/// the wiring unit tests cannot reach: XAML parsing, resource lookups, and the column visibility rules
/// applied in the constructor.
/// </summary>
[Collection(WpfSmokeCollection.Name)]
public sealed class WindowSmokeTests
{
    private readonly WpfSmokeFixture _wpf;

    public WindowSmokeTests(WpfSmokeFixture wpf)
    {
        _wpf = wpf;
    }

    [Fact]
    public void ChromiumSetupWindow_LoadsAsAConsentPromptWithNoInFlightState()
    {
        _wpf.Run(() =>
        {
            var window = new ChromiumSetupWindow();
            try
            {
                window.Measure(new Size(470, 205));
                window.Arrange(new Rect(0, 0, 470, 205));

                var download = Assert.IsType<Button>(window.FindName("DownloadButton"));
                var cancel = Assert.IsType<Button>(window.FindName("NotNowButton"));
                Assert.True(cancel.IsCancel, "Cancel must stay IsCancel so Esc dismisses the prompt.");
                Assert.True(download.IsDefault, "Download must stay the default so Enter accepts.");

                // The download runs behind the shared busy overlay, so this window must own no progress
                // state — an earlier version blocked its own close while an install was in flight.
                Assert.Null(window.FindName("DownloadProgressBar"));
                Assert.Null(window.FindName("StatusTextBlock"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VersionWindow_LoadsWithDeterminateUpdateOverlay()
    {
        _wpf.Run(() =>
        {
            var window = new VersionWindow("1.0.0", status: null);
            try
            {
                window.Measure(new Size(440, 340));
                window.Arrange(new Rect(0, 0, 440, 340));

                var overlay = Assert.IsType<BusyOverlayControl>(window.FindName("BusyOverlay"));
                overlay.Show("Downloading update", "Preparing…");
                overlay.IsIndeterminate = false;
                overlay.ProgressValue = 42;
                window.UpdateLayout();

                Assert.True(overlay.IsBusy);
                Assert.False(overlay.IsIndeterminate);
                Assert.Equal(42, overlay.ProgressValue);
                Assert.Equal("Cancel", Assert.IsType<Button>(overlay.FindName("CancelButton")).Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BuildingSlotsWindow_LoadsWithTheImageAndACloseButton()
    {
        _wpf.Run(() =>
        {
            var window = new BuildingSlotsWindow();
            try
            {
                window.Measure(new Size(1000, 820));
                window.Arrange(new Rect(0, 0, 1000, 820));

                var close = Assert.IsType<Button>(window.FindName("CloseButton"));
                Assert.True(close.IsCancel, "Close must stay IsCancel so Esc closes the window.");
                Assert.Equal("Close", close.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VillageSettingsWindow_HidesTribeColumnWhenEveryVillageSharesOneTribe()
    {
        AssertTribeColumnVisibility(
            [BuildRow("Capital", "Egyptians"), BuildRow("Second", "Egyptians")],
            Visibility.Collapsed);
    }

    [Fact]
    public void VillageSettingsWindow_HidesTribeColumnWhenNoTribeIsKnown()
    {
        AssertTribeColumnVisibility(
            [BuildRow("Capital", string.Empty), BuildRow("Second", string.Empty)],
            Visibility.Collapsed);
    }

    [Fact]
    public void VillageSettingsWindow_ShowsTribeColumnOnASpecialServerWithMixedTribes()
    {
        AssertTribeColumnVisibility(
            [BuildRow("Capital", "Spartans"), BuildRow("Second", "Egyptians"), BuildRow("Third", "Huns")],
            Visibility.Visible);
    }

    private void AssertTribeColumnVisibility(IReadOnlyList<VillageSettingsRow> rows, Visibility expected)
    {
        _wpf.Run(() =>
        {
            var window = new VillageSettingsWindow(
                rows,
                onEnabledChanged: _ => { },
                onNpcTradeChanged: _ => { },
                onHeroResourcesChanged: _ => { },
                onConstructFasterChanged: _ => { },
                onGroupsChanged: _ => { },
                onTroopSettingsRequested: _ => { },
                onSmithyUpgradeSettingsRequested: _ => { },
                onTownHallSettingsRequested: _ => { },
                onHeroResourceSettingsRequested: _ => { },
                onConstructFasterSettingsRequested: _ => { },
                onSaved: () => { });
            try
            {
                var column = Assert.IsType<DataGridTextColumn>(window.FindName("TribeColumn"));
                Assert.Equal(expected, column.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static VillageSettingsRow BuildRow(string name, string tribe) => new()
    {
        Name = name,
        PopText = "100",
        TribeText = tribe,
        KeyInfo = new VillageSettingsStore.VillageKeyInfo($"key:{name}", name, 0, 0, false),
        GroupToggles = [],
    };
}
