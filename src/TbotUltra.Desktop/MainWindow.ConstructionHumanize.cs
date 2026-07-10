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
        config[BotOptionPayloadKeys.ConstructionHumanizeDelayEnabled] = settings.Enabled;
        config[BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMin] = percentMin;
        config[BotOptionPayloadKeys.ConstructionHumanizeQueuePercentMax] = percentMax;
        config[BotOptionPayloadKeys.ConstructionHumanizeMaxDelayMinutes] = maxDelay;
        config[BotOptionPayloadKeys.ConstructionHumanizeNoPlusMinMinutes] = noPlusMin;
        config[BotOptionPayloadKeys.ConstructionHumanizeNoPlusMaxMinutes] = noPlusMax;
        _botConfigStore.Save(config);
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
