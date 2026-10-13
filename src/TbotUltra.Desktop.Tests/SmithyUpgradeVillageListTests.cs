using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SmithyUpgradeVillageListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tbot-smithy-list-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadSelectionCounts_ReadsAllVillagesAndKeepsAccountsSeparate()
    {
        SmithyUpgradeTargetsStore.Save(_root, "alice", "xy:1|2", [new("unit:1", "Legionnaire", 3)]);
        SmithyUpgradeTargetsStore.Save(_root, "alice", "xy:3|4", [
            new("unit:2", "Praetorian", 5),
            new("unit:3", "Imperian", 7),
        ]);

        var counts = SmithyUpgradeTargetsStore.LoadSelectionCounts(_root, "alice");

        Assert.Equal(1, counts["XY:1|2"]);
        Assert.Equal(2, counts["xy:3|4"]);
        Assert.Empty(SmithyUpgradeTargetsStore.LoadSelectionCounts(_root, "bob"));
    }

    [Fact]
    public void VillageRows_ReportSelectionAndRouteCommandsToTheCorrectVillage()
    {
        var vm = new TroopTrainingViewModel();
        var village = new VillageSettingsStore.VillageKeyInfo("xy:1|2", "Home", 1, 2, false);
        var row = new SmithyUpgradeVillageRow(village, 0, false);
        SmithyUpgradeVillageRow? settingsRow = null;
        SmithyUpgradeVillageRow? toggleRow = null;
        vm.SmithyVillageSettingsRequested += value => settingsRow = value;
        vm.SmithyVillageToggleRequested += value => toggleRow = value;
        vm.SmithyVillages.Add(row);

        vm.OpenSmithyVillageSettingsCommand.Execute(row);
        vm.ToggleSmithyVillageCommand.Execute(row);

        Assert.Same(row, settingsRow);
        Assert.Same(row, toggleRow);
        Assert.Equal("No troops selected", row.SelectionSummary);
        Assert.Equal("2 troops selected", (row with { SelectedTroopCount = 2 }).SelectionSummary);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
