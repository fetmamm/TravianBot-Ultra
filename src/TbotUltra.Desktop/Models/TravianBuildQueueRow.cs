namespace TbotUltra.Desktop.Models;

public sealed class TravianBuildQueueRow
{
    public string Name { get; init; } = string.Empty;
    public string LevelText { get; init; } = "-";
    public string CountdownText { get; init; } = "-";
    public string FinishAtText { get; init; } = "-";
}
