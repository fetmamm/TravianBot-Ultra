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
    // Backoff written when a run finished without a state token (e.g. the verify read failed) and nothing
    // is remembered yet, so a persistent failure cannot re-queue on every ~20s tick.
    private static readonly TimeSpan ProductionBonusFailureBackoff = TimeSpan.FromMinutes(30);

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

        if (!ProductionBonusStateStore.ShouldAttemptNow(_projectRoot, _accountStore.ActiveAccountName(), DateTimeOffset.UtcNow))
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
        var account = _accountStore.ActiveAccountName();
        var states = ProductionBonusDomParser.ParseResultToken(message);
        if (states.Count == 0)
        {
            // The run produced no state token (e.g. skipped, or the verify read failed). If nothing is
            // remembered yet, stamp a short backoff so the loop does not re-queue on every tick.
            StampProductionBonusBackoffIfEmpty(account);
            return;
        }

        var now = DateTimeOffset.UtcNow;

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

    private void StampProductionBonusBackoffIfEmpty(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        // Only stamp when nothing is remembered — an existing set of timers already gates the loop.
        if (ProductionBonusStateStore.Load(_projectRoot, account).Count > 0)
        {
            return;
        }

        var next = DateTimeOffset.UtcNow.Add(ProductionBonusFailureBackoff);
        var placeholder = ProductionBonusDomParser.Resources
            .Select(resource => new ProductionBonusResourceTimer(resource, 0, DateTimeOffset.UtcNow, next))
            .ToList();
        ProductionBonusStateStore.Save(_projectRoot, account, placeholder);
        AppendLog($"Production bonus: run produced no state — backing off {ProductionBonusFailureBackoff.TotalMinutes:0} min before retry.");
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

        // Avoid racing the auto-activation over the session/state: if one is queued or running, let it finish.
        if (HasActiveProductionBonusTask())
        {
            AppendLog("Production bonus: a +15% activation is in progress — skipping the manual scan.");
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
