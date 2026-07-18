using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TravianClientPathTests
{
    [Fact]
    public void Paths_PreserveOfficialNavigationTargets()
    {
        Assert.Equal("/build.php?id=39&gid=16&tt=1", TravianClient.Paths.RallyPointTroops);
        Assert.Equal("/build.php?id=39&gid=16&tt=2", TravianClient.Paths.RallyPointSendTroops);
        Assert.Equal("/build.php?id=39&gid=16&tt=99", TravianClient.Paths.RallyPointFarmLists);
        Assert.Equal("/hero/adventures", TravianClient.Paths.HeroAdventures);
        Assert.Equal("/hero/inventory", TravianClient.Paths.HeroInventory);
        Assert.Equal("/hero/attributes", TravianClient.Paths.HeroAttributes);
        Assert.Equal("/messages", TravianClient.Paths.Messages);
        Assert.Equal("/messages/write", TravianClient.Paths.MessagesWrite);
        Assert.Equal("/report", TravianClient.Paths.Reports);
        Assert.Equal("/build.php?id=7", TravianClient.Paths.BuildBySlot(7));
        Assert.Equal("/build.php?id=7&t=5", TravianClient.Paths.BuildBySlotTab(7, 5));
        Assert.Equal("/build.php?id=7", TravianClient.Paths.BuildBySlotWithGid(7, null));
        Assert.Equal("/build.php?id=7", TravianClient.Paths.BuildBySlotWithGid(7, 0));
        Assert.Equal("/build.php?id=23&gid=28", TravianClient.Paths.BuildBySlotWithGid(23, 28));
        Assert.Equal("/build.php?id=30&category=2", TravianClient.Paths.BuildBySlotWithCategory(30, 2));
    }
}
