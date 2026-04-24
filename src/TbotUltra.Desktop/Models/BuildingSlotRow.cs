using System;

namespace TbotUltra.Desktop.Models;

public sealed class BuildingSlotRow
{
    public int SlotId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int? Level { get; init; }
    public int? Gid { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Requirements { get; init; } = string.Empty;
    public int? PendingTargetLevel { get; init; }
    public string PendingConstructName { get; init; } = string.Empty;
    public double MapLeft { get; init; }
    public double MapTop { get; init; }

    public string LevelLabel => Level is int value ? value.ToString() : "unknown";
    public bool IsOccupied => !string.IsNullOrWhiteSpace(Name)
        && !string.Equals(Name, "Empty", StringComparison.OrdinalIgnoreCase)
        && ((Level ?? 0) > 0 || (Gid ?? 0) > 0 || !Name.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase));
    public bool HasPendingUpgrade => PendingTargetLevel is int pending && pending > (Level ?? 0);
    public bool HasPendingConstruct => !IsOccupied && !string.IsNullOrWhiteSpace(PendingConstructName);
    public string SlotLabel => $"Slot {SlotId}";
    public string NameLabel => IsOccupied
        ? Name
        : HasPendingConstruct
            ? $"{PendingConstructName} (queued)"
            : "Empty";
    public string LevelStatusLabel => IsOccupied
        ? HasPendingUpgrade
            ? $"Level {LevelLabel} -> {PendingTargetLevel}"
            : $"Level {LevelLabel}"
        : "Empty slot";
    public string BadgeText => IsOccupied ? LevelLabel : "+";
    public string ActionHint => IsOccupied
        ? "Click to queue +1 level upgrade."
        : "Click to choose and queue a building.";
}
