using System.Text.Json.Nodes;
using System.Windows.Controls;
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
            Assert.Single(window.TownHallRows);
            Assert.True(window.TownHallQueue.IsRestartDelayEnabled);
            Assert.True(window.BreweryRestartDelay.IsEnabled);
            Assert.Equal("5", window.BreweryRestartDelay.DelayMinMinutes);
            Assert.Equal("40", window.BreweryRestartDelay.DelayMaxMinutes);
            Assert.True(window.HeroAdventureRestartDelay.IsEnabled);
            Assert.Equal("5", window.HeroAdventureRestartDelay.DelayMinMinutes);
            Assert.Equal("20", window.HeroAdventureRestartDelay.DelayMaxMinutes);
            Assert.True(window.SmithyUpgradeRestartDelay.IsEnabled);
            Assert.Equal("10", window.SmithyUpgradeRestartDelay.DelayMinMinutes);
            Assert.Equal("30", window.SmithyUpgradeRestartDelay.DelayMaxMinutes);
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
