using System.IO;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TroopTrainingSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tbot_tt_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var payload = Make(barracksEnabled: true, troop: "Phalanx", fallback: 60);
        TroopTrainingSettingsStore.Save(_root, "acc", "xy:1-2", payload);

        var loaded = TroopTrainingSettingsStore.Load(_root, "acc", "xy:1-2");

        Assert.NotNull(loaded);
        Assert.True(loaded!.Barracks.Enabled);
        Assert.Equal("Phalanx", loaded.Barracks.TroopType);
        Assert.Equal(60, loaded.FallbackCooldownSeconds);
    }

    [Fact]
    public void Load_UnknownVillage_ReturnsNull()
    {
        TroopTrainingSettingsStore.Save(_root, "acc", "xy:1-2", Make());

        Assert.Null(TroopTrainingSettingsStore.Load(_root, "acc", "xy:9-9"));
    }

    [Fact]
    public void Load_MissingAccountOrKey_ReturnsNull()
    {
        Assert.Null(TroopTrainingSettingsStore.Load(_root, "acc", "xy:1-2"));
        Assert.Null(TroopTrainingSettingsStore.Load(_root, null, "xy:1-2"));
        Assert.Null(TroopTrainingSettingsStore.Load(_root, "acc", null));
    }

    [Fact]
    public void SaveForVillages_WritesToEveryKey()
    {
        TroopTrainingSettingsStore.SaveForVillages(_root, "acc", new[] { "xy:1-1", "xy:2-2" }, Make(troop: "Swordsman"));

        Assert.Equal("Swordsman", TroopTrainingSettingsStore.Load(_root, "acc", "xy:1-1")!.Barracks.TroopType);
        Assert.Equal("Swordsman", TroopTrainingSettingsStore.Load(_root, "acc", "xy:2-2")!.Barracks.TroopType);
    }

    private static TroopTrainingPayload Make(bool barracksEnabled = false, string troop = "", int fallback = 120)
    {
        var building = new TroopTrainingBuildingPayload(
            barracksEnabled, troop, "no_limit", "maximum", 0, "timed", 0, 0, 30, 180, true, true, true, true);
        return new TroopTrainingPayload(building, building, building, fallback);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
