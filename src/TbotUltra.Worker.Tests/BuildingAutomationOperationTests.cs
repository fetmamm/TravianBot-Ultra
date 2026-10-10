using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using TbotUltra.Worker.Services.Automation;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BuildingAutomationOperationTests
{
    [Fact]
    public async Task ExecuteAsync_RoutesConstructWithoutChangingFallbackArguments()
    {
        var client = new FakeBuildingClient();
        var operation = new BuildingAutomationOperation(client);

        var result = await operation.ExecuteAsync(
            new BuildingAutomationRequest(
                BuildingAutomationAction.Construct,
                SlotId: 28,
                Gid: 10,
                Name: "Warehouse",
                AllowSlotFallback: true,
                FallbackExcludedSlots: "19,20"),
            CancellationToken.None);

        Assert.Equal("constructed", result);
        Assert.Equal((28, 10, "Warehouse", true, "19,20"), client.ConstructRequest);
    }

    [Fact]
    public async Task ExecuteAsync_RoutesUpgradeToMaxWithConfiguredAttemptLimit()
    {
        var client = new FakeBuildingClient();
        var operation = new BuildingAutomationOperation(client);

        var result = await operation.ExecuteAsync(
            new BuildingAutomationRequest(BuildingAutomationAction.UpgradeToMax, SlotId: 21, Name: "Warehouse", MaxAttempts: 12),
            CancellationToken.None);

        Assert.Equal("upgraded", result);
        Assert.Equal((21, 12, "Warehouse"), client.UpgradeToMaxRequest);
    }

    [Fact]
    public async Task ResourceOperation_RoutesBulkUpgradeWithoutChangingSelection()
    {
        var client = new FakeResourceUpgradeClient();
        var operation = new ResourceAutomationOperation(client);

        var result = await operation.UpgradeAllAsync(12, "smart", "wood,crop", "10:10:1788163200", CancellationToken.None);

        Assert.Equal("resources upgraded", result);
        Assert.Equal((12, "smart", "wood,crop", "10:10:1788163200"), client.BulkRequest);
    }

    [Fact]
    public async Task HeroOperation_RoutesAllManageOptionsWithoutChangingDefaults()
    {
        var client = new FakeHeroClient();
        var operation = new HeroAutomationOperation(client);

        var result = await operation.ManageAsync(35, true, false, true, 90, "offense", "longest", 55, CancellationToken.None);

        Assert.Equal("managed", result);
        Assert.Equal((35, true, false, true, 90, "offense", "longest", 55), client.ManageRequest);
    }

    [Fact]
    public async Task ReadBreweryCelebrationStatusAsync_RoutesToBuildingClient()
    {
        var client = new FakeBuildingClient();

        var result = await new BuildingAutomationOperation(client)
            .ReadBreweryCelebrationStatusAsync(null, CancellationToken.None);

        Assert.True(client.ReadBreweryStatusRequested);
        Assert.Equal("status", result.StatusText);
    }

    [Fact]
    public async Task HeroOperation_RoutesAdventureTuningCommands()
    {
        var client = new FakeHeroClient();
        var operation = new HeroAutomationOperation(client);

        var hardResult = await operation.IncreaseAdventuresToHardAsync(CancellationToken.None);
        var timeResult = await operation.ReduceAdventuresTimeAsync(CancellationToken.None);

        Assert.True(client.IncreaseAdventureDangerRequested);
        Assert.True(client.ReduceAdventureTimeRequested);
        Assert.Equal("hard", hardResult);
        Assert.Equal("reduced", timeResult);
    }

    [Fact]
    public async Task BuildingOperation_ForwardsStatusAndCelebrationArgumentsWithoutChangingThem()
    {
        var client = new FakeBuildingClient();
        var operation = new BuildingAutomationOperation(client);
        IReadOnlyList<Building> buildings = [];
        using var cancellation = new CancellationTokenSource();

        await operation.ReadSmithyUpgradeStatusAsync(buildings, cancellation.Token);
        await operation.ReadSmithyQueueFromCurrentPageAsync(cancellation.Token);
        await operation.RunBreweryCelebrationAsync(true, 1.5, 2.5, cancellation.Token);
        await operation.RunTownHallCelebrationAsync("great", 3, false, 4.5, 5.5, cancellation.Token);

        Assert.Same(buildings, client.SmithyBuildings);
        Assert.Equal((true, 1.5, 2.5), client.BreweryRequest);
        Assert.Equal(("great", 3, false, 4.5, 5.5), client.TownHallRequest);
        Assert.All(client.CancellationTokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Fact]
    public async Task ResourceAndHeroOperations_ForwardAllReadArgumentsAndCancellation()
    {
        var resourceClient = new FakeResourceUpgradeClient();
        var resourceOperation = new ResourceAutomationOperation(resourceClient);
        var heroClient = new FakeHeroClient();
        var heroOperation = new HeroAutomationOperation(heroClient);
        using var cancellation = new CancellationTokenSource();

        Assert.Equal("resource upgraded", await resourceOperation.UpgradeSingleAsync(7, 12, cancellation.Token));
        await heroOperation.DispatchAdventureAsync(cancellation.Token);
        await heroOperation.ReviveIfNeededAsync(true, cancellation.Token);
        await heroOperation.RefreshAdventureCountAsync(cancellation.Token);
        await heroOperation.HasLevelUpIndicatorAsync(cancellation.Token);
        await heroOperation.IsRevivingAsync(cancellation.Token);
        await heroOperation.IsHomeAsync(cancellation.Token);
        await heroOperation.ReadCurrentPageHpAsync(cancellation.Token);
        await heroOperation.HasClaimableTasksAsync(cancellation.Token);
        await heroOperation.HasClaimableDailyQuestsAsync(cancellation.Token);
        await heroOperation.ReadAttributesAsync(cancellation.Token);
        await heroOperation.ReadInventoryResourcesAsync(cancellation.Token);
        await heroOperation.SpendAttributePointsAsync("resources", cancellation.Token);

        Assert.Equal((7, 12), resourceClient.SingleRequest);
        Assert.Equal(
            ["dispatch", "revive", "count", "level-up", "reviving", "home", "hp", "tasks", "daily", "attributes", "inventory", "spend"],
            heroClient.Calls);
        Assert.All(heroClient.CancellationTokens, token => Assert.Equal(cancellation.Token, token));
    }

    private sealed class FakeBuildingClient : IBuildingClient
    {
        public (int SlotId, int Gid, string Name, bool AllowFallback, string? ExcludedSlots) ConstructRequest { get; private set; }
        public (int SlotId, int MaxAttempts, string? ExpectedName) UpgradeToMaxRequest { get; private set; }
        public bool ReadBreweryStatusRequested { get; private set; }
        public IReadOnlyList<Building>? SmithyBuildings { get; private set; }
        public (bool Enabled, double Min, double Max) BreweryRequest { get; private set; }
        public (string Mode, int Count, bool Enabled, double Min, double Max) TownHallRequest { get; private set; }
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<VillageStatus> ReadBuildingsStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VillageStatus> ReadCurrentBuildingOverviewStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VillageStatus> ReadBuildingOverviewStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> DemolishBuildingToLevelAsync(string targetBuildingSlotOrName, int targetLevel, CancellationToken cancellationToken = default) => Task.FromResult("demolished");
        public Task<string> UpgradeBuildingToLevelAsync(int slotId, int targetLevel, CancellationToken cancellationToken = default, string? expectedBuildingName = null) => Task.FromResult("upgraded");

        public Task<string> UpgradeBuildingToMaxAsync(int slotId, int maxAttempts = 30, CancellationToken cancellationToken = default, string? expectedBuildingName = null)
        {
            UpgradeToMaxRequest = (slotId, maxAttempts, expectedBuildingName);
            return Task.FromResult("upgraded");
        }

        public Task<string> ConstructBuildingAsync(int slotId, int gid, string name, CancellationToken cancellationToken = default, bool allowSlotFallback = false, string? fallbackExcludedSlots = null)
        {
            ConstructRequest = (slotId, gid, name, allowSlotFallback, fallbackExcludedSlots);
            return Task.FromResult("constructed");
        }

        public Task<string> RunBreweryCelebrationAsync(bool restartDelayEnabled, double restartDelayMinMinutes, double restartDelayMaxMinutes, CancellationToken cancellationToken = default)
        {
            BreweryRequest = (restartDelayEnabled, restartDelayMinMinutes, restartDelayMaxMinutes);
            CancellationTokens.Add(cancellationToken);
            return Task.FromResult("brewery celebration started");
        }

        public Task<BreweryCelebrationStatus> ReadBreweryCelebrationStatusAsync(IReadOnlyList<Building>? knownBuildings = null, CancellationToken cancellationToken = default)
        {
            ReadBreweryStatusRequested = true;
            return Task.FromResult(new BreweryCelebrationStatus(false, null, false, null, false, null, "N/A", "status"));
        }

        public Task<string> RunTownHallCelebrationAsync(string mode, int count, bool restartDelayEnabled, double restartDelayMinMinutes, double restartDelayMaxMinutes, CancellationToken cancellationToken = default)
        {
            TownHallRequest = (mode, count, restartDelayEnabled, restartDelayMinMinutes, restartDelayMaxMinutes);
            CancellationTokens.Add(cancellationToken);
            return Task.FromResult("town hall celebration started");
        }

        public Task<string> UpgradeSelectedTroopsAtSmithyAsync(IReadOnlyList<SmithyTroopTarget> targets, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SmithyUpgradeStatus> ReadSmithyUpgradeStatusAsync(IReadOnlyList<Building>? knownBuildings = null, CancellationToken cancellationToken = default)
        {
            SmithyBuildings = knownBuildings;
            CancellationTokens.Add(cancellationToken);
            return Task.FromResult(new SmithyUpgradeStatus(false, null, 0, null, [], "", ""));
        }
        public Task<string> ReadSmithyQueueFromCurrentPageTestAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokens.Add(cancellationToken);
            return Task.FromResult("smithy queue");
        }
        public Task<IReadOnlyList<ActiveConstruction>> ReadActiveConstructionsAsync(CancellationToken cancellationToken = default, bool allowNavigationToBuildings = true, ActiveConstructionReadMode readMode = ActiveConstructionReadMode.FreshForMutation) => throw new NotSupportedException();
        public Task<ConstructionSlotStatus> EvaluateConstructionSlotsAsync(string tribe, bool travianPlusActive, CancellationToken cancellationToken = default, bool allowNavigationToBuildings = true) => throw new NotSupportedException();
        public Task<int> WaitForConstructionSlotIfBusyAsync(ConstructionKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeResourceUpgradeClient : IResourceUpgradeClient
    {
        public (int TargetLevel, string Strategy, string? Types, string? Projections) BulkRequest { get; private set; }
        public (int SlotId, int TargetLevel) SingleRequest { get; private set; }

        public Task<string> UpgradeResourceToLevelAsync(int slotId, int targetLevel, CancellationToken cancellationToken = default)
        {
            SingleRequest = (slotId, targetLevel);
            return Task.FromResult("resource upgraded");
        }

        public Task<string> UpgradeAllResourcesToLevelAsync(
            int targetLevel,
            string buildStrategy = "lowest_first",
            string? resourceTypes = null,
            string? queuedLevelProjections = null,
            CancellationToken cancellationToken = default)
        {
            BulkRequest = (targetLevel, buildStrategy, resourceTypes, queuedLevelProjections);
            return Task.FromResult("resources upgraded");
        }
    }

    private sealed class FakeHeroClient : IHeroClient
    {
        public (int MinHp, bool AutoRevive, bool AutoAssign, bool AutoOintments, int OintmentTarget, string StatPriority, string AdventureOrder, int RegenPercent) ManageRequest { get; private set; }
        public bool IncreaseAdventureDangerRequested { get; private set; }
        public bool ReduceAdventureTimeRequested { get; private set; }
        public List<string> Calls { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<HeroAdventureDispatchResult> SendHeroOnAdventureAsync(CancellationToken cancellationToken = default) => Record("dispatch", cancellationToken, new HeroAdventureDispatchResult(true, null, 0, false, null, "done"));
        public Task<int?> RefreshAdventureCountAsync(bool forceReload = true, CancellationToken cancellationToken = default) => Record("count", cancellationToken, (int?)3);
        public Task<bool> HasHeroLevelUpIndicatorOnCurrentPageAsync(CancellationToken cancellationToken = default) => Record("level-up", cancellationToken, true);
        public Task<bool> CheckAndReviveDeadHeroOnCurrentPageAsync(bool autoRevive, CancellationToken cancellationToken = default) => Record("revive", cancellationToken, autoRevive);
        public Task<bool> IsHeroRevivingOnCurrentPageAsync(CancellationToken cancellationToken = default) => Record("reviving", cancellationToken, false);
        public Task<bool> IsHeroHomeOnCurrentPageAsync(CancellationToken cancellationToken = default) => Record("home", cancellationToken, true);
        public Task<int?> ReadHeroHpFromCurrentPageAsync(CancellationToken cancellationToken = default) => Record("hp", cancellationToken, (int?)80);
        public Task<bool> HasClaimableTasksOnCurrentPageAsync(CancellationToken cancellationToken = default) => Record("tasks", cancellationToken, true);
        public Task<bool> HasClaimableDailyQuestsOnCurrentPageAsync(CancellationToken cancellationToken = default) => Record("daily", cancellationToken, false);

        public Task<string> ManageHeroAsync(int minHpForAdventure, bool autoRevive, bool autoAssignPoints, bool autoUseOintments, int ointmentTargetHpPercent, string statPriority, string adventurePickOrder = "shortest", int heroHpRegenPerDayPercent = 40, CancellationToken cancellationToken = default)
        {
            ManageRequest = (minHpForAdventure, autoRevive, autoAssignPoints, autoUseOintments, ointmentTargetHpPercent, statPriority, adventurePickOrder, heroHpRegenPerDayPercent);
            return Task.FromResult("managed");
        }

        public Task<string> SpendHeroAttributePointsAsync(string statPriority, CancellationToken cancellationToken = default) => Record("spend", cancellationToken, "spent");
        public Task<HeroAttributeSnapshot> ReadHeroAttributeSnapshotAsync(CancellationToken cancellationToken = default) => Record("attributes", cancellationToken, new HeroAttributeSnapshot());
        public Task<HeroInventoryResources> ReadHeroInventoryResourcesAsync(CancellationToken cancellationToken = default, bool suppressUiSync = false) => Record("inventory", cancellationToken, new HeroInventoryResources());
        public HeroInventoryResources? TryGetCachedHeroInventory() => null;
        public Task<string> IncreaseAdventuresToHardAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokens.Add(cancellationToken);
            IncreaseAdventureDangerRequested = true;
            return Task.FromResult("hard");
        }

        public Task<string> ReduceAdventuresTimeAsync(CancellationToken cancellationToken = default)
        {
            CancellationTokens.Add(cancellationToken);
            ReduceAdventureTimeRequested = true;
            return Task.FromResult("reduced");
        }

        private Task<T> Record<T>(string call, CancellationToken cancellationToken, T result)
        {
            Calls.Add(call);
            CancellationTokens.Add(cancellationToken);
            return Task.FromResult(result);
        }
    }
}
