namespace TbotUltra.Desktop.Models;

public sealed class OasisInfo
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Landscape { get; init; }
    public bool IsOccupied { get; init; }
    public string OasisType { get; init; } = string.Empty;
}
