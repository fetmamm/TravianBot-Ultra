namespace TbotUltra.Desktop.Models;

public sealed class OasisInfo
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Landscape { get; init; }
    public bool IsOccupied { get; init; }
    public string OasisType { get; init; } = string.Empty;
    public string Animals { get; init; } = string.Empty;
    public string OwnerPlayer { get; init; } = string.Empty;
    public string OwnerAlliance { get; init; } = string.Empty;
}
