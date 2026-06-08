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

    [Fact]
    public void Save_ExistingIdUpdatesListWithoutCreatingDuplicate()
    {
        var store = CreateStore();
        var list = new TravcoListStore.TravcoSavedList
        {
            Name = "Original",
            Rows = [new TravcoListStore.TravcoSavedRow { Village = "Old village", Coordinates = "1|2" }],
        };
        store.Save(list);

        list.Name = "Edited";
        list.Rows =
        [
            new TravcoListStore.TravcoSavedRow
            {
                Village = "New village",
                Pop = 196,
                Coordinates = "1|2",
            },
        ];
        store.Save(list);

        var saved = Assert.Single(store.LoadAll());
        Assert.Equal(list.Id, saved.Id);
        Assert.Equal("Edited", saved.Name);
        var row = Assert.Single(saved.Rows);
        Assert.Equal("New village", row.Village);
        Assert.Equal(196, row.Pop);
    }

    [Fact]
    public void Save_RemovesDuplicateVillagesByCoordinates()
    {
        var store = CreateStore();
        var list = new TravcoListStore.TravcoSavedList
        {
            Name = "Duplicates",
            Rows =
            [
                new TravcoListStore.TravcoSavedRow
                {
                    Account = "Player",
                    Village = "Village",
                    Coordinates = "[-26|-66]",
                },
                new TravcoListStore.TravcoSavedRow
                {
                    Account = "Player renamed",
                    Village = "Village renamed",
                    Coordinates = "-26 | -66",
                },
            ],
        };

        store.Save(list);

        var saved = Assert.Single(store.LoadAll());
        Assert.Single(saved.Rows);
        Assert.Equal("Player", saved.Rows[0].Account);
        Assert.Equal("-26|-66", saved.Rows[0].Coordinates);
    }

    [Fact]
    public void Save_SkipsMissingCoordinatesAndWritesAlarm()
    {
        var messages = new List<string>();
        var store = new TravcoListStore(_root, () => _account, messages.Add);
        var list = new TravcoListStore.TravcoSavedList
        {
            Name = "Invalid coordinates",
            Rows =
            [
                new TravcoListStore.TravcoSavedRow
                {
                    Account = "Missing",
                    Village = "No coordinates",
                    Coordinates = "",
                },
                new TravcoListStore.TravcoSavedRow
                {
                    Account = "Valid",
                    Village = "Has coordinates",
                    Coordinates = "1|2",
                },
            ],
        };

        store.Save(list);

        var saved = Assert.Single(store.LoadAll());
        var row = Assert.Single(saved.Rows);
        Assert.Equal("Valid", row.Account);
        Assert.Contains(messages, message =>
            message.StartsWith("ALARM:", StringComparison.Ordinal)
            && message.Contains("skipped 1 village", StringComparison.Ordinal));
    }

    private TravcoListStore CreateStore() => new(_root, () => _account);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
