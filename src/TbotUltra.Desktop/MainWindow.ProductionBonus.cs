using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    // Account-wide free +15% production bonus videos. Queued from the background refresh when the toggle
    // is on and the store says the next attempt is due; state is persisted so the popup timers survive
    // restart. Mirrors the auto-collect daily-quests pattern.
    private void TryQueueActivateProductionBonus(BotOptions options)
    {
        if (!IsProductionBonusVideoEnabledNow(options))
        {
            RemovePendingProductionBonus();
            return;
        }

        if (HasActiveProductionBonusTask())
        {
            return;
        }

        var timers = ProductionBonusStateStore.Load(_projectRoot, _accountStore.ActiveAccountName());
        if (!ProductionBonusStateStore.ShouldAttemptNow(timers, DateTimeOffset.UtcNow))
        {
            return;
        }

        // Account-wide feature: no per-village payload; the worker reads dorf1 wherever the active village is.
        _botService.EnqueueRuntime("activate_production_bonus", "Activate 15% production", null, priority: -40, maxRetries: 1);
        AppendLog("Production bonus: queued activate_production_bonus (free +15% videos).");
    }

    // Parses the worker's production_bonus=... token and persists absolute end/next-attempt times so the
    // dashboard popup can restore and count down the timers.
    private void ApplyProductionBonusResult(string? message)
    {
        var states = ProductionBonusDomParser.ParseResultToken(message);
        if (states.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var account = _accountStore.ActiveAccountName();

        // Human-like: never fire at the exact moment a cooldown timer expires (24h re-activation, 25%
        // expiry, or the failed-video retry) — add one random delay (min..max minutes) on top. Only
        // "available now" (next attempt = 0, e.g. from a scan) and empty state run promptly. Bonus-end
        // times are never jittered (real expiry shown in the popup).
        var settings = ProductionBonusStateStore.LoadSettings(_projectRoot, account);
        var jitter = TimeSpan.FromMinutes(Random.Shared.Next(settings.DelayMinMinutes, settings.DelayMaxMinutes + 1));

        var timers = states
            .Select(state =>
            {
                var nextAttempt = now.AddSeconds(state.NextAttemptSeconds);
                if (state.NextAttemptSeconds > 0)
                {
                    nextAttempt = nextAttempt.Add(jitter);
                }

                return new ProductionBonusResourceTimer(
                    state.Resource,
                    state.Bonus,
                    now.AddSeconds(state.RemainingSeconds),
                    nextAttempt);
            })
            .ToList();

        ProductionBonusStateStore.Save(_projectRoot, account, timers);
        AppendLog($"Production bonus: saved timers ({FormatProductionBonusStates(states)}); next-run jitter +{jitter.TotalMinutes:0} min.");
    }

    private void ProductionBonusSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var account = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(account))
        {
            AppendLog("Production bonus settings: no active account.");
            return;
        }

        var window = new ProductionBonusSettingsWindow(_projectRoot, account, ScanProductionBonusTimersManually, ClearProductionBonusTimers)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    // Wipes the remembered production-bonus timers so a fresh run can be tested from scratch.
    private void ClearProductionBonusTimers()
    {
        ProductionBonusStateStore.Clear(_projectRoot, _accountStore.ActiveAccountName());
        AppendLog("Production bonus: cleared remembered timers.");
    }

    // Read-only scan of the Advantages timers (no video). Runs as a manual operation on its own session
    // so it works even when the bot is paused or not running, mirroring the adventure-video test buttons.
    private void ScanProductionBonusTimersManually()
        => _ = GuardUiAsync(ScanProductionBonusTimersManuallyAsync);

    private async Task ScanProductionBonusTimersManuallyAsync()
    {
        if (BlockIfSessionSleeping("Scan production bonus timers"))
        {
            return;
        }

        var operationId = BeginOperation("ScanProductionBonus");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] scanning production bonus timers.");
            var result = await _botService.RunScanProductionBonusTimersAsync(options, AppendLog, operationToken);
            ApplyProductionBonusResult(result);
            CompleteOperation(operationId, operationSw, result);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Production bonus scan paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            DisposeOperationCts();
        }
    }

    private bool IsProductionBonusVideoEnabledNow(BotOptions options)
    {
        if (!options.ProductionBonusVideoEnabled)
        {
            return false;
        }

        return ReadCheckBoxChecked(ProductionBonusVideoCheckBox, fallback: options.ProductionBonusVideoEnabled);
    }

    private bool HasActiveProductionBonusTask()
    {
        return _botService.GetQueueItemsForDisplay()
            .Any(item =>
                string.Equals(item.TaskName, "activate_production_bonus", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
    }

    private void RemovePendingProductionBonus()
    {
        var pending = _botService.GetQueueItemsForDisplay()
            .Where(item =>
                string.Equals(item.TaskName, "activate_production_bonus", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Paused)
            .ToList();

        foreach (var item in pending)
        {
            _botService.RemoveQueueItem(item.Id);
        }

        if (pending.Count > 0)
        {
            AppendLog($"Production bonus: disabled — removed {pending.Count} queued activate_production_bonus item(s).");
        }
    }

    private static string FormatProductionBonusStates(
        IReadOnlyList<ProductionBonusDomParser.ProductionBonusResourceState> states)
    {
        return string.Join(
            ", ",
            states.Select(state => $"{state.Resource}={(state.Bonus == 0 ? "none" : state.Bonus + "%")}"));
    }
}
