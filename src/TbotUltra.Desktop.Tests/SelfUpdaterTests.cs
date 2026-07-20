using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

/// <summary>
/// The updater overlays a new build onto the user's install folder. If it ever stops excluding the
/// user-data paths, an update silently wipes every account, proxy, village setting and build queue —
/// so the exclusions matter far more than the copy itself.
/// </summary>
public sealed class SelfUpdaterTests
{
    private static readonly string Script = SelfUpdater.BuildUpdaterScript();

    [Theory]
    // config/ holds bot.json, proxies.json, building_templates.json and config/accounts/<account>/
    // (queue.json, settings.json, proxy_plan.json, village_cache.json, session state).
    [InlineData("'config'")]
    [InlineData("'logs'")]
    [InlineData("'playwright'")]
    // .env holds the account credentials.
    [InlineData("'.env'")]
    public void ExcludesUserDataFromTheOverlay(string excludedPath)
    {
        Assert.Contains(excludedPath, Script, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludesDirectoriesAndFilesWithTheRightRobocopySwitches()
    {
        Assert.Contains("'/XD'", Script, StringComparison.Ordinal);
        Assert.Contains("'/XF'", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverMirrors()
    {
        // /MIR deletes destination files missing from the source, which would remove user data that the
        // new build does not ship. The overlay must only add and overwrite. Matched in quoted argument
        // form so the script's own comment about not using /MIR does not trip this.
        Assert.DoesNotContain("'/MIR'", Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'/PURGE'", Script, StringComparison.OrdinalIgnoreCase);
    }
}
