namespace TbotUltra.Desktop.Models;

public sealed class VillageSelectionItem
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool IsCapital { get; init; }
    public int? CoordX { get; init; }
    public int? CoordY { get; init; }
    public int? Population { get; init; }
    public int? CropFields { get; init; }

    public string CoordsText => (CoordX.HasValue && CoordY.HasValue)
        ? $"({CoordX} | {CoordY})"
        : string.Empty;

    public string PopText
    {
        get
        {
            if (CropFields.HasValue && Population.HasValue)
            {
                return $"{CropFields.Value}c - {Population.Value}";
            }

            if (Population.HasValue)
            {
                return Population.Value.ToString();
            }

            if (CropFields.HasValue)
            {
                return $"{CropFields.Value}c";
            }

            return string.Empty;
        }
    }

    public string NameWithCoords
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Name))
            {
                parts.Add(Name);
            }

            if (!string.IsNullOrWhiteSpace(CoordsText))
            {
                parts.Add(CoordsText);
            }

            // Population/crop fields are intentionally omitted here: the village dropdown shows
            // only the village name and coordinates.
            return string.Join(" ", parts);
        }
    }

    public string CapitalText => IsCapital ? "(Capital)" : string.Empty;

    public string DisplayName => Name;

    public override string ToString() => DisplayName;
}
