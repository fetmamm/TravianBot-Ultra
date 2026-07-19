using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
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
    public void ChromiumSetupWindow_LoadsWithProgressHiddenUntilTheDownloadStarts()
    {
        _wpf.Run(() =>
        {
            var window = new ChromiumSetupWindow(Path.GetTempPath(), _ => { });
            try
            {
                window.Measure(new Size(470, 250));
                window.Arrange(new Rect(0, 0, 470, 250));

                var download = Assert.IsType<Button>(window.FindName("DownloadButton"));
                var notNow = Assert.IsType<Button>(window.FindName("NotNowButton"));
                Assert.True(notNow.IsCancel, "Not now must stay IsCancel so Esc dismisses the prompt.");
                Assert.True(download.IsDefault, "Download must stay the default so Enter accepts.");

                // Progress only belongs on screen once a download is actually running.
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<ProgressBar>(window.FindName("DownloadProgressBar")).Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<TextBlock>(window.FindName("StatusTextBlock")).Visibility);
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
