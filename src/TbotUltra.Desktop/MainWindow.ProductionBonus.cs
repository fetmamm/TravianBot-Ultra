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

    // Backoff between read_daily_reset attempts (per account) so a repeatedly-failing dialog read cannot
    // re-queue on every ~20s refresh. Cleared as soon as a reset hour is successfully read.
    private static readonly TimeSpan DailyResetReadBackoff = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, DateTimeOffset> _dailyResetReadBackoffUntilUtc = new(StringComparer.OrdinalIgnoreCase);

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
        // scheduled at the daily reset (server-local hour, resolved below); for +25% and failed-video
        // retries the reported relative wait is used. Bonus-end times are never jittered.
        var settings = ProductionBonusStateStore.LoadSettings(_projectRoot, account);
        var delay = ResolveProductionBonusRandomDelay(settings);

        // Manual override (General settings) wins; otherwise the hour auto-detected from the daily quests
        // dialog. Null => not known yet => the scheduler polls hourly until read_daily_reset lands it.
        var resetHour = GetEffectiveDailyResetHour(account);

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

    // Resolves the effective daily reset hour (server-local, whole hour) used to schedule +15% retries: the
    // manual override from General settings when enabled, otherwise the per-account hour auto-detected from
    // the daily quests dialog. Null when neither is set (still unknown) — the scheduler polls hourly then.
    private int? GetEffectiveDailyResetHour(string? account)
    {
        var (overrideEnabled, overrideHour) = ReadDailyResetManualOverride();
        if (overrideEnabled)
        {
            return overrideHour;
        }

        if (string.IsNullOrWhiteSpace(account))
        {
            return null;
        }

        return ProductionBonusStateStore.LoadSettings(_projectRoot, account).DetectedResetHour;
    }

    // Reads the global (bot.json) manual-override toggle + hour. Best-effort: any read/parse failure is
    // treated as "override off" so the auto-detected hour is used.
    private (bool Enabled, int Hour) ReadDailyResetManualOverride()
    {
        try
        {
            var config = _botConfigStore.Load();
            var enabled = config[BotOptionPayloadKeys.DailyServerResetManualOverrideEnabled]?.GetValue<bool>() ?? false;
            var hour = config[BotOptionPayloadKeys.DailyServerResetManualHour]?.GetValue<int>() ?? 0;
            return (enabled, Math.Clamp(hour, 0, 23));
        }
        catch
        {
            return (false, 0);
        }
    }

    // Ensures the daily server-reset hour is known: on first start for an account (or whenever it is still
    // unknown) queue a one-off read of the daily quests dialog. Skipped when the +15% feature is off (its
    // only consumer) or a manual override is set. Backed off so a persistent read failure cannot spam.
    private void TryQueueReadDailyResetHour(BotOptions options)
    {
        if (!IsProductionBonusVideoEnabledNow(options))
        {
            return;
        }

        if (ReadDailyResetManualOverride().Enabled)
        {
            return; // manual hour wins — no need to detect
        }

        var account = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        if (ProductionBonusStateStore.LoadSettings(_projectRoot, account).DetectedResetHour is not null)
        {
            return; // already known
        }

        if (HasActiveReadDailyResetTask())
        {
            return;
        }

        if (_dailyResetReadBackoffUntilUtc.TryGetValue(account, out var until) && DateTimeOffset.UtcNow < until)
        {
            return;
        }

        _dailyResetReadBackoffUntilUtc[account] = DateTimeOffset.UtcNow.Add(DailyResetReadBackoff);
        _botService.EnqueueRuntime("read_daily_reset", "Read daily server reset time", null, priority: -45, maxRetries: 1);
        AppendLog("Daily reset: reset hour unknown — queued read_daily_reset (daily quests dialog).");
    }

    // Parses a daily_reset_hour=HH token from a read_daily_reset / collect_daily_quests result and remembers
    // it per account. On a change, re-normalizes any stored +15% timers to the freshly-known reset hour.
    private void ApplyDailyResetReadResult(string? message)
    {
        var hour = DailyResetDomParser.TryParseResetHourToken(message);
        if (hour is null)
        {
            return;
        }

        var account = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        var previous = ProductionBonusStateStore.LoadSettings(_projectRoot, account).DetectedResetHour;
        ProductionBonusStateStore.SaveDetectedResetHour(_projectRoot, account, hour.Value);
        _dailyResetReadBackoffUntilUtc.Remove(account);
        if (previous != hour)
        {
            AppendLog($"Daily reset: detected daily server reset at {hour:00}:00 server time.");
            NormalizeStoredProductionBonus15Timers(account);
        }
    }

    private bool HasActiveReadDailyResetTask()
    {
        return _botService.GetQueueItemsForDisplay()
            .Any(item =>
                string.Equals(item.TaskName, "read_daily_reset", StringComparison.OrdinalIgnoreCase)
                && item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
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
        // While the reset hour is unknown there is nothing to cap to — a later read/hourly run resolves it.
        if (GetEffectiveDailyResetHour(account) is not int resetHour)
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
