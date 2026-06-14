namespace TbotUltra.Desktop.Models;

public sealed class TravianBuildQueueRow
{
    public string Icon { get; init; } = "\uE7C3";
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string LevelText { get; init; } = "-";
    public string FinishAtText { get; init; } = "-";
}
