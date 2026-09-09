using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
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
    public void TroopSettingsWindows_LoadWithSyncControlsAndAllTargetsSelected()
    {
        _wpf.Run(() =>
        {
            var payload = TroopTrainingQuickSettings.FromOptions(new BotOptions());
            var rows = new[]
            {
                new TroopTrainingQuickVillageRow("v1", "Village 1", true, payload, "Teutons"),
                new TroopTrainingQuickVillageRow("v2", "Village 2", false, payload, "Teutons"),
            };
            var optionsWindow = new TroopTrainingOptionsWindow(rows);
            var syncWindow = new TroopTrainingSettingsSyncWindow(rows);
            try
            {
                optionsWindow.Measure(new Size(980, 600));
                optionsWindow.Arrange(new Rect(0, 0, 980, 600));
                syncWindow.Measure(new Size(520, 520));
                syncWindow.Arrange(new Rect(0, 0, 520, 520));

                var syncButton = Assert.IsType<Button>(optionsWindow.FindName("SyncSettingsButton"));
                var source = Assert.IsType<ComboBox>(syncWindow.FindName("SourceVillageComboBox"));
                Assert.Equal("Sync settings", syncButton.Content);
                Assert.Equal(2, source.Items.Count);
                Assert.All(syncWindow.Targets, item => Assert.True(item.IsSelected));
                Assert.Single(syncWindow.Targets, item => item.IsSource);
            }
            finally
            {
                syncWindow.Close();
                optionsWindow.Close();
            }
        });
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
    public void UpdateAvailableWindow_LoadsAsModelessReleaseNotification()
    {
        _wpf.Run(() =>
        {
            var window = new UpdateAvailableWindow("1.0.0", "1.1.0");
            var dismissed = 0;
            window.DismissRequested += (_, _) => dismissed++;
            try
            {
                window.Measure(new Size(510, 332));
                window.Arrange(new Rect(0, 0, 510, 332));

                Assert.False(window.ShowInTaskbar);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.Equal("v1.0.0", Assert.IsType<TextBlock>(window.FindName("CurrentVersionText")).Text);
                Assert.Equal("v1.1.0", Assert.IsType<TextBlock>(window.FindName("LatestVersionText")).Text);
                Assert.Equal("Update", Assert.IsType<Button>(window.FindName("UpdateButton")).Content);
                Assert.Equal("Dont update now", Assert.IsType<Button>(window.FindName("DontUpdateNowButton")).Content);
            }
            finally
            {
                window.Close();
            }

            Assert.Equal(1, dismissed);
        });
    }

    [Fact]
    public void DebugWindow_ProvidesSideEffectFreeUpdateVersionPreview()
    {
        _wpf.Run(() =>
        {
            var window = new FunctionTestWindow();
            try
            {
                var requested = false;
                window.UpdateVersionPreviewRequested += (_, _) => requested = true;
                var button = Assert.IsType<Button>(window.FindName("UpdateVersionPreviewButton"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
                Assert.Equal("Update version", button.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DebugWindow_ProvidesOneYellowFarmMoveAction()
    {
        _wpf.Run(() =>
        {
            var window = new FunctionTestWindow();
            try
            {
                var requested = false;
                window.MoveLossFarmsTestRequested += (_, _) => requested = true;
                var button = Assert.IsType<Button>(window.FindName("TestMoveLossFarmsButton"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
                Assert.Equal("Move red/yellow farms", button.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DebugWindow_ProvidesIncomingAttackSoundTest()
    {
        _wpf.Run(() =>
        {
            var window = new FunctionTestWindow();
            try
            {
                var requested = false;
                window.IncomingAttackSoundTestRequested += (_, _) => requested = true;
                var button = Assert.IsType<Button>(window.FindName("TestIncomingAttackSoundButton"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
                Assert.Equal("Test incoming attack sound", button.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DebugWindow_ProvidesCheckCapitalAction()
    {
        _wpf.Run(() =>
        {
            var window = new FunctionTestWindow();
            try
            {
                var requested = false;
                window.CheckCapitalRequested += (_, _) => requested = true;
                var button = Assert.IsType<Button>(window.FindName("CheckCapitalButton"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
                Assert.Equal("Check capital", button.Content);
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
    public void CatapultClearButton_ResetsTroopAmountsToZero()
    {
        _wpf.Run(() =>
        {
            var window = new CatapultWaveWindow(
                "Romans",
                new Dictionary<string, long> { ["Legionnaire"] = 100 });
            try
            {
                var firstAttackGrid = Assert.IsType<Grid>(window.FindName("FirstAttackTroopsGrid"));
                var wavesGrid = Assert.IsType<Grid>(window.FindName("WaveTroopsGrid"));
                AssertSoftButtonColors(Assert.IsType<Button>(window.FindName("SwitchVillageButton")), "SuccessBgBrush", "SuccessBorderBrush", "SuccessTextBrush");
                AssertSoftButtonColors(Assert.IsType<Button>(window.FindName("StartButton")), "SuccessBgBrush", "SuccessBorderBrush", "SuccessTextBrush");
                AssertSoftButtonColors(Assert.IsType<Button>(window.FindName("ClearButton")), "AmberBg200Brush", "WarningBorderBrush", "WarningText2Brush");
                var tabDelay = Assert.IsType<ComboBox>(window.FindName("TabOpenDelayComboBox"));
                Assert.Equal(["50 ms", "100 ms", "200 ms", "300 ms", "500 ms"], tabDelay.Items.OfType<ComboBoxItem>().Select(item => item.Content));
                Assert.Equal("100 ms", Assert.IsType<ComboBoxItem>(tabDelay.SelectedItem).Content);
                var troopInputs = firstAttackGrid.Children.OfType<TextBox>()
                    .Concat(wavesGrid.Children.OfType<TextBox>())
                    .ToArray();
                Assert.NotEmpty(troopInputs);

                foreach (var input in troopInputs)
                {
                    input.Text = "17";
                }

                Assert.IsType<Button>(window.FindName("ClearButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.All(troopInputs, input => Assert.Equal("0", input.Text));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CatapultFirstAttackTargetPickers_UseTheCurrentTribeBuildingCatalog()
    {
        _wpf.Run(() =>
        {
            var window = new CatapultWaveWindow("Romans");
            try
            {
                var firstTarget = Assert.IsType<ComboBox>(window.FindName("FirstAttackTarget1ComboBox"));
                var secondTarget = Assert.IsType<ComboBox>(window.FindName("FirstAttackTarget2ComboBox"));
                var firstRandom = Assert.IsType<RadioButton>(window.FindName("FirstAttackTarget1RandomRadioButton"));

                Assert.Contains("Main Building", firstTarget.Items.OfType<string>());
                Assert.Equal(firstTarget.Items.OfType<string>(), secondTarget.Items.OfType<string>());
                Assert.True(firstRandom.IsChecked);

                firstTarget.SelectedItem = "Main Building";
                Assert.False(firstRandom.IsChecked);

                firstRandom.IsChecked = true;
                Assert.Null(firstTarget.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VillageSettingsPanel_HidesTribeColumnWhenEveryVillageSharesOneTribe()
    {
        AssertTribeColumnVisibility(
            [BuildRow("Capital", "Egyptians"), BuildRow("Second", "Egyptians")],
            Visibility.Collapsed);
    }

    [Fact]
    public void VillageSettingsPanel_HidesTribeColumnWhenNoTribeIsKnown()
    {
        AssertTribeColumnVisibility(
            [BuildRow("Capital", string.Empty), BuildRow("Second", string.Empty)],
            Visibility.Collapsed);
    }

    [Fact]
    public void BuildingTemplateImportWindow_LoadsConflictManifest()
    {
        _wpf.Run(() =>
        {
            var template = new BuildingTemplate
            {
                Name = "Shared starter",
                CreatedByTribe = "Gauls",
                Rows = [new BuildingTemplateRow { Gid = 15, BuildingName = "Main Building", TargetLevel = 3 }],
            };
            var candidate = new BuildingTemplateImportCandidate(template, [], TribeMismatch: true);
            var window = new BuildingTemplateImportWindow([candidate], [template.Id]);
            try
            {
                window.Measure(new Size(900, 560));
                window.Arrange(new Rect(0, 0, 900, 560));
                window.UpdateLayout();

                var row = Assert.Single(window.Rows);
                Assert.True(row.HasConflict);
                Assert.True(row.TribeMismatch);
                Assert.Equal("Conflict · Different tribe", row.StatusText);
                Assert.Equal(BuildingTemplateImportConflictAction.ImportAsCopy, row.SelectedConflictChoice.Action);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VillageSettingsPanel_ShowsTribeColumnOnASpecialServerWithMixedTribes()
    {
        AssertTribeColumnVisibility(
            [BuildRow("Capital", "Spartans"), BuildRow("Second", "Egyptians"), BuildRow("Third", "Huns")],
            Visibility.Visible);
    }

    private void AssertTribeColumnVisibility(IReadOnlyList<VillageSettingsRow> rows, Visibility expected)
    {
        _wpf.Run(() =>
        {
            var panel = new Views.VillageSettingsPanel(
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
            var column = Assert.IsType<DataGridTextColumn>(panel.FindName("TribeColumn"));
            Assert.Equal(expected, column.Visibility);
        });
    }

    private static void AssertSoftButtonColors(Button button, string backgroundKey, string borderKey, string foregroundKey)
    {
        Assert.Equal(ThemeColors.Get(backgroundKey), Assert.IsType<SolidColorBrush>(button.Background).Color);
        Assert.Equal(ThemeColors.Get(borderKey), Assert.IsType<SolidColorBrush>(button.BorderBrush).Color);
        Assert.Equal(ThemeColors.Get(foregroundKey), Assert.IsType<SolidColorBrush>(button.Foreground).Color);
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
