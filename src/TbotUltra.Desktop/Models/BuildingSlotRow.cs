using System;
using TbotUltra.Worker.Services;

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
    public int? PendingConstructGid { get; init; }
    public bool IsDemolishing { get; init; }
    public double MapLeft { get; init; }
    public double MapTop { get; init; }

    public bool IsWallSlot { get; init; }
    public bool IsRallyPointSlot { get; init; }

    public string LevelLabel => Level is int value ? value.ToString() : "unknown";
    public bool IsOccupied => !string.IsNullOrWhiteSpace(Name)
        && !string.Equals(Name, "Empty", StringComparison.OrdinalIgnoreCase)
        && (Level ?? 0) > 0;
    public bool IsReservedForConstruction => HasPendingConstruct;
    public bool HasPendingUpgrade => PendingTargetLevel is int pending && pending > (Level ?? 0);
    public bool HasPendingConstruct => !IsOccupied && !string.IsNullOrWhiteSpace(PendingConstructName);
    public bool CanQueueUpgrade => IsOccupied || HasPendingConstruct;
    public string UpgradeName => IsOccupied ? Name : PendingConstructName;
    public int UpgradeBaseLevel => HasPendingConstruct
        ? Math.Max(1, Math.Max(Level ?? 0, PendingTargetLevel ?? 0))
        : Math.Max(Level ?? 0, PendingTargetLevel ?? 0);
    public int? UpgradeGid => IsOccupied ? Gid : PendingConstructGid;
    public string SlotLabel => $"Slot {SlotId}";
    public string NameLabel => IsOccupied
        ? Name
        : HasPendingConstruct
            ? $"{PendingConstructName} (constructing)"
            : (IsWallSlot || IsRallyPointSlot) && !string.IsNullOrWhiteSpace(Name) && !string.Equals(Name, "Empty", StringComparison.OrdinalIgnoreCase)
                ? Name
                : "Empty";
    public string LevelStatusLabel => IsOccupied
        ? IsDemolishing
            ? $"Level {LevelLabel} (Demolishing)"
            : HasPendingUpgrade
                ? $"Level {LevelLabel} ({PendingTargetLevel})"
                : $"Level {LevelLabel}"
        : IsWallSlot || IsRallyPointSlot
            ? "Level 0"
            : "Empty slot";
    public string BadgeText => IsOccupied ? LevelLabel : "+";
    public string ActionHint => IsOccupied
        ? "Click to queue +1 level upgrade."
        : HasPendingConstruct
            ? "This slot is already reserved by a queued construction."
            : "Click to choose and queue a building.";

    public bool IsMaxLevel => CanQueueUpgrade
        && UpgradeGid is int gid
        && UpgradeBaseLevel >= BuildingCatalogService.MaxLevelFor(gid);
}
