namespace TbotUltra.Desktop.Models;

public sealed class OasisListRow
{
    public string Coordinates { get; init; } = string.Empty;
    public string OasisType { get; init; } = string.Empty;
    public bool IsOccupied { get; init; }
    public string OccupiedText => IsOccupied ? "Yes" : "No";

    public static OasisListRow FromOasis(OasisInfo oasis)
    {
        return new OasisListRow
        {
            Coordinates = $"{oasis.X}|{oasis.Y}",
            OasisType = oasis.OasisType,
            IsOccupied = oasis.IsOccupied,
        };
    }
}
