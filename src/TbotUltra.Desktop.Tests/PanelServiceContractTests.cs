using System.Text.Json.Nodes;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class PanelServiceContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-panel-services-{Guid.NewGuid():N}");

    [Fact]
    public void QueuePanelService_ForwardsEveryUserQueueTransitionUnchanged()
    {
        var client = new RecordingQueueClient();
        var service = new QueuePanelService(client);
        var id = Guid.NewGuid();
        var payload = new Dictionary<string, string> { ["village_key"] = "xy:1|2" };

        Assert.Same(client.Items, service.GetItems());
        Assert.True(service.Remove(id));
        Assert.Equal(1, service.RemoveMany([id]));
        Assert.True(service.MoveUp(id));
        Assert.True(service.MoveDown(id));
        Assert.True(service.MoveToTop(id));
        Assert.True(service.MoveToBottom(id));
        Assert.True(service.ApplyOrder([id, Guid.NewGuid()]));
        Assert.True(service.Pause(id));
        Assert.True(service.Resume(id));
        Assert.True(service.Retry(id));
        var queued = service.Enqueue("send_farmlists", payload, priority: 7, maxRetries: 4);

        Assert.Equal(["get", "remove", "remove-many", "up", "down", "top", "bottom", "apply-order", "pause", "resume", "retry", "enqueue"], client.Calls);
        Assert.All(client.ItemIds, actual => Assert.Equal(id, actual));
        Assert.Same(payload, client.EnqueuedPayload);
        Assert.Equal(("send_farmlists", 7, 4), (client.EnqueuedTaskName, client.EnqueuedPriority, client.EnqueuedMaxRetries));
        Assert.Same(client.EnqueuedItem, queued);
    }

    [Fact]
    public void BuildingsPanelService_PreservesQueuePayloadsAndAtomicReconciliation()
    {
        var client = new RecordingBuildingsClient();
        var service = new BuildingsPanelService(client);
        var payload = new Dictionary<string, string> { ["target_level"] = "12" };
        var request = new QueueItemCreateRequest("upgrade_building_to_level", payload, 9, 2);
        var id = Guid.NewGuid();
        var update = new QueuePayloadUpdate(id, new Dictionary<string, string> { ["slot"] = "18" });

        Assert.Same(client.Items, service.GetQueueItems());
        Assert.Same(client.BatchResult, service.EnqueueBatch([request]));
        Assert.Same(client.EnqueuedItem, service.Enqueue("upgrade_building_to_level", payload, 9, 2));
        Assert.True(service.Remove(id));
        Assert.True(service.UpdatePending(id, payload));
        Assert.True(service.ApplyPendingReconciliation([id], [update]));

        Assert.Equal(["get", "batch", "enqueue", "remove", "update", "reconcile"], client.Calls);
        Assert.Same(payload, client.EnqueuedPayload);
        Assert.Same(payload, client.UpdatedPayload);
        Assert.Equal(id, client.UpdatedId);
        Assert.Equal([id], client.ReconciliationRemovals);
        Assert.Same(update, Assert.Single(client.ReconciliationUpdates));
    }

    [Fact]
    public async Task HeroPanelService_ForwardsEveryWorkerReadWithTheActiveCancellationToken()
    {
        var client = new RecordingHeroClient();
        var service = new HeroPanelService(client, CreateConfigStore());
        var options = new BotOptions();
        using var cancellation = new CancellationTokenSource();
        Action<string> log = _ => { };

        Assert.Equal(client.Attributes, await service.ReadAttributesAsync(options, log, cancellation.Token));
        Assert.Equal(client.AdventureCount, await service.ReadAdventureCountAsync(options, log, cancellation.Token));
        Assert.Equal(client.Hp, await service.ReadHpAsync(options, log, cancellation.Token));
        Assert.Equal(client.Inventory, await service.ReadInventoryAsync(options, log, cancellation.Token));

        Assert.Equal(["attributes", "adventures", "hp", "inventory"], client.Calls);
        Assert.All(client.Options, actual => Assert.Same(options, actual));
        Assert.All(client.CancellationTokens, actual => Assert.Equal(cancellation.Token, actual));
    }

    [Fact]
    public void HeroPanelService_PersistsTheCompleteSettingsContractAndRemovesLegacyKeys()
    {
        var store = CreateConfigStore();
        store.Save(new JsonObject
        {
            ["hero_hide_mode_enabled"] = true,
            ["hero_hide_mode"] = "legacy",
            ["unrelated"] = "keep",
        });
        var vm = new HeroViewModel
        {
            MinHpForAdventure = 77,
            AutoRevive = false,
            AutoAssignPoints = false,
            AutoUseOintments = true,
            OintmentTargetHpPercent = 90,
            IsAdventurePickTop = true,
            ContinuousAdventures = true,
            IncreaseAdventuresToHard = true,
            ReduceAdventureTime = true,
            AdventureVideoChancePercent = 35,
        };
        vm.LoadPriorityFromConfig("offence_bonus,resources,defence_bonus,fighting_strength");
        vm.LoadMaximumsFromConfig("resources=40,fighting_strength=80,offence_bonus=0,defence_bonus=100");
        var service = new HeroPanelService(new RecordingHeroClient(), store);

        service.PersistSettings(vm);
        vm.LoadPriorityFromConfig("resources,fighting_strength,offence_bonus,defence_bonus");
        service.PersistPriority(vm);
        var persisted = store.Load();

        Assert.Equal(77, persisted[BotOptionPayloadKeys.HeroMinHpForAdventure]!.GetValue<int>());
        Assert.False(persisted[BotOptionPayloadKeys.HeroAutoRevive]!.GetValue<bool>());
        Assert.False(persisted[BotOptionPayloadKeys.HeroAutoAssignPoints]!.GetValue<bool>());
        Assert.True(persisted[BotOptionPayloadKeys.HeroAutoUseOintments]!.GetValue<bool>());
        Assert.Equal(90, persisted[BotOptionPayloadKeys.HeroOintmentTargetHpPercent]!.GetValue<int>());
        Assert.Equal("resources,fighting_strength,offence_bonus,defence_bonus", persisted[BotOptionPayloadKeys.HeroStatPriority]!.GetValue<string>());
        Assert.Equal("resources=40,fighting_strength=80,offence_bonus=0,defence_bonus=100", persisted[BotOptionPayloadKeys.HeroStatMaximums]!.GetValue<string>());
        Assert.Equal("top", persisted[BotOptionPayloadKeys.HeroAdventurePickOrder]!.GetValue<string>());
        Assert.True(persisted[BotOptionPayloadKeys.HeroContinuousAdventures]!.GetValue<bool>());
        Assert.True(persisted[BotOptionPayloadKeys.IncreaseAdventuresToHard]!.GetValue<bool>());
        Assert.True(persisted[BotOptionPayloadKeys.ReduceAdventureTime]!.GetValue<bool>());
        Assert.Equal(35, persisted[BotOptionPayloadKeys.HeroAdventureVideoChancePercent]!.GetValue<int>());
        Assert.False(persisted.ContainsKey("hero_hide_mode_enabled"));
        Assert.False(persisted.ContainsKey("hero_hide_mode"));
        Assert.Equal("keep", persisted["unrelated"]!.GetValue<string>());
    }

    [Fact]
    public void HeroPanelService_CreatesBoundedIndependentAdventurePayloads()
    {
        var vm = new HeroViewModel { ContinuousAdventures = true, MinHpForAdventure = 64, OintmentTargetHpPercent = 80 };
        vm.LoadPriorityFromConfig("resources,fighting_strength,offence_bonus,defence_bonus");
        var service = new HeroPanelService(new RecordingHeroClient(), CreateConfigStore());

        var payloads = service.CreateAdventurePayloads(vm, availableAdventures: 24);
        payloads[0][BotOptionPayloadKeys.HeroMinHpForAdventure] = "1";

        Assert.Equal(20, payloads.Count);
        Assert.Equal("1", payloads[0][BotOptionPayloadKeys.HeroMinHpForAdventure]);
        Assert.Equal("64", payloads[1][BotOptionPayloadKeys.HeroMinHpForAdventure]);
        Assert.Equal("80", payloads[1][BotOptionPayloadKeys.HeroOintmentTargetHpPercent]);
        Assert.All(payloads, payload => Assert.Equal("resources,fighting_strength,offence_bonus,defence_bonus", payload[BotOptionPayloadKeys.HeroStatPriority]));
        Assert.Single(service.CreateAdventurePayloads(new HeroViewModel(), availableAdventures: 24));
    }

    [Fact]
    public async Task FarmListsWorkflow_ExposesBrowserActionsWithoutCallerLoggingDependency()
    {
        var client = new RecordingFarmingClient();
        var service = CreateFarmListsWorkflow(client, CreateConfigStore());
        var options = new BotOptions();
        var request = new FarmListCreateRequest(["A"], "Capital", "did:1", "Phalanx", 3);
        using var cancellation = new CancellationTokenSource();

        Assert.True(await service.IsGoldClubActiveAsync(options, cancellation.Token));
        Assert.Same(client.Overview, await service.ReadOverviewAsync(options, cancellation.Token));
        Assert.Equal(client.Identity, await service.ReadTargetProtectionIdentityAsync(options, cancellation.Token));
        Assert.Equal(client.CreateResult, await service.CreateListsAsync(options, request, null, cancellation.Token));
        Assert.Equal(2, await service.SendOneAsync(options, "A", cancellation.Token));
        Assert.Equal(3, await service.SendSelectedAsync(options, ["A"], ["11"], cancellation.Token));
        Assert.Equal(4, await service.SendAllAsync(options, cancellation.Token));

        Assert.Equal(["gold", "overview", "identity", "create", "one", "selected", "all"], client.Calls);
        Assert.Same(request, client.CreateRequest);
        Assert.Equal("A", client.SendOneName);
        Assert.Equal(["A"], client.SelectedNames);
        Assert.Equal(["11"], client.SelectedIds);
        Assert.All(client.CancellationTokens, actual => Assert.Equal(cancellation.Token, actual));
    }

    [Fact]
    public void FarmListsWorkflow_ProjectsMergedOverviewAndPersistedSelection()
    {
        var workflow = CreateFarmListsWorkflow(new RecordingFarmingClient(), CreateConfigStore());
        var options = new BotOptions
        {
            ContinuousFarmListIds = ["lid-1"],
            ContinuousFarmDispatchDelayMinMinutes = 12,
            ContinuousFarmDispatchDelayMaxMinutes = 18,
        };
        var lists = new[]
        {
            new FarmListOverview(" Raiders ", 2, 3, 30, "lid-1", 100, ["1|2"], VillageName: "Capital", VillageIndex: 0),
            new FarmListOverview("Raiders", 3, 3, null, "lid-1", 100, ["1|2", "3|4"], VillageName: "Capital", VillageIndex: 0),
            new FarmListOverview("Disabled", 1, 1, null, "lid-2", 50, ["5|6"], VillageName: "Second", VillageIndex: 1),
        };

        var projection = workflow.ProjectOverview(
            lists,
            options,
            new Dictionary<string, string> { ["Capital"] = "(10|20)" },
            new FarmListsPresentationOptions(true, true, 4));

        Assert.Equal(2, projection.Rows.Count);
        var raiders = Assert.Single(projection.Rows, row => row.ListId == "lid-1");
        Assert.Equal("Capital (10|20)", raiders.VillageHeaderText);
        Assert.Equal(3, raiders.ActiveFarmCount);
        Assert.True(raiders.IsEnabled);
        Assert.Equal("12", raiders.IntervalMinMinutesText);
        Assert.False(Assert.Single(projection.Rows, row => row.ListId == "lid-2").IsEnabled);
        Assert.Equal(["1|2", "3|4", "5|6"], projection.AnalyzedCoordinates.Order());
        Assert.Contains("'Raiders' 1/3", projection.IncompleteReads);

        var loadResult = workflow.BuildAddFarmsLoadResult(
            [new TravcoListStore.TravcoSavedList { Name = "Source" }],
            new FarmTargetIdentity(true, "Owner", null));
        Assert.True(loadResult.Ok);
        Assert.Equal(2, loadResult.TargetLists.Count);
        Assert.Contains("3|4", loadResult.ExistingCoordinates);
        Assert.Contains("'Raiders' 1/3", loadResult.IncompleteFarmLists!);
    }

    [Fact]
    public async Task FarmListsWorkflow_OwnsSnapshotRoundTripAndTimerRebase()
    {
        var workflow = CreateFarmListsWorkflow(new RecordingFarmingClient(), CreateConfigStore());
        var lists = new[]
        {
            new FarmListOverview(
                "Raiders", 2, 3, 60, "lid-1", 100, ["1|2", "3|4"],
                VillageName: "Capital", VillageIndex: 0),
        };

        await workflow.SaveSnapshotAsync(lists, CancellationToken.None);
        var fresh = await workflow.LoadFreshSnapshotAsync(CancellationToken.None);
        var restored = await workflow.LoadRestoredSnapshotAsync(DateTimeOffset.UtcNow.AddSeconds(30));

        Assert.Equal("lid-1", Assert.Single(fresh!).ListId);
        var restoredList = Assert.Single(restored!);
        Assert.InRange(restoredList.RemainingSeconds!.Value, 29, 30);
        Assert.Equal(["1|2", "3|4"], restoredList.FarmCoordinates);
        Assert.Equal("Capital", restoredList.VillageName);
    }

    [Fact]
    public void FarmListsWorkflow_OwnsDispatchTransitions()
    {
        var workflow = CreateFarmListsWorkflow(new RecordingFarmingClient(), CreateConfigStore());
        var options = new BotOptions
        {
            ContinuousFarmDispatchDelayMinMinutes = 10,
            ContinuousFarmDispatchDelayMaxMinutes = 10,
        };
        var row = new FarmListStatusRow { Name = "Raiders", ListId = "lid-1", RemainingSeconds = 45 };

        Assert.True(workflow.RecordDispatch(row, succeeded: true, options));
        Assert.NotNull(row.LastSentAtUtc);
        Assert.Equal(row.LastSentAtUtc!.Value.AddMinutes(10), row.NextSendAtUtc);
        Assert.False(row.LastSendFailed);

        Assert.True(workflow.PersistDispatchInterval(row, 20, 20, options));
        Assert.Equal(row.LastSentAtUtc.Value.AddMinutes(20), row.NextSendAtUtc);

        row.RemainingSeconds = 90;
        Assert.True(workflow.ReconcileDispatches(
            [row],
            [FarmListsWorkflow.DispatchKey(row)],
            options));
        Assert.False(row.LastSendFailed);

        row.ActiveFarmCount = 1;
        row.TotalFarmCount = 1;
        row.RemainingSeconds = null;
        row.IsEnabled = true;
        Assert.Equal(["lid:lid-1"], workflow.GetReadyDispatchKeys([row], enabledOnly: true));
        Assert.Equal(["lid:lid-1"], workflow.GetAutoDispatchKeys([row], sendAllLists: true));
    }

    [Fact]
    public void FarmListsWorkflow_PreparesAndPersistsTargetProtection()
    {
        var store = CreateConfigStore();
        store.Save(new JsonObject());
        var workflow = CreateFarmListsWorkflow(new RecordingFarmingClient(), store);
        var unavailable = workflow.PrepareTargetProtection(
            new FarmTargetIdentity(false, null, null),
            new AddFarmsProtectionPreferences(true, "Enemy", "Rivals"));

        Assert.False(unavailable.IsAvailable);

        var prepared = workflow.PrepareTargetProtection(
            new FarmTargetIdentity(true, "Owner", "Friends"),
            new AddFarmsProtectionPreferences(true, " Enemy ; enemy\nBandit ", "Rivals"));

        Assert.True(prepared.IsAvailable);
        Assert.Equal(
            FarmTargetProtectionDecision.ExcludedPlayer,
            prepared.Context!.Evaluate(false, new FarmTargetIdentity(true, "Bandit", null)));
        Assert.Equal(
            FarmTargetProtectionDecision.ExcludedAlliance,
            prepared.Context.Evaluate(false, new FarmTargetIdentity(true, "Someone", "Friends")));
        var loaded = workflow.LoadTargetProtectionPreferences();
        Assert.True(loaded.ExcludeOwnAlliance);
        Assert.Equal(" Enemy ; enemy\nBandit ", loaded.ExcludedPlayers);
        Assert.Equal("Rivals", loaded.ExcludedAlliances);
    }

    [Fact]
    public async Task FarmListsWorkflow_OrchestratesCreateAndMultiListAdd()
    {
        var store = CreateConfigStore();
        store.Save(new JsonObject());
        var client = new RecordingFarmingClient();
        var workflow = CreateFarmListsWorkflow(client, store);
        var options = new BotOptions();
        var createRequest = new FarmListCreateRequest(["A"], "Capital", "did:1", "Phalanx", 3);

        var created = await workflow.CreateAfterAnalysisAsync(
            options,
            createRequest,
            new Progress<FarmListCreateProgress>(),
            CancellationToken.None);

        Assert.Equal(client.CreateResult, created);
        Assert.Equal(["gold", "overview", "create"], client.Calls);

        client.Calls.Clear();
        var protection = new FarmTargetProtectionContext("Owner", null, false, [], []);
        var added = await workflow.RunAddPlansAsync(
            options,
            [
                new OfficialFarmAddPlan(Guid.NewGuid(), "Source", "A", 5, [new FarmCoordinate(1, 2)]),
                new OfficialFarmAddPlan(Guid.NewGuid(), "Source", "B", 5, [new FarmCoordinate(3, 4)]),
            ],
            true,
            "Phalanx",
            3,
            protection,
            new Progress<FarmAddProgress>(),
            CancellationToken.None);

        Assert.Equal(["add", "add"], client.Calls);
        Assert.Equal(10, added.Requested);
        Assert.Equal(6, added.Added);
        Assert.Equal(2, added.Duplicates);
        Assert.Equal(2, added.Failed);
    }

    [Fact]
    public async Task FarmListsWorkflow_PausesAndResumesOriginalAutomationMode()
    {
        var store = CreateConfigStore();
        store.Save(new JsonObject());
        var automation = new RecordingFarmListsAutomationAdapter { ContinuousLoopRunning = true };
        var workflow = new FarmListsWorkflow(
            new RecordingFarmingClient(),
            automation,
            store,
            _root,
            () => "alice",
            _ => { });

        var resume = await workflow.PauseAutomationAsync(CancellationToken.None);

        Assert.True(resume.ContinuousLoop);
        Assert.False(automation.ContinuousLoopRunning);

        await workflow.ResumeAutomationAsync(resume);

        Assert.True(automation.ContinuousLoopRunning);
        Assert.False(automation.AutoQueueRunning);
    }

    [Fact]
    public void FarmListsWorkflow_PersistsSelectionAndRefreshesPendingPayload()
    {
        var store = CreateConfigStore();
        store.Save(new JsonObject());
        var queueItem = new QueueItem
        {
            TaskName = "send_farmlists",
            Group = QueueGroup.Farming,
            Status = QueueStatus.Pending,
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.TargetVillageKey] = "xy:1|2",
                [BotOptionPayloadKeys.ContinuousFarmListNames] = "Old",
            },
        };
        var deadline = queueItem.NextAttemptAt;
        var automation = new RecordingFarmListsAutomationAdapter { QueueItems = [queueItem] };
        var workflow = new FarmListsWorkflow(
            new RecordingFarmingClient(), automation, store, _root, () => "alice", _ => { });

        workflow.SaveSelection(
        [
            new FarmListStatusRow { Name = "Raiders", ListId = "lid-1", IsEnabled = true },
            new FarmListStatusRow { Name = "Disabled", ListId = "lid-2", IsEnabled = false },
        ]);

        var config = store.Load();
        Assert.Equal("Raiders", config[BotOptionPayloadKeys.ContinuousFarmListNames]![0]!.GetValue<string>());
        Assert.Equal("lid-1", config[BotOptionPayloadKeys.ContinuousFarmListIds]![0]!.GetValue<string>());
        Assert.Equal("Raiders", automation.UpdatedPayload![BotOptionPayloadKeys.ContinuousFarmListNames]);
        Assert.Equal("lid-1", automation.UpdatedPayload[BotOptionPayloadKeys.ContinuousFarmListIds]);
        Assert.Equal(deadline, queueItem.NextAttemptAt);
    }

    [Fact]
    public void FarmListsWorkflow_PersistsDestinationStateWithoutChangingTheContract()
    {
        var store = CreateConfigStore();
        store.Save(new JsonObject
        {
            [BotOptionPayloadKeys.ContinuousFarmLossDestinationListId] = "old-id",
            [BotOptionPayloadKeys.ContinuousFarmLossDestinationBaseName] = "Old base",
            ["unrelated"] = "keep",
        });
        var service = CreateFarmListsWorkflow(new RecordingFarmingClient(), store);

        var result = service.SaveSettings(new FarmingPanelSettings(
            SendMode: FarmingDefaults.SendModeSharedSchedule,
            DispatchDelayMinMinutes: 4,
            DispatchDelayMaxMinutes: 9,
            DeactivateRedLosses: true,
            DeactivateYellowLosses: true,
            DeactivateRedOasisLosses: true,
            DeactivateYellowOasisLosses: false,
            MoveRedLosses: true,
            MoveYellowLosses: true,
            SelectedRedDestination: new FarmLossDestinationOption("red-id", "Red farms", "Capital", 3, 12),
            SelectedYellowDestination: new FarmLossDestinationOption("yellow-id", "Yellow farms", "Capital", 4, 12)));
        service.SaveDestinationBaseName(true, "Pinned red base");
        var persisted = store.Load();

        Assert.Equal(FarmingDefaults.SendModeSharedSchedule, result.SendMode);
        Assert.Equal(FarmingDefaults.SendModeSharedSchedule, persisted[BotOptionPayloadKeys.ContinuousFarmSendMode]!.GetValue<string>());
        Assert.True(result.MoveRedLossesEnabled);
        Assert.True(result.MoveYellowLossesEnabled);
        Assert.Equal("red-id", persisted[BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId]!.GetValue<string>());
        Assert.Equal("Red farms", persisted[BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListName]!.GetValue<string>());
        Assert.Equal("Pinned red base", persisted[BotOptionPayloadKeys.ContinuousFarmRedLossDestinationBaseName]!.GetValue<string>());
        Assert.Equal("yellow-id", persisted[BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId]!.GetValue<string>());
        Assert.Equal(4, persisted[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinMinutes]!.GetValue<int>());
        Assert.Equal(9, persisted[BotOptionPayloadKeys.ContinuousFarmDispatchDelayMaxMinutes]!.GetValue<int>());
        Assert.True(persisted[BotOptionPayloadKeys.ContinuousFarmMoveLosses]!.GetValue<bool>());
        Assert.Equal("keep", persisted["unrelated"]!.GetValue<string>());

        var disabled = service.SaveSettings(new FarmingPanelSettings(
            FarmingDefaults.SendModeAllAtOnce, 1, 2,
            false, false,
            false, false,
            true, true,
            new FarmLossDestinationOption("red", "Red", "Capital", 1, 2),
            new FarmLossDestinationOption("yellow", "Yellow", "Capital", 1, 2)));
        Assert.False(disabled.MoveRedLossesEnabled);
        Assert.False(disabled.MoveYellowLossesEnabled);
        Assert.False(store.Load()[BotOptionPayloadKeys.ContinuousFarmMoveLosses]!.GetValue<bool>());
    }

    [Fact]
    public void ResourcesPanelService_RoundTripsAccountAndVillageScopedSettings()
    {
        var config = CreateConfigStore();
        config.Save(new JsonObject());
        var villages = new VillageSettingsStore(_root, () => "alice");
        var service = new ResourcesPanelService(config, villages);
        var village = new VillageSettingsStore.VillageKeyInfo("did:2", "Capital", 2, -3, true);

        service.SaveBuildStrategy("balanced");
        service.SaveUpgradeTypes(village, ["wood", "crop"]);

        Assert.Equal("balanced", CreateConfigStore().Load()[BotOptionPayloadKeys.ResourceBuildStrategy]!.GetValue<string>());
        Assert.Equal(["wood", "crop"], new VillageSettingsStore(_root, () => "alice").GetResourceUpgradeTypes(village));
    }

    [Fact]
    public async Task TroopTrainingPanelService_ForwardsEveryWorkerReadWithTheActiveCancellationToken()
    {
        var client = new RecordingTroopTrainingClient();
        var service = new TroopTrainingPanelService(client, CreateConfigStore(), _root);
        var options = new BotOptions();
        IReadOnlyList<Building> buildings = [];
        using var cancellation = new CancellationTokenSource();
        Action<string> log = _ => { };

        Assert.Same(client.Status, await service.ReadBuildingsAsync(options, log, cancellation.Token));
        Assert.Same(client.Queues, await service.ReadQueuesAsync(options, log, buildings, cancellation.Token));
        Assert.Same(client.Smithy, await service.ReadSmithyStatusAsync(options, log, buildings, cancellation.Token));
        Assert.Same(client.Brewery, await service.ReadBreweryStatusAsync(options, log, buildings, cancellation.Token));

        Assert.Equal(["buildings", "queues", "smithy", "brewery"], client.Calls);
        Assert.All(client.CancellationTokens, actual => Assert.Equal(cancellation.Token, actual));
        Assert.All(client.BuildingArguments, actual => Assert.Same(buildings, actual));
    }

    [Fact]
    public void TroopTrainingPanelService_RoundTripsVillageAndGlobalSettings()
    {
        var store = CreateConfigStore();
        store.Save(new JsonObject());
        var service = new TroopTrainingPanelService(new RecordingTroopTrainingClient(), store, _root);
        var payload = TrainingPayload("Phalanx", fallback: 45);

        service.SaveVillageSettings("alice", "xy:1|2", payload);
        service.SaveVillageSettings("alice", ["xy:3|4", "xy:5|6"], payload);
        var vm = new TroopTrainingViewModel { NpcTradeEnabled = true, GoldLimit = 321 };
        service.SaveGlobalSettings(vm);

        Assert.Equal(payload, service.LoadVillageSettings("alice", "xy:1|2"));
        Assert.Equal(payload, service.LoadVillageSettings("alice", "xy:3|4"));
        Assert.Equal(payload, service.LoadVillageSettings("alice", "xy:5|6"));
        var persisted = store.Load();
        Assert.True(persisted[BotOptionPayloadKeys.NpcTradeEnabled]!.GetValue<bool>());
        Assert.Equal(321, persisted[BotOptionPayloadKeys.GoldLimit]!.GetValue<int>());
    }

    private BotConfigStore CreateConfigStore()
    {
        Directory.CreateDirectory(_root);
        return new BotConfigStore(Path.Combine(_root, "bot.json"), _root, () => "alice");
    }

    private FarmListsWorkflow CreateFarmListsWorkflow(IFarmListsBrowserAdapter client, BotConfigStore store)
        => new(client, new RecordingFarmListsAutomationAdapter(), store, _root, () => "alice", _ => { });

    private sealed class RecordingFarmListsAutomationAdapter : IFarmListsAutomationAdapter
    {
        public bool ContinuousLoopRunning { get; set; }
        public bool StartContinuousAfterQueueStop { get; set; }
        public bool AutoQueueRunning { get; set; }
        public bool UiBusy { get; set; }
        public bool SessionAvailable { get; set; } = true;
        public IReadOnlyList<QueueItem> QueueItems { get; set; } = [];
        public Dictionary<string, string>? UpdatedPayload { get; private set; }
        public void ClearPendingRestarts() { }
        public void RequestStopAfterCurrentAction()
        {
            ContinuousLoopRunning = false;
            AutoQueueRunning = false;
            UiBusy = false;
        }
        public void UpdateExecutionIndicator() { }
        public void StartContinuousLoop() => ContinuousLoopRunning = true;
        public Task StartAutoQueueAsync()
        {
            AutoQueueRunning = true;
            return Task.CompletedTask;
        }
        public bool UpdateDeferredQueueItem(Guid id, Dictionary<string, string> payload)
        {
            UpdatedPayload = payload;
            return true;
        }
    }

    private static TroopTrainingPayload TrainingPayload(string troop, int fallback)
    {
        var building = new TroopTrainingBuildingPayload(true, troop, "no_limit", "fixed", 10, "timed", 1, 20, 30, 60, true, true, true, true);
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
            // Best-effort cleanup for a temporary test directory.
        }
    }

    private sealed class RecordingQueueClient : IQueuePanelClient
    {
        public IReadOnlyList<QueueItem> Items { get; } = [new QueueItem { TaskName = "queued" }];
        public QueueItem EnqueuedItem { get; } = new() { TaskName = "queued" };
        public List<string> Calls { get; } = [];
        public List<Guid> ItemIds { get; } = [];
        public string EnqueuedTaskName { get; private set; } = string.Empty;
        public Dictionary<string, string>? EnqueuedPayload { get; private set; }
        public int EnqueuedPriority { get; private set; }
        public int EnqueuedMaxRetries { get; private set; }

        public IReadOnlyList<QueueItem> GetItems() { Calls.Add("get"); return Items; }
        public bool Remove(Guid id) => Record("remove", id);
        public int RemoveMany(IReadOnlyCollection<Guid> ids) { Calls.Add("remove-many"); return ids.Count; }
        public bool MoveUp(Guid id) => Record("up", id);
        public bool MoveDown(Guid id) => Record("down", id);
        public bool MoveToTop(Guid id) => Record("top", id);
        public bool MoveToBottom(Guid id) => Record("bottom", id);
        public bool ApplyOrder(IReadOnlyList<Guid> orderedIds) { Calls.Add("apply-order"); return orderedIds.Count > 0; }
        public bool Pause(Guid id) => Record("pause", id);
        public bool Resume(Guid id) => Record("resume", id);
        public bool Retry(Guid id) => Record("retry", id);
        public QueueItem Enqueue(string taskName, Dictionary<string, string> payload, int priority, int maxRetries)
        {
            Calls.Add("enqueue"); EnqueuedTaskName = taskName; EnqueuedPayload = payload; EnqueuedPriority = priority; EnqueuedMaxRetries = maxRetries; return EnqueuedItem;
        }
        private bool Record(string call, Guid id) { Calls.Add(call); ItemIds.Add(id); return true; }
    }

    private sealed class RecordingBuildingsClient : IBuildingsPanelClient
    {
        public IReadOnlyList<QueueItem> Items { get; } = [new QueueItem { TaskName = "existing" }];
        public IReadOnlyList<QueueItem> BatchResult { get; } = [new QueueItem { TaskName = "batch" }];
        public QueueItem EnqueuedItem { get; } = new() { TaskName = "single" };
        public List<string> Calls { get; } = [];
        public Dictionary<string, string>? EnqueuedPayload { get; private set; }
        public Dictionary<string, string>? UpdatedPayload { get; private set; }
        public Guid UpdatedId { get; private set; }
        public IReadOnlyList<Guid> ReconciliationRemovals { get; private set; } = [];
        public IReadOnlyList<QueuePayloadUpdate> ReconciliationUpdates { get; private set; } = [];

        public IReadOnlyList<QueueItem> GetQueueItems() { Calls.Add("get"); return Items; }
        public IReadOnlyList<QueueItem> EnqueueBatch(IReadOnlyList<QueueItemCreateRequest> requests) { Calls.Add("batch"); return BatchResult; }
        public QueueItem Enqueue(string taskName, Dictionary<string, string> payload, int priority, int maxRetries) { Calls.Add("enqueue"); EnqueuedPayload = payload; return EnqueuedItem; }
        public bool Remove(Guid id) { Calls.Add("remove"); return true; }
        public bool UpdatePending(Guid id, Dictionary<string, string> payload) { Calls.Add("update"); UpdatedId = id; UpdatedPayload = payload; return true; }
        public bool ApplyPendingReconciliation(IReadOnlyList<Guid> removals, IReadOnlyList<QueuePayloadUpdate> updates) { Calls.Add("reconcile"); ReconciliationRemovals = removals; ReconciliationUpdates = updates; return true; }
    }

    private sealed class RecordingHeroClient : IHeroPanelClient
    {
        public HeroAttributeSnapshot Attributes { get; } = new(FreePoints: 3);
        public int? AdventureCount { get; } = 4;
        public int? Hp { get; } = 75;
        public HeroInventoryResources Inventory { get; } = new(1, 2, 3, 4);
        public List<string> Calls { get; } = [];
        public List<BotOptions> Options { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];
        public Task<HeroAttributeSnapshot> ReadAttributesAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken) => Record("attributes", options, cancellationToken, Attributes);
        public Task<int?> ReadAdventureCountAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken) => Record("adventures", options, cancellationToken, AdventureCount);
        public Task<int?> ReadHpAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken) => Record("hp", options, cancellationToken, Hp);
        public Task<HeroInventoryResources> ReadInventoryAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken) => Record("inventory", options, cancellationToken, Inventory);
        private Task<T> Record<T>(string call, BotOptions options, CancellationToken token, T result) { Calls.Add(call); Options.Add(options); CancellationTokens.Add(token); return Task.FromResult(result); }
    }

    private sealed class RecordingFarmingClient : IFarmListsBrowserAdapter
    {
        public IReadOnlyList<FarmListOverview> Overview { get; } = [new("A", 1, 2, 30)];
        public FarmAddBatchResult AddResult { get; } = new("A", 5, 5, 3, 1, 1);
        public FarmTargetIdentity Identity { get; } = new(true, "Owner", "Alliance");
        public FarmListCreateBatchResult CreateResult { get; } = new(1, 1, ["A"]);
        public List<string> Calls { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];
        public IReadOnlyList<FarmCoordinate>? Coordinates { get; private set; }
        public FarmListCreateRequest? CreateRequest { get; private set; }
        public string? SendOneName { get; private set; }
        public IReadOnlyCollection<string>? SelectedNames { get; private set; }
        public IReadOnlyCollection<string>? SelectedIds { get; private set; }
        public Task<bool> ReadAndPersistGoldClubStatusAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken) => Record("gold", cancellationToken, true);
        public Task<IReadOnlyList<FarmListOverview>> ReadOverviewAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken) => Record("overview", cancellationToken, Overview);
        public Task<FarmAddBatchResult> AddFarmsAsync(BotOptions options, string farmListName, string troopType, int troopCount, int requestedCount, IReadOnlyList<FarmCoordinate> coordinates, bool useDefaultTroops, FarmTargetProtectionContext? protection, Action<string> log, IProgress<FarmAddProgress>? progress, CancellationToken cancellationToken) { Coordinates = coordinates; return Record("add", cancellationToken, AddResult); }
        public Task<FarmTargetIdentity> ReadTargetProtectionIdentityAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken) => Record("identity", cancellationToken, Identity);
        public Task<FarmListCreateBatchResult> CreateListsAsync(BotOptions options, FarmListCreateRequest request, Action<string> log, IProgress<FarmListCreateProgress>? progress, CancellationToken cancellationToken) { CreateRequest = request; return Record("create", cancellationToken, CreateResult); }
        public Task<int?> SendOneAsync(BotOptions options, string farmListName, Action<string> log, CancellationToken cancellationToken) { SendOneName = farmListName; return Record("one", cancellationToken, (int?)2); }
        public Task<int> SendSelectedAsync(BotOptions options, IReadOnlyCollection<string> names, IReadOnlyCollection<string> ids, Action<string> log, CancellationToken cancellationToken) { SelectedNames = names; SelectedIds = ids; return Record("selected", cancellationToken, 3); }
        public Task<int> SendAllAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken) => Record("all", cancellationToken, 4);
        private Task<T> Record<T>(string call, CancellationToken token, T result) { Calls.Add(call); CancellationTokens.Add(token); return Task.FromResult(result); }
    }

    private sealed class RecordingTroopTrainingClient : ITroopTrainingPanelClient
    {
        public VillageStatus Status { get; } = new("Capital", [], new Dictionary<string, string>(), [], [], []);
        public IReadOnlyList<TroopTrainingQueueStatus> Queues { get; } = [];
        public SmithyUpgradeStatus Smithy { get; } = new(false, null, 0, null, [], "", "");
        public BreweryCelebrationStatus Brewery { get; } = new(false, null, false, null, false, null, "", "");
        public List<string> Calls { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];
        public List<IReadOnlyList<Building>?> BuildingArguments { get; } = [];
        public Task<VillageStatus> ReadBuildingsAsync(BotOptions options, Action<string> log, CancellationToken cancellationToken) => Record("buildings", cancellationToken, Status);
        public Task<IReadOnlyList<TroopTrainingQueueStatus>> ReadQueuesAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken) { BuildingArguments.Add(buildings); return Record("queues", cancellationToken, Queues); }
        public Task<SmithyUpgradeStatus> ReadSmithyStatusAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken) { BuildingArguments.Add(buildings); return Record("smithy", cancellationToken, Smithy); }
        public Task<BreweryCelebrationStatus> ReadBreweryStatusAsync(BotOptions options, Action<string> log, IReadOnlyList<Building>? buildings, CancellationToken cancellationToken) { BuildingArguments.Add(buildings); return Record("brewery", cancellationToken, Brewery); }
        private Task<T> Record<T>(string call, CancellationToken token, T result) { Calls.Add(call); CancellationTokens.Add(token); return Task.FromResult(result); }
    }
}
