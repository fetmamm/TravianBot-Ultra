namespace TbotUltra.Desktop.Models;

public sealed class ResourceFieldRow
{
    public int SlotId { get; init; }
    public string FieldType { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int? Level { get; init; }
    public string Url { get; init; } = string.Empty;
    public int? PendingTargetLevel { get; init; }
    public bool IsMaxLevel { get; init; }

    public string LevelLabel => PendingTargetLevel is int pending
        ? $"({pending})"
        : $"{Level ?? 0}";
}
