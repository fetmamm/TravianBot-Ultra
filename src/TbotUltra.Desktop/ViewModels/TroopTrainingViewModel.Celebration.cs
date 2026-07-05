using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model for the Troops tab's "Build troops" card. Owns the three
/// per-building rule rows (Barracks / Stable / Workshop) plus all the
/// pure logic that operates on them: load from / write to <see cref="BotOptions"/>,
/// recompute the troop dropdown for the current tribe, apply queue status
/// from a <see cref="VillageStatus"/>, tick countdowns, etc.
///
/// Service-bound work (fetching building or queue status from the worker)
/// stays on MainWindow; the VM exposes <see cref="ConfigChanged"/> so
/// MainWindow can persist + update group running indicators when the user
/// edits a row.
/// </summary>
public sealed partial class TroopTrainingViewModel
{
    public bool UpdateAutoCelebrationAvailability(string? tribe)
    {
        var isTeutons = string.Equals(tribe?.Trim(), "Teutons", StringComparison.OrdinalIgnoreCase);
        var configChanged = false;

        _isConfigSuppressed = true;
        try
        {
            IsAutoCelebrationAvailableForCurrentTribe = isTeutons;
            if (isTeutons)
            {
                if (!_autoCelebrationExplicitlyConfigured && !AutoCelebrationEnabled)
                {
                    AutoCelebrationEnabled = true;
                    configChanged = true;
                }

                if (!AutoCelebrationEnabled)
                {
                    AutoCelebrationStatusText = "Disabled.";
                }
                else if (string.Equals(AutoCelebrationStatusText, "Teutons only.", StringComparison.Ordinal))
                {
                    AutoCelebrationStatusText = "Status not loaded.";
                }
            }
            else
            {
                if (AutoCelebrationEnabled)
                {
                    AutoCelebrationEnabled = false;
                    configChanged = true;
                }

                AutoCelebrationCanStart = false;
                AutoCelebrationRemainingSeconds = null;
                AutoCelebrationStatusText = "Teutons only.";
            }
        }
        finally
        {
            _isConfigSuppressed = false;
        }

        return configChanged;
    }

    public void ApplyBreweryCelebrationStatus(BreweryCelebrationStatus status)
    {
        AutoCelebrationCanStart = status.IsAvailableForTribe
            && status.IsCapital == true
            && status.BreweryExists
            && !status.CelebrationRunning;
        AutoCelebrationRemainingSeconds = status.CelebrationRunning ? status.RemainingSeconds : null;
        AutoCelebrationStatusText = string.IsNullOrWhiteSpace(status.StatusText)
            ? "Status unavailable."
            : status.StatusText;
        BreweryExists = status.BreweryExists;
    }

    /// <summary>
    /// Sets the brewery-found indicator from outside the authoritative status pipeline
    /// (e.g. the MainWindow per-village slot cache learns about a brewery via the
    /// dorf2 scan path or a remote probe). Keeps the troops-tab indicator in sync
    /// with the dashboard's actual knowledge.
    /// </summary>
    public void MarkBreweryExists(bool exists)
    {
        BreweryExists = exists;
    }

    public void ResetBreweryCelebrationStatus(string statusText = "Status not loaded.")
    {
        AutoCelebrationCanStart = false;
        AutoCelebrationRemainingSeconds = null;
        AutoCelebrationStatusText = statusText;
    }

    /// <summary>
    /// Updates the celebration status text without wiping the cached remaining-seconds
    /// timer or CanStart flag. Used by periodic local refreshes that re-confirm the
    /// brewery exists but don't have a fresh authoritative reading to publish — we don't
    /// want a 20s status-refresh to clear a running celebration's countdown.
    /// </summary>
    public void UpdateBreweryStatusTextOnly(string statusText)
    {
        AutoCelebrationStatusText = statusText;
    }

    /// <summary>
    /// Marks the active village as non-capital without wiping the running celebration timer.
    /// The brewery is account-wide (capital only), so any in-progress celebration keeps
    /// ticking even while the user views a different village.
    /// </summary>
    public void MarkBreweryNonCapital(string statusText = "Capital village required.")
    {
        AutoCelebrationCanStart = false;
        AutoCelebrationStatusText = statusText;
    }

    /// <summary>
    /// Pushes a freshly-observed brewery celebration timer (parsed from the queue defer
    /// signal of run_brewery_celebration) into the troops-tab badge so it stays in sync
    /// with the dashboard countdown. The continuous-loop task always defers when the
    /// celebration is running, so without this push the dashboard had a timer but the
    /// troops badge stayed N/A.
    /// </summary>
    public void PushBreweryCelebrationRemainingSeconds(int seconds, string statusText)
    {
        if (seconds <= 0)
        {
            return;
        }

        AutoCelebrationCanStart = false;
        AutoCelebrationRemainingSeconds = seconds;
        BreweryExists = true;
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            AutoCelebrationStatusText = statusText;
        }
    }

    public int? ResolveBreweryCelebrationGroupRemainingSeconds()
    {
        if (!AutoCelebrationEnabled || !IsAutoCelebrationAvailableForCurrentTribe)
        {
            return null;
        }

        return AutoCelebrationRemainingSeconds is > 0
            ? AutoCelebrationRemainingSeconds
            : null;
    }

    /// <summary>
    /// Mirrors the dashboard automation-loop "Brewery Celebration" row timer onto the troops-tab
    /// celebration badge so the two panels never disagree (the row shows a deferred "next try"
    /// countdown while the badge would otherwise sit on "Ready"). Display-only: it does not touch
    /// CanStart / BreweryExists or the loop's own timer source, so there is no feedback into the loop.
    /// </summary>
    public void SyncBreweryCelebrationLoopWait(int? loopRemainingSeconds)
    {
        var normalized = loopRemainingSeconds is > 0 ? loopRemainingSeconds : null;
        if (_breweryLoopWaitSeconds == normalized)
        {
            return;
        }

        _breweryLoopWaitSeconds = normalized;
        RaiseCelebrationTimerChanged();
    }

    /// <summary>
    /// Clears the mirrored loop timer (account/village switch) and refreshes the badge. Kept separate
    /// so reset paths in other partials do not poke the field directly.
    /// </summary>
    private void ClearBreweryLoopWait()
    {
        if (_breweryLoopWaitSeconds is null)
        {
            return;
        }

        _breweryLoopWaitSeconds = null;
        RaiseCelebrationTimerChanged();
    }

    private void RaiseCelebrationTimerChanged()
    {
        OnPropertyChanged(nameof(AutoCelebrationTimerText));
        OnPropertyChanged(nameof(AutoCelebrationBadgeBackground));
        OnPropertyChanged(nameof(AutoCelebrationBadgeBorderBrush));
        OnPropertyChanged(nameof(AutoCelebrationBadgeForeground));
    }

    // Seconds actually shown on the celebration badge: a live/running celebration timer wins,
    // otherwise the mirrored brewery loop-card wait (the deferred "next try" countdown).
    private int? EffectiveCelebrationTimerSeconds
        => AutoCelebrationRemainingSeconds is > 0
            ? AutoCelebrationRemainingSeconds
            : _breweryLoopWaitSeconds is > 0
                ? _breweryLoopWaitSeconds
                : null;

    public bool AutoCelebrationEnabled
    {
        get => _autoCelebrationEnabled;
        set
        {
            // Don't normalize against IsAutoCelebrationAvailableForCurrentTribe here —
            // during startup the tribe state is applied AFTER the config is loaded, so
            // forcing the value to false when availability isn't yet known would silently
            // unset a previously-checked checkbox. Consumers (ContinuousLoop,
            // ResolveBreweryCelebrationGroupRemainingSeconds) already gate on tribe
            // availability before acting on AutoCelebrationEnabled, and the non-Teutons
            // branch of UpdateAutoCelebrationAvailability still flips this to false when
            // the tribe actually changes.
            if (!SetProperty(ref _autoCelebrationEnabled, value))
            {
                return;
            }

            _autoCelebrationExplicitlyConfigured = true;
            if (!value)
            {
                AutoCelebrationCanStart = false;
                if (IsAutoCelebrationAvailableForCurrentTribe)
                {
                    AutoCelebrationStatusText = "Disabled.";
                }
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool IsAutoCelebrationAvailableForCurrentTribe
    {
        get => _isAutoCelebrationAvailableForCurrentTribe;
        private set
        {
            if (!SetProperty(ref _isAutoCelebrationAvailableForCurrentTribe, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAutoCelebrationCheckboxEnabled));
            OnPropertyChanged(nameof(AutoCelebrationTimerText));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBackground));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBorderBrush));
            OnPropertyChanged(nameof(AutoCelebrationBadgeForeground));
            OnPropertyChanged(nameof(BreweryFoundIndicatorText));
            OnPropertyChanged(nameof(BreweryFoundBadgeBackground));
            OnPropertyChanged(nameof(BreweryFoundBadgeBorderBrush));
            OnPropertyChanged(nameof(BreweryFoundBadgeForeground));
        }
    }

    public bool IsAutoCelebrationCheckboxEnabled => IsAutoCelebrationAvailableForCurrentTribe;

    public bool AutoCelebrationCanStart
    {
        get => _autoCelebrationCanStart;
        private set
        {
            if (!SetProperty(ref _autoCelebrationCanStart, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AutoCelebrationTimerText));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBackground));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBorderBrush));
            OnPropertyChanged(nameof(AutoCelebrationBadgeForeground));
        }
    }

    public int? AutoCelebrationRemainingSeconds
    {
        get => _autoCelebrationRemainingSeconds;
        private set
        {
            var normalized = value.HasValue ? Math.Max(0, value.Value) : (int?)null;
            if (!SetProperty(ref _autoCelebrationRemainingSeconds, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(AutoCelebrationTimerText));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBackground));
            OnPropertyChanged(nameof(AutoCelebrationBadgeBorderBrush));
            OnPropertyChanged(nameof(AutoCelebrationBadgeForeground));
            // A running celebration implies the brewery exists, so the Y/N indicator
            // should reflect that too even if no explicit BreweryExists update arrived.
            OnPropertyChanged(nameof(BreweryFoundIndicatorText));
            OnPropertyChanged(nameof(BreweryFoundBadgeBackground));
            OnPropertyChanged(nameof(BreweryFoundBadgeBorderBrush));
            OnPropertyChanged(nameof(BreweryFoundBadgeForeground));
        }
    }

    public bool BreweryExists
    {
        get => _breweryExists;
        private set
        {
            if (!SetProperty(ref _breweryExists, value))
            {
                return;
            }

            OnPropertyChanged(nameof(BreweryFoundIndicatorText));
            OnPropertyChanged(nameof(BreweryFoundBadgeBackground));
            OnPropertyChanged(nameof(BreweryFoundBadgeBorderBrush));
            OnPropertyChanged(nameof(BreweryFoundBadgeForeground));
        }
    }

    /// <summary>"Y" if the brewery has been confirmed (cache hit, scan, or a running
    /// celebration), "N" otherwise. Shown next to "Brewery found:" on the troops tab.</summary>
    public string BreweryFoundIndicatorText
    {
        get
        {
            if (!IsAutoCelebrationAvailableForCurrentTribe)
            {
                return "N/A";
            }

            // A running celebration is itself proof the brewery exists, regardless of
            // whether the most recent dorf2 scan happened to surface gid=35.
            if (BreweryExists || AutoCelebrationRemainingSeconds is > 0)
            {
                return "Y";
            }

            return "N";
        }
    }

    public Brush BreweryFoundBadgeBackground => !IsAutoCelebrationAvailableForCurrentTribe
        ? ThemeColors.Brush("ControlBackgroundBrush")
        : (BreweryExists || AutoCelebrationRemainingSeconds is > 0)
            ? ThemeColors.Brush("SuccessBgBrush")
            : ThemeColors.Brush("DangerBgBrush");

    public Brush BreweryFoundBadgeBorderBrush => !IsAutoCelebrationAvailableForCurrentTribe
        ? ThemeColors.Brush("BorderMutedBrush")
        : (BreweryExists || AutoCelebrationRemainingSeconds is > 0)
            ? ThemeColors.Brush("SuccessBorderBrush")
            : ThemeColors.Brush("RedBrush");

    public Brush BreweryFoundBadgeForeground => !IsAutoCelebrationAvailableForCurrentTribe
        ? ThemeColors.Brush("TextMutedBrush")
        : (BreweryExists || AutoCelebrationRemainingSeconds is > 0)
            ? ThemeColors.Brush("SuccessTextBrush")
            : ThemeColors.Brush("DangerStrongBrush");

    public string AutoCelebrationStatusText
    {
        get => _autoCelebrationStatusText;
        private set => SetProperty(ref _autoCelebrationStatusText, string.IsNullOrWhiteSpace(value) ? "Status unavailable." : value.Trim());
    }

    public string AutoCelebrationTimerText
    {
        get
        {
            if (!IsAutoCelebrationAvailableForCurrentTribe)
            {
                return "N/A";
            }

            if (EffectiveCelebrationTimerSeconds is > 0)
            {
                var time = TimeSpan.FromSeconds(EffectiveCelebrationTimerSeconds.Value);
                return time.TotalHours >= 1
                    ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
                    : $"{time.Minutes:00}:{time.Seconds:00}";
            }

            return AutoCelebrationCanStart ? "Ready" : "N/A";
        }
    }

    public Brush AutoCelebrationBadgeBackground => !IsAutoCelebrationAvailableForCurrentTribe
        ? ThemeColors.Brush("ControlBackgroundBrush")
        : EffectiveCelebrationTimerSeconds is > 0
            ? ThemeColors.Brush("WarningBgBrush")
            : AutoCelebrationCanStart
                ? ThemeColors.Brush("SuccessBgBrush")
                : ThemeColors.Brush("ControlBackgroundBrush");

    public Brush AutoCelebrationBadgeBorderBrush => !IsAutoCelebrationAvailableForCurrentTribe
        ? ThemeColors.Brush("BorderMutedBrush")
        : EffectiveCelebrationTimerSeconds is > 0
            ? ThemeColors.Brush("WarningBorderBrush")
            : AutoCelebrationCanStart
                ? ThemeColors.Brush("SuccessBorderBrush")
                : ThemeColors.Brush("BorderMutedBrush");

    public Brush AutoCelebrationBadgeForeground => !IsAutoCelebrationAvailableForCurrentTribe
        ? ThemeColors.Brush("TextMutedBrush")
        : EffectiveCelebrationTimerSeconds is > 0
            ? ThemeColors.Brush("WarningTextBrush")
            : AutoCelebrationCanStart
                ? ThemeColors.Brush("SuccessTextBrush")
                : ThemeColors.Brush("TextMutedBrush");

}