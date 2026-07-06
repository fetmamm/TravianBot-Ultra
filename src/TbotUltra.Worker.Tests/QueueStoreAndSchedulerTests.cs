using CoreTaskCatalog = TbotUltra.Core.Tasks.TaskCatalog;
using System.Text.Json;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class QueueStoreAndSchedulerTests : IDisposable
{
    private readonly string _root;
    private readonly string _queuePath;

    public QueueStoreAndSchedulerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _queuePath = Path.Combine(_root, "queue.json");
    }

    [Fact]
    public void Scheduler_UsesPriorityAndFifo()
    {
        var store = new JsonQueueStore(_queuePath);
        var scheduler = new PriorityFifoQueueScheduler();

        var low = store.Add("status", null, priority: 1, maxRetries: 3);
        Thread.Sleep(20);
        var highA = store.Add("scan_all_villages", null, priority: 5, maxRetries: 3);
        Thread.Sleep(20);
        var highB = store.Add("account_snapshot", null, priority: 5, maxRetries: 3);

        var ordered = scheduler.OrderForDisplay(store.GetAll()).ToList();
        Assert.Equal(highA.Id, ordered[0].Id);
        Assert.Equal(highB.Id, ordered[1].Id);
        Assert.Equal(low.Id, ordered[2].Id);

        var next = scheduler.SelectNext(store.GetAll());
        Assert.NotNull(next);
        Assert.Equal(highA.Id, next!.Id);
    }

    [Fact]
    public void PauseAndResume_ChangesEligibility()
    {
        var store = new JsonQueueStore(_queuePath);
        var scheduler = new PriorityFifoQueueScheduler();
        var item = store.Add("status", null, priority: 1, maxRetries: 3);

        Assert.True(store.Pause(item.Id));
        var paused = store.GetAll().Single(entry => entry.Id == item.Id);
        Assert.Equal(QueueStatus.Paused, paused.Status);
        Assert.Null(scheduler.SelectNext(store.GetAll()));

        Assert.True(store.Resume(item.Id));
        var resumed = store.GetAll().Single(entry => entry.Id == item.Id);
        Assert.Equal(QueueStatus.Pending, resumed.Status);
        Assert.NotNull(scheduler.SelectNext(store.GetAll()));
    }

    [Fact]
    public void MarkExecutionFailed_RetriesAndThenFails()
    {
        var store = new JsonQueueStore(_queuePath);
        var item = store.Add("status", null, priority: 1, maxRetries: 1);

        Assert.True(store.MarkRunning(item.Id));
        Assert.True(store.MarkExecutionFailed(item.Id));
        var firstFail = store.GetAll().Single(entry => entry.Id == item.Id);
        Assert.Equal(1, firstFail.Retries);
        Assert.Equal(QueueStatus.Pending, firstFail.Status);

        Assert.True(store.MarkRunning(item.Id));
        Assert.True(store.MarkExecutionFailed(item.Id));
        var secondFail = store.GetAll().Single(entry => entry.Id == item.Id);
        Assert.Equal(2, secondFail.Retries);
        Assert.Equal(QueueStatus.Failed, secondFail.Status);
    }

    [Fact]
    public void ResetOrphanedRunningItems_RestoresRunningToPending()
    {
        var store = new JsonQueueStore(_queuePath);
        var scheduler = new PriorityFifoQueueScheduler();
        var running = store.Add("status", null, priority: 1, maxRetries: 3);
        var pending = store.Add("scan_all_villages", null, priority: 1, maxRetries: 3);
        Assert.True(store.MarkRunning(running.Id));

        var resetCount = store.ResetOrphanedRunningItems();

        Assert.Equal(1, resetCount);
        var recovered = store.GetAll().Single(entry => entry.Id == running.Id);
        Assert.Equal(QueueStatus.Pending, recovered.Status);
        // Recovered items defer briefly (the crash may have happened after the browser action but
        // before the defer persisted) — so the recovered head is pending but not immediately due.
        Assert.True(recovered.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(30));
        var untouched = store.GetAll().Single(entry => entry.Id == pending.Id);
        Assert.Equal(QueueStatus.Pending, untouched.Status);
        Assert.Null(scheduler.SelectNext(store.GetAll()));
    }

    [Fact]
    public void ResetOrphanedRunningItems_NoRunning_ReturnsZero()
    {
        var store = new JsonQueueStore(_queuePath);
        store.Add("status", null, priority: 1, maxRetries: 3);

        Assert.Equal(0, store.ResetOrphanedRunningItems());
    }

    [Fact]
    public void DynamicPathProvider_IsolatesQueuesPerResolvedPath()
    {
        // The Desktop app constructs the store with a path provider that follows the active account.
        // Switching the resolved path must point the same store instance at a different queue file,
        // with no bleed between them (mirrors switching accounts at runtime).
        var account = "alice";
        var store = new JsonQueueStore(() => Path.Combine(_root, $"{account}.queue.json"));

        var aliceItem = store.Add("status", null, priority: 1, maxRetries: 3);

        account = "bob";
        Assert.Empty(store.GetAll());
        var bobItem = store.Add("scan_all_villages", null, priority: 1, maxRetries: 3);
        Assert.Single(store.GetAll());
        Assert.Equal(bobItem.Id, store.GetAll()[0].Id);

        account = "alice";
        var aliceItems = store.GetAll();
        Assert.Single(aliceItems);
        Assert.Equal(aliceItem.Id, aliceItems[0].Id);

        Assert.True(File.Exists(Path.Combine(_root, "alice.queue.json")));
        Assert.True(File.Exists(Path.Combine(_root, "bob.queue.json")));
    }

    [Fact]
    public void TaskCatalog_AllowsKnownTasks_AndRejectsUnknown()
    {
        Assert.True(TbotUltra.Worker.Services.TaskCatalog.IsAllowed("status"));
        Assert.True(TbotUltra.Worker.Services.TaskCatalog.IsAllowed("upgrade_building_to_max"));
        Assert.True(TbotUltra.Worker.Services.TaskCatalog.IsAllowed("demolish_building_to_level"));
        Assert.True(TbotUltra.Worker.Services.TaskCatalog.IsAllowed("hero_manage"));
        Assert.True(TbotUltra.Worker.Services.TaskCatalog.IsAllowed("spend_hero_attribute_points"));
        Assert.True(TbotUltra.Worker.Services.TaskCatalog.IsAllowed("send_resources_between_villages"));
        Assert.True(TbotUltra.Worker.Services.TaskCatalog.IsAllowed("send_reinforcements_between_villages"));
        Assert.True(TbotUltra.Worker.Services.TaskCatalog.IsAllowed("collect_daily_quests"));
        Assert.False(TbotUltra.Worker.Services.TaskCatalog.IsAllowed("train_troops"));
    }

    [Fact]
    public void TaskCatalog_Descriptors_PreserveAllowedTaskOrder()
    {
        Assert.Equal(
            [
                "status",
                "scan_all_villages",
                "account_snapshot",
                "upgrade_resource_to_level",
                "upgrade_all_resources_to_level",
                "upgrade_building_to_level",
                "upgrade_building_to_max",
                "construct_building",
                "load_buildings_snapshot",
                "demolish_building_to_level",
                "hero_manage",
                "spend_hero_attribute_points",
                "upgrade_troops_at_smithy",
                "build_troops",
                "run_brewery_celebration",
                "run_town_hall_celebration",
                "send_farmlists",
                "send_resources_between_villages",
                "send_reinforcements_between_villages",
                "collect_tasks",
                "collect_daily_quests",
                "activate_production_bonus",
            ],
            CoreTaskCatalog.AllowedTaskNames);
    }

    [Theory]
    [InlineData("upgrade_resource_to_level", TaskGroup.Construction, TaskPayloadKind.ResourceUpgrade)]
    [InlineData("upgrade_building_to_level", TaskGroup.Construction, TaskPayloadKind.BuildingUpgrade)]
    [InlineData("upgrade_building_to_max", TaskGroup.Construction, TaskPayloadKind.BuildingUpgrade)]
    [InlineData("construct_building", TaskGroup.Construction, TaskPayloadKind.BuildingConstruct)]
    [InlineData("hero_manage", TaskGroup.Hero, TaskPayloadKind.Hero)]
    [InlineData("spend_hero_attribute_points", TaskGroup.Hero, TaskPayloadKind.Hero)]
    [InlineData("send_farmlists", TaskGroup.Farming, TaskPayloadKind.Farming)]
    [InlineData("build_troops", TaskGroup.TroopTraining, TaskPayloadKind.TroopTraining)]
    [InlineData("run_brewery_celebration", TaskGroup.BreweryCelebration, TaskPayloadKind.Brewery)]
    [InlineData("run_town_hall_celebration", TaskGroup.TownHallCelebration, TaskPayloadKind.None)]
    [InlineData("send_resources_between_villages", TaskGroup.ResourceTransfer, TaskPayloadKind.ResourceTransfer)]
    [InlineData("collect_daily_quests", TaskGroup.Construction, TaskPayloadKind.None)]
    public void TaskCatalog_Descriptors_ExposeGroupAndPayloadKind(string taskName, TaskGroup group, TaskPayloadKind payloadKind)
    {
        Assert.True(CoreTaskCatalog.TryGetDescriptor(taskName, out var descriptor));
        Assert.Equal(group, descriptor.Group);
        Assert.Equal(payloadKind, descriptor.PayloadKind);
    }

    [Theory]
    [InlineData("status", QueueGroup.Construction)]
    [InlineData("upgrade_resource_to_level", QueueGroup.Construction)]
    [InlineData("construct_building", QueueGroup.Construction)]
    [InlineData("hero_manage", QueueGroup.Hero)]
    [InlineData("spend_hero_attribute_points", QueueGroup.Hero)]
    [InlineData("upgrade_troops_at_smithy", QueueGroup.Troops)]
    [InlineData("build_troops", QueueGroup.TroopTraining)]
    [InlineData("run_brewery_celebration", QueueGroup.BreweryCelebration)]
    [InlineData("run_town_hall_celebration", QueueGroup.TownHallCelebration)]
    [InlineData("send_farmlists", QueueGroup.Farming)]
    [InlineData("send_resources_between_villages", QueueGroup.ResourceTransfer)]
    [InlineData("send_reinforcements_between_villages", QueueGroup.Reinforcements)]
    [InlineData("collect_daily_quests", QueueGroup.Construction)]
    public void QueueGroupCatalog_ResolvesKnownTasks_FromDescriptors(string taskName, QueueGroup expected)
    {
        Assert.Equal(expected, QueueGroupCatalog.ResolveGroup(taskName));
    }

    [Fact]
    public void ResourceUpgradePayload_ParsesAndSerializesDictionary()
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeSlotId] = "4",
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = "50",
            [BotOptionPayloadKeys.ResourceUpgradeName] = "Clay pit",
        };

        Assert.True(ResourceUpgradePayload.TryFromDictionary(payload, out var parsed, maxLevel: 20));
        Assert.NotNull(parsed);
        Assert.Equal(4, parsed!.SlotId);
        Assert.Equal(20, parsed.TargetLevel);
        Assert.Equal("Clay pit", parsed.Name);
        var serialized = parsed.ToDictionary();
        Assert.Equal(3, serialized.Count);
        Assert.Equal("4", serialized[BotOptionPayloadKeys.ResourceUpgradeSlotId]);
        Assert.Equal("20", serialized[BotOptionPayloadKeys.ResourceUpgradeTargetLevel]);
        Assert.Equal("Clay pit", serialized[BotOptionPayloadKeys.ResourceUpgradeName]);
    }

    [Theory]
    [InlineData("", "10")]
    [InlineData("19", "10")]
    [InlineData("4", "0")]
    [InlineData("4", "bad")]
    public void ResourceUpgradePayload_RejectsInvalidPayload(string slotId, string targetLevel)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeSlotId] = slotId,
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = targetLevel,
        };

        Assert.False(ResourceUpgradePayload.TryFromDictionary(payload, out _));
    }

    [Fact]
    public void BuildingPayloads_ParseAndSerializeDictionary()
    {
        var upgrade = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = "20",
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = "10",
            [BotOptionPayloadKeys.BuildingUpgradeName] = "Main Building",
        };
        var construct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingConstructSlotId] = "21",
            [BotOptionPayloadKeys.BuildingConstructGid] = "19",
            [BotOptionPayloadKeys.BuildingConstructName] = "Barracks",
        };

        Assert.True(BuildingUpgradePayload.TryFromDictionary(upgrade, out var parsedUpgrade));
        Assert.Equal(20, parsedUpgrade!.SlotId);
        Assert.Equal(10, parsedUpgrade.TargetLevel);
        Assert.Equal("Main Building", parsedUpgrade.Name);
        var serializedUpgrade = parsedUpgrade.ToDictionary();
        Assert.Equal(3, serializedUpgrade.Count);
        Assert.Equal("20", serializedUpgrade[BotOptionPayloadKeys.BuildingUpgradeSlotId]);
        Assert.Equal("10", serializedUpgrade[BotOptionPayloadKeys.BuildingUpgradeTargetLevel]);
        Assert.Equal("Main Building", serializedUpgrade[BotOptionPayloadKeys.BuildingUpgradeName]);

        Assert.True(BuildingConstructPayload.TryFromDictionary(construct, out var parsedConstruct));
        Assert.Equal(21, parsedConstruct!.SlotId);
        Assert.Equal(19, parsedConstruct.Gid);
        Assert.Equal("Barracks", parsedConstruct.Name);
        var serializedConstruct = parsedConstruct.ToDictionary();
        Assert.Equal(3, serializedConstruct.Count);
        Assert.Equal("21", serializedConstruct[BotOptionPayloadKeys.BuildingConstructSlotId]);
        Assert.Equal("19", serializedConstruct[BotOptionPayloadKeys.BuildingConstructGid]);
        Assert.Equal("Barracks", serializedConstruct[BotOptionPayloadKeys.BuildingConstructName]);
    }

    [Fact]
    public void BuildingPayloads_RejectInvalidPayload()
    {
        Assert.False(BuildingUpgradePayload.TryFromDictionary(
            new Dictionary<string, string> { [BotOptionPayloadKeys.BuildingUpgradeSlotId] = "0" },
            out _));
        Assert.False(BuildingConstructPayload.TryFromDictionary(
            new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.BuildingConstructSlotId] = "21",
                [BotOptionPayloadKeys.BuildingConstructGid] = "0",
            },
            out _));
    }

    [Fact]
    public void HeroPayload_ParsesAndSerializesDictionary()
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.HeroMinHpForAdventure] = "60",
            [BotOptionPayloadKeys.HeroAutoRevive] = "true",
            [BotOptionPayloadKeys.HeroAutoAssignPoints] = "false",
            [BotOptionPayloadKeys.HeroAutoUseOintments] = "true",
            [BotOptionPayloadKeys.HeroStatPriority] = "resources,fighting_strength",
            [BotOptionPayloadKeys.HeroAdventurePickOrder] = "top",
        };

        Assert.True(HeroPayload.TryFromDictionary(payload, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(60, parsed!.MinHpForAdventure);
        Assert.True(parsed.AutoRevive);
        Assert.False(parsed.AutoAssignPoints);
        Assert.True(parsed.AutoUseOintments);
        Assert.Equal("top", parsed.AdventurePickOrder);
        var serialized = parsed.ToDictionary();
        Assert.Equal(6, serialized.Count);
        Assert.Equal("60", serialized[BotOptionPayloadKeys.HeroMinHpForAdventure]);
        Assert.Equal("true", serialized[BotOptionPayloadKeys.HeroAutoRevive]);
        Assert.Equal("false", serialized[BotOptionPayloadKeys.HeroAutoAssignPoints]);
        Assert.Equal("top", serialized[BotOptionPayloadKeys.HeroAdventurePickOrder]);
    }

    [Theory]
    [InlineData("0", "true")]
    [InlineData("101", "true")]
    [InlineData("60", "maybe")]
    public void HeroPayload_RejectsInvalidPayload(string minHp, string autoRevive)
    {
        Assert.False(HeroPayload.TryFromDictionary(
            new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.HeroMinHpForAdventure] = minHp,
                [BotOptionPayloadKeys.HeroAutoRevive] = autoRevive,
            },
            out _));
    }

    [Fact]
    public void ResourceTransferPayload_ParsesAndSerializesDictionary()
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceTransferEnabled] = "true",
            [BotOptionPayloadKeys.ResourceTransferTargetVillageName] = "Capital",
            [BotOptionPayloadKeys.ResourceTransferSourceVillageNames] = "A,B,A",
            [BotOptionPayloadKeys.ResourceTransferSourceThresholdPercent] = "120",
            [BotOptionPayloadKeys.ResourceTransferSourceKeepPercent] = "5",
            [BotOptionPayloadKeys.ResourceTransferTargetFillPercent] = "90",
            [BotOptionPayloadKeys.ResourceTransferSendWood] = "true",
            [BotOptionPayloadKeys.ResourceTransferSendClay] = "false",
            [BotOptionPayloadKeys.ResourceTransferSendIron] = "true",
            [BotOptionPayloadKeys.ResourceTransferSendCrop] = "false",
        };

        Assert.True(ResourceTransferPayload.TryFromDictionary(payload, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal("Capital", parsed!.TargetVillageName);
        Assert.Equal(["A", "B"], parsed.SourceVillageNames);
        Assert.Equal(100, parsed.SourceThresholdPercent);
        var serialized = parsed.ToDictionary();
        Assert.Equal(10, serialized.Count);
        Assert.Equal("A,B", serialized[BotOptionPayloadKeys.ResourceTransferSourceVillageNames]);
        Assert.Equal("false", serialized[BotOptionPayloadKeys.ResourceTransferSendClay]);
    }

    [Fact]
    public void ReinforcementsPayload_ParsesAndSerializesDictionary()
    {
        var rules = new[]
        {
            new ReinforcementTroopRule
            {
                AccountName = "acc",
                SourceVillageName = "A",
                TroopType = "Phalanx",
                AmountMode = "all_available",
                Amount = 1,
                IsEnabled = true,
            },
        };
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ReinforcementsEnabled] = "true",
            [BotOptionPayloadKeys.ReinforcementsTargetVillageName] = "Capital",
            [BotOptionPayloadKeys.ReinforcementsSourceVillageNames] = "A,B",
            [BotOptionPayloadKeys.ReinforcementsTroopRules] = JsonSerializer.Serialize(rules),
        };

        Assert.True(ReinforcementsPayload.TryFromDictionary(payload, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal("Capital", parsed!.TargetVillageName);
        Assert.Equal(["A", "B"], parsed.SourceVillageNames);
        Assert.Single(parsed.TroopRules);
        var serialized = parsed.ToDictionary();
        Assert.Equal(4, serialized.Count);
        Assert.Equal("A,B", serialized[BotOptionPayloadKeys.ReinforcementsSourceVillageNames]);
        var serializedRules = JsonSerializer.Deserialize<List<ReinforcementTroopRule>>(serialized[BotOptionPayloadKeys.ReinforcementsTroopRules]);
        Assert.Single(serializedRules!);
        Assert.Equal("Phalanx", serializedRules![0].TroopType);
    }

    [Fact]
    public void FarmingPayload_ParsesAndSerializesDictionary()
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ContinuousFarmListNames] = "List A,List B,List A",
        };

        Assert.True(FarmingPayload.TryFromDictionary(payload, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(["List A", "List B"], parsed!.FarmListNames);
        var serialized = parsed.ToDictionary();
        Assert.Single(serialized);
        Assert.Equal("List A,List B", serialized[BotOptionPayloadKeys.ContinuousFarmListNames]);
    }

    [Fact]
    public void FarmingPayload_RoundTripsListIds()
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ContinuousFarmListNames] = "List A,List B",
            [BotOptionPayloadKeys.ContinuousFarmListIds] = "39,42,39",
        };

        Assert.True(FarmingPayload.TryFromDictionary(payload, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(["39", "42"], parsed!.FarmListIds);

        var serialized = parsed.ToDictionary();
        Assert.Equal("List A,List B", serialized[BotOptionPayloadKeys.ContinuousFarmListNames]);
        Assert.Equal("39,42", serialized[BotOptionPayloadKeys.ContinuousFarmListIds]);
    }

    [Fact]
    public void FarmingPayload_OmitsListIdsKeyWhenEmpty()
    {
        var parsed = new FarmingPayload(["List A"]);
        var serialized = parsed.ToDictionary();
        Assert.Single(serialized);
        Assert.False(serialized.ContainsKey(BotOptionPayloadKeys.ContinuousFarmListIds));
    }

    [Fact]
    public void BreweryPayload_ParsesAndSerializesDictionary()
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BreweryAutoCelebrationEnabled] = "false",
        };

        Assert.True(BreweryPayload.TryFromDictionary(payload, out var parsed));
        Assert.NotNull(parsed);
        Assert.False(parsed!.AutoCelebrationEnabled);
        var serialized = parsed.ToDictionary();
        Assert.Single(serialized);
        Assert.Equal("false", serialized[BotOptionPayloadKeys.BreweryAutoCelebrationEnabled]);
    }

    [Fact]
    public void TroopTrainingPayload_ParsesAndSerializesDictionary()
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TroopTrainingBarracksEnabled] = "true",
            [BotOptionPayloadKeys.TroopTrainingBarracksTroopType] = "Clubswinger",
            [BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours] = "2",
            [BotOptionPayloadKeys.TroopTrainingBarracksAmountMode] = "fixed",
            [BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent] = "120",
            [BotOptionPayloadKeys.TroopTrainingBarracksRunMode] = "continuous",
            [BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops] = "10",
            [BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent] = "20",
            [BotOptionPayloadKeys.TroopTrainingBarracksCheckWood] = "true",
            [BotOptionPayloadKeys.TroopTrainingBarracksCheckClay] = "true",
            [BotOptionPayloadKeys.TroopTrainingBarracksCheckIron] = "false",
            [BotOptionPayloadKeys.TroopTrainingBarracksCheckCrop] = "true",
            [BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds] = "60",
        };

        Assert.True(TroopTrainingPayload.TryFromDictionary(payload, out var parsed));
        Assert.NotNull(parsed);
        Assert.True(parsed!.Barracks.Enabled);
        Assert.Equal("Clubswinger", parsed.Barracks.TroopType);
        Assert.Equal(100, parsed.Barracks.KeepResourcesPercent);
        Assert.False(parsed.Barracks.CheckIron);
        Assert.Equal(60, parsed.FallbackCooldownSeconds);
        var serialized = parsed.ToDictionary();
        Assert.Equal(43, serialized.Count);
        Assert.Equal("true", serialized[BotOptionPayloadKeys.TroopTrainingBarracksEnabled]);
        Assert.Equal("Clubswinger", serialized[BotOptionPayloadKeys.TroopTrainingBarracksTroopType]);
        Assert.Equal("100", serialized[BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent]);
        Assert.Equal("60", serialized[BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds]);
    }

    [Fact]
    public void TroopTrainingPayload_RejectsInvalidPayload()
    {
        Assert.False(TroopTrainingPayload.TryFromDictionary(
            new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.TroopTrainingBarracksEnabled] = "maybe",
            },
            out _));
    }

    [Fact]
    public void QueueGroupCatalog_ResolvesResourceTransferTask()
    {
        Assert.Equal(QueueGroup.ResourceTransfer, QueueGroupCatalog.ResolveGroup("send_resources_between_villages"));
        Assert.True(QueueGroupCatalog.TryParse("resource_transfer", out var group));
        Assert.Equal(QueueGroup.ResourceTransfer, group);
    }

    [Fact]
    public void QueueGroupCatalog_ResolvesReinforcementsTask()
    {
        Assert.Equal(QueueGroup.Reinforcements, QueueGroupCatalog.ResolveGroup("send_reinforcements_between_villages"));
        Assert.True(QueueGroupCatalog.TryParse("reinforcements", out var group));
        Assert.Equal(QueueGroup.Reinforcements, group);
    }

    [Fact]
    public void QueueStore_HandlesHundredsOfOperations_WithoutLosingItems()
    {
        var store = new JsonQueueStore(_queuePath);

        for (var i = 0; i < 150; i++)
        {
            store.Add("status", null, priority: i % 3, maxRetries: 3);
        }

        var items = store.GetAll();
        Assert.Equal(150, items.Count);

        foreach (var item in items.Take(100))
        {
            Assert.True(store.MarkRunning(item.Id));
            Assert.True(store.MarkSucceeded(item.Id));
        }

        var succeededCount = store.GetAll().Count(item => item.Status == QueueStatus.Succeeded);
        Assert.Equal(100, succeededCount);
    }

    [Fact]
    public void QueueStore_RemovesStaleTempFileBeforeSave()
    {
        File.WriteAllText($"{_queuePath}.tmp", "stale");
        var store = new JsonQueueStore(_queuePath);

        store.Add("status", null, priority: 1, maxRetries: 3);

        Assert.False(File.Exists($"{_queuePath}.tmp"));
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void BotTaskRunner_RegistersHandlers_ForEveryAllowedTask()
    {
        var allowed = TbotUltra.Worker.Services.TaskCatalog.AllowedTaskNames;
        var registered = BotTaskRunner.RegisteredTaskNames;

        foreach (var task in allowed)
        {
            Assert.Contains(task, registered, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var task in registered)
        {
            Assert.Contains(task, allowed, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Resource slot 1 blocked (BlockedByQueue): workers busy.", true)]
    [InlineData("Building cannot be built yet. Missing requirements.", true)]
    [InlineData("Slot 20 reports max level reached.", true)]
    [InlineData("Slot 20: already at level 3.", false)]
    [InlineData("", false)]
    public void BotTaskRunner_IsBlockedTaskResult_MatchesKnownFormats(string result, bool expected)
    {
        Assert.Equal(expected, BotTaskRunner.IsBlockedTaskResult(result));
    }

    [Theory]
    [InlineData("Construct skipped: Rally Point already exists at slot 39 (confirmed 'Rally Point' level 1). Removing from queue.", ConstructionTaskOutcome.AlreadyExists)]
    [InlineData("Constructed Warehouse in slot 20 (confirmed level 1 on dorf2).", ConstructionTaskOutcome.ConfirmedComplete)]
    [InlineData("Slot 20: already at level 3.", ConstructionTaskOutcome.AlreadySatisfied)]
    [InlineData("Queued Marketplace in slot 21. Evidence: ...", ConstructionTaskOutcome.QueuedOrInProgress)]
    public void BotTaskRunner_ClassifyConstructionTaskResult_MapsKnownResults(string result, ConstructionTaskOutcome expected)
    {
        Assert.Equal(expected, BotTaskRunner.ClassifyConstructionTaskResult("construct_building", result));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
