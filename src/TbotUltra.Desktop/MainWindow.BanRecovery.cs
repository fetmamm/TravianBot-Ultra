using System.Diagnostics;
using System.Text;
using System.Windows;
using TbotUltra.Core.Tasks;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async Task<bool> TryRunPendingBanRecoveryAsync()
    {
        var accountName = _accountStore.ActiveAccountName();
        var recovery = string.IsNullOrWhiteSpace(accountName) ? null : _banRecoveryStore.Load(accountName);
        if (recovery is null || recovery.Stage == BanRecoveryStage.Banned)
        {
            return false;
        }

        var operationId = BeginOperation("BanRecoveryScan");
        var stopwatch = Stopwatch.StartNew();
        var token = _loopController.StartOperation("ban-recovery-scan");
        ToggleUiBusy(true);
        ShowBusyOverlay("Ban recovery", "Reading the current village list...");
        try
        {
            var options = LoadBotOptions() with
            {
                VillageStatusSweepDorf1Enabled = true,
                VillageStatusSweepDorf2Enabled = true,
                VillageStatusSweepSmithyEnabled = false,
                VillageStatusSweepBarracksEnabled = false,
                VillageStatusSweepStableEnabled = false,
                VillageStatusSweepWorkshopEnabled = false,
                VillageStatusSweepTownHallEnabled = false,
                VillageStatusSweepBreweryEnabled = false,
            };
            var accountSnapshot = await _botService.ReadAccountSnapshotForScanAsync(options, AppendLog, token);
            var villages = accountSnapshot.Villages
                .Where(village => !string.IsNullOrWhiteSpace(village.Name))
                .GroupBy(village => GetVillageKey(village.Url, village.CoordX, village.CoordY, village.Name), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (villages.Count == 0) throw new InvalidOperationException("No villages were found for the recovery scan.");

            var current = new Dictionary<string, VillageStatus>(StringComparer.OrdinalIgnoreCase);
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < villages.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var village = villages[index];
                var key = GetVillageKey(village.Url, village.CoordX, village.CoordY, village.Name);
                BusyOverlay.Text = $"Recovery scan {index + 1}/{villages.Count}: {village.Name}";
                try
                {
                    // This reader only navigates and reads dorf1/dorf2. Unlike the normal Village scan,
                    // it does not collect rewards, generate runtime work, reconcile, or execute tasks.
                    var status = await ReadAccountScanVillageWithRetryAsync(
                        options, village, token, requireCompleteStructure: true);
                    current[key] = status;
                    AppendLog($"[ban-recovery] read '{village.Name}': fields={status.ResourceFields.Count}, buildings={status.Buildings.Count}.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AccountAccessException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed.Add(key);
                    AppendLog($"[ban-recovery] scan failed for '{village.Name}' after retries: {ex.Message}");
                }
            }

            // Publish the verified observations only after every navigation attempt has finished. The
            // read-only flag persists/UI-refreshes structure but cannot arm construction fill or wake waits.
            foreach (var pair in current)
            {
                CacheVillageStatus(pair.Value, pair.Value.ActiveVillage,
                    triggerDeferredWaitRefresh: false, readOnlyObservation: true);
            }

            _banRecoveryStore.SetStage(accountName, BanRecoveryStage.DecisionPending);
            var plan = BanRecoveryPlanner.Plan(
                recovery.Baseline,
                current,
                failed,
                _botService.GetQueueItemsForDisplay());
            foreach (var issue in plan.Issues)
            {
                AppendLog($"[ban-recovery] skipped village='{issue.VillageName}' key='{issue.VillageKey}': {issue.Message}");
            }
            var result = ShowBanRecoveryDecision(recovery, plan);
            if (result == MessageBoxResult.Yes)
            {
                if (!plan.HasWork)
                {
                    _banRecoveryStore.Clear(accountName);
                    AppDialog.Show(this, "No verified building or resource-field levels need rebuilding. The existing construction queue was kept.",
                        "Ban recovery", MessageBoxButton.OK, MessageBoxImage.Information);
                    CompleteOperation(operationId, stopwatch, "No verified reconstruction work was required.");
                    return true;
                }

                if (!ConfirmConstructionSettingsForRecovery(plan, current))
                {
                    StatusTextBlock.Text = "Recovery queue creation canceled. Automation remains stopped.";
                    CompleteOperation(operationId, stopwatch, "Queue creation canceled; recovery snapshot retained.");
                    return true;
                }

                var created = _botService.ReplaceActiveQueueGroup(QueueGroup.Construction, plan.Requests);
                _banRecoveryStore.Clear(accountName);
                RefreshQueueUi();
                StatusTextBlock.Text = "Recovery queue created. Click Start bot to begin rebuilding.";
                AppDialog.Show(this,
                    $"Recovery queue created with {created.Count} task(s).\n\nClick Start bot to begin rebuilding.",
                    "Recovery queue ready", MessageBoxButton.OK, MessageBoxImage.Information);
                CompleteOperation(operationId, stopwatch, $"Created {created.Count} recovery task(s); bot remains stopped.");
                return true;
            }

            _banRecoveryStore.Clear(accountName);
            if (result == MessageBoxResult.No)
            {
                CompleteOperation(operationId, stopwatch, "User continued without rebuilding.");
                StatusTextBlock.Text = "Ban recovery skipped; starting normal automation.";
                StartContinuousLoopRunner();
                return true;
            }

            CompleteOperation(operationId, stopwatch, "User kept automation stopped and discarded recovery data.");
            StatusTextBlock.Text = "Ban recovery closed. Automation remains stopped; the next Start bot runs normally.";
            return true;
        }
        catch (AccountAccessException ex)
        {
            await HoldAccountAutomationAsync(ex);
            FailOperation(operationId, stopwatch, ex);
            return true;
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{operationId}] INFO ban recovery scan canceled; recovery state retained.");
            StatusTextBlock.Text = "Ban recovery scan canceled. Automation remains stopped.";
            return true;
        }
        catch (Exception ex)
        {
            FailOperation(operationId, stopwatch, ex);
            StatusTextBlock.Text = "Ban recovery failed. Automation remains stopped; recovery data was retained.";
            AppDialog.Show(this, $"The recovery scan could not finish. No queue was changed.\n\n{ex.Message}",
                "Ban recovery failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
        finally
        {
            HideBusyOverlay();
            ToggleUiBusy(false);
            DisposeOperationCts();
        }
    }

    private MessageBoxResult ShowBanRecoveryDecision(BanRecoveryState recovery, BanRecoveryPlan plan)
    {
        var text = new StringBuilder();
        var snapshotTime = recovery.SourceSnapshotAtUtc ?? recovery.CapturedAtUtc;
        var timeKind = recovery.SourceSnapshotAtUtc.HasValue ? "last village scan" : "ban detection (legacy cache age unknown)";
        text.AppendLine($"Pre-ban snapshot: {snapshotTime.ToLocalTime():yyyy-MM-dd HH:mm} ({timeKind})");
        text.AppendLine($"Affected villages: {plan.AffectedVillageKeys.Count}");
        if (plan.AffectedVillageKeys.Count > 0)
        {
            var affectedNames = plan.AffectedVillageKeys
                .Select(key => recovery.Baseline.TryGetValue(key, out var status) ? status.ActiveVillage : key)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            text.AppendLine($"Villages to rebuild: {string.Join(", ", affectedNames.Take(10))}"
                + (affectedNames.Count > 10 ? $" (+{affectedNames.Count - 10} more)" : string.Empty));
        }
        text.AppendLine($"Lost levels found: {plan.LostLevels}");
        text.AppendLine($"Recovery tasks: {plan.Requests.Count}");
        text.AppendLine($"Existing construction tasks to replace: {plan.ExistingConstructionItemsToReplace}");
        if (plan.Issues.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Skipped or incomplete:");
            foreach (var issue in plan.Issues.Take(12)) text.AppendLine($"- {issue.VillageName}: {issue.Message}");
            if (plan.Issues.Count > 12) text.AppendLine($"- ...and {plan.Issues.Count - 12} more (see log).");
        }
        text.AppendLine();
        text.Append("Rebuild villages replaces active Construction tasks only. Other queue groups and history are kept.");

        return AppDialog.ShowCustom(this, text.ToString(), "Ban recovery",
            [("Rebuild villages", MessageBoxResult.Yes), ("Continue without rebuilding", MessageBoxResult.No), ("Keep stopped", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxResult.Cancel,
            successResult: MessageBoxResult.Yes);
    }

    private bool ConfirmConstructionSettingsForRecovery(BanRecoveryPlan plan, IReadOnlyDictionary<string, VillageStatus> current)
    {
        var constructionKey = QueueGroupCatalog.GetKey(QueueGroup.Construction);
        var disabled = plan.AffectedVillageKeys.Where(key =>
        {
            var groups = _villageSettingsStore.GetEnabledGroups(key);
            return groups is not null && !groups.Contains(constructionKey, StringComparer.OrdinalIgnoreCase);
        }).ToList();
        if (disabled.Count == 0) return true;

        var result = AppDialog.ShowCustom(this,
            $"Construction is disabled in {disabled.Count} affected village(s).\n\nEnable it for those villages, keep it disabled (recovery tasks will wait), or cancel queue creation.",
            "Construction disabled",
            [("Enable construction", MessageBoxResult.Yes), ("Keep disabled", MessageBoxResult.No), ("Cancel rebuild", MessageBoxResult.Cancel)],
            MessageBoxImage.Question, MessageBoxResult.Cancel, MessageBoxResult.Cancel,
            successResult: MessageBoxResult.Yes);
        if (result == MessageBoxResult.Cancel) return false;
        if (result != MessageBoxResult.Yes) return true;

        foreach (var key in disabled)
        {
            if (!current.TryGetValue(key, out var status)) continue;
            var groups = (_villageSettingsStore.GetEnabledGroups(key) ?? []).ToList();
            if (!groups.Contains(constructionKey, StringComparer.OrdinalIgnoreCase)) groups.Add(constructionKey);
            PersistAutomationGroupsForVillage(
                new VillageSettingsStore.VillageKeyInfo(
                    key,
                    status.ActiveVillage,
                    status.ActiveVillageCoordX,
                    status.ActiveVillageCoordY,
                    status.IsCapital == true),
                groups);
        }
        return true;
    }
}
