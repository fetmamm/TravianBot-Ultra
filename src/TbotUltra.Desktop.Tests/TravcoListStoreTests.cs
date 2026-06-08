using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TravcoListStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-travco-tests", Guid.NewGuid().ToString("N"));
    private string _account = "alice";

    public TravcoListStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SaveLoadDelete_AreAccountScoped()
    {
        var store = CreateStore();
        var list = new TravcoListStore.TravcoSavedList
        {
            Name = "Page 1",
            Rows =
            [
                new TravcoListStore.TravcoSavedRow
                {
                    Account = "Player",
                    Village = "Village",
                    Coordinates = "1|2",
                    Selected = true,
                },
            ],
        };

        store.Save(list);

        Assert.True(File.Exists(AccountStoragePaths.TravcoListsPath(_root, "alice")));
        Assert.Single(store.LoadAll());

        _account = "bob";
        store.InvalidateCache();
        Assert.Empty(store.LoadAll());

        _account = "alice";
        store.InvalidateCache();
        Assert.True(store.Delete(list.Id));
        Assert.Empty(store.LoadAll());
    }

    private TravcoListStore CreateStore() => new(_root, () => _account);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
