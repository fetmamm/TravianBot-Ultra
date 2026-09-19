using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using TbotUltra.Worker.Services.Automation;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class CoreOperationContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-core-operation-{Guid.NewGuid():N}");

    [Fact]
    public async Task PostLoginSnapshotOperation_PreservesReadOrderSnapshotFlagsAndCancellation()
    {
        var client = new RecordingPostLoginClient();
        var store = new AccountAnalysisStore(_root);
        var operation = new PostLoginSnapshotOperation(client, store);
        var logs = new List<string>();
        var options = new BotOptions
        {
            ServerName = "Test world",
            PostLoginAnalyzeHeroInventory = true,
            PostLoginAnalyzeNewAccount = false,
            PostLoginReadTroopTrainingQueue = true,
        };
        using var cancellation = new CancellationTokenSource();

        var result = await operation.LoadAsync(options, logs.Add, cancellation.Token);

        Assert.Same(client.HeroInventory, result.HeroInventory);
        Assert.Equal(7, result.AdventureCount);
        Assert.Same(client.TroopQueues, result.VillageStatus.TroopTrainingQueues);
        Assert.Equal(["inventory", "account", "buildings", "village", "training", "adventures"], client.Calls);
        Assert.Equal((true, false, false, true, true), client.AccountSnapshotArguments);
        Assert.All(client.CancellationTokens, token => Assert.Equal(cancellation.Token, token));
        Assert.True(store.TryLoad("alice", out var persisted, "https://example.com"));
        Assert.Equal("Teutons", persisted!.Tribe);
        Assert.Contains("Loading post-login data for server Test world.", logs);
    }

    [Fact]
    public async Task GoldClubStatusOperation_PersistsVerifiedSignalAndFallbackTribe()
    {
        var client = new RecordingGoldClubClient();
        var store = new AccountAnalysisStore(_root);
        store.SaveNewAccountAnalysisStatus("alice", "https://example.com", completed: true);
        var operation = new GoldClubStatusOperation(client, store);
        var account = new AccountOptions { Name = "alice", Username = "user", Password = "secret" };
        using var cancellation = new CancellationTokenSource();

        var result = await operation.ReadAndPersistAsync(account, new BotOptions { BaseUrl = "https://example.com" }, _ => { }, cancellation.Token);

        Assert.True(result);
        Assert.Equal(["gold", "account"], client.Calls);
        Assert.All(client.CancellationTokens, token => Assert.Equal(cancellation.Token, token));
        Assert.True(store.TryLoad("alice", out var persisted, "https://example.com"));
        Assert.True(persisted!.GoldClubEnabled);
        Assert.Equal("Gauls", persisted.Tribe);
        Assert.True(persisted.NewAccountAnalysisCompleted);
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
            // Best-effort cleanup for a temporary test directory.
        }
    }

    private sealed class RecordingPostLoginClient : IPostLoginSnapshotClient
    {
        public string AccountName => "alice";
        public string ServerUrl => "https://example.com";
        public string? KnownAccountTribe => null;
        public bool? KnownGoldClubEnabled => false;
        public HeroInventoryResources HeroInventory { get; } = new(1, 2, 3, 4);
        public IReadOnlyList<TroopTrainingQueueStatus> TroopQueues { get; } = [];
        public List<string> Calls { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];
        public (bool ForceRefresh, bool PreferCurrent, bool RestorePage, bool SuppressUiSync, bool SkipOverview) AccountSnapshotArguments { get; private set; }

        public Task<HeroInventoryResources> ReadHeroInventoryResourcesAsync(CancellationToken cancellationToken = default, bool suppressUiSync = false)
            => Record("inventory", cancellationToken, HeroInventory);

        public Task<AccountSnapshot> ReadAccountSnapshotAsync(bool forceRefreshVillages = false, bool preferCurrentPageVillages = false, bool restorePageAfterProfile = true, bool suppressEnsureUiSync = false, bool skipOverviewNavigation = false, CancellationToken cancellationToken = default)
        {
            AccountSnapshotArguments = (forceRefreshVillages, preferCurrentPageVillages, restorePageAfterProfile, suppressEnsureUiSync, skipOverviewNavigation);
            return Record("account", cancellationToken, new AccountSnapshot("Teutons", "Capital", 1, [new Village("Capital", "/dorf1.php", true, 1, -2, Tribe: "Teutons")]));
        }

        public Task<VillageStatus> ReadBuildingsStatusAsync(CancellationToken cancellationToken = default)
            => Record("buildings", cancellationToken, new VillageStatus("Capital", [], new Dictionary<string, string>(), [], [], []));

        public Task<VillageStatus> ReadVillageStatusAsync(IReadOnlyList<Village> knownVillages, IReadOnlyList<Building> knownBuildings, CancellationToken cancellationToken = default)
            => Record("village", cancellationToken, new VillageStatus("Capital", knownVillages, new Dictionary<string, string>(), [], knownBuildings, [], UnreadMessages: 2, UnreadReports: 3));

        public Task<IReadOnlyList<TroopTrainingQueueStatus>> ReadTroopTrainingQueuesAsync(IReadOnlyList<Building>? knownBuildings = null, CancellationToken cancellationToken = default)
            => Record("training", cancellationToken, TroopQueues);

        public Task<int?> RefreshAdventureCountAsync(bool forceReload = true, CancellationToken cancellationToken = default)
            => Record("adventures", cancellationToken, (int?)7);

        private Task<T> Record<T>(string call, CancellationToken token, T result)
        {
            Calls.Add(call);
            CancellationTokens.Add(token);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingGoldClubClient : IGoldClubStatusClient
    {
        public string AccountName => "alice";
        public string ServerUrl => "https://example.com";
        public List<string> Calls { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];
        public Task<bool> ReadGoldClubStatusAsync(CancellationToken cancellationToken = default) => Record("gold", cancellationToken, true);
        public Task<AccountSnapshot> ReadAccountSnapshotAsync(bool forceRefreshVillages = false, bool preferCurrentPageVillages = false, bool restorePageAfterProfile = true, bool suppressEnsureUiSync = false, bool skipOverviewNavigation = false, CancellationToken cancellationToken = default)
            => Record("account", cancellationToken, new AccountSnapshot("Gauls", "Capital", 1, []));
        private Task<T> Record<T>(string call, CancellationToken token, T result)
        {
            Calls.Add(call);
            CancellationTokens.Add(token);
            return Task.FromResult(result);
        }
    }
}
