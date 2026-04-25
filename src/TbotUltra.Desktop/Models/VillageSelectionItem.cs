namespace TbotUltra.Desktop.Models;

public sealed class VillageSelectionItem
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool IsCapital { get; init; }
    public int? CoordX { get; init; }
    public int? CoordY { get; init; }

    public string CoordsText => (CoordX.HasValue && CoordY.HasValue)
        ? $"({CoordX}|{CoordY})"
        : string.Empty;

    public string NameWithCoords => string.IsNullOrWhiteSpace(CoordsText)
        ? Name
        : $"{Name} {CoordsText}";

    public string CapitalText => IsCapital ? "(Capital)" : string.Empty;

    public string DisplayName => IsCapital ? $"{Name} (C)" : Name;

    public override string ToString() => DisplayName;
}
