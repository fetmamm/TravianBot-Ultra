using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageGroupToggleSynchronizerTests
{
    [Fact]
    public void Apply_UpdatesMatchingVillageAndLeavesOtherVillagesUntouched()
    {
        var target = Row("xy:1|2", "Alpha", ("hero", true), ("town_hall_celebration", true));
        var other = Row("xy:3|4", "Beta", ("hero", false), ("town_hall_celebration", true));

        var matched = VillageGroupToggleSynchronizer.Apply(
            [target, other],
            new VillageSettingsStore.VillageKeyInfo("xy:1|2", "Alpha", 1, 2, false),
            ["hero"]);

        Assert.True(matched);
        Assert.True(target.GroupToggles.Single(toggle => toggle.GroupKey == "hero").IsEnabled);
        Assert.False(target.GroupToggles.Single(toggle => toggle.GroupKey == "town_hall_celebration").IsEnabled);
        Assert.True(other.GroupToggles.Single(toggle => toggle.GroupKey == "town_hall_celebration").IsEnabled);
    }

    [Fact]
    public void Apply_UsesVillageNameWhenUiAndStoreKeysDiffer()
    {
        var row = Row("did:42", "Alpha", ("town_hall_celebration", true));

        var matched = VillageGroupToggleSynchronizer.Apply(
            [row],
            new VillageSettingsStore.VillageKeyInfo("xy:1|2", "Alpha", 1, 2, false),
            []);

        Assert.True(matched);
        Assert.False(row.GroupToggles.Single().IsEnabled);
    }

    private static VillageSettingsRow Row(
        string key,
        string name,
        params (string Key, bool Enabled)[] toggles) =>
        new()
        {
            Name = name,
            KeyInfo = new VillageSettingsStore.VillageKeyInfo(key, name, null, null, false),
            GroupToggles = toggles.Select(toggle => new VillageGroupToggle
            {
                GroupKey = toggle.Key,
                IsEnabled = toggle.Enabled,
            }).ToList(),
        };
}
