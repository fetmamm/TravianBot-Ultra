using TbotUltra.Core.Configuration;
using TbotUltra.Core.Farming;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ContinuousRuntimeItemPreparationTests
{
    [Fact]
    public async Task EnabledTroopTrainingVillage_GeneratesOneTargetedRuntimeItem()
    {
        var port = new InMemoryPort
        {
            EnabledGroups = [QueueGroup.TroopTraining],
            ConsideredGroups = [QueueGroup.TroopTraining],
            Villages = [new AutomationRuntimeVillage("1:2", "Alpha", "/dorf1.php?newdid=1", false, 1, 2)],
        };

        await new ContinuousRuntimeItemPreparation(port).PrepareAsync(
            new BotOptions { TroopTrainingBarracksEnabled = true },
            default);

        var item = Assert.Single(port.Queue.Items);
        Assert.Equal("build_troops", item.TaskName);
        Assert.Equal("1:2", item.Payload[BotOptionPayloadKeys.TargetVillageKey]);
    }

    [Fact]
    public async Task VillageScopedPreparation_DoesNotGenerateAccountGlobalTransferItem()
    {
        var port = new InMemoryPort
        {
            EnabledGroups = [QueueGroup.ResourceTransfer],
            ConsideredGroups = [QueueGroup.ResourceTransfer],
            Villages = [new AutomationRuntimeVillage("1:2", "Alpha", null, false, 1, 2)],
            ResourceTransferReady = true,
        };

        await new ContinuousRuntimeItemPreparation(port).PrepareAsync(
            new BotOptions(),
            default,
            onlyVillage: port.Villages[0]);

        Assert.Empty(port.Queue.Items);
    }

    [Fact]
    public async Task HeroAdventureObservation_GeneratesHeroRuntimeItem()
    {
        var port = new InMemoryPort
        {
            EnabledGroups = [QueueGroup.Hero],
            ConsideredGroups = [QueueGroup.Hero],
            AdventureCount = 2,
        };

        await new ContinuousRuntimeItemPreparation(port).PrepareAsync(new BotOptions(), default);

        Assert.Equal("hero_manage", Assert.Single(port.Queue.Items).TaskName);
        Assert.Equal(2, port.AppliedAdventureCount);
    }

    [Fact]
    public async Task FarmingWithoutGoldClub_RecordsBlockWithoutQueueingWork()
    {
        var port = new InMemoryPort
        {
            EnabledGroups = [QueueGroup.Farming],
            ConsideredGroups = [QueueGroup.Farming],
            GoldClubEnabled = false,
        };

        await new ContinuousRuntimeItemPreparation(port).PrepareAsync(new BotOptions(), default);

        Assert.True(port.MissingGoldClubBlocked);
        Assert.Empty(port.Queue.Items);
    }

    [Fact]
    public async Task SendAllFarming_QueuesOneAccountWideDispatchInsteadOfOnePerVillage()
    {
        var port = new InMemoryPort
        {
            EnabledGroups = [QueueGroup.Farming],
            ConsideredGroups = [QueueGroup.Farming],
            Villages =
            [
                new AutomationRuntimeVillage("1:2", "Alpha", "/dorf1.php?newdid=1", false, 1, 2),
                new AutomationRuntimeVillage("3:4", "Beta", "/dorf1.php?newdid=2", false, 3, 4),
                new AutomationRuntimeVillage("5:6", "Gamma", "/dorf1.php?newdid=3", false, 5, 6),
            ],
            FarmListSelection = new AutomationFarmListSelection([], [], 4),
        };

        await new ContinuousRuntimeItemPreparation(port).PrepareAsync(
            new BotOptions { ContinuousFarmSendMode = FarmingDefaults.SendModeAllAtOnce },
            default);
        await new ContinuousRuntimeItemPreparation(port).PrepareAsync(
            new BotOptions { ContinuousFarmSendMode = FarmingDefaults.SendModeAllAtOnce },
            default);

        var item = Assert.Single(port.Queue.Items);
        Assert.Equal("send_farmlists", item.TaskName);
        Assert.Equal("Send all farmlists", item.DisplayName);
    }

    [Fact]
    public async Task ExistingScheduledFarmingDispatch_DoesNotAnalyzeFarmlistsAgain()
    {
        var port = new InMemoryPort
        {
            EnabledGroups = [QueueGroup.Farming],
            ConsideredGroups = [QueueGroup.Farming],
            Villages = [new AutomationRuntimeVillage("1:2", "Alpha", "/dorf1.php?newdid=1", false, 1, 2)],
            FarmListSelection = new AutomationFarmListSelection(["List A"], ["7"], 1),
        };
        port.Queue.Items.Add(new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "send_farmlists",
            DisplayName = "Send selected farmlists",
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.TargetVillageKey] = "1:2",
            },
            Status = QueueStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(20),
        });

        await new ContinuousRuntimeItemPreparation(port).PrepareAsync(new BotOptions(), default);

        Assert.Equal(0, port.EnsureFarmListsReadyCalls);
        Assert.Single(port.Queue.Items);
    }

    [Fact]
    public async Task MissingVillageFarmingDispatch_ReusesKnownSelectionWithoutAnalysis()
    {
        var port = new InMemoryPort
        {
            EnabledGroups = [QueueGroup.Farming],
            ConsideredGroups = [QueueGroup.Farming],
            Villages =
            [
                new AutomationRuntimeVillage("1:2", "Alpha", "/dorf1.php?newdid=1", false, 1, 2),
                new AutomationRuntimeVillage("3:4", "Beta", "/dorf1.php?newdid=2", false, 3, 4),
            ],
            FarmListSelection = new AutomationFarmListSelection(["List A"], ["7"], 1),
        };
        port.Queue.Items.Add(new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "send_farmlists",
            DisplayName = "Send selected farmlists",
            Payload = new Dictionary<string, string>
            {
                [BotOptionPayloadKeys.TargetVillageKey] = "1:2",
            },
            Status = QueueStatus.Pending,
        });

        await new ContinuousRuntimeItemPreparation(port).PrepareAsync(new BotOptions(), default);

        Assert.Equal(0, port.EnsureFarmListsReadyCalls);
        Assert.Equal(2, port.Queue.Items.Count);
        Assert.Contains(port.Queue.Items, item =>
            item.Payload.GetValueOrDefault(BotOptionPayloadKeys.TargetVillageKey) == "3:4");
    }

    [Fact]
    public async Task MissingFarmingSelection_AnalyzesBeforeCreatingDispatch()
    {
        var port = new InMemoryPort
        {
            EnabledGroups = [QueueGroup.Farming],
            ConsideredGroups = [QueueGroup.Farming],
            Villages = [new AutomationRuntimeVillage("1:2", "Alpha", "/dorf1.php?newdid=1", false, 1, 2)],
        };

        await new ContinuousRuntimeItemPreparation(port).PrepareAsync(new BotOptions(), default);

        Assert.Equal(1, port.EnsureFarmListsReadyCalls);
        Assert.Empty(port.Queue.Items);
    }

    private sealed class InMemoryPort : IContinuousRuntimeItemPreparationPort
    {
        public IReadOnlyList<QueueGroup> EnabledGroups { get; init; } = [];
        public IReadOnlyList<QueueGroup> ConsideredGroups { get; init; } = [];
        public IReadOnlyList<AutomationRuntimeVillage> Villages { get; init; } = [];
        public int? AdventureCount { get; init; }
        public int? AppliedAdventureCount { get; private set; }
        public bool GoldClubEnabled { get; init; } = true;
        public bool MissingGoldClubBlocked { get; private set; }
        public bool ResourceTransferReady { get; init; }
        public int EnsureFarmListsReadyCalls { get; private set; }
        public AutomationFarmListSelection FarmListSelection { get; init; } = new([], [], 0);
        public InMemoryQueuePort Queue { get; } = new();
        public IAutomationRuntimeQueuePort RuntimeQueue => Queue;
        public bool IsBreweryCelebrationAvailable => false;
        public bool FarmingBlockedForOtherReason => false;
        public IReadOnlyList<QueueGroup> GetEnabledGroups() => EnabledGroups;
        public IReadOnlyList<QueueGroup> GetConsideredGroups() => ConsideredGroups;
        public IReadOnlySet<QueueGroup> GetEnabledGroupsForVillage(string villageKey) =>
            EnabledGroups.ToHashSet();
        public bool ShouldKeepHeroAdventurePolling() => false;
        public IReadOnlyList<QueueItem> GetQueueItems() => Queue.Items;
        public string? CanonicalizeVillageKey(string? key) => key;
        public IReadOnlyList<AutomationRuntimeVillage> GetAutomationVillages(AutomationRuntimeVillage? onlyVillage) =>
            onlyVillage is null ? Villages : [onlyVillage];
        public void PrepareHeroCropAntiStarve(BotOptions options) { }
        public ValueTask<int?> RefreshAdventureCountAsync(BotOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AdventureCount);
        public ValueTask ApplyHeroAdventureAvailabilityAsync(int? adventureCount)
        {
            AppliedAdventureCount = adventureCount;
            return ValueTask.CompletedTask;
        }
        public Dictionary<string, string> BuildHeroPayload() => new()
        {
            [BotOptionPayloadKeys.HeroStatPriority] = "offense",
        };
        public bool IsTroopsGroupBlocked() => false;
        public IReadOnlyDictionary<string, string> LoadSmithyPayload(string villageKey) =>
            new Dictionary<string, string>();
        public Dictionary<string, string> BuildVillagePayload(AutomationRuntimeVillage village) => new()
        {
            [BotOptionPayloadKeys.TargetVillageName] = village.Name,
            [BotOptionPayloadKeys.TargetVillageKey] = village.Key,
        };
        public Dictionary<string, string>? LoadTroopTrainingPayload(string villageKey) => null;
        public bool HasEnabledTroopTrainingBuilding(BotOptions options) =>
            options.TroopTrainingBarracksEnabled;
        public bool ShouldGateTroopTrainingOnActiveQueue(BotOptions options) => false;
        public int? ResolveActiveTroopTrainingQueueWaitSeconds(
            AutomationRuntimeVillage village,
            BotOptions options) => null;
        public string? LoadTownHallMode(string villageKey) => null;
        public TownHallCelebrationState? LoadActiveTownHallState(string villageKey, DateTimeOffset now) => null;
        public bool DeferPendingItem(Guid itemId, Dictionary<string, string>? payload, TimeSpan delay) => true;
        public ValueTask<bool> ResolveGoldClubStatusAsync(
            BotOptions options,
            CancellationToken cancellationToken) => ValueTask.FromResult(GoldClubEnabled);
        public void UpdateGoldClubInfo(bool enabled) { }
        public ValueTask EnsureFarmListsReadyAsync(
            BotOptions options,
            CancellationToken cancellationToken)
        {
            EnsureFarmListsReadyCalls++;
            return ValueTask.CompletedTask;
        }
        public AutomationFarmListSelection GetFarmListSelection() => FarmListSelection;
        public void SetFarmingBlockedForMissingLists() { }
        public void ClearFarmingMissingListsBlock() { }
        public void SetFarmingBlockedForMissingGoldClub() => MissingGoldClubBlocked = true;
        public bool CanRunResourceTransfer(BotOptions options) => ResourceTransferReady;
        public bool CanRunReinforcements(BotOptions options) => false;
        public Dictionary<string, string> BuildReinforcementPayload(
            BotOptions options,
            IReadOnlyList<string> selectedSources) => [];
        public bool ScheduleReinforcementSend(
            Dictionary<string, string> payload,
            TimeSpan delay,
            BotOptions options) => true;
        public string FormatServerTime(DateTimeOffset value) => value.ToString("O");
        public string FormatDuration(int seconds) => $"{seconds}s";
        public void Log(string message) { }
        public void LogVerbose(string message, string key) { }
    }

    private sealed class InMemoryQueuePort : IAutomationRuntimeQueuePort
    {
        public List<QueueItem> Items { get; } = [];
        public QueueItem Enqueue(AutomationRuntimeItemSpec spec)
        {
            var item = new QueueItem
            {
                Id = Guid.NewGuid(),
                TaskName = spec.TaskName,
                DisplayName = spec.DisplayName,
                Payload = spec.Payload ?? [],
                Priority = spec.Priority,
                MaxRetries = spec.MaxRetries,
                Status = QueueStatus.Pending,
            };
            Items.Add(item);
            return item;
        }
        public bool UpdatePendingPayload(Guid id, Dictionary<string, string> payload)
        {
            var item = Items.Single(candidate => candidate.Id == id);
            item.Payload = payload;
            return true;
        }
        public bool UpdatePendingPriority(Guid id, int priority)
        {
            Items.Single(candidate => candidate.Id == id).Priority = priority;
            return true;
        }
    }
}
