using System;
using System.Collections.Generic;
using System.Linq;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public static class LiveQueueRowFactory
{
    public static IReadOnlyList<TravianBuildQueueRow> BuildConstructionRows(
        IReadOnlyList<ActiveConstruction> activeConstructions,
        int slotCount,
        bool hasStatus,
        DateTimeOffset nowUtc,
        Func<DateTimeOffset, string> finishTimeFormatter)
    {
        var rows = activeConstructions
            .Take(slotCount)
            .Select(construction => new TravianBuildQueueRow
            {
                Name = construction.Name,
                LevelText = construction.Level.HasValue ? $"Level {construction.Level.Value}" : "-",
                CountdownText = FormatCountdown(
                    construction.Finish?.RemainingSecondsAt(nowUtc) ?? construction.TimeLeftSeconds),
                FinishAtText = construction.Finish is not null
                    ? finishTimeFormatter(construction.Finish.FinishUtc)
                    : construction.FinishAtText ?? "-",
            })
            .ToList();

        PadConstructionRows(rows, slotCount, hasStatus);
        return rows;
    }

    public static IReadOnlyList<TravianSmithyQueueRow> BuildSmithyRows(
        IReadOnlyList<ActiveSmithyUpgrade> activeUpgrades,
        int slotCount,
        bool hasStatus,
        DateTimeOffset nowUtc,
        Func<DateTimeOffset, string> finishTimeFormatter)
    {
        var rows = activeUpgrades
            .Take(slotCount)
            .Select(upgrade => new TravianSmithyQueueRow
            {
                Name = upgrade.Name,
                LevelText = upgrade.TargetLevel.HasValue ? $"Level {upgrade.TargetLevel.Value}" : "-",
                CountdownText = FormatCountdown(
                    upgrade.Finish?.RemainingSecondsAt(nowUtc) ?? upgrade.TimeLeftSeconds),
                FinishAtText = upgrade.Finish is not null
                    ? finishTimeFormatter(upgrade.Finish.FinishUtc)
                    : "-",
            })
            .ToList();

        PadSmithyRows(rows, slotCount, hasStatus);
        return rows;
    }

    private static void PadConstructionRows(
        ICollection<TravianBuildQueueRow> rows,
        int slotCount,
        bool hasStatus)
    {
        while (rows.Count < slotCount)
        {
            rows.Add(new TravianBuildQueueRow { Name = hasStatus ? "Ready" : "Not loaded" });
        }
    }

    private static void PadSmithyRows(
        ICollection<TravianSmithyQueueRow> rows,
        int slotCount,
        bool hasStatus)
    {
        while (rows.Count < slotCount)
        {
            rows.Add(new TravianSmithyQueueRow { Name = hasStatus ? "Ready" : "Not loaded" });
        }
    }

    private static string FormatCountdown(int? seconds)
    {
        if (!seconds.HasValue)
        {
            return "-";
        }

        var value = Math.Max(0, seconds.Value);
        var time = TimeSpan.FromSeconds(value);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }
}
