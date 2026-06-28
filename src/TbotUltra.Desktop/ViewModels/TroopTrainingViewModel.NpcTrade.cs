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
    public bool NpcTradeEnabled
    {
        get => _npcTradeEnabled;
        set
        {
            if (!SetProperty(ref _npcTradeEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAnyNpcTradeEnabled));
            OnPropertyChanged(nameof(NpcTradeMasterEnabled));
            OnPropertyChanged(nameof(NpcTradeStatusText));
            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public int NpcTradeThresholdPercent
    {
        get => _npcTradeThresholdPercent;
        set
        {
            var normalized = Math.Clamp(value, 1, 100);
            if (!SetProperty(ref _npcTradeThresholdPercent, normalized))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeConstructionEnabled
    {
        get => _npcTradeConstructionEnabled;
        set
        {
            if (!SetProperty(ref _npcTradeConstructionEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAnyNpcTradeEnabled));
            OnPropertyChanged(nameof(NpcTradeMasterEnabled));
            OnPropertyChanged(nameof(NpcTradeStatusText));
            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeAnalyzeWood
    {
        get => _npcTradeAnalyzeWood;
        set
        {
            if (!SetProperty(ref _npcTradeAnalyzeWood, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeAnalyzeClay
    {
        get => _npcTradeAnalyzeClay;
        set
        {
            if (!SetProperty(ref _npcTradeAnalyzeClay, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeAnalyzeIron
    {
        get => _npcTradeAnalyzeIron;
        set
        {
            if (!SetProperty(ref _npcTradeAnalyzeIron, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeAnalyzeCrop
    {
        get => _npcTradeAnalyzeCrop;
        set
        {
            if (!SetProperty(ref _npcTradeAnalyzeCrop, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool NpcTradeBuildTimeLimitEnabled
    {
        get => _npcTradeBuildTimeLimitEnabled;
        set
        {
            if (!SetProperty(ref _npcTradeBuildTimeLimitEnabled, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public int NpcTradeBuildTimeLimitSeconds
    {
        get => _npcTradeBuildTimeLimitSeconds;
        set
        {
            var normalized = value switch
            {
                30 or 60 or 300 or 1200 or 3600 => value,
                _ => 60,
            };

            if (!SetProperty(ref _npcTradeBuildTimeLimitSeconds, normalized))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public bool IsAnyNpcTradeEnabled => NpcTradeEnabled || NpcTradeConstructionEnabled;

    // Single on/off master used by the dashboard Auto settings row. On = enable NPC trade for both
    // troops and building/resource-field upgrades; off = disable both. The per-feature toggles and
    // detailed thresholds stay on the NPC / Trade tab.
    public bool NpcTradeMasterEnabled
    {
        get => IsAnyNpcTradeEnabled;
        set
        {
            if (NpcTradeEnabled == value && NpcTradeConstructionEnabled == value)
            {
                return;
            }

            NpcTradeEnabled = value;
            NpcTradeConstructionEnabled = value;
        }
    }

    public string NpcTradeStatusText => (NpcTradeEnabled, NpcTradeConstructionEnabled) switch
    {
        (true, true) => "Trades for troops, buildings, and resource fields.",
        (true, false) => "Trades while building troops.",
        (false, true) => "Trades while upgrading buildings and resource fields.",
        _ => "NPC trade is off.",
    };

    public bool AllowGoldSpending
    {
        get => _allowGoldSpending;
        set
        {
            if (!SetProperty(ref _allowGoldSpending, value))
            {
                return;
            }

            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public int GoldLimit
    {
        get => _goldLimit;
        set
        {
            var normalized = Math.Clamp(value, 0, 1000);
            if (!SetProperty(ref _goldLimit, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(GoldLimitText));
            if (!_isConfigSuppressed)
            {
                ConfigChanged?.Invoke();
            }
        }
    }

    public string GoldLimitText => $"Gold limit: {GoldLimit}";

}