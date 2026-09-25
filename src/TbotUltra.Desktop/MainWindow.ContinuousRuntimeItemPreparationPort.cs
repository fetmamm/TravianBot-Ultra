using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowContinuousRuntimeItemPreparationPort(
        MainWindow owner,
        AutomationQueueEligibility queueEligibility)
        : IContinuousRuntimeItemPreparationPort
    {
        private readonly MainWindowAutomationRuntimeQueuePort _runtimeQueue = new(owner);

        public IAutomationRuntimeQueuePort RuntimeQueue => _runtimeQueue;
        public bool IsBreweryCelebrationAvailable =>
            owner._troopTrainingViewModel.IsAutoCelebrationAvailableForCurrentTribe;
        public bool FarmingBlockedForOtherReason => owner.IsFarmingGroupBlocked()
            && !string.Equals(
                owner._farmingBlockedReasonKey,
                FarmingBlockedReasonNoGoldClub,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                owner._farmingBlockedReasonKey,
                FarmingBlockedReasonNoFarmLists,
                StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<QueueGroup> GetEnabledGroups() =>
            owner.GetContinuousLoopEnabledGroupsInOrder();
        public IReadOnlyList<QueueGroup> GetConsideredGroups() =>
            owner.GetContinuousLoopConsideredGroupsInOrder();
        public IReadOnlySet<QueueGroup> GetEnabledGroupsForVillage(string villageKey) =>
            QueueGroupCatalog.AllGroups
                .Where(group => queueEligibility.IsGroupEnabled(villageKey, group))
                .ToHashSet();
        public bool ShouldKeepHeroAdventurePolling() => owner.ShouldKeepHeroAdventurePolling();
        public IReadOnlyList<QueueItem> GetQueueItems() => owner._botService.GetQueueItemsForDisplay();
        public string? CanonicalizeVillageKey(string? key) =>
            owner._villageSettingsStore.ResolveCanonicalKey(key);
        public IReadOnlyList<AutomationRuntimeVillage> GetAutomationVillages(AutomationRuntimeVillage? onlyVillage)
        {
            if (onlyVillage is not null)
            {
                return owner._villageSettingsStore.IsEnabledByKey(onlyVillage.Key, defaultIfUnknown: false)
                    ? [onlyVillage]
                    : [];
            }

            return owner.GetEnabledAutomationVillages()
                .Select(village => new AutomationRuntimeVillage(
                    GetVillageKey(village),
                    village.Name,
                    village.Url,
                    village.IsCapital,
                    village.CoordX,
                    village.CoordY))
                .ToList();
        }

        public void PrepareHeroCropAntiStarve(BotOptions options)
        {
            owner.RemoveDisabledHeroCropAntiStarveTasks(options);
            owner.SeedHeroCropAntiStarveObservations(options);
            owner.ActivateDueHeroCropAntiStarveObservations(options);
        }

        public async ValueTask<int?> RefreshAdventureCountAsync(
            BotOptions options,
            CancellationToken cancellationToken)
        {
            using var activity = owner._dashboardActivityTracker.Begin("Checking Hero adventures");
            return await owner._botService.RefreshAdventureCountAsync(
                options,
                owner.AppendLog,
                cancellationToken);
        }

        public ValueTask ApplyHeroAdventureAvailabilityAsync(int? adventureCount) =>
            new(owner.Dispatcher.InvokeAsync(() => owner.ApplyHeroAdventureAvailability(adventureCount)).Task);

        public Dictionary<string, string> BuildHeroPayload() => owner.BuildHeroRuntimePayload();
        public bool IsTroopsGroupBlocked() => owner.IsTroopsGroupBlocked();
        public IReadOnlyDictionary<string, string> LoadSmithyPayload(string villageKey)
        {
            var targets = SmithyUpgradeTargetsStore.Load(
                owner._projectRoot,
                owner._accountStore.ActiveAccountName(),
                villageKey);
            return new SmithyUpgradePayload(
                    targets.Select(target => new SmithyTroopTarget(
                        target.Key,
                        target.TargetLevel,
                        target.Name)).ToList())
                .ToDictionary();
        }
        public Dictionary<string, string> BuildVillagePayload(AutomationRuntimeVillage village) =>
            owner.BuildVillageRuntimePayload(new Models.VillageSelectionItem
            {
                Name = village.Name,
                Url = village.Url ?? string.Empty,
                IsCapital = village.IsCapital,
                CoordX = village.CoordX,
                CoordY = village.CoordY,
            });
        public Dictionary<string, string>? LoadTroopTrainingPayload(string villageKey) =>
            TroopTrainingSettingsStore.Load(
                owner._projectRoot,
                owner._accountStore.ActiveAccountName(),
                villageKey)?.ToDictionary();
        public bool HasEnabledTroopTrainingBuilding(BotOptions options) =>
            MainWindow.HasEnabledTroopTrainingBuilding(options);
        public bool ShouldGateTroopTrainingOnActiveQueue(BotOptions options) =>
            MainWindow.ShouldGateTroopTrainingEnqueueOnActiveQueue(options);
        public int? ResolveActiveTroopTrainingQueueWaitSeconds(
            AutomationRuntimeVillage village,
            BotOptions options) => owner.ResolveActiveTroopTrainingQueueWaitSeconds(
                new Models.VillageSelectionItem
                {
                    Name = village.Name,
                    Url = village.Url ?? string.Empty,
                    IsCapital = village.IsCapital,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                },
                options);
        public string? LoadTownHallMode(string villageKey) => TownHallSettingsStore.LoadMode(
            owner._projectRoot,
            owner._accountStore.ActiveAccountName(),
            villageKey);
        public TownHallCelebrationState? LoadActiveTownHallState(
            string villageKey,
            DateTimeOffset now) => TownHallCelebrationStateStore.LoadActive(
                owner._projectRoot,
                owner._accountStore.ActiveAccountName(),
                villageKey,
                now);
        public bool DeferPendingItem(
            Guid itemId,
            Dictionary<string, string>? payload,
            TimeSpan delay) => owner._botService.UpdateDeferredQueueItem(itemId, payload, delay);
        public ValueTask<bool> ResolveGoldClubStatusAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.ResolveContinuousGoldClubStatusAsync(options, cancellationToken));
        public void UpdateGoldClubInfo(bool enabled) => owner.UpdateGoldClubInfo(enabled);
        public ValueTask EnsureFarmListsReadyAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.EnsureContinuousFarmListsReadyAsync(options, cancellationToken));
        public AutomationFarmListSelection GetFarmListSelection() => owner.RunOnUi(() =>
        {
            var enabled = owner._farmLists
                .Where(item => IsRealFarmListRow(item) && item.IsEnabled)
                .ToList();
            return new AutomationFarmListSelection(
                enabled
                    .Select(item => item.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                enabled
                    .Select(item => item.ListId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                owner._farmLists.Count(IsRealFarmListRow));
        });
        public void SetFarmingBlockedForMissingLists() => owner.SetFarmingBlockedState(
            FarmingBlockedReasonNoFarmLists,
            "No farmlists available");
        public void ClearFarmingMissingListsBlock()
        {
            if (string.Equals(
                owner._farmingBlockedReasonKey,
                FarmingBlockedReasonNoFarmLists,
                StringComparison.OrdinalIgnoreCase))
            {
                owner.ClearFarmingBlockedState();
            }
        }
        public void SetFarmingBlockedForMissingGoldClub() => owner.SetFarmingBlockedState(
            FarmingBlockedReasonNoGoldClub,
            "No goldclub");
        public bool CanRunResourceTransfer(BotOptions options) =>
            owner.CanRunResourceTransfer(options, out _);
        public bool CanRunReinforcements(BotOptions options) =>
            owner.CanRunReinforcements(options, out _);
        public Dictionary<string, string> BuildReinforcementPayload(
            BotOptions options,
            IReadOnlyList<string> selectedSources) =>
            owner.BuildAutomaticReinforcementPayload(options, selectedSources);
        public bool ScheduleReinforcementSend(
            Dictionary<string, string> payload,
            TimeSpan delay,
            BotOptions options) => owner.ScheduleAutomaticReinforcementSend(payload, delay, options);
        public string FormatServerTime(DateTimeOffset value) => owner.FormatQueueServerTime(value);
        public string FormatDuration(int seconds) => FormatSmithyDuration(seconds);
        public void Log(string message) => owner.AppendLog(message);
        public void LogVerbose(string message, string key) => owner.AppendLoopPickVerbose(message, key);
    }
}
