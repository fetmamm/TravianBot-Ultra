using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

// Runs on the shared WPF smoke thread: once any test creates Application.Current, constructing a
// Window on a second STA thread deadlocks against that Application's dispatcher.
[Collection(WpfSmokeCollection.Name)]
public sealed class SettingsWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-settings-window-{Guid.NewGuid():N}");
    private readonly WpfSmokeFixture _wpf;

    public SettingsWindowTests(WpfSmokeFixture wpf)
    {
        _wpf = wpf;
    }

    [Fact]
    public void CelebrationsCategory_LoadsTownHallControlsAndRequestedTab()
    {
        _wpf.Run(() =>
        {
            Directory.CreateDirectory(_root);
            var configPath = Path.Combine(_root, "bot.json");
            File.WriteAllText(configPath, new JsonObject().ToJsonString());
            var store = new BotConfigStore(configPath, _root, () => string.Empty);
            var rows = new[]
            {
                new TownHallOverviewRow("xy:1|2", "Village", true, TownHallCelebrationDefaults.Small),
            };

            var window = new SettingsWindow(
                store,
                initialCategory: SettingsCategory.Celebrations,
                townHallRows: rows,
                dailyGoldSpent: 3,
                dailySilverSpent: 40);

            var tabs = Assert.IsType<TabControl>(window.FindName("SettingsCategoryTabControl"));
            Assert.Equal((int)SettingsCategory.Celebrations, tabs.SelectedIndex);
            Assert.Single(window.SettingsVm.Celebrations.TownHallRows);
            Assert.True(window.SettingsVm.Celebrations.TownHallQueue.IsRestartDelayEnabled);
            Assert.True(window.SettingsVm.Celebrations.BreweryRestartDelay.IsEnabled);
            Assert.Equal("5", window.SettingsVm.Celebrations.BreweryRestartDelay.DelayMinMinutes);
            Assert.Equal("40", window.SettingsVm.Celebrations.BreweryRestartDelay.DelayMaxMinutes);
            Assert.True(window.SettingsVm.Hero.AdventureRestartDelay.IsEnabled);
            Assert.Equal("3", window.SettingsVm.Hero.AdventureRestartDelay.DelayMinMinutes);
            Assert.Equal("15", window.SettingsVm.Hero.AdventureRestartDelay.DelayMaxMinutes);
            Assert.True(window.SettingsVm.Hero.SmithyUpgradeRestartDelay.IsEnabled);
            Assert.Equal("10", window.SettingsVm.Hero.SmithyUpgradeRestartDelay.DelayMinMinutes);
            Assert.Equal("30", window.SettingsVm.Hero.SmithyUpgradeRestartDelay.DelayMaxMinutes);
            Assert.False(window.SettingsVm.Hero.CropAntiStarveEnabled);
            Assert.Equal("30", window.SettingsVm.Hero.CropAntiStarveTriggerMinutes);
            Assert.Equal("90", window.SettingsVm.Hero.CropAntiStarveTargetMinutes);
            Assert.Equal("10000", window.SettingsVm.Hero.CropAntiStarveMaxCropPerTransfer);
            Assert.Equal("5000", window.SettingsVm.Hero.CropAntiStarveMinHeroCropRemaining);
            Assert.Equal("100", Assert.IsType<TextBox>(window.FindName("GoldLimitTextBox")).Text);
            Assert.Equal("20", Assert.IsType<TextBox>(window.FindName("DailyGoldSpendingLimitTextBox")).Text);
            Assert.Equal("100", Assert.IsType<TextBox>(window.FindName("SilverLimitTextBox")).Text);
            Assert.Equal("10000", Assert.IsType<TextBox>(window.FindName("DailySilverSpendingLimitTextBox")).Text);
            Assert.Equal("3 / 20", Assert.IsType<TextBlock>(window.FindName("DailyGoldSpendingUsageTextBlock")).Text);
            Assert.Equal("40 / 10000", Assert.IsType<TextBlock>(window.FindName("DailySilverSpendingUsageTextBlock")).Text);
            Assert.IsType<TextBox>(window.FindName("DailyGoldSpendingLimitTextBox")).Text = "25";
            Assert.Equal("3 / 25", Assert.IsType<TextBlock>(window.FindName("DailyGoldSpendingUsageTextBlock")).Text);
            Assert.NotNull(window.FindName("ResetDailyGoldLimitButton"));
            Assert.NotNull(window.FindName("ResetDailySilverLimitButton"));
            Assert.NotNull(window.FindName("AllowGoldSpendingCheckBox"));
            window.Close();
        });
    }

    [Fact]
    public void GeneralCategory_LoadsVideoSoundSettingAndControl()
    {
        _wpf.Run(() =>
        {
            var store = CreateStore(new JsonObject
            {
                [BotOptionPayloadKeys.TurnOffVideoSound] = false,
            });
            var window = new SettingsWindow(store, initialCategory: SettingsCategory.General);
            try
            {
                var checkBox = Assert.IsType<CheckBox>(window.FindName("TurnOffVideoSoundCheckBox"));

                Assert.Equal("Turn off video sound", checkBox.Content);
                Assert.False(window.SettingsVm.TurnOffVideoSound);
                Assert.False(checkBox.IsChecked);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PacingCategory_LoadsShortVillageWaitDropdown()
    {
        _wpf.Run(() =>
        {
            var store = CreateStore(new JsonObject
            {
                [BotOptionPayloadKeys.ShortVillageDeferSeconds] = 90,
            });
            var window = new SettingsWindow(store, initialCategory: SettingsCategory.Pacing);
            try
            {
                var comboBox = Assert.IsType<ComboBox>(window.FindName("ShortVillageDeferComboBox"));

                Assert.Equal(90, window.SettingsVm.Pacing.ShortVillageDeferSeconds);
                Assert.Equal(["20 s", "60 s", "90 s"], comboBox.Items
                    .OfType<ComboBoxItem>()
                    .Select(item => item.Content?.ToString() ?? string.Empty)
                    .ToArray());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PacingCategory_LoadsIdleDorf2ParkingSettingAndTooltip()
    {
        _wpf.Run(() =>
        {
            var store = CreateStore(new JsonObject
            {
                [BotOptionPayloadKeys.ParkOnDorf2WhileIdle] = true,
            });
            var window = new SettingsWindow(store, initialCategory: SettingsCategory.Pacing);
            try
            {
                var checkBox = Assert.IsType<CheckBox>(window.FindName("ParkOnDorf2WhileIdleCheckBox"));
                var infoIcon = Assert.IsType<ContentControl>(window.FindName("ParkOnDorf2WhileIdleInfoIcon"));

                Assert.True(window.SettingsVm.Pacing.ParkOnDorf2WhileIdle);
                Assert.Equal("Park on Dorf2 while idle", checkBox.Content);
                Assert.Equal(
                    "When enabled, active automation parks the current village on Dorf2 instead of Dorf1 when no work is due for at least 60 seconds. It does not move from other pages, interfere with manual browsing, or act while automation is stopped. Incoming attack monitoring and Troop Evasion may temporarily keep or return to Dorf1.",
                    infoIcon.ToolTip);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PacingCategory_ShowsSmartSleepRecommendation()
    {
        _wpf.Run(() =>
        {
            var window = new SettingsWindow(
                CreateStore(new JsonObject()),
                initialCategory: SettingsCategory.Pacing);
            try
            {
                var badge = Assert.IsType<Border>(window.FindName("SmartSleepRecommendedBadge"));
                var label = Assert.IsType<TextBlock>(badge.Child);

                Assert.Equal("Recommended", label.Text);
                Assert.Equal(HorizontalAlignment.Right, badge.HorizontalAlignment);
                Assert.Equal(VerticalAlignment.Top, badge.VerticalAlignment);
                Assert.True(window.SettingsVm.Pacing.SmartSleepEnabled);
                Assert.False(window.SettingsVm.Pacing.SessionPacingEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void GeneralCategory_LoadsStartBrowserMinimizedSettingAndControl()
    {
        _wpf.Run(() =>
        {
            var store = CreateStore(new JsonObject
            {
                [BotOptionPayloadKeys.StartBrowserMinimized] = true,
            });
            var window = new SettingsWindow(store, initialCategory: SettingsCategory.General);
            try
            {
                var checkBox = Assert.IsType<CheckBox>(window.FindName("StartBrowserMinimizedCheckBox"));

                Assert.Equal("Always start Chrome minimized", checkBox.Content);
                Assert.True(window.SettingsVm.StartBrowserMinimized);
                Assert.Equal(
                    "SettingsVm.StartBrowserMinimized",
                    checkBox.GetBindingExpression(CheckBox.IsCheckedProperty)?.ParentBinding.Path.Path);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Footer_PlacesImportAndExportTogetherBeforeOtherActions()
    {
        _wpf.Run(() =>
        {
            var window = new SettingsWindow(CreateStore(new JsonObject()));
            try
            {
                var import = Assert.IsType<Button>(window.FindName("ImportSettingsButton"));
                var export = Assert.IsType<Button>(window.FindName("ExportSettingsButton"));
                var reset = Assert.IsType<Button>(window.FindName("ResetSettingsButton"));
                var panel = Assert.IsType<StackPanel>(import.Parent);

                Assert.Same(panel, export.Parent);
                Assert.Same(panel, reset.Parent);
                Assert.Equal(0, panel.Children.IndexOf(import));
                Assert.Equal(1, panel.Children.IndexOf(export));
                Assert.Equal(2, panel.Children.IndexOf(reset));
                Assert.Equal(20, export.Margin.Right);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void NormalizedDraft_UsesUnsavedFormValuesWithoutWritingToDisk()
    {
        _wpf.Run(() =>
        {
            var store = CreateStore(new JsonObject
            {
                [BotOptionPayloadKeys.AutomaticallyCheckLanguage] = true,
            });
            var configPath = Path.Combine(_root, "bot.json");
            var before = File.ReadAllText(configPath);
            var window = new SettingsWindow(store);
            try
            {
                ShowWindowForTest(window);
                DrainDispatcher();
                window.SettingsVm.AutomaticallyCheckLanguage = false;

                Assert.True(window.TryBuildNormalizedConfigDraft(out var draft));
                Assert.False(draft[BotOptionPayloadKeys.AutomaticallyCheckLanguage]!.GetValue<bool>());
                Assert.Equal(before, File.ReadAllText(configPath));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OpeningWithPersistedRiskyValues_DoesNotShowUserConfirmationDialogs()
    {
        _wpf.Run(() =>
        {
            var store = CreateStore(new JsonObject
            {
                [BotOptionPayloadKeys.DetailedBrowserLoggingEnabled] = true,
                [BotOptionPayloadKeys.SessionPacingDailyMaxHours] = 14,
            });
            var shownTitles = new List<string>();
            using var dialogCloser = CaptureDialogs(shownTitles);
            var window = new SettingsWindow(store);
            try
            {
                ShowWindowForTest(window);
                DrainDispatcher();

                Assert.Empty(shownTitles);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OpeningWithPersistedRiskyValues_DoesNotShowDelayedConfirmationDialogs()
    {
        _wpf.Run(() =>
        {
            var store = CreateStore(new JsonObject
            {
                [BotOptionPayloadKeys.DetailedBrowserLoggingEnabled] = true,
                [BotOptionPayloadKeys.SessionPacingDailyMaxHours] = 14,
            });
            var shownTitles = new List<string>();
            using var dialogCloser = CaptureDialogs(shownTitles);
            var window = new SettingsWindow(store);
            try
            {
                ShowWindowForTest(window);
                PumpDispatcherFor(TimeSpan.FromMilliseconds(250));

                Assert.Empty(shownTitles);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UserChangesToRiskyValues_ShowTheirRespectiveConfirmationDialogs()
    {
        _wpf.Run(() =>
        {
            var store = CreateStore(new JsonObject
            {
                [BotOptionPayloadKeys.DetailedBrowserLoggingEnabled] = false,
                [BotOptionPayloadKeys.SessionPacingDailyMaxHours] = 1,
            });
            var shownTitles = new List<string>();
            using var dialogCloser = CaptureDialogs(shownTitles);
            var window = new SettingsWindow(store);
            try
            {
                ShowWindowForTest(window);
                DrainDispatcher();
                Assert.Empty(shownTitles);

                var dailyMaxHours = Assert.IsType<ComboBox>(window.FindName("SessionDailyMaxHoursComboBox"));
                dailyMaxHours.SelectedItem = dailyMaxHours.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => Equals(item.Tag, "14"));
                DrainDispatcher();

                dailyMaxHours.SelectedItem = dailyMaxHours.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => Equals(item.Tag, "0"));
                DrainDispatcher();

                Assert.IsType<CheckBox>(window.FindName("DetailedBrowserLoggingCheckBox")).IsChecked = true;
                DrainDispatcher();

                Assert.Equal(2, shownTitles.Count(title => title == "Daily runtime warning"));
                Assert.Contains("Enable detailed browser logging?", shownTitles);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private BotConfigStore CreateStore(JsonObject config)
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "bot.json");
        var store = new BotConfigStore(configPath, _root, () => string.Empty);
        store.Save(config);
        return store;
    }

    private static IDisposable CaptureDialogs(ICollection<string> shownTitles)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            foreach (var dialog in Application.Current.Windows.OfType<AppDialog>().ToList())
            {
                shownTitles.Add(dialog.Title);
                dialog.Close();
            }
        };
        timer.Start();
        return new DisposableAction(timer.Stop);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void ShowWindowForTest(Window window)
    {
        window.ShowInTaskbar = false;
        window.Opacity = 0;
        window.Show();
    }

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
