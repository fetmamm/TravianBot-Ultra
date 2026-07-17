using System;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

// Load/save for the "Humanize construction start delay" settings shown in the Buildings tab's
// Construction settings card. The panel owns the controls and calls these host methods so the
// values persist through the shared per-account config store (same flow as the Dashboard toggles).
// The continuous loop reloads BotOptions each iteration, so a saved change applies to the next
// construction without a restart.
public partial class MainWindow
{
    internal ConstructionHumanizeSettings GetConstructionHumanizeSettings()
    {
        var options = LoadBotOptions();
        return new ConstructionHumanizeSettings(
            options.ConstructionHumanizeDelayEnabled,
            options.ConstructionHumanizeQueuePercentMin,
            options.ConstructionHumanizeQueuePercentMax,
            options.ConstructionHumanizeMaxDelayMinutes,
            options.ConstructionHumanizeNoPlusMinMinutes,
            options.ConstructionHumanizeNoPlusMaxMinutes);
    }

    internal void SaveConstructionHumanizeSettings(ConstructionHumanizeSettings settings)
    {
        if (_botConfigStore is null)
        {
            return;
        }

        // Clamp to sane bounds and keep max >= min, mirroring SettingsWindow's WriteDelayRange.
        var percentMin = Math.Clamp(settings.QueuePercentMin, 0, 100);
        var percentMax = Math.Clamp(Math.Max(percentMin, settings.QueuePercentMax), 0, 100);
        var maxDelay = Math.Clamp(settings.MaxDelayMinutes, 0, 600);
        var noPlusMin = Math.Clamp(settings.NoPlusMinMinutes, 0, 600);
        var noPlusMax = Math.Clamp(Math.Max(noPlusMin, settings.NoPlusMaxMinutes), 0, 600);

        var config = _botConfigStore.Load();
        var wasEnabled = config[BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled]?.GetValue<bool>()
            ?? PacingDefaults.ConstructionHumanizeDelayEnabled;
        var stateVersion = config[BotOptionPayloadKeys.ConstructionHumanizeStateVersion]?.GetValue<int>() ?? 0;
        config[BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled] = settings.Enabled;
        config[BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin] = percentMin;
        config[BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax] = percentMax;
        config[BotOptionPayloadKeys.ConstructionHumanizeMaxDelayMinutes] = maxDelay;
        config[BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes] = noPlusMin;
        config[BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes] = noPlusMax;
        if (wasEnabled != settings.Enabled)
        {
            config[BotOptionPayloadKeys.ConstructionHumanizeStateVersion] = stateVersion == int.MaxValue
                ? 1
                : stateVersion + 1;
        }
        _botConfigStore.Save(config);

        if (wasEnabled != settings.Enabled)
        {
            ApplyConstructionHumanizeToggleTransition(settings.Enabled);
        }
    }

    private void ApplyConstructionHumanizeToggleTransition(bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        var resetCount = 0;
        foreach (var item in _botService.GetQueueItemsForDisplay()
                     .Where(item => item.Status == TbotUltra.Worker.Domain.QueueStatus.Pending)
                     .Where(item => IsConstructionQueueTask(item.TaskName)))
        {
            var reset = Desktop.Services.ConstructionQueueState.ResolveHumanizeToggleReset(item, now);
            var keysToRemove = item.Payload.Keys
                .Where(key => !reset.Payload.ContainsKey(key))
                .ToArray();
            if (!reset.Changed || !_botService.PatchDeferredQueueItem(item.Id, null, keysToRemove, reset.Delay))
            {
                continue;
            }

            item.Payload = reset.Payload;
            resetCount++;
        }

        AppendLog($"[construction-humanize] {(enabled ? "enabled" : "disabled")}; cleared stale pacing state from {resetCount} pending row(s).");
        Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
        RefreshQueueUi();
    }
}

/// <summary>Values behind the Construction settings "Humanize construction start delay" fields.</summary>
public sealed record ConstructionHumanizeSettings(
    bool Enabled,
    double QueuePercentMin,
    double QueuePercentMax,
    double MaxDelayMinutes,
    double NoPlusMinMinutes,
    double NoPlusMaxMinutes);
