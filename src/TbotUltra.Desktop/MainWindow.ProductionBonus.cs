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

        var account = _accountStore.ActiveAccountName();
        NormalizeStoredProductionBonus15Timers(account);

        if (!ProductionBonusStateStore.ShouldAttemptNow(_projectRoot, account, DateTimeOffset.UtcNow))
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
        var serverUtcOffset = ProductionBonusDomParser.ParseServerUtcOffsetToken(message) ?? _queueServerTimeOffset;

        // Human-like: never fire at the exact moment a cooldown/reset expires. For +15% the retry is
        // scheduled at the daily reset (server-local hour, resolved per account below); for +25% and
        // failed-video retries the reported relative wait is used. Bonus-end times are never jittered.
        var settings = ProductionBonusStateStore.LoadSettings(_projectRoot, account);
        var delay = ResolveProductionBonusRandomDelay(settings);

        // Resolve (and, in auto mode, learn) the daily reset hour. Null => still learning => poll hourly.
        var resetHour = ResolveProductionBonusResetHour(account, settings, message, now, serverUtcOffset);

        var timers = states
            .Select(state =>
            {
                return new ProductionBonusResourceTimer(
                    state.Resource,
                    state.Bonus,
                    now.AddSeconds(state.RemainingSeconds),
                    ProductionBonusScheduleCalculator.ResolveNextAttemptUtc(state, now, serverUtcOffset, delay, resetHour));
            })
            .ToList();

        ProductionBonusStateStore.Save(_projectRoot, account, timers);
        AppendLog($"Production bonus: saved timers ({FormatProductionBonusStates(states)}); next-run delay +{delay.TotalMinutes:0} min.");
    }

    // True when a manual scan found no active bonus on any resource but the free +15% videos are
    // available to activate — the cue to press the purple buttons instead of only reading the timers.
    private static bool ShouldActivateProductionBonusAfterScan(string? scanResult)
    {
        if (ProductionBonusDomParser.ParseFreeVideoAvailableToken(scanResult) != true)
        {
            return false;
        }

        var states = ProductionBonusDomParser.ParseResultToken(scanResult);
        return !states.Any(state => state.RemainingSeconds > 0);
    }

    // Resolves the daily reset hour (server-local, whole hour) used to schedule +15% retries. In manual
    // mode returns the user's hour. In auto mode returns the learned hour, or — while still learning —
    // null (which makes the scheduler poll hourly) and, on each run, records the free-video availability so
    // the unavailable→available transition (= the reset moment) can be detected and locked in.
    private int? ResolveProductionBonusResetHour(
        string? account,
        ProductionBonusSettings settings,
        string? message,
        DateTimeOffset nowUtc,
        TimeSpan serverUtcOffset)
    {
        if (string.Equals(settings.ResetMode, ProductionBonusStateStore.ResetModeManual, StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(settings.ManualResetHour, 0, 23);
        }

        // Auto mode.
        if (settings.LearnedResetHour is int learned)
        {
            return learned;
        }

        var availableNow = ProductionBonusDomParser.ParseFreeVideoAvailableToken(message);
        if (availableNow is null || string.IsNullOrWhiteSpace(account))
        {
            return null; // can't tell — keep polling hourly
        }

        var currentServerHour = nowUtc.ToOffset(serverUtcOffset).Hour;
        if (availableNow.Value && settings.LastPollServerHour is not null && !settings.LastPollFreeVideoAvailable)
        {
            // Free video just went unavailable→available: this hour is the daily reset. Lock it in.
            ProductionBonusStateStore.SaveLearnState(_projectRoot, account, currentServerHour, currentServerHour, true);
            AppendLog($"Production bonus: learned daily reset hour = {currentServerHour:00}:00 server time (auto).");
            return currentServerHour;
        }

        // No transition yet — remember this poll so the next hourly run can detect the flip.
        ProductionBonusStateStore.SaveLearnState(_projectRoot, account, null, currentServerHour, availableNow.Value);
        return null;
    }

    // Reset hour for the pre-run timer normalization: manual hour, or learned hour in auto mode. Null while
    // auto-learning (no capping then — the next hourly run re-evaluates).
    private static int? ResolveProductionBonusResetHourFromSettings(ProductionBonusSettings settings)
    {
        if (string.Equals(settings.ResetMode, ProductionBonusStateStore.ResetModeManual, StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(settings.ManualResetHour, 0, 23);
        }

        return settings.LearnedResetHour;
    }

    private void NormalizeStoredProductionBonus15Timers(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        var timers = ProductionBonusStateStore.Load(_projectRoot, account);
        if (timers.Count == 0 || timers.All(timer => timer.Bonus != 15))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var settings = ProductionBonusStateStore.LoadSettings(_projectRoot, account);
        // While auto-learning (reset hour unknown) there is nothing to cap to — the next hourly run resolves it.
        if (ResolveProductionBonusResetHourFromSettings(settings) is not int resetHour)
        {
            return;
        }

        var maxDelay = TimeSpan.FromMinutes(settings.DelayMaxMinutes);
        var serverUtcOffset = _queueServerTimeOffset;
        var changed = false;
        var normalized = timers
            .Select(timer =>
            {
                if (timer.Bonus != 15 || timer.NextAttemptAtUtc <= now)
                {
                    return timer;
                }

                var capUtc = ResolveStoredProductionBonus15CapUtc(timer, now, serverUtcOffset, maxDelay, resetHour);
                if (timer.NextAttemptAtUtc <= capUtc)
                {
                    return timer;
                }

                changed = true;
                return timer with { NextAttemptAtUtc = capUtc };
            })
            .ToList();

        if (!changed)
        {
            return;
        }

        ProductionBonusStateStore.Save(_projectRoot, account, normalized);
        AppendLog($"Production bonus: normalized old +15% timers to the daily {resetHour:00}:00 server-time reset.");
    }

    private static DateTimeOffset ResolveStoredProductionBonus15CapUtc(
        ProductionBonusResourceTimer timer,
        DateTimeOffset nowUtc,
        TimeSpan serverUtcOffset,
        TimeSpan maxDelay,
        int resetHour)
    {
        var now = nowUtc.ToUniversalTime();
        var serverNow = now.ToOffset(serverUtcOffset);
        if (timer.BonusEndsAtUtc <= now && serverNow.TimeOfDay >= TimeSpan.FromHours(resetHour))
        {
            return now;
        }

        var remainingSeconds = Math.Max(0, (int)Math.Ceiling((timer.BonusEndsAtUtc - now).TotalSeconds));
        var state = new ProductionBonusDomParser.ProductionBonusResourceState(
            timer.Resource,
            15,
            remainingSeconds,
            ProductionBonusDomParser.NextAttemptAfterDailyResetSeconds,
            false);
        return ProductionBonusScheduleCalculator.ResolveNextAttemptUtc(state, now, serverUtcOffset, maxDelay, resetHour);
    }

    private static TimeSpan ResolveProductionBonusRandomDelay(ProductionBonusSettings settings)
        => TimeSpan.FromMinutes(Random.Shared.Next(settings.DelayMinMinutes, settings.DelayMaxMinutes + 1));

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
            // Persist the scanned timers and (in auto mode) feed the daily-reset-hour learning.
            ApplyProductionBonusResult(result);

            // If nothing is currently active but the free +15% videos are available to click, go ahead and
            // activate them as part of the manual scan (press the purple buttons / watch the videos).
            if (ShouldActivateProductionBonusAfterScan(result))
            {
                AppendLog("Production bonus: no active timers and free +15% videos available — activating now.");
                var activationResult = await _botService.RunActivateProductionBonusVideosAsync(options, AppendLog, operationToken);
                ApplyProductionBonusResult(activationResult);
                CompleteOperation(operationId, operationSw, activationResult);
            }
            else
            {
                CompleteOperation(operationId, operationSw, result);
            }
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
