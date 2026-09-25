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
    public void IsAllowed_RejectsFarmListWithoutVillageIdentity()
    {
        var (eligibility, _, _, _) = CreateEligibility();

        Assert.False(eligibility.IsAllowed(new QueueItem
        {
            TaskName = "send_farmlists",
            Group = QueueGroup.Farming,
        }));
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
