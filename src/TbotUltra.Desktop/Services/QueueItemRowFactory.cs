using System.Globalization;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services;

public readonly record struct QueueItemEstimate(bool HasData, double Seconds, long Wood, long Clay, long Iron, long Crop)
{
    public static QueueItemEstimate None => new(false, 0, 0, 0, 0, 0);
}

public static class QueueItemRowFactory
{
    public static QueueItemRow Create(
        QueueItem item,
        QueueItemEstimate estimate,
        Guid? displayRunningId,
        Func<QueueItem, string?> resolveVillageName,
        Func<QueueItem, string?> resolveVillageKey,
        Func<QueueItem, string> resolveDisplayName,
        Func<DateTimeOffset, string> formatServerTime)
    {
        var isAutomaticRepair = IsAutomaticRepairItem(item);
        var displayName = resolveDisplayName(item);
        if (isAutomaticRepair && !displayName.StartsWith("[AUTO FIX]", StringComparison.OrdinalIgnoreCase))
        {
            displayName = $"[AUTO FIX] {displayName}";
        }

        return new QueueItemRow
        {
            Id = item.Id,
            Group = item.Group,
            GroupName = QueueGroupCatalog.GetTitle(item.Group),
            VillageName = resolveVillageName(item) ?? "-",
            VillageKey = resolveVillageKey(item) ?? string.Empty,
            DisplayName = displayName,
            TaskName = item.TaskName,
            Status = item.Id == displayRunningId ? QueueStatus.Running : item.Status,
            Retries = item.Retries,
            MaxRetries = item.MaxRetries,
            IsRuntimeOnly = item.IsRuntimeOnly,
            IsAutomaticRepair = isAutomaticRepair,
            AutomaticRepairReason = ResolveAutomaticRepairReason(item),
            CreatedAt = item.CreatedAt,
            NextAttemptAtServer = formatServerTime(item.NextAttemptAt),
            CreatedAtServer = formatServerTime(item.CreatedAt),
            HasEstimate = estimate.HasData,
            EstimateSeconds = estimate.Seconds,
            EstimateWood = estimate.Wood,
            EstimateClay = estimate.Clay,
            EstimateIron = estimate.Iron,
            EstimateCrop = estimate.Crop,
            BuildTimeText = estimate.HasData ? FormatBuildDuration(estimate.Seconds) : "-",
            WoodText = estimate.HasData ? FormatResourceAmount(estimate.Wood) : "-",
            ClayText = estimate.HasData ? FormatResourceAmount(estimate.Clay) : "-",
            IronText = estimate.HasData ? FormatResourceAmount(estimate.Iron) : "-",
            CropText = estimate.HasData ? FormatResourceAmount(estimate.Crop) : "-",
        };
    }

    public static string FormatBuildDuration(double totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0s";
        }

        var span = TimeSpan.FromSeconds(Math.Round(totalSeconds));
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes}m {span.Seconds}s";
        }

        return $"{span.Seconds}s";
    }

    public static string FormatQueueDurationTooltip(double totalSeconds)
        => $"Time: {FormatBuildDuration(totalSeconds)}\n"
            + $"Time (25%): {FormatBuildDuration(totalSeconds * 0.75)}";

    public static string FormatResourceAmount(long amount)
        => amount.ToString("N0", CultureInfo.InvariantCulture);

    private static bool IsAutomaticRepairItem(QueueItem item)
    {
        return item.Payload.TryGetValue(TbotUltra.Core.Configuration.BotOptionPayloadKeys.AutoAddedBy, out var source)
            && string.Equals(
                source,
                TbotUltra.Core.Configuration.BotOptionPayloadKeys.AutoAddedByConstructionRequirementRepair,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAutomaticRepairReason(QueueItem item)
    {
        if (!IsAutomaticRepairItem(item))
        {
            return string.Empty;
        }

        return item.Payload.TryGetValue(TbotUltra.Core.Configuration.BotOptionPayloadKeys.AutoAddedReason, out var reason)
            && !string.IsNullOrWhiteSpace(reason)
                ? reason.Trim()
                : "Automatically added to repair missing construction requirements.";
    }
}
