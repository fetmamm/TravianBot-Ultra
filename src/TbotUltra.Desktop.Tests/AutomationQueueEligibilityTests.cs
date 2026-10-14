using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using Xunit;
using Info = TbotUltra.Desktop.Services.VillageSettingsStore.VillageKeyInfo;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationQueueEligibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "tbot-ultra-queue-eligibility-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void IsAllowed_AccountTaskIsNotGatedByVillageSettings()
    {
        var (eligibility, port, _, village) = CreateEligibility();
        port.VillageKey = village.Key;

        Assert.True(eligibility.IsAllowed(new QueueItem { Group = QueueGroup.Account }));
    }

    [Fact]
    public void IsAllowed_AccountWideFarmListDoesNotRequireVillageIdentity()
    {
        var (eligibility, _, store, village) = CreateEligibility();
        store.SetEnabledGroups(village, ["farming"]);

        Assert.True(eligibility.IsAllowed(new QueueItem
        {
            TaskName = "send_farmlists",
            Group = QueueGroup.Farming,
        }));
    }

    [Fact]
    public void IsAllowed_AccountWideFarmListRunsWhenAnotherVillageHasFarmingEnabled()
    {
        var (eligibility, port, store, originalVillage) = CreateEligibility();
        var enabledVillage = new Info(VillageKey.FromCoords(3, 4), "Other", 3, 4, IsCapital: false);
        store.Merge([originalVillage, enabledVillage]);
        store.SetEnabledGroups(originalVillage, ["hero"]);
        store.SetEnabledGroups(enabledVillage, ["farming"]);
        port.VillageKey = originalVillage.Key;
        var now = DateTimeOffset.UtcNow;
        var dispatch = new QueueItem
        {
            Id = Guid.NewGuid(),
            TaskName = "send_farmlists",
            Group = QueueGroup.Farming,
            Status = QueueStatus.Pending,
            NextAttemptAt = now.AddSeconds(-1),
        };

        Assert.True(eligibility.IsAllowed(dispatch));
        var selection = AutomationQueueSelector.Select(
            new AutomationQueueSelectionInput(
                [new ContinuousLoopSelectionCandidate(
                    dispatch, originalVillage.Key, eligibility.IsAllowed(dispatch), IsUtilityEnabled: false)],
                [QueueGroup.Farming],
                new VillageBatchSnapshot(enabledVillage.Key, 0),
                ActiveVillageKey: enabledVillage.Key,
                Now: now,
                ShortVillageDeferSeconds: 60,
                Preview: false),
            (_, _, _) => null);
        Assert.Equal(dispatch.Id, selection.Selected?.Id);

        store.SetEnabledGroups(enabledVillage, ["hero"]);
        Assert.False(eligibility.IsAllowed(dispatch));
    }

    [Fact]
    public void IsAllowed_HeroRaidDependsOnlyOnGlobalToggle()
    {
        var (_, port, store, village) = CreateEligibility();
        port.VillageKey = village.Key;
        store.SetEnabled(village, enabled: false);
        store.SetEnabledGroups(village, []);
        var raid = new QueueItem { TaskName = "hero_raid", Group = QueueGroup.Hero };

        Assert.True(new AutomationQueueEligibility(store, () => false, port, () => true).IsAllowed(raid));
        Assert.False(new AutomationQueueEligibility(store, () => true, port, () => false).IsAllowed(raid));
    }

    [Fact]
    public void IsAllowed_RequiresEnabledVillageAndGroup()
    {
        var (eligibility, port, store, village) = CreateEligibility();
        port.VillageKey = village.Key;
        store.SetEnabledGroups(village, ["hero"]);

        Assert.True(eligibility.IsAllowed(new QueueItem { Group = QueueGroup.Hero }));

        store.SetEnabled(village, enabled: false);
        Assert.False(eligibility.IsAllowed(new QueueItem { Group = QueueGroup.Hero }));
    }

    [Fact]
    public void IsAllowed_QueuedDemolitionDoesNotRequireGroupToggle()
    {
        var (eligibility, port, store, village) = CreateEligibility();
        port.VillageKey = village.Key;
        store.SetEnabledGroups(village, ["hero"]);

        Assert.True(eligibility.IsAllowed(new QueueItem
        {
            TaskName = "demolish_building_to_level",
            Group = QueueGroup.Demolish,
        }));
    }

    [Fact]
    public void IsAllowed_RallyPointRepairUsesHeroGroup()
    {
        var (eligibility, port, store, village) = CreateEligibility();
        port.VillageKey = village.Key;
        store.SetEnabledGroups(village, ["hero"]);
        var item = new QueueItem { Group = QueueGroup.Construction };
        item.Payload[BotOptionPayloadKeys.AutoAddedBy] = BotOptionPayloadKeys.AutoAddedByHeroRallyPointRepair;

        Assert.True(eligibility.IsAllowed(item));
    }

    [Fact]
    public void IsGroupEnabled_FarmingRequiresGoldClub()
    {
        var (eligibility, _, store, village) = CreateEligibility(goldClubAvailable: false);
        store.SetEnabledGroups(village, ["farming"]);

        Assert.False(eligibility.IsGroupEnabled(village.Key, QueueGroup.Farming));
        Assert.False(eligibility.IsAllowed(new QueueItem
        {
            TaskName = "send_farmlists",
            Group = QueueGroup.Farming,
        }));
    }

    private (AutomationQueueEligibility Eligibility, InMemoryPort Port, VillageSettingsStore Store, Info Village)
        CreateEligibility(bool? goldClubAvailable = true)
    {
        Directory.CreateDirectory(_root);
        var store = new VillageSettingsStore(_root, () => "alice");
        var village = new Info(VillageKey.FromCoords(0, 0), "Capital", 0, 0, IsCapital: true);
        store.Merge([village]);
        var port = new InMemoryPort();
        return (new AutomationQueueEligibility(store, () => goldClubAvailable, port), port, store, village);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class InMemoryPort : IAutomationQueueEligibilityPort
    {
        internal string? VillageKey { get; set; }
        internal string? VillageName { get; set; }
        public string? GetVillageKey(QueueItem item) => VillageKey;
        public string? GetVillageName(QueueItem item) => VillageName;
    }
}
