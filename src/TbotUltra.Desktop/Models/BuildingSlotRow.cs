namespace TbotUltra.Desktop.Models;

public sealed class BuildingSlotRow
{
    public int SlotId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int? Level { get; init; }
    public int? Gid { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Requirements { get; init; } = string.Empty;

    public string LevelLabel => Level is int value ? value.ToString() : "-";
}
