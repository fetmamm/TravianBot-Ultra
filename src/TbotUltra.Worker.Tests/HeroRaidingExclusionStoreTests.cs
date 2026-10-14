using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidingExclusionStoreTests
{
    [Fact]
    public void SaveAndLoad_PersistsCoordinatesPerAccountAndWorld()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-hero-raiding-{Guid.NewGuid():N}");
        try
        {
            var store = new HeroRaidingExclusionStore(root, _ => { });
            store.Save("account-a", "https://ts1.travian.com", [(1, 2), (3, 4), (1, 2)]);

            Assert.Equal(2, store.Load("account-a", "https://ts1.travian.com").Count);
            Assert.Empty(store.Load("account-b", "https://ts1.travian.com"));
            Assert.Empty(store.Load("account-a", "https://ts2.travian.com"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_CorruptFileBlocksAutomationRatherThanDroppingExclusions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-hero-raiding-{Guid.NewGuid():N}");
        try
        {
            var path = AccountStoragePaths.HeroRaidingExclusionsPath(root, "account", "https://ts1.travian.com");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{invalid json");

            var store = new HeroRaidingExclusionStore(root, _ => { });
            Assert.Throws<InvalidOperationException>(() => store.Load("account", "https://ts1.travian.com"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
