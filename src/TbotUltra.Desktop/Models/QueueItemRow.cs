using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public sealed class QueueItemRow
{
    public Guid Id { get; init; }
    public QueueGroup Group { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public string VillageName { get; init; } = string.Empty;
    // Stable village key (newdid-based) used to filter the queue per village; empty = no village.
    public string VillageKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string TaskName { get; init; } = string.Empty;
    public QueueStatus Status { get; init; }
    public int Retries { get; init; }
    public int MaxRetries { get; init; }
    public string RetriesText => $"{Retries}/{MaxRetries}";
    public bool IsRuntimeOnly { get; init; }
    public bool IsAutomaticRepair { get; init; }
    public string AutomaticRepairReason { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedAtServer { get; init; } = string.Empty;
    public string NextAttemptAtServer { get; init; } = string.Empty;

    // Construction estimate (build time + resource cost). Only set for construction tasks; other
    // tasks (farming, hero, ...) leave these blank. Raw values back the totals; the *Text props
    // back the grid cells. HasEstimate gates whether a row contributes to the totals.
    public bool HasEstimate { get; init; }
    public double EstimateSeconds { get; init; }
    public long EstimateWood { get; init; }
    public long EstimateClay { get; init; }
    public long EstimateIron { get; init; }
    public long EstimateCrop { get; init; }

    public string BuildTimeText { get; init; } = "-";
    public string WoodText { get; init; } = "-";
    public string ClayText { get; init; } = "-";
    public string IronText { get; init; } = "-";
    public string CropText { get; init; } = "-";

    // Combined (uncolored) cost string for the pop-out grid, which is built in code without per-resource coloring.
    public string CostText => HasEstimate ? $"{WoodText} | {ClayText} | {IronText} | {CropText}" : "-";
}
