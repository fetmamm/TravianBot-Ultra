using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services.Orchestration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private sealed class MainWindowVillageStatusRoundPort(MainWindow owner)
        : IContinuousVillageStatusRoundPort
    {
        private BotOptions _options = new();
        private IReadOnlyDictionary<string, VillageSelectionItem> _villagesByKey =
            new Dictionary<string, VillageSelectionItem>();

        public string? ActiveAccountName => owner._accountStore.ActiveAccountName();
        public string? ActiveVillageKey => owner._activeWorkingVillageKey;
        public DateTimeOffset GetNextRoundUtc() => owner.GetVillageStatusSweepNextScanUtc();
        public ValueTask<bool> EnsureVillageMembershipVerifiedAsync(
            BotOptions options,
            CancellationToken cancellationToken) =>
            new(owner.EnsureVillageMembershipVerifiedBeforeAutomationAsync(options, cancellationToken));
        public async ValueTask<IReadOnlyList<VillageStatusRoundVillage>> LoadVillagesAsync(
            BotOptions options)
        {
            _options = options;
            var villages = await owner.Dispatcher.InvokeAsync(() =>
            {
                var source = (owner.DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
                    ?? (owner.VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
                    ?? Enumerable.Empty<VillageSelectionItem>();
                return source
                    .Where(village => !string.IsNullOrWhiteSpace(village.Name)
                        && !string.Equals(village.Name, "-", StringComparison.Ordinal))
                    .GroupBy(GetVillageKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            });
            _villagesByKey = villages.ToDictionary(GetVillageKey, StringComparer.OrdinalIgnoreCase);
            return villages
                .Select(village => new VillageStatusRoundVillage(
                    GetVillageKey(village),
                    village.Name,
                    village.Url))
                .ToList();
        }
        public IDisposable BeginRoundActivity(int villageCount) =>
            owner._dashboardActivityTracker.Begin(
                owner._automationDesk.LoginVillageStatusRoundPending
                    ? $"Village round (0/{villageCount})"
                    : $"Village scan (0/{villageCount})");
        public VillageStatusRoundScheduleResult ScheduleNext(
            string? expectedAccountName,
            int minMinutes,
            int maxMinutes) => owner._automationDesk.ScheduleNextVillageStatusRound(
                expectedAccountName,
                owner._accountStore.ActiveAccountName(),
                minMinutes,
                maxMinutes);

        public async ValueTask PrepareAsync(CancellationToken cancellationToken)
        {
            try
            {
                await owner._automationDesk.PrepareRuntimeItemsAsync(
                    _options,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                owner.AppendLog(
                    "[village-scan] runtime preparation failed; existing queued work will still run: "
                    + FormatExceptionForLog(ex));
            }

            owner.AppendLog($"[village-scan] starting round for {_villagesByKey.Count} village(s).");
        }

        public ValueTask<VillageStatusRoundVisitResult> VisitAsync(
            VillageStatusRoundVillage village,
            int villageNumber,
            int totalVillageCount,
            bool inboxStatusChecked,
            CancellationToken cancellationToken) =>
            owner.VisitVillageStatusRoundAsync(
                _options,
                _villagesByKey[village.Key],
                villageNumber,
                totalVillageCount,
                inboxStatusChecked,
                cancellationToken);

        public ValueTask DelayBeforeNextVillageAsync(CancellationToken cancellationToken) =>
            new(ActionPacer.FromOptions(_options, owner.AppendLog).DelayAsync(
                _options.VillageStatusSweepVillageMinSeconds,
                _options.VillageStatusSweepVillageMaxSeconds,
                cancellationToken,
                "Village scan: next village"));

        public void Log(string message) => owner.AppendLog(message);
    }
}
