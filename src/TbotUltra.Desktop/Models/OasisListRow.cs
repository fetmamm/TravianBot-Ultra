namespace TbotUltra.Desktop.Models;

public sealed class OasisListRow
{
    public string Coordinates { get; init; } = string.Empty;
    public string OasisType { get; init; } = string.Empty;
    public bool IsOccupied { get; init; }
    public string OccupiedText => IsOccupied ? "Yes" : "No";
    public string Animals { get; init; } = string.Empty;
    public string OwnerPlayer { get; init; } = string.Empty;
    public string OwnerAlliance { get; init; } = string.Empty;

    public static OasisListRow FromOasis(OasisInfo oasis)
    {
        return new OasisListRow
        {
            Coordinates = $"{oasis.X}|{oasis.Y}",
            OasisType = oasis.OasisType,
            IsOccupied = oasis.IsOccupied,
            Animals = oasis.Animals,
            OwnerPlayer = oasis.OwnerPlayer,
            OwnerAlliance = oasis.OwnerAlliance,
        };
    }
}
