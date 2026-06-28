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
    /// <summary>
    /// Applies queue / building-exists status onto the rows from a fresh
    /// <see cref="VillageStatus"/>. <paramref name="fallbackQueues"/> is used
    /// when <paramref name="status"/> doesn't carry its own
    /// <c>TroopTrainingQueues</c> (e.g. when a building snapshot is loaded
    /// without a queue read).
    /// </summary>
    public void ApplyStatus(VillageStatus status, IReadOnlyList<TroopTrainingQueueStatus>? fallbackQueues)
    {
        var queueStatuses = status.TroopTrainingQueues ?? fallbackQueues;
        foreach (var option in Buildings)
        {
            var queueStatus = queueStatuses?.FirstOrDefault(item => item.BuildingType == option.BuildingType);
            if (queueStatus is not null)
            {
                option.Exists = queueStatus.Exists;
                option.QueueRemainingSeconds = queueStatus.RemainingSeconds;
                option.QueueFinish = queueStatus.Finish;
                option.QueueStatusText = queueStatus.Exists
                    ? $"Queue: {queueStatus.RemainingText}"
                    : "Building not found";
                continue;
            }

            if (option.Exists)
            {
                if (string.IsNullOrWhiteSpace(option.QueueStatusText))
                {
                    option.QueueStatusText = "Queue not loaded.";
                }

                continue;
            }

            var buildingExists = status.Buildings.Any(item =>
                item.SlotId is > 0
                && ((option.BuildingType == TroopTrainingBuildingType.Barracks && (item.Gid ?? 0) == 19)
                    || (option.BuildingType == TroopTrainingBuildingType.Stable && (item.Gid ?? 0) == 20)
                    || (option.BuildingType == TroopTrainingBuildingType.Workshop && (item.Gid ?? 0) == 21)
                    || string.Equals(item.Name, option.Title, StringComparison.OrdinalIgnoreCase)));
            option.Exists = buildingExists;
            if (!buildingExists)
            {
                option.QueueRemainingSeconds = null;
                option.QueueFinish = null;
                option.QueueStatusText = "Building not found";
            }
            else if (string.IsNullOrWhiteSpace(option.QueueStatusText))
            {
                option.QueueStatusText = "Queue not loaded.";
            }
        }
    }

    /// <summary>Resets every row to a fresh "queue not loaded" state.</summary>
    public void ResetQueueStatus()
    {
        foreach (var option in Buildings)
        {
            option.Exists = false;
            option.QueueRemainingSeconds = null;
            option.QueueFinish = null;
            option.QueueStatusText = "Queue not loaded.";
        }
    }

    public void ResetRuntimeState()
    {
        InfoText = "Configure troop building rules and refresh queues when needed.";
        ResetQueueStatus();
        BreweryExists = false;
        AutoCelebrationCanStart = false;
        AutoCelebrationRemainingSeconds = null;
        AutoCelebrationStatusText = "Status not loaded.";
    }

    public void ClearRuntimeTimers()
    {
        foreach (var option in Buildings)
        {
            option.QueueRemainingSeconds = null;
            option.QueueFinish = null;
            option.QueueStatusText = option.Exists
                ? "Queue refresh requested."
                : "Building not available in this village.";
        }

        AutoCelebrationRemainingSeconds = null;
        AutoCelebrationCanStart = BreweryExists;
        AutoCelebrationStatusText = BreweryExists
            ? "Status refresh requested."
            : "Brewery not available in this village.";
    }

    /// <summary>
    /// Returns the smallest positive remaining-seconds across all enabled
    /// rows, or <c>null</c> if any enabled row has no current queue (which
    /// means the group is ready to run again).
    /// </summary>
    public int? ResolveGroupRemainingSeconds()
    {
        var enabled = Buildings
            .Where(item => item.IsEnabled && item.Exists && !string.IsNullOrWhiteSpace(item.SelectedTroop))
            .ToList();
        if (enabled.Count <= 0)
        {
            return null;
        }

        if (enabled.Any(item => (item.QueueRemainingSeconds ?? 0) <= 0))
        {
            return null;
        }

        return enabled
            .Select(item => item.QueueRemainingSeconds ?? 0)
            .Where(seconds => seconds > 0)
            .DefaultIfEmpty(0)
            .Min();
    }

    /// <summary>
    /// Recomputes every row's queue countdown from its absolute finish against the server clock (source of
    /// truth), falling back to a 1s decrement when no finish is known. Called by the 1Hz clock timer.
    /// </summary>
    public void TickCountdowns(DateTimeOffset serverNow)
    {
        if (Buildings.Count <= 0)
        {
            return;
        }

        foreach (var option in Buildings)
        {
            option.Tick(serverNow);
        }

        if (AutoCelebrationRemainingSeconds is > 0)
        {
            AutoCelebrationRemainingSeconds = Math.Max(0, AutoCelebrationRemainingSeconds.Value - 1);
            if (AutoCelebrationRemainingSeconds == 0)
            {
                AutoCelebrationCanStart = true;
                AutoCelebrationStatusText = "Ready.";
                AutoCelebrationRemainingSeconds = null;
            }
        }
    }

}