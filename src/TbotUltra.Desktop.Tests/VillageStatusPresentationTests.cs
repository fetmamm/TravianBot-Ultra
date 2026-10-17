using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageStatusPresentationTests
{
    [Fact]
    public void VillagePickerSignature_IgnoresResourceOnlyChanges()
    {
        var original = CreateStatus(new Dictionary<string, string> { ["wood"] = "100" });
        var changedResources = CreateStatus(new Dictionary<string, string> { ["wood"] = "200" });

        Assert.Equal(
            MainWindow.BuildVillagePickerPresentationSignature(original),
            MainWindow.BuildVillagePickerPresentationSignature(changedResources));
    }

    [Fact]
    public void VillagePickerSignature_ChangesWithActiveVillage()
    {
        var original = CreateStatus(new Dictionary<string, string> { ["wood"] = "100" });
        var changedVillage = original with
        {
            ActiveVillage = "Second",
            ActiveVillageCoordX = 2,
            ActiveVillageCoordY = 3,
        };

        Assert.NotEqual(
            MainWindow.BuildVillagePickerPresentationSignature(original),
            MainWindow.BuildVillagePickerPresentationSignature(changedVillage));
    }

    private static VillageStatus CreateStatus(IReadOnlyDictionary<string, string> resources)
        => new(
            ActiveVillage: "Capital",
            Villages:
            [
                new Village("Capital", "?newdid=1", true, 0, 0, 100),
            ],
            Resources: resources,
            ResourceFields: [],
            Buildings: [],
            BuildQueue: [],
            VillageCount: 1,
            ActiveVillageCoordX: 0,
            ActiveVillageCoordY: 0);
}
