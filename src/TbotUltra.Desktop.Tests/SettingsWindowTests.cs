using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Controls;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SettingsWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-settings-window-{Guid.NewGuid():N}");

    [Fact]
    public void CelebrationsCategory_LoadsTownHallControlsAndRequestedTab()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
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
                    townHallRows: rows);

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
                Assert.NotNull(window.FindName("GoldLimitSlider"));
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Window construction timed out.");
        Assert.Null(failure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
